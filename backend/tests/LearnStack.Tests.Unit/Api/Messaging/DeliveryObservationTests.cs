using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using FluentAssertions;
using LearnStack.Api.Messaging;
using LearnStack.Infrastructure.Messaging;
using LearnStack.SharedKernel.Messaging;
using LearnStack.Tests.Unit.Application.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Messaging;

[Collection(DeliveryCommitGateIsolation.Name)]
public sealed class DeliveryObservationTests
{
    [Fact]
    public async Task Health_requires_progress_and_fresh_snapshot_on_the_monotonic_clock()
    {
        var time = new ManualTimeProvider();
        using var observation = Create(time);
        var check = new DeliveryHealthCheck(observation);
        observation.IsHealthy.Should().BeFalse();
        observation.Started();
        observation.Progress();
        observation.IsHealthy.Should().BeFalse("there is no successful queue observation yet");
        observation.Refreshed(new(9, 4, 20));
        (await check.CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Healthy);

        time.Advance(TimeSpan.FromSeconds(44));
        observation.IsHealthy.Should().BeTrue();
        observation.HeartbeatAge.Should().Be(TimeSpan.FromSeconds(44));
        time.UtcNow = time.UtcNow.AddDays(-30);
        observation.IsHealthy.Should().BeTrue("wall-clock adjustments do not reset elapsed progress");
        time.Advance(TimeSpan.FromSeconds(1));
        observation.IsHealthy.Should().BeFalse("45 seconds is the stale boundary");
        observation.Progress();
        observation.IsHealthy.Should().BeFalse("fresh progress cannot conceal a stale SQL snapshot");
        observation.Refreshed(new(8, 3, 25));
        observation.IsHealthy.Should().BeTrue();
        observation.Stopped();
        (await check.CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public void Failed_refresh_retains_immutable_snapshot_and_terminal_failure_survives_empty_polls()
    {
        using var observation = Create(new ManualTimeProvider());
        observation.Started();
        observation.Progress();
        observation.Refreshed(new(7, 5, 13));
        var before = observation.Snapshot;
        observation.RefreshFailed();
        observation.Snapshot.Queue.Should().BeSameAs(before.Queue);
        before.RefreshFailed.Should().BeFalse("published snapshots are immutable");
        observation.IsHealthy.Should().BeFalse();
        observation.Refreshed(new(6, 5, 13));
        observation.IsHealthy.Should().BeTrue();

        observation.TerminalAuditFailed();
        observation.Progress();
        observation.Refreshed(new(0, 0, 0));
        observation.IsHealthy.Should().BeFalse("a successful empty poll is not terminal audit recovery");
        observation.TerminalAuditSucceeded();
        observation.IsHealthy.Should().BeTrue();
        observation.Outstanding(1);
        observation.IsHealthy.Should().BeFalse();
        observation.Outstanding(0);
        observation.IsHealthy.Should().BeTrue();
        observation.LoopFailed();
        observation.IsHealthy.Should().BeFalse();
        observation.Progress();
        observation.IsHealthy.Should().BeTrue();
    }

    [Fact]
    public void Exact_metric_inventory_has_memory_only_gauges_and_finite_event_labels()
    {
        var instruments = new List<Instrument>();
        var measurements = new List<(Instrument Instrument, double Value, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name != DeliveryObservation.InstrumentationName) return;
            lock (instruments) instruments.Add(instrument);
            owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            lock (measurements) measurements.Add((instrument, value, tags.ToArray()));
        });
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            lock (measurements) measurements.Add((instrument, value, tags.ToArray()));
        });
        listener.Start();
        var time = new ManualTimeProvider();
        using var observation = Create(time);
        observation.Started();
        observation.Progress();
        observation.Refreshed(new(17, 11, 23));
        observation.Duration(nameof(TestEvent), "proof.subscription.v1", TimeSpan.FromSeconds(3.5));
        Meter ownMeter;
        lock (measurements) ownMeter = measurements.Single(value => value.Value == 3.5).Instrument.Meter;
        observation.Failed("private-stored-type-and-tenant", "private-subscription");
        observation.Deadletter("retired", "proof.subscription.v1");
        observation.Deduplicated(nameof(TestEvent), "proof.subscription.v1");
        observation.LeaseLost();
        time.Advance(TimeSpan.FromSeconds(8));
        listener.RecordObservableInstruments();

        Instrument[] ownInstruments;
        lock (instruments) ownInstruments = instruments.Where(value => ReferenceEquals(value.Meter, ownMeter)).ToArray();
        ownInstruments.Select(value => value.Name).Should().BeEquivalentTo(
        [
            "learnstack_delivery_pending_count", "learnstack_delivery_execute_duration_seconds",
            "learnstack_delivery_failed_total", "learnstack_inbox_deadletter_total",
            "learnstack_delivery_lease_lost_total", "learnstack_delivery_oldest_eligible_age_seconds",
            "learnstack_delivery_deferred_count", "learnstack_delivery_heartbeat_age_seconds", "learnstack_inbox_dedup_total",
        ]);
        lock (measurements)
        {
            var own = measurements.Where(value => ReferenceEquals(value.Instrument.Meter, ownMeter)).ToArray();
            own.Single(value => value.Instrument.Name == "learnstack_delivery_pending_count").Value.Should().Be(17);
            own.Single(value => value.Instrument.Name == "learnstack_delivery_deferred_count").Value.Should().Be(11);
            own.Single(value => value.Instrument.Name == "learnstack_delivery_oldest_eligible_age_seconds").Value.Should().Be(23);
            own.Single(value => value.Instrument.Name == "learnstack_delivery_heartbeat_age_seconds").Value.Should().Be(8);
            own.Single(value => value.Instrument.Name == "learnstack_delivery_failed_total").Tags
                .Should().Equal([new KeyValuePair<string, object?>("event_type", "invalid"), new("subscription", "invalid")]);
            own.Where(value => value.Instrument.IsObservable || value.Instrument.Name == "learnstack_delivery_lease_lost_total")
                .Should().OnlyContain(value => value.Tags.Length == 0);
            own.SelectMany(value => value.Tags).Should().OnlyContain(value => value.Key == "event_type" || value.Key == "subscription");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Attempts_get_fresh_canonical_trace_without_inheriting_ambient_identity(bool listen)
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => listen && source.Name == DeliveryObservation.InstrumentationName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var ambient = new Activity("private-caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        using var observation = Create(new ManualTimeProvider());
        var actualSource = (ActivitySource)typeof(DeliveryObservation)
            .GetField("_activities", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(observation)!;
        actualSource.HasListeners().Should().Be(listen,
            "the isolated collection verifies the actual scheduler source's listener state");
        if (!listen)
        {
            using var controlSource = new ActivitySource("learnstack.delivery.no-listener-proof");
            using var control = controlSource.StartActivity("control");
            control.Should().BeNull();
        }
        using var first = observation.BeginAttempt("private-undeclared-type", "private-subscription");
        first.Should().NotBeNull("the scheduler must create an audit trace even when its actual source has no listener");
        using var second = observation.BeginAttempt(nameof(TestEvent), "proof.subscription.v1");
        ActivityContext.TryParse(first.Id, null, out var firstContext).Should().BeTrue();
        ActivityContext.TryParse(second.Id, null, out var secondContext).Should().BeTrue();
        first.Id.Should().HaveLength(55);
        firstContext.TraceId.Should().NotBe(ambient.TraceId).And.NotBe(secondContext.TraceId);
        first.GetTagItem("event_type").Should().Be("invalid");
        first.GetTagItem("subscription").Should().Be("invalid");
        second.GetTagItem("subscription").Should().Be("proof.subscription.v1");
        first.GetTagItem("correlation.id").Should().BeNull();
    }

    [Fact]
    public void Production_bounds_validate_and_unsafe_margins_or_unbounded_capacity_are_refused()
    {
        var defaults = new DeliveryDispatchOptions();
        defaults.Validate();
        defaults.ExecutionTimeout.Should().Be(TimeSpan.FromSeconds(10));
        defaults.SqlTimeout.Should().Be(TimeSpan.FromSeconds(5));
        defaults.LeaseDuration.Should().Be(TimeSpan.FromSeconds(30));
        defaults.MaxOutstandingExecutions.Should().Be(4);
        defaults.ShutdownTimeout.Should().Be(TimeSpan.FromSeconds(30));
        DeliveryDispatchOptions[] invalid = [
            defaults with { ExecutionTimeout = TimeSpan.Zero },
            defaults with { SqlTimeout = TimeSpan.FromSeconds(6) },
            defaults with { LeaseDuration = TimeSpan.FromSeconds(20) },
            defaults with { ProgressTimeout = TimeSpan.FromSeconds(30) },
            defaults with { IdlePoll = TimeSpan.Zero },
            defaults with { SnapshotInterval = TimeSpan.FromSeconds(6) },
            defaults with { ShutdownTimeout = TimeSpan.FromSeconds(31) },
            defaults with { MaxOutstandingExecutions = 5 },
        ];
        foreach (var option in invalid)
            FluentActions.Invoking(option.Validate).Should().Throw<ArgumentException>();
    }

    private static DeliveryObservation Create(TimeProvider time) => new(time, new DeliveryDispatchOptions(),
        OutboxEventRegistry.Create([OutboxEventRegistration.Required(typeof(TestEvent), [OutboxSubscriptionManifest.Pending("proof.subscription.v1")])], [],
            IntegrationEventHandlerRegistry.FromServiceDescriptors(new ServiceCollection()), [typeof(TestEvent).Assembly]));

    private sealed record TestEvent : IntegrationEventBase
    {
        public override string Topic => "learnstack.test.observation";
        public override string PartitionKey => TenantId.ToString("D");
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }
}
