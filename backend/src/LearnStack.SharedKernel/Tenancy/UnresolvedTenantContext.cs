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
/// scoped instance once they resolve. Until Packet 7 lands
/// <c>TenantResolverMiddleware</c>, every request runs against this default —
/// the <c>TenantContextBehavior</c> short-circuits with
/// <c>Result.Fail(tenant_mismatch)</c> before any handler runs.
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
