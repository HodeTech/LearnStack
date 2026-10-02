using System.Collections.Immutable;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence;

public sealed class TenancySeedStateReader(TenancyDbContext context, ITenantContext tenantContext) : ISeedStateReader
{
    public async Task<TenantSeedDto?> ReadTenantAsync(CancellationToken cancellationToken)
    {
        var row = await context.Tenants.AsNoTracking().Include(tenant => tenant.Locales)
            .SingleOrDefaultAsync(tenant => tenant.Id == tenantContext.TenantId && tenant.DeletedAt == null,
                cancellationToken);
        return row is null ? null : new TenantSeedDto(row.Id, row.Slug, row.DisplayName, row.Status.ToString(),
            row.DefaultOrganizationId, row.Version, row.Locales.OrderBy(locale => locale.Sort)
                .ThenBy(locale => locale.Locale, StringComparer.Ordinal)
                .Select(locale => new TenantLocaleSeedDto(locale.Locale, locale.IsEnabled, locale.IsDefault, locale.Sort))
                .ToImmutableArray());
    }

    public async Task<OrganizationSeedDto?> ReadOrganizationAsync(
        OrganizationId organizationId, CancellationToken cancellationToken)
    {
        var row = await context.Organizations.AsNoTracking().SingleOrDefaultAsync(
            organization => organization.Id == organizationId && organization.DeletedAt == null, cancellationToken);
        return row is null ? null : new OrganizationSeedDto(row.Id, row.TenantId, row.Slug, row.DisplayName,
            row.Status.ToString());
    }

    public async Task<HostMappingSeedDto?> ReadHostMappingAsync(string host, CancellationToken cancellationToken)
    {
        var canonical = EffectiveHost.Normalize(host);
        var row = await context.PlatformHostMappings.AsNoTracking().SingleOrDefaultAsync(
            mapping => mapping.Host == canonical && mapping.TenantId == tenantContext.TenantId, cancellationToken);
        return row is null ? null : new HostMappingSeedDto(row.Host, row.TenantId, row.OrganizationId,
            row.IsActive, row.IsPubliclyLive);
    }

    public async Task<SettingSeedDto?> ReadSettingAsync(TenantSettingId settingId, CancellationToken cancellationToken)
    {
        var row = await context.TenantSettings.AsNoTracking().SingleOrDefaultAsync(
            setting => setting.Id == settingId && setting.DeletedAt == null, cancellationToken);
        return row is null ? null : new SettingSeedDto(row.Id.Value, row.TenantId, row.OrganizationId,
            row.Key, row.Value, row.Version);
    }
}
