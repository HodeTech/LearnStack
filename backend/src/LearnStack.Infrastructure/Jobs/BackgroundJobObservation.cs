using System.Collections.Frozen;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Required process telemetry. Collection reads only the once-bound concrete memory owners.</summary>
public sealed partial class BackgroundJobObservation : IDisposable
{
    public const string InstrumentationName = "learnstack.jobs";
    private readonly Meter _meter = new(InstrumentationName);
    private readonly ActivitySource _activities = new(InstrumentationName);
    private readonly FrozenSet<string> _pools;
    private readonly FrozenSet<string> _groups;
    private readonly FrozenSet<string> _jobs, _retiredJobs;
    private readonly TimeProvider _time;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _failed, _unknown;
    private Binding? _binding;
    private int _faulted, _disposed;

    public BackgroundJobObservation(BackgroundJobDefinitionRegistry definitions, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(time);
        _pools = definitions.Infrastructure.Pools.Keys.ToFrozenSet(StringComparer.Ordinal);
        _groups = definitions.Infrastructure.CapacityGroups.Keys.ToFrozenSet(StringComparer.Ordinal);
        _jobs = definitions.DefinitionKeys.ToFrozenSet(StringComparer.Ordinal);
        _retiredJobs = definitions.RetiredKeys.ToFrozenSet(StringComparer.Ordinal);
        _time = time;
        _meter.CreateObservableGauge("learnstack_job_state_count", CollectCounts, "1");
        _meter.CreateObservableGauge("learnstack_job_oldest_eligible_age_seconds", CollectEligibleAge, "s");
        _meter.CreateObservableGauge("learnstack_job_heartbeat_age_seconds", CollectHeartbeatAge, "s");
        _meter.CreateObservableGauge("learnstack_job_component_state", CollectComponents, "1");
        _meter.CreateObservableCounter("learnstack_job_control_total", CollectControlTotals, "1");
        _duration = _meter.CreateHistogram<double>("learnstack_job_duration_seconds", "s");
        _failed = _meter.CreateCounter<long>("learnstack_job_failed_total", "1");
        _unknown = _meter.CreateCounter<long>("learnstack_job_unknown_outcome_total", "1");
    }

    internal bool IsHealthy => Volatile.Read(ref _faulted) == 0 && Volatile.Read(ref _disposed) == 0;

