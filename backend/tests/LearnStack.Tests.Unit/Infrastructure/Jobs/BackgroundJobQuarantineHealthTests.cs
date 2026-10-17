using System.Reflection;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

public sealed class BackgroundJobQuarantineHealthTests
{
    [Fact]
    public async Task A_valid_snapshot_cadence_longer_than_progress_stays_fresh_without_weakening_lane_progress()
    {
        var bounds = new BackgroundJobRuntimeOptions
        {
            SnapshotInterval = TimeSpan.FromSeconds(60),
            LeaseDuration = TimeSpan.FromSeconds(15),
            RenewalInterval = TimeSpan.FromSeconds(1),
            SqlTimeout = TimeSpan.FromSeconds(1),
            ProgressTimeout = TimeSpan.FromSeconds(30),
        };
        bounds.Validate();
        await using var fixture = new Fixture(bounds);
        fixture.Observe(false);
        fixture.Time.Advance(TimeSpan.FromSeconds(60));
        fixture.Control.Snapshot.QuarantineObservationHealthy.Should().BeTrue("a supported 60-second cadence must not expire at 30 seconds");
        fixture.Control.Snapshot.RecoveryHealthy.Should().BeFalse("the original lane progress deadline remains independent");
        fixture.Progress();
        fixture.Control.Snapshot.RecoveryHealthy.Should().BeTrue();
        fixture.Control.Snapshot.QuarantineObservationHealthy.Should().BeTrue();
        fixture.Time.Advance(TimeSpan.FromSeconds(100));
        fixture.Control.Snapshot.QuarantineObservationHealthy.Should().BeFalse();
        fixture.Control.Snapshot.HasQuarantinedJobs.Should().BeFalse("staleness preserves, rather than replaces, the last sample");
    }

    [Fact]
    public async Task Unknown_and_failed_refreshes_stay_unhealthy_despite_unrelated_control_progress()
    {
        await using var fixture = new Fixture(new());
        fixture.Control.Snapshot.HasQuarantinedJobs.Should().BeNull();
        fixture.Control.Snapshot.QuarantineObservationHealthy.Should().BeFalse();
        fixture.Observe(false);
        fixture.Control.Snapshot.QuarantineObservationHealthy.Should().BeTrue();
        Set(fixture.Control, "_quarantineFault", true);
        Set(fixture.Control, "_controlFault", true);
        fixture.Progress();
        Set(fixture.Control, "_controlFault", false);
        fixture.Control.Snapshot.RecoveryHealthy.Should().BeTrue();
        fixture.Control.Snapshot.QuarantineObservationHealthy.Should().BeFalse();
        fixture.Control.Snapshot.HasQuarantinedJobs.Should().BeFalse();
        fixture.Observe(true);
        fixture.Control.Snapshot.QuarantineObservationHealthy.Should().BeTrue("the observation itself succeeded");
        fixture.Control.Snapshot.HasQuarantinedJobs.Should().BeTrue();
    }

    [Theory]
    [InlineData(null, false, true, false)]
    [InlineData(false, null, true, false)]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, true)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, true, false)]
    public void Runtime_health_requires_a_fresh_known_negative_quarantine_sample(bool? present, bool? controlPresent, bool fresh, bool healthy)
    {
        var control = new BackgroundJobControlHealth(0, 0, true, true, true, false, 0, 0, 0, 0)
        {
            HasQuarantinedJobs = present,
            HasQuarantinedControl = controlPresent,
            QuarantineObservationHealthy = fresh,
            Recurrence = new(0, 0, 0, false, false, true, true, false, false, true),
        };
        var snapshot = new BackgroundJobRuntimeSnapshot(BackgroundJobRuntimeState.Running,
            new(3, 4, 1, 16, 52), 3, 0, 0, null, true, true, true, true, false, [], [], control);
        snapshot.IsHealthy.Should().Be(healthy);
    }

    [Fact]
    public async Task A_stale_or_failed_control_quarantine_sample_preserves_its_positive_value()
    {
        await using var fixture = new Fixture(new());
        fixture.Control.Snapshot.HasQuarantinedControl.Should().BeNull();
        fixture.Observe(false, true);
        fixture.Control.Snapshot.HasQuarantinedControl.Should().BeTrue();
        fixture.Control.Snapshot.QuarantineObservationHealthy.Should().BeTrue();
        Set(fixture.Control, "_quarantineFault", true);
        fixture.Progress();
        fixture.Control.Snapshot.QuarantineObservationHealthy.Should().BeFalse();
        fixture.Control.Snapshot.HasQuarantinedControl.Should().BeTrue();
        fixture.Observe(false, true);
        fixture.Time.Advance(TimeSpan.FromMinutes(3));
        fixture.Control.Snapshot.QuarantineObservationHealthy.Should().BeFalse();
        fixture.Control.Snapshot.HasQuarantinedControl.Should().BeTrue();
    }

    // These are memory/freshness controls only. Real reads and restart truth are
    // qualified separately against guarded PostgreSQL and the native facade.
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly PrivilegedDataSource _source = new(() => throw new InvalidOperationException("Memory health must not resolve a source."));
        internal Clock Time { get; } = new();
        internal BackgroundJobControl Control { get; }
        internal Fixture(BackgroundJobRuntimeOptions bounds)
        {
            var definitions = BackgroundJobDefinitionRegistry.CreateEmpty();
            var executor = new JobExecutor(Substitute.For<IServiceScopeFactory>(), Substitute.For<ITenantContextAccessor>(),
                new BackgroundJobExecutionAdmission(definitions, definitions, Substitute.For<IAuditCatalog>()), bounds);
            Control = new(_source, definitions, executor, new(definitions.Infrastructure), bounds, new SystemGuidFactory(), Time);
        }
        internal void Observe(bool value, bool controlValue = false)
        {
            Set(Control, "_quarantinePresence", value);
            Set(Control, "_controlQuarantinePresence", controlValue);
            Set(Control, "_quarantineObservedAt", Time.GetTimestamp());
            Set(Control, "_quarantineFault", false);
            Progress();
        }
        internal void Progress()
        {
            Set(Control, "_renewalProgress", Time.GetTimestamp());
            Set(Control, "_controlProgress", Time.GetTimestamp());
            Set(Control, "_terminalProgress", Time.GetTimestamp());
        }
        public async ValueTask DisposeAsync()
        {
            Control.RequestStop();
            await Control.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            _source.IsValueCreated.Should().BeFalse();
            await _source.DisposeAsync();
        }
    }
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        internal void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }
    private static void Set(object target, string field, object value) =>
        target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
