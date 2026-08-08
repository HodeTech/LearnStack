using LearnStack.SharedKernel.Resilience;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LearnStack.Infrastructure.Resilience;

/// <summary>
/// Composition-root extension that registers a single
/// <see cref="IProviderResilience{TPort}"/> for the given <paramref name="portName"/>.
/// The configuration shape is fixed in
/// <see href="../../../docs/decisions/0032-exception-handling-logging-and-observability.md">ADR-0032</see>
/// — <c>Resilience:&lt;portName&gt;:</c>.
/// </summary>
/// <remarks>
/// <para>
/// The decorator wiring itself lives on the per-adapter <c>AddProviderAdapter</c>
/// path (added together with the first real adapter; see
/// <c>add-provider-adapter</c> skill). Phase 02a Packet 3 ships the
/// resilience socket only — adapters consume
/// <see cref="IProviderResilience{TPort}"/> from their constructor and route
/// outbound calls through <c>Pipeline.ExecuteAsync</c>. Subsequent packets
/// can layer a Scrutor / DynamicProxy-based decorator on top without
/// changing the socket shape.
/// </para>
/// </remarks>
public static class ProviderResilienceRegistration
{
    public static IServiceCollection AddProviderResilience<TPort>(
        this IServiceCollection services,
        IConfiguration configuration,
        string portName)
        where TPort : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);

        var options = configuration
            .GetSection($"Resilience:{portName}")
            .Get<ResilienceOptions>() ?? new ResilienceOptions();

        services.AddSingleton<IProviderResilience<TPort>>(
            _ => new ProviderResilience<TPort>(portName, options));

        return services;
    }
}
