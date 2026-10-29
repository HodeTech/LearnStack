using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;

namespace LearnStack.Tools.Seeder;

/// <summary>The closed v1 declarations; callers cannot submit a new tenant or locale vector.</summary>
public sealed class FixedSeedTenantContinuationSource : ISeedTenantContinuationSource
{
    public SeedTenantDeclaration? GetDeclaration(SeedTenantContinuationAct act)
    {
        ArgumentNullException.ThrowIfNull(act);
        var tenant = SeedData.All.SingleOrDefault(value => value.TenantId == act.TenantId);
        if (tenant is null) return null;
        var matches = act.Operation switch
        {
            SeedTenantContinuationOperation.ProvisionTenant => act.TargetId is null && act.Locale is null && act.Host is null,
            SeedTenantContinuationOperation.IdentityFoundation => act.TargetId is null && act.Locale is null && act.Host is null,
            SeedTenantContinuationOperation.CreateOrganization => act.TargetId == tenant.SecondOrganization.OrganizationId.Value
                && act.Locale is null && act.Host is null,
            SeedTenantContinuationOperation.MapHost => act.Host == tenant.Host && act.TargetId is null && act.Locale is null,
            SeedTenantContinuationOperation.AddLocale => act.TargetId is null && act.Host is null
                && tenant.Curriculum is { } curriculum && curriculum.Locales.Any(locale => locale.Locale == act.Locale),
            SeedTenantContinuationOperation.SetBranding => act.TargetId == tenant.Curriculum?.Theme.Id
                && act.TargetId is not null && act.Locale is null && act.Host is null,
            _ => false,
        };
        return matches ? new(tenant.TenantId, tenant.Slug, tenant.DisplayName,
            new(tenant.DefaultOrganization.OrganizationId, tenant.DefaultOrganization.Slug, tenant.DefaultOrganization.DisplayName),
            new(tenant.SecondOrganization.OrganizationId, tenant.SecondOrganization.Slug, tenant.SecondOrganization.DisplayName),
            tenant.Curriculum is { } content
                ? [.. content.Locales.OrderByDescending(locale => locale.IsDefault).ThenBy(locale => locale.Sort)
                    .Select(locale => new TenantLocaleSeedDto(locale.Locale, locale.IsEnabled, locale.IsDefault, locale.Sort))]
                : [], tenant.Host, tenant.MapHostToDefaultOrganization ? tenant.DefaultOrganization.OrganizationId : null,
            tenant.Curriculum?.Theme.Id, tenant.Curriculum?.Theme.Value) : null;
    }
}
