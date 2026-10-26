using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;

/// <summary>Four fresh lifecycle facts for the current trusted announced scope; no row or directory projection.</summary>
public sealed record IdentityScopeLifecycle(
    bool TenantExistsAndNotDeleted,
    bool TenantAllowsOrdinaryWork,
    bool OrganizationBelongsToTenant,
    bool OrganizationAllowsOrdinaryWork);

public interface IIdentityScopeLifecycleReader
{
    Task<IdentityScopeLifecycle> ReadAsync(TenantId tenantId, OrganizationId? organizationId,
        CancellationToken cancellationToken = default);
}
