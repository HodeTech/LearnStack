using LearnStack.Modules.Customization.Application.Contracts.Seeding;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.Modules.Customization.Domain;

namespace LearnStack.Modules.Customization.Application.Abstractions;

/// <summary>Filtered, uncached verification on the caller's announced transaction.</summary>
public interface ISeedStateReader
{
    Task<ContentTypeSeedDto?> ReadContentTypeAsync(TenantContentTypeId contentTypeId, CancellationToken cancellationToken);
    Task<TaxonomySeedDto?> ReadTaxonomyAsync(TenantLevelTaxonomyId taxonomyId, CancellationToken cancellationToken);
}
