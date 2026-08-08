using Polly;

namespace LearnStack.SharedKernel.Resilience;

/// <summary>
/// Carrier for the Polly v8 <see cref="ResiliencePipeline"/> that wraps a
/// provider adapter (<c>ILiveClassProvider</c>, <c>IPaymentProvider</c>,
/// <c>IStorageProvider</c>, <c>ISearchProvider</c>, …). Per
/// <see href="../../../docs/decisions/0032-exception-handling-logging-and-observability.md">ADR-0032
/// § Sub-decision 5</see> every adapter receives one of these in its
/// constructor and routes outbound calls through <see cref="Pipeline"/>.
/// </summary>
/// <typeparam name="TPort">The port interface the resilience policy is keyed
/// to (e.g. <c>ILiveClassProvider</c>). Used as a DI discriminator only —
/// the type itself is not consumed at runtime.</typeparam>
/// <remarks>
/// <para>
/// The pipeline is built once from <c>appsettings.Resilience:&lt;PortName&gt;:</c>
/// (Standards 09 § Provider Resilience — Polly v8 ResiliencePipeline) with
/// retry (exponential backoff + jitter), circuit breaker, timeout, and
/// bulkhead policies.
/// </para>
/// <para>
/// Hub HTTP clients (<c>IEntitlementProvider</c>, <c>IUsageReporter</c>,
/// <c>IHubTenantSync</c>) are <strong>excluded</strong> from this pattern —
/// their resilience lives inside the mTLS + signed-JWT + HMAC wrapper per
/// <see href="../../../docs/decisions/0019-learnstack-hub.md">ADR-0019</see>.
/// </para>
/// </remarks>
#pragma warning disable CA1040 // Avoid empty interfaces — the generic type parameter is the DI discriminator.
public interface IProviderResilience<TPort>
#pragma warning restore CA1040
    where TPort : class
{
    /// <summary>The pre-built Polly v8 pipeline; thread-safe and meant to be reused.</summary>
    ResiliencePipeline Pipeline { get; }

    /// <summary>The configuration section name (<c>"liveclass"</c>, <c>"payment"</c>, …).</summary>
    string PortName { get; }
}
