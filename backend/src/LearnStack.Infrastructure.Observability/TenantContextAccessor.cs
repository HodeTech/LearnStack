using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Infrastructure.Observability;

/// <summary>
/// Singleton, <see cref="AsyncLocal{T}"/>-backed implementation of
/// <see cref="ITenantContextAccessor"/>. Per ADR-0032 § Sub-decision 10:
/// cross-cutting infrastructure (OTel span processor, Serilog enricher,
/// Sentry enricher) reads the current tenant context through this accessor
/// instead of injecting <see cref="ITenantContext"/> directly — that context is
/// registered transient and resolved from this accessor on every access, so a
/// singleton processor capturing it would pin one request's value for the
/// process lifetime rather than fail at startup.
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
