using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Modules.Tenancy.Application.Abstractions;

/// <summary>Bounded, detached Tenancy facts on the current announced ordinary owner.</summary>
public interface ITenancyAdministrationReader
{
    Task<TenantAdministrationFacts?> ReadTenantAsync(CancellationToken cancellationToken = default);
    Task<OrganizationAdministrationFacts?> ReadOrganizationAsync(OrganizationId id, CancellationToken cancellationToken = default);
    Task<bool> HasOtherActiveOrganizationAsync(OrganizationId id, CancellationToken cancellationToken = default);
    /// <summary>Only the current tenant's nondeleted, orgless branding.theme identity.</summary>
    Task<TenantBrandingFacts?> ReadBrandingAsync(CancellationToken cancellationToken = default);
    /// <summary>Tenant default/version metadata and bounded rows come from one joined SQL snapshot.</summary>
    Task<OrganizationDirectoryPage?> ListOrganizationsAsync(string? cursor, int limit, CancellationToken cancellationToken = default);
    /// <summary>The organization and its Tenant default reference come from one joined SQL snapshot.</summary>
    Task<OrganizationAdministrationDetail?> ReadOrganizationDetailAsync(OrganizationId id, CancellationToken cancellationToken = default);
}

public sealed record TenantAdministrationFacts(TenantId Id, TenantStatus Status, bool IsDeleted,
    OrganizationId? DefaultOrganizationId, long Version);

public sealed record OrganizationAdministrationFacts(OrganizationId Id, TenantId TenantId, OrganizationStatus Status,
    bool IsDeleted, long Version);

public sealed record TenantBrandingFacts(TenantSettingId Id, long Version);

/// <summary>One detached actual aggregate for exact read-audit designation, with joined default metadata.</summary>
public sealed record OrganizationAdministrationDetail(Organization Organization, bool IsDefault)
{
    public override string ToString() => nameof(OrganizationAdministrationDetail);
}

public enum TenancyAdministrationStorageFailure { Provider = 1, Conflict = 2, Invariant = 3, Plan = 4, InvalidCursor = 5, SlugTaken = 6 }

/// <summary>Fixed source-owned failure; provider text and inner exceptions never cross this boundary.</summary>
public sealed class TenancyAdministrationStorageException(TenancyAdministrationStorageFailure failure)
    : Exception("Tenancy administration storage refused the operation.")
{
    public TenancyAdministrationStorageFailure Failure { get; } = failure;
}
