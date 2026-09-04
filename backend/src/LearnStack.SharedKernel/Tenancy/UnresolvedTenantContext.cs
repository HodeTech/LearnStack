using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Tenancy;

/// <summary>
/// Default <see cref="ITenantContext"/> registered at the composition root
/// so modules can inject the contract before any resolution middleware has
/// populated the request. <see cref="IsResolved"/> is <c>false</c>; reading
/// <see cref="TenantId"/> raises <see cref="InvalidOperationException"/> so
/// the caller cannot accidentally proceed with a zero <see cref="Guid"/>.
/// </summary>
/// <remarks>
/// The real population sites (per ADR-0032 § Sub-decision 10) overwrite the
/// scoped instance once they resolve. <c>TenantResolverMiddleware</c> is the first of
/// them <b>on an HTTP request</b> — <c>InProcessEventBus</c> has written the accessor
/// for the integration-event handler scope since Packet 5 — and it writes this instance
/// <b>explicitly</b> on the requests that
/// legitimately have no tenant — a platform host, matrix rows 13 and 15. That is not
/// a refusal: the pipeline decides what may run without a tenant. Every request that
/// classification never classified, and every non-HTTP entry point until Phase 02b
/// wires its own, still arrives here by default.
/// </remarks>
public sealed class UnresolvedTenantContext : ITenantContext
{
    public static UnresolvedTenantContext Instance { get; } = new();

    public bool IsResolved => false;

    public TenantId TenantId => throw new InvalidOperationException(
        "TenantId is not available on an unresolved tenant context. Gate reads on IsResolved.");

    public OrganizationId? OrganizationId => null;

    public UserId? UserId => null;

    public string? CorrelationId => null;

    public string? ModuleName => null;
}
