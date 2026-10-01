using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Modules.Tenancy.Application.Abstractions;

/// <summary>Filtered, uncached verification on the caller's announced transaction.</summary>
public interface ISeedStateReader
{
    Task<TenantSeedDto?> ReadTenantAsync(CancellationToken cancellationToken);
    Task<OrganizationSeedDto?> ReadOrganizationAsync(OrganizationId organizationId, CancellationToken cancellationToken);
    Task<HostMappingSeedDto?> ReadHostMappingAsync(string host, CancellationToken cancellationToken);
    Task<SettingSeedDto?> ReadSettingAsync(Guid settingId, CancellationToken cancellationToken);
}
