using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Tenancy;

/// <summary>
/// The institution host's independent public scope, preserved by the factory after
/// reconciliation succeeds (ADR-0036 Amendment 8 and ADR-0052).
/// </summary>
public sealed class HostScope
{
    internal HostScope(TenantId tenantId, OrganizationId? organizationId)
    {
        TenantId = tenantId;
        OrganizationId = organizationId;
    }

    public TenantId TenantId { get; }

    public OrganizationId? OrganizationId { get; }
}
