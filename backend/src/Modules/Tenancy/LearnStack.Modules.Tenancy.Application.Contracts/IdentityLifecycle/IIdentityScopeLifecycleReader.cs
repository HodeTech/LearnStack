using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;

/// <summary>Four fresh lifecycle facts for the current trusted announced scope; no row or directory projection.</summary>
public sealed record IdentityScopeLifecycle(
    bool TenantExistsAndNotDeleted,
    bool TenantAllowsOrdinaryWork,
    bool OrganizationBelongsToTenant,
    bool OrganizationAllowsOrdinaryWork);

/// <summary>One scope group whose lifecycle the platform scope asks about: a tenant and one organization, or none.</summary>
/// <param name="TenantId">A real tenant, never the platform sentinel.</param>
/// <param name="OrganizationId">The organization, or <see langword="null"/> for the tenant-wide group.</param>
public sealed record IdentityLifecycleScope(TenantId TenantId, OrganizationId? OrganizationId);

public interface IIdentityScopeLifecycleReader
{
    /// <summary>The most groups one platform-scope read answers; the closure's participant read is bounded the same.</summary>
    public const int MaximumPlatformScopes = 1024;

    Task<IdentityScopeLifecycle> ReadAsync(TenantId tenantId, OrganizationId? organizationId,
        CancellationToken cancellationToken = default);

    /// <summary>Reads on the authorization source's actual isolated child; it never uses the ambient business owner.</summary>
    Task<IdentityScopeLifecycle> ReadAuthorizationAsync(IUnitOfWork child, AuthorizationCandidate candidate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The same four facts for each of <paramref name="scopes"/>, in their order, read in one statement on an entered
    /// platform scope's own transaction (ADR-0107 § 7: a closure reads each participant tenant's lifecycle through this
    /// port, so no cross-module join exists). It announces nothing and needs no announcement: the platform role reads the
    /// Tenancy tables by its own grant. A tenant-wide scope's organization facts are false, as <see cref="ReadAsync"/>
    /// answers them.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The list is empty or longer than <see cref="MaximumPlatformScopes"/>, or a scope names no real tenant or an
    /// unassigned organization.
    /// </exception>
    Task<IReadOnlyList<IdentityScopeLifecycle>> ReadOnPlatformScopeAsync(IPlatformAdminScopeHandle handle,
        IReadOnlyList<IdentityLifecycleScope> scopes, CancellationToken cancellationToken = default);
}

/// <summary>A payload-free authorization read failure; failed actual disposal must retain its source owner.</summary>
public sealed class IdentityScopeLifecycleReadException(bool cleanupFailed)
    : InfrastructureException("Identity scope lifecycle is unavailable.")
{
    public bool CleanupFailed { get; } = cleanupFailed;
}
