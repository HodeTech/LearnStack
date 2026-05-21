using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LearnStack.Infrastructure.Observability;

/// <summary>
/// Composition-root extension that registers the singleton
/// <see cref="ITenantContextAccessor"/> + <see cref="TenantContextSpanProcessor"/>.
/// The OpenTelemetry tracing pipeline itself is wired in
/// <c>LearnStack.Api.Composition.CrossCuttingFoundationExtensions</c>; this
/// extension only registers the types the pipeline depends on, so the
/// singleton lifetimes are correct before the SDK builds.
/// </summary>
public static class ObservabilityRegistration
{
    public static IServiceCollection AddLearnStackObservabilityServices(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ITenantContextAccessor, TenantContextAccessor>();
        services.TryAddSingleton<TenantContextSpanProcessor>();

        return services;
    }
}
