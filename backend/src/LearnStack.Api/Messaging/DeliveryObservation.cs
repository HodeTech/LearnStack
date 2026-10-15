using System.Collections.Frozen;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using LearnStack.Infrastructure.Messaging;

namespace LearnStack.Api.Messaging;

/// <summary>Immutable process observation; timestamps use TimeProvider's monotonic clock.</summary>
public sealed record DeliveryObservationSnapshot(
    DeliveryQueueSnapshot Queue,
    bool Started = false,
    bool Stopped = false,
    long? LastProgressTimestamp = null,
    long? LastSnapshotTimestamp = null,
    int OutstandingExecutions = 0,
    bool RefreshFailed = false,
    bool LoopFailed = false,
    bool TerminalAuditFailed = false);

/// <summary>Bounded dispatch telemetry; observable callbacks only read memory.</summary>
public sealed class DeliveryObservation : IDisposable
{
    public const string InstrumentationName = "learnstack.delivery";
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly DeliveryDispatchOptions _options;
    private readonly FrozenSet<string> _labels;
    private readonly FrozenSet<string> _subscriptions;
    private readonly Meter _meter = new(InstrumentationName);
    private readonly ActivitySource _activities = new(InstrumentationName);
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _failed;
    private readonly Counter<long> _deadletter;
    private readonly Counter<long> _dedup;
    private readonly Counter<long> _leaseLost;
    private DeliveryObservationSnapshot _snapshot = new(new DeliveryQueueSnapshot(0, 0, 0));

    public DeliveryObservation(TimeProvider time, DeliveryDispatchOptions options, OutboxEventRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(registry);
        _time = time;
        _options = options;
        _labels = registry.EventTypeLabels.Concat(["unknown", "retired", "invalid"])
            .ToFrozenSet(StringComparer.Ordinal);
        _subscriptions = registry.SubscriptionIds.ToFrozenSet(StringComparer.Ordinal);
        _dedup = _meter.CreateCounter<long>("learnstack_inbox_dedup_total");
        _duration = _meter.CreateHistogram<double>("learnstack_delivery_execute_duration_seconds", "s");
        _failed = _meter.CreateCounter<long>("learnstack_delivery_failed_total");
        _deadletter = _meter.CreateCounter<long>("learnstack_inbox_deadletter_total");
        _leaseLost = _meter.CreateCounter<long>("learnstack_delivery_lease_lost_total");
        _meter.CreateObservableGauge("learnstack_delivery_pending_count", () => Snapshot.Queue.PendingCount);
        _meter.CreateObservableGauge("learnstack_delivery_oldest_eligible_age_seconds",
            () => Snapshot.Queue.OldestEligibleAgeSeconds, "s");
        _meter.CreateObservableGauge("learnstack_delivery_deferred_count", () => Snapshot.Queue.DeferredCount);
        _meter.CreateObservableGauge("learnstack_delivery_heartbeat_age_seconds", () => HeartbeatAge.TotalSeconds, "s");
    }

    public DeliveryObservationSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public TimeSpan HeartbeatAge => Age(Snapshot.LastProgressTimestamp);

    public bool IsHealthy
    {
        get
        {
            var snapshot = Snapshot;
            return snapshot.Started && !snapshot.Stopped && !snapshot.RefreshFailed
                && !snapshot.LoopFailed && !snapshot.TerminalAuditFailed
                && snapshot.OutstandingExecutions == 0
                && Age(snapshot.LastProgressTimestamp) < _options.ProgressTimeout
                && Age(snapshot.LastSnapshotTimestamp) < _options.ProgressTimeout;
        }
    }

    internal void Started() => Update(value => value with { Started = true, Stopped = false });
    internal void Stopped() => Update(value => value with { Stopped = true });
    internal void Progress() => Update(value => value with
    {
        LastProgressTimestamp = _time.GetTimestamp(),
        LoopFailed = false,
    });
    internal void Refreshed(DeliveryQueueSnapshot queue) => Update(value => value with
    {
        Queue = queue,
        LastSnapshotTimestamp = _time.GetTimestamp(),
        RefreshFailed = false,
    });
    internal void RefreshFailed() => Update(value => value with { RefreshFailed = true });
    internal void LoopFailed() => Update(value => value with { LoopFailed = true });
    internal void TerminalAuditFailed() => Update(value => value with { TerminalAuditFailed = true });
    internal void TerminalAuditSucceeded() => Update(value => value with { TerminalAuditFailed = false });
    internal void Outstanding(int count) => Update(value => value with { OutstandingExecutions = count });
    internal void LeaseLost() => _leaseLost.Add(1);
    internal void Failed(string label, string subscription) => _failed.Add(1, Labels(label, subscription));
    internal void Deadletter(string label, string subscription) => _deadletter.Add(1, Labels(label, subscription));
    internal void Deduplicated(string label, string subscription) => _dedup.Add(1, Labels(label, subscription));
    internal void Duration(string label, string subscription, TimeSpan duration) =>
        _duration.Record(Math.Max(0, duration.TotalSeconds), Labels(label, subscription));

    internal Activity BeginAttempt(string label, string subscription)
    {
        // The scheduler has no causal-envelope SELECT grant. Every attempt starts
        // its own canonical trace; the application executor restores original causality.
        var parent = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.None);
        var activity = _activities.StartActivity("delivery.dispatch", ActivityKind.Internal,
            parent)
            ?? new Activity("delivery.dispatch").SetIdFormat(ActivityIdFormat.W3C)
                .SetParentId(parent.TraceId, parent.SpanId, parent.TraceFlags).Start();
        activity.SetTag("event_type", SafeLabel(label));
        activity.SetTag("subscription", SafeSubscription(subscription));
        return activity;
    }

    private KeyValuePair<string, object?>[] Labels(string label, string subscription) =>
        [new("event_type", SafeLabel(label)), new("subscription", SafeSubscription(subscription))];
    private string SafeLabel(string label) => _labels.Contains(label) ? label : "invalid";
    private string SafeSubscription(string subscription) => _subscriptions.Contains(subscription) ? subscription : "invalid";
    private TimeSpan Age(long? timestamp) => timestamp is { } value
        ? _time.GetElapsedTime(value, _time.GetTimestamp()) : _options.ProgressTimeout;

    private void Update(Func<DeliveryObservationSnapshot, DeliveryObservationSnapshot> change)
    {
        lock (_gate) Volatile.Write(ref _snapshot, change(_snapshot));
    }

    public void Dispose()
    {
        _activities.Dispose();
        _meter.Dispose();
    }
}
