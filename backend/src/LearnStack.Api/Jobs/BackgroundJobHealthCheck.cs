using LearnStack.Infrastructure.Jobs;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LearnStack.Api.Jobs;

/// <summary>Memory-only private readiness; this check is not mapped by public liveness.</summary>
public sealed class BackgroundJobHealthCheck(BackgroundJobRuntime runtime) : IHealthCheck
{
    public const string Name = "jobs";

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var snapshot = runtime.Snapshot;
        var data = new Dictionary<string, object>(StringComparer.Ordinal) { ["state"] = snapshot.State.ToString() };
        foreach (var pool in snapshot.Pools) data.Add("pool:" + pool.Pool, pool);
        return Task.FromResult(snapshot.IsHealthy
            ? HealthCheckResult.Healthy(data: data)
            : HealthCheckResult.Unhealthy("Background jobs are not healthy.", data: data));
    }
}
