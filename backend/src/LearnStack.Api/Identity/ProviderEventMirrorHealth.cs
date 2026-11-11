using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Diagnostics.Metrics;
using LearnStack.Infrastructure.Identity.ProviderEvents;
using LearnStack.SharedKernel.Errors;

namespace LearnStack.Api.Identity;

/// <summary>Machine mirror health, deliberately independent from the public /healthz liveness response.</summary>
internal sealed class ProviderEventMirrorHealth : IHealthCheck
{
    internal const string Name = "identity-provider-mirror";
    private HealthCheckResult _current = HealthCheckResult.Unhealthy("Provider mirror activation is unqualified.");
    private ProviderEventHealthState? _snapshot;
    private bool _productionFaulted;
    public ProviderEventMirrorHealth(IMeterFactory meters)
    {
        var meter = meters.Create("learnstack.identity");
        meter.CreateObservableGauge("learnstack_identity_provider_mirror_health", () =>
        {
            lock (_gate) return _current.Status == HealthStatus.Healthy ? 0 : _current.Status == HealthStatus.Degraded ? 1 : 2;
        }, description: "0=current traversal, 1=stale/incomplete, 2=blocked or unresolved retention risk; never a gap-free capture assertion.");
        meter.CreateObservableGauge("learnstack_identity_provider_mirror_pending_count", () => Problems(false));
        meter.CreateObservableGauge("learnstack_identity_provider_mirror_oldest_problem_age_seconds", () => Problems(true), "s");
    }
    internal void Blocked(ProviderMirrorStage stage, IdentityEventProviderFailure? failure) =>
        Set(HealthCheckResult.Unhealthy("Provider mirror source or storage is unavailable.", data: new Dictionary<string, object>
        { ["stage"] = stage.ToString(), ["source_failure"] = failure?.ToString() ?? "none" }));
    internal void ProductionFailed()
    {
        lock (_gate) _productionFaulted = true;
        Set(HealthCheckResult.Unhealthy("Provider target production failed; inspect retained targets and requalify the process."));
    }
    internal void Observe(ProviderEventHealthState snapshot, bool sourceReady)
    {
        var data = new Dictionary<string, object>();
        foreach (var problem in snapshot.Problems)
        { data.Add(problem.Reason + "_count", problem.Count); data.Add(problem.Reason + "_age_seconds", problem.OldestAgeSeconds); }
        if (snapshot.LastAttemptedAt is { } attempted) data.Add("last_attempted_at", attempted);
        if (snapshot.LastCompletedAt is { } completed) data.Add("last_completed_at", completed);
        bool productionFaulted;
        lock (_gate) { _snapshot = snapshot; productionFaulted = _productionFaulted; }
        data.Add("production_faulted", productionFaulted);
        if (!sourceReady) return; // Keep the closed source/auth fault rather than overwriting it with ledger-read success.
        Set(productionFaulted ? HealthCheckResult.Unhealthy("Provider target production failed; inspect retained targets and requalify the process.", data: data)
            : snapshot.Critical ? HealthCheckResult.Unhealthy("Provider mirror has an unresolved retention risk or historical gap.", data: data)
            : snapshot.Degraded ? HealthCheckResult.Degraded("Provider mirror has no recent complete retained traversal.", data: data)
            : HealthCheckResult.Healthy("Provider mirror retained traversal is current; gap-free capture is not guaranteed.", data));
    }
    private Measurement<double>[] Problems(bool age)
    {
        string[] reasons = ["fanout_exceeded", "source_collision", "malformed", "oversized", "out_of_window"];
        lock (_gate) return reasons.Select(reason =>
        {
            var problem = _snapshot?.Problems.FirstOrDefault(item => item.Reason == reason);
            return new Measurement<double>(problem is null ? 0 : age ? problem.OldestAgeSeconds : problem.Count,
                new KeyValuePair<string, object?>("reason", reason));
        }).ToArray();
    }
    private readonly object _gate = new();
    private void Set(HealthCheckResult value) { lock (_gate) _current = value; }
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    { lock (_gate) return Task.FromResult(_current); }
}
