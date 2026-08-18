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
/// There is no decorator and no <c>AddProviderAdapter</c> — the adapter takes
/// <see cref="IProviderResilience{TPort}"/> as a constructor collaborator and
/// routes outbound calls through <c>Pipeline.ExecuteAsync</c> itself (see the
/// <c>add-provider-adapter</c> skill). C# forbids a type parameter as a base
/// type, so no <c>ResilientProviderAdapter&lt;TPort&gt; : TPort</c> can exist;
/// ADR-0032 Amendment 2 records that its original example could not compile.
/// Phase 02a Packet 3 ships the resilience socket only.
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
