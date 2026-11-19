using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Infrastructure.PlatformOperator;

/// <summary>
/// The immutable tenant context of one admitted operator act, for the tenant it names, which may be the tenant the act is about to create (ADR-0102 § 1).
/// </summary>
/// <remarks>
/// Resolved and ambient, as a seed, event or job context is, with no actor: the business columns of
/// an operator act carry the registry actor and the audit rows carry the operator in metadata
/// (ADR-0101 § 9), so nothing here names a user. Only
/// <see cref="PlatformOperatorActExecutor"/> constructs one.
/// </remarks>
internal sealed class OperatorTenantContext(TenantId tenantId, string? correlationId) : ITenantContext
{
    public bool IsResolved => true;

    public TenantContextOrigin? Origin => TenantContextOrigin.Ambient;

    public TenantId TenantId { get; } = tenantId;

    public OrganizationId? OrganizationId => null;

    public UserId? UserId => null;

    public string? CorrelationId { get; } = correlationId;

    public string? ModuleName => null;
}
