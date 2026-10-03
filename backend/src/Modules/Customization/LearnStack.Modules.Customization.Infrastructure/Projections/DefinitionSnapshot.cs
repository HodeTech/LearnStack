using System.Collections.Immutable;
using LearnStack.Modules.Customization.Application.Contracts.Definitions;
using LearnStack.SharedKernel.Localization;

namespace LearnStack.Modules.Customization.Infrastructure.Projections;

internal sealed record ContentTypeFamily(ImmutableDictionary<DefinitionRevision, UntranslatedContentType> Definitions);
internal sealed record TaxonomyFamily(ImmutableDictionary<DefinitionRevision, UntranslatedTaxonomy> Definitions);
internal sealed record UntranslatedContentType(
    Guid Id, DefinitionStatus Status, LocalizedText DisplayName,
    string RendererKey, ImmutableArray<TextCardFieldDto> Fields);
internal sealed record UntranslatedTaxonomy(
    Guid Id, DefinitionStatus Status, LocalizedText DisplayName,
    ImmutableArray<TaxonomyBandDto> Bands);
internal sealed record DefinitionSnapshot(
    long? Generation, bool HasDefinitionRows, ContentTypeFamily ContentTypes, TaxonomyFamily Taxonomies);
