using LearnStack.Infrastructure.Observability.Serilog;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LearnStack.Infrastructure.Observability;

/// <summary>
/// Composition-root extension that registers the singleton
/// <see cref="ITenantContextAccessor"/>, the
/// <see cref="TenantContextSpanProcessor"/>, and the Serilog enrichers
/// (<see cref="RedactSensitiveFieldsEnricher"/> +
/// <see cref="CorrelationContextEnricher"/>) so the Serilog and OTel
/// pipelines wired by <c>LearnStack.Api</c> can resolve them as singletons.
/// </summary>
public static class ObservabilityRegistration
{
    public static IServiceCollection AddLearnStackObservabilityServices(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ITenantContextAccessor, TenantContextAccessor>();
        services.TryAddSingleton<TenantContextSpanProcessor>();
        services.TryAddSingleton<RedactSensitiveFieldsEnricher>();
        services.TryAddSingleton<CorrelationContextEnricher>();

        return services;
    }
}
