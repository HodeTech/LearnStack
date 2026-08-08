using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Infrastructure.Observability;

/// <summary>
/// Singleton, <see cref="AsyncLocal{T}"/>-backed implementation of
/// <see cref="ITenantContextAccessor"/>. Per ADR-0032 § Sub-decision 10:
/// cross-cutting infrastructure (OTel span processor, Serilog enricher,
/// Sentry enricher) reads the current tenant context through this accessor
/// instead of injecting the request-scoped <see cref="ITenantContext"/>
/// directly — the lifetime mismatch (singleton processor versus scoped
/// context) would otherwise fail at startup.
/// </summary>
internal sealed class TenantContextAccessor : ITenantContextAccessor
{
    private static readonly AsyncLocal<ITenantContext?> CurrentContext = new();

    public ITenantContext? Current
    {
        get => CurrentContext.Value;
        set => CurrentContext.Value = value;
    }
}
