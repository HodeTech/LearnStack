using LearnStack.Modules.Customization.Application.Contracts.Seeding;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Modules.Customization.Application.Abstractions;

/// <summary>Filtered, uncached verification on the caller's announced transaction.</summary>
public interface ISeedStateReader
{
    Task<ContentTypeSeedDto?> ReadContentTypeAsync(Guid contentTypeId, CancellationToken cancellationToken);
    Task<TaxonomySeedDto?> ReadTaxonomyAsync(Guid taxonomyId, CancellationToken cancellationToken);
}
