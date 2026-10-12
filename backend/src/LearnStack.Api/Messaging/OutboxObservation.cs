using System.Collections.Frozen;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using LearnStack.Infrastructure.Messaging;

namespace LearnStack.Api.Messaging;

/// <summary>Immutable process observation; timestamps use TimeProvider's monotonic clock.</summary>
public sealed record OutboxObservationSnapshot(
    OutboxQueueSnapshot Queue,
    bool Started = false,
    bool Stopped = false,
    long? LastProgressTimestamp = null,
    long? LastSnapshotTimestamp = null,
    int OutstandingPublishes = 0,
    bool RefreshFailed = false,
    bool LoopFailed = false,
    bool TerminalAuditFailed = false);

/// <summary>Bounded dispatch telemetry; observable callbacks only read memory.</summary>
public sealed class OutboxObservation : IDisposable
{
    public const string InstrumentationName = "learnstack.outbox";
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly OutboxDispatchOptions _options;
    private readonly FrozenSet<string> _labels;
    private readonly Meter _meter = new(InstrumentationName);
    private readonly ActivitySource _activities = new(InstrumentationName);
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _failed;
    private readonly Counter<long> _deadletter;
    private readonly Counter<long> _leaseLost;
    private OutboxObservationSnapshot _snapshot = new(new OutboxQueueSnapshot(0, 0, 0));

    public OutboxObservation(TimeProvider time, OutboxDispatchOptions options, OutboxEventRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(registry);
        _time = time;
        _options = options;
        _labels = registry.EventTypeLabels.Concat(["unknown", "retired", "invalid"])
            .ToFrozenSet(StringComparer.Ordinal);
        _duration = _meter.CreateHistogram<double>("learnstack_outbox_dispatch_duration_seconds", "s");
        _failed = _meter.CreateCounter<long>("learnstack_outbox_dispatch_failed_total");
        _deadletter = _meter.CreateCounter<long>("learnstack_outbox_deadletter_total");
        _leaseLost = _meter.CreateCounter<long>("learnstack_outbox_lease_lost_total");
        _meter.CreateObservableGauge("learnstack_outbox_pending_count", () => Snapshot.Queue.PendingCount);
        _meter.CreateObservableGauge("learnstack_outbox_oldest_eligible_age_seconds",
            () => Snapshot.Queue.OldestEligibleAgeSeconds, "s");
        _meter.CreateObservableGauge("learnstack_outbox_deferred_count", () => Snapshot.Queue.DeferredCount);
        _meter.CreateObservableGauge("learnstack_outbox_heartbeat_age_seconds", () => HeartbeatAge.TotalSeconds, "s");
    }

    public OutboxObservationSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public TimeSpan HeartbeatAge => Age(Snapshot.LastProgressTimestamp);

    public bool IsHealthy
    {
        get
        {
            var snapshot = Snapshot;
            return snapshot.Started && !snapshot.Stopped && !snapshot.RefreshFailed
                && !snapshot.LoopFailed && !snapshot.TerminalAuditFailed
                && snapshot.OutstandingPublishes == 0
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
    internal void Refreshed(OutboxQueueSnapshot queue) => Update(value => value with
    {
        Queue = queue,
        LastSnapshotTimestamp = _time.GetTimestamp(),
        RefreshFailed = false,
    });
    internal void RefreshFailed() => Update(value => value with { RefreshFailed = true });
    internal void LoopFailed() => Update(value => value with { LoopFailed = true });
    internal void TerminalAuditFailed() => Update(value => value with { TerminalAuditFailed = true });
    internal void TerminalAuditSucceeded() => Update(value => value with { TerminalAuditFailed = false });
    internal void Outstanding(int count) => Update(value => value with { OutstandingPublishes = count });
    internal void LeaseLost() => _leaseLost.Add(1);
    internal void Failed(string label) => _failed.Add(1, EventType(label));
    internal void Deadletter(string label) => _deadletter.Add(1, EventType(label));
    internal void Duration(string label, TimeSpan duration) =>
        _duration.Record(Math.Max(0, duration.TotalSeconds), EventType(label));

    internal Activity BeginAttempt(string correlation, string label)
    {
        var valid = ActivityContext.TryParse(correlation, traceState: null, out var parent);
        var activity = _activities.StartActivity("outbox.dispatch", ActivityKind.Producer, parent);
        if (activity is null)
        {
            // Audit needs a fresh valid attempt trace even with no telemetry listener.
            activity = new Activity("outbox.dispatch").SetIdFormat(ActivityIdFormat.W3C);
            if (valid) activity.SetParentId(parent.TraceId, parent.SpanId, parent.TraceFlags);
            activity.Start();
        }

        activity.SetTag("event_type", SafeLabel(label));
        // Reconstruct the bounded identity; a future-version traceparent may
        // contain additional fields that must not become diagnostic data.
        if (valid) activity.SetTag("correlation.id",
            $"00-{parent.TraceId}-{parent.SpanId}-{((parent.TraceFlags & ActivityTraceFlags.Recorded) != 0 ? "01" : "00")}");
        return activity;
    }

    private KeyValuePair<string, object?> EventType(string label) => new("event_type", SafeLabel(label));
    private string SafeLabel(string label) => _labels.Contains(label) ? label : "invalid";
    private TimeSpan Age(long? timestamp) => timestamp is { } value
        ? _time.GetElapsedTime(value, _time.GetTimestamp()) : _options.ProgressTimeout;

    private void Update(Func<OutboxObservationSnapshot, OutboxObservationSnapshot> change)
    {
        lock (_gate) Volatile.Write(ref _snapshot, change(_snapshot));
    }

    public void Dispose()
    {
        _activities.Dispose();
        _meter.Dispose();
    }
}
