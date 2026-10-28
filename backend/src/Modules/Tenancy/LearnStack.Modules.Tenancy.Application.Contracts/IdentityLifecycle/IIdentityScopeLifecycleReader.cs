using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;

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

    /// <summary>Reads on the authorization source's actual isolated child; it never uses the ambient business owner.</summary>
    Task<IdentityScopeLifecycle> ReadAuthorizationAsync(IUnitOfWork child, AuthorizationCandidate candidate,
        CancellationToken cancellationToken = default);
}

/// <summary>A payload-free authorization read failure; failed actual disposal must retain its source owner.</summary>
public sealed class IdentityScopeLifecycleReadException(bool cleanupFailed)
    : InfrastructureException("Identity scope lifecycle is unavailable.")
{
    public bool CleanupFailed { get; } = cleanupFailed;
}
