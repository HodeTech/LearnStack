using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LearnStack.Api.Messaging;

/// <summary>Private readiness observation; the public liveness route does not select this check.</summary>
public sealed class DeliveryHealthCheck(DeliveryObservation observation) : IHealthCheck
{
    public const string Name = "delivery";

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default) => Task.FromResult(observation.IsHealthy
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Delivery dispatch is not healthy."));
}
