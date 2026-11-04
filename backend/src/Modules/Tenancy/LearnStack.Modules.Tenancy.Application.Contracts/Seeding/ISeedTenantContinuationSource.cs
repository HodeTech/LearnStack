using System.Collections.Immutable;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Modules.Tenancy.Application.Contracts.Seeding;

public enum SeedTenantContinuationOperation
{
    ProvisionTenant = 1, CreateOrganization = 2, MapHost = 3, AddLocale = 4, SetBranding = 5, IdentityFoundation = 6,
}

/// <summary>Finite seed target, never human authorization or an installation receipt.</summary>
public sealed record SeedTenantContinuationAct(SeedTenantContinuationOperation Operation,
    TenantId TenantId, Guid? TargetId = null, string? Locale = null, string? Host = null);

public sealed record SeedOrganizationDeclaration(OrganizationId Id, string Slug, string DisplayName);

/// <summary>Immutable original v1 pins; distinct witnessed purposes define pristine or retained completion predicates.</summary>
public sealed record SeedTenantDeclaration(TenantId TenantId, string Slug, string DisplayName,
    SeedOrganizationDeclaration DefaultOrganization, SeedOrganizationDeclaration SecondOrganization,
    ImmutableArray<TenantLocaleSeedDto> OriginalLocales, string PublicHost, OrganizationId? HostOrganizationId,
    Guid? ThemeId, string? ThemeValue);

public interface ISeedTenantContinuationSource
{
    SeedTenantDeclaration? GetDeclaration(SeedTenantContinuationAct act);
}

public sealed class DenyAllSeedTenantContinuationSource : ISeedTenantContinuationSource
{
    public SeedTenantDeclaration? GetDeclaration(SeedTenantContinuationAct act) => null;
}
