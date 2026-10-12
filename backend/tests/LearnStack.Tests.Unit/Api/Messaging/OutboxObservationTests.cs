using System.Diagnostics;
using System.Diagnostics.Metrics;
using FluentAssertions;
using LearnStack.Api.Messaging;
using LearnStack.Infrastructure.Messaging;
using LearnStack.SharedKernel.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Messaging;

public sealed class OutboxObservationTests
{
    [Fact]
    public async Task Health_requires_progress_and_fresh_snapshot_on_the_monotonic_clock()
    {
        var time = new ManualTimeProvider();
        using var observation = Create(time);
        var check = new OutboxHealthCheck(observation);
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
            if (instrument.Meter.Name != OutboxObservation.InstrumentationName) return;
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
        observation.Duration(nameof(TestEvent), TimeSpan.FromSeconds(3.5));
        Meter ownMeter;
        lock (measurements) ownMeter = measurements.Single(value => value.Value == 3.5).Instrument.Meter;
        observation.Failed("private-stored-type-and-tenant");
        observation.Deadletter("retired");
        observation.LeaseLost();
        time.Advance(TimeSpan.FromSeconds(8));
        listener.RecordObservableInstruments();

        Instrument[] ownInstruments;
        lock (instruments) ownInstruments = instruments.Where(value => ReferenceEquals(value.Meter, ownMeter)).ToArray();
        ownInstruments.Select(value => value.Name).Should().BeEquivalentTo(
        [
            "learnstack_outbox_pending_count", "learnstack_outbox_dispatch_duration_seconds",
            "learnstack_outbox_dispatch_failed_total", "learnstack_outbox_deadletter_total",
            "learnstack_outbox_lease_lost_total", "learnstack_outbox_oldest_eligible_age_seconds",
            "learnstack_outbox_deferred_count", "learnstack_outbox_heartbeat_age_seconds",
        ]);
        lock (measurements)
        {
            var own = measurements.Where(value => ReferenceEquals(value.Instrument.Meter, ownMeter)).ToArray();
            own.Single(value => value.Instrument.Name == "learnstack_outbox_pending_count").Value.Should().Be(17);
            own.Single(value => value.Instrument.Name == "learnstack_outbox_deferred_count").Value.Should().Be(11);
            own.Single(value => value.Instrument.Name == "learnstack_outbox_oldest_eligible_age_seconds").Value.Should().Be(23);
            own.Single(value => value.Instrument.Name == "learnstack_outbox_heartbeat_age_seconds").Value.Should().Be(8);
            own.Single(value => value.Instrument.Name == "learnstack_outbox_dispatch_failed_total").Tags
                .Should().Equal([new KeyValuePair<string, object?>("event_type", "invalid")]);
            own.Where(value => value.Instrument.IsObservable || value.Instrument.Name == "learnstack_outbox_lease_lost_total")
                .Should().OnlyContain(value => value.Tags.Length == 0);
            own.SelectMany(value => value.Tags).Should().OnlyContain(value => value.Key == "event_type");
        }
    }

    [Theory]
    [InlineData("00-0123456789abcdef0123456789abcdef-0123456789abcdef-01")]
    [InlineData("01-0123456789abcdef0123456789abcdef-0123456789abcdef-01-private-extension")]
    [InlineData("private-invalid-correlation")]
    public void Attempts_always_get_fresh_valid_trace_identity_without_listeners(string correlation)
    {
        using var observation = Create(new ManualTimeProvider());
        using var first = observation.BeginAttempt(correlation, "private-undeclared-type");
        ActivityContext.TryParse(first.Id, null, out var firstContext).Should().BeTrue();
        using var second = observation.BeginAttempt(correlation, "unknown");
        ActivityContext.TryParse(second.Id, null, out var secondContext).Should().BeTrue();
        firstContext.SpanId.Should().NotBe(secondContext.SpanId);
        first.GetTagItem("event_type").Should().Be("invalid");
        if (!ActivityContext.TryParse(correlation, null, out _))
            first.GetTagItem("correlation.id").Should().BeNull();
        else
            first.GetTagItem("correlation.id").Should().Be(
                "00-0123456789abcdef0123456789abcdef-0123456789abcdef-01");
    }

    private static OutboxObservation Create(TimeProvider time) => new(time, new OutboxDispatchOptions(),
        OutboxEventRegistry.Create([OutboxEventRegistration.IntentionallyUnsubscribed(typeof(TestEvent))], [],
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