    internal void Bind(BackgroundJobRuntime runtime, BackgroundJobRecurrenceRegistry recurrences, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(recurrences);
        ArgumentNullException.ThrowIfNull(time);
        var binding = new Binding(runtime, time, recurrences.Keys.ToFrozenSet(StringComparer.Ordinal));
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.CompareExchange(ref _binding, binding, null) is not null)
            throw new InvalidOperationException("Job observation binds one actual runtime before startup.");
    }

    private BackgroundJobRuntimeSnapshot? Capture(out Binding? binding)
    {
        binding = Volatile.Read(ref _binding);
        if (binding is null) return null;
        try { return binding.Runtime.Snapshot; }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
        {
            Interlocked.Exchange(ref _faulted, 1);
            return null;
        }
    }

    // Each callback captures once, then materializes a bounded sparse result.
    // No iterator can re-enter a runtime getter during listener callbacks.
    private IEnumerable<Measurement<long>> CollectCounts()
    {
        var snapshot = Capture(out var binding);
        var points = new List<Measurement<long>>();
        if (snapshot is null || binding is null) return points;
        points.Add(Count(snapshot.RetainedInvocations, "retained_total"));
        foreach (var pool in snapshot.Pools)
        {
            var name = Pool(pool.Pool);
            points.Add(Count(pool.RunningInvocations, "running", pool: name));
            points.Add(Count(pool.RetainedInvocations, "retained", pool: name));
        }
        points.Add(Count(snapshot.Bridge?.Outstanding ?? 0, "bridge_outstanding"));
        points.Add(Count(snapshot.Bridge?.Retained ?? 0, "bridge_retained"));
        if (snapshot.Control is not { } control) return points;
        points.Add(Count(control.Executions, "execution_owners"));
        points.Add(Count(control.PendingOutcomes, "pending_outcomes"));
        if (control.Recurrence is { } recurrence)
        {
            points.Add(Count(recurrence.ApplicationOwners, "recipient_owners"));
            points.Add(Count(recurrence.RetainedApplications, "recipient_retained"));
            points.Add(Count(recurrence.PendingHistory, "recipient_history"));
        }
        if (control.Observation is not { } observation) return points;
        foreach (var profile in Profiles)
            points.Add(Count(Value(observation, BackgroundJobObservationFamily.Queue, profile), "pending", profile: profile));
        foreach (var artifact in Artifacts)
        {
            points.Add(Count(Value(observation, BackgroundJobObservationFamily.Quarantine, artifact), "quarantined", artifact: artifact));
            for (var reason = 1; reason <= 5; reason++)
                points.Add(Count(Value(observation, BackgroundJobObservationFamily.Deferred, artifact, reason),
                    "deferred", artifact: artifact, reason: DeferredReason(reason)));
        }
        foreach (var group in _groups.Concat(["unknown", "invalid"]))
            points.Add(Count(Value(observation, BackgroundJobObservationFamily.ReservedCapacity, group), "reserved", group: group));
        foreach (var key in binding.Recurrences.Concat(["unknown", "invalid"]))
            for (var artifact = 1; artifact <= 3; artifact++)
                points.Add(Count(Value(observation, BackgroundJobObservationFamily.FanoutPending, key, artifact),
                    "fanout_pending", recurrence: key, artifact: Artifacts[artifact]));
        return points;
    }

    private IEnumerable<Measurement<double>> CollectEligibleAge()
    {
        var snapshot = Capture(out _);
        var points = new List<Measurement<double>>();
        if (snapshot?.Control is not { Observation: { } observation })
            return points;
        foreach (var profile in Profiles)
        {
            var value = observation.Points.SingleOrDefault(point => point.Family == BackgroundJobObservationFamily.Queue
                && point.Label == profile);
            // SQL measures age at statement time. The conservative freshness
            // timestamp predates source acquisition, so it is not an age anchor.
            var age = value is { EligibleCount: > 0 } ? value.OldestEligibleAgeSeconds : 0;
            points.Add(new(age, new KeyValuePair<string, object?>("profile", profile)));
        }
        return points;
    }

    private IEnumerable<Measurement<double>> CollectHeartbeatAge()
    {
        var snapshot = Capture(out var binding);
        var points = new List<Measurement<double>>();
        if (snapshot is null || binding is null) return points;
        foreach (var pool in snapshot.Pools)
            Add(pool.MetadataHeartbeatTimestamp, "metadata", Pool(pool.Pool));
        Add(snapshot.Bridge?.LastProgressTimestamp, "bridge");
        Add(snapshot.Control?.RenewalTimestamp, "renewal");
        Add(snapshot.Control?.RecoveryTimestamp, "recovery");
        Add(snapshot.Control?.TerminalTimestamp, "terminal");
        Add(snapshot.Control?.ObservationTimestamp, "durable_observation");
        Add(snapshot.Control?.Recurrence?.ProgressTimestamp, "recurrence");
        return points;

        void Add(long? timestamp, string component, string pool = "none")
        {
            if (timestamp is { } value)
                points.Add(new(Age(binding.Time, value), new("component", component), new("pool", pool)));
        }
    }

    private IEnumerable<Measurement<long>> CollectControlTotals()
    {
        var snapshot = Capture(out _);
        var points = new List<Measurement<long>>();
        if (snapshot is null) return points;
        foreach (var pool in snapshot.Pools) Add(pool.Generations, "generation", Pool(pool.Pool));
        foreach (var component in snapshot.Maintenance)
            Add(component.Invocations, component.Component is "expiration" ? "expiration_invocation" : "fetched_heartbeat_invocation");
        if (snapshot.Bridge is { } bridge)
        {
            Add(bridge.Acknowledged, "bridge_acknowledged");
            Add(bridge.Failed, "bridge_failed");
            Add(bridge.Deferred, "bridge_deferred");
        }
        if (snapshot.Control is { } control)
        {
            Add(control.Renewals, "renewal_acknowledged");
            Add(control.RenewalsConfirmed, "renewal_history_confirmed");
            Add(control.Retries, "retry_confirmed");
            Add(control.Terminalized, "terminal_confirmed");
            Add(control.UnknownOutcomes, "uncertainty_observation");
        }
        return points;
        void Add(long value, string operation, string pool = "none") =>
            points.Add(new(value, new("operation", operation), new("pool", pool)));
    }

    private IEnumerable<Measurement<long>> CollectComponents()
    {
        var snapshot = Capture(out var binding);
        var points = new List<Measurement<long>>();
        Fact("observation", "healthy", IsHealthy);
        Fact("runtime", "bound", binding is not null);
        Fact("runtime", "snapshot_fault", binding is not null && snapshot is null);
        foreach (var state in Enum.GetValues<BackgroundJobRuntimeState>())
            Fact("runtime", state.ToString(), snapshot?.State == state);
        Fact("runtime", "not_bound", binding is null);
        if (snapshot is null) return points;
        Fact("runtime", "healthy", snapshot.IsHealthy);
        Fact("runtime", "bootstrap_joined", snapshot.BootstrapJoined);
        Fact("runtime", "cleanup_confirmed", snapshot.CleanupConfirmed);
        foreach (var name in _pools)
        {
            var pool = snapshot.Pools.SingleOrDefault(candidate => candidate.Pool == name);
            foreach (var state in Enum.GetValues<HangfirePoolOwnerState>())
                Fact("pool", state.ToString(), pool is not null && pool.State == state.ToString(), name);
            foreach (var reason in Enum.GetValues<HangfireGenerationReason>())
                points.Add(new(pool is not null && pool.Reason == reason.ToString() ? 1 : 0,
                    new("component", "pool"), new("pool", name), new("state", "reason"), new("reason", reason.ToString())));
            Fact("pool", "captured", pool?.GenerationCaptured == true, name);
            Fact("pool", "children_joined", pool?.NormalChildrenJoined == true, name);
            Fact("pool", "outer_joined", pool?.OuterJoined == true, name);
            Fact("pool", "cleanup_confirmed", pool?.GenerationCleanupComplete == true, name);
            Fact("metadata", "not_observed", pool?.MetadataHeartbeatTimestamp is null, name);
            Fact("metadata", "fault", pool?.MetadataHeartbeatFault == true, name);
        }
        foreach (var maintenance in snapshot.Maintenance)
        {
            var component = maintenance.Component is "expiration" ? "expiration" : "fetched_job_heartbeat";
            foreach (var state in Enum.GetValues<HangfireSharedComponentState>())
                Fact(component, state.ToString(), maintenance.State == state.ToString());
            foreach (var outcome in Enum.GetValues<HangfireSharedComponentOutcome>())
                points.Add(new(maintenance.Outcome == outcome.ToString() ? 1 : 0,
                    new("component", component), new("pool", "none"), new("state", "outcome"), new("reason", outcome.ToString())));
            Fact(component, "actual_joined", maintenance.ActualJoined);
            Fact(component, "owns_resources", maintenance.OwnsResources);
            Fact(component, "storage_fault", maintenance.StorageFault);
            Fact(component, "healthy", maintenance.IsHealthy);
        }
        Fact("bridge", "healthy", snapshot.BridgeHealthy);
        Missing("bridge", snapshot.Bridge?.LastProgressTimestamp);
        var control = snapshot.Control;
        Fact("renewal", "healthy", control?.RenewalHealthy == true);
        Missing("renewal", control?.RenewalTimestamp);
        Fact("recovery", "healthy", control?.RecoveryHealthy == true);
        Missing("recovery", control?.RecoveryTimestamp);
        Fact("terminal", "healthy", control?.TerminalHealthy == true);
        Missing("terminal", control?.TerminalTimestamp);
        Fact("durable_observation", "healthy", control?.ObservationHealthy == true);
        Fact("durable_observation", "refresh_failed", control?.ObservationFailed == true);
        Missing("durable_observation", control?.ObservationTimestamp);
        Fact("recurrence", "healthy", control?.Recurrence?.IsHealthy == true);
        Fact("recurrence", "native_healthy", control?.Recurrence?.NativeHealthy == true);
        Fact("recurrence", "sql_healthy", control?.Recurrence?.SqlHealthy == true);
        Missing("recurrence", control?.Recurrence?.ProgressTimestamp);
        return points;
        void Missing(string component, long? timestamp) => Fact(component, "not_observed", timestamp is null);
        void Fact(string component, string state, bool value, string pool = "none") =>
            points.Add(new(value ? 1 : 0, new("component", component), new("pool", pool), new("state", state), new("reason", "none")));
    }

    private static readonly string[] Profiles = ["transactional", "external", "invalid"];
    private static readonly string[] Artifacts = ["job", "recurrence", "occurrence", "recipient"];
    private static string DeferredReason(int code) => code switch
    {
        1 => "unknown",
        2 => "retired",
        3 => "pending",
        4 => "deployment_mismatch",
        5 => "invalid",
        _ => throw new InvalidOperationException("The observation reason is not declared."),
    };
    private string Pool(string name) => _pools.Contains(name) ? name : "invalid";
    private static double Age(TimeProvider time, long timestamp) => Math.Max(0, time.GetElapsedTime(timestamp, time.GetTimestamp()).TotalSeconds);
    private static long Value(BackgroundJobDurableObservation observation, BackgroundJobObservationFamily family, string label, int code = 0) =>
        observation.Points.SingleOrDefault(point => point.Family == family && point.Label == label && point.Code == code)?.Count ?? 0;
    private static Measurement<long> Count(long value, string kind, string pool = "none", string profile = "none",
        string group = "none", string recurrence = "none", string artifact = "none", string reason = "none") =>
        new(value, new("kind", kind), new("pool", pool), new("profile", profile), new("capacity_group", group),
            new("recurrence", recurrence), new("artifact", artifact), new("reason", reason));

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        try { _meter.Dispose(); }
        finally { _activities.Dispose(); }
    }

    private sealed record Binding(BackgroundJobRuntime Runtime, TimeProvider Time, FrozenSet<string> Recurrences);
}
