using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LearnStack.Api.Messaging;

/// <summary>Private readiness observation; the public liveness route does not select this check.</summary>
public sealed class OutboxHealthCheck(OutboxObservation observation) : IHealthCheck
{
    public const string Name = "outbox";

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default) => Task.FromResult(observation.IsHealthy
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Outbox dispatch is not healthy."));
}
