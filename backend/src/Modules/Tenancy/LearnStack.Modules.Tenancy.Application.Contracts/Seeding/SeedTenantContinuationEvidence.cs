using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Modules.Tenancy.Application.Contracts.Seeding;

/// <summary>The retained v1 Tenant prefix; an edited or removed fact is never an unfinished addition.</summary>
public static class SeedTenantContinuationEvidence
{
    public static bool IsPristinePrefix(TenantSeedDto row, SeedTenantDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(declaration);
        var count = row.Locales.Length;
        return row.Id == declaration.TenantId && row.Slug == declaration.Slug && row.DisplayName == declaration.DisplayName
            && row.Status == "Trial" && row.DefaultOrganizationId == declaration.DefaultOrganization.Id
            && row.CreatedAt != default && row.CreatedBy == UserId.SystemActor
            && row.UpdatedAt is { } updated && updated != default && row.UpdatedBy == UserId.SystemActor
            && row.DeletedAt is null && row.DeletedBy is null && row.AdoptedSeedOriginVersion == 0
            && count <= declaration.OriginalLocales.Length && row.Version == 1L + count
            && row.Locales.Distinct().Count() == count
            && declaration.OriginalLocales.Take(count).All(row.Locales.Contains);
    }
}
