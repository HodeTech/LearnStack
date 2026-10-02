using System.Collections.Immutable;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;

namespace LearnStack.Modules.Customization.Application.Contracts.Definitions;

public readonly record struct DefinitionRevision(string Key, int SchemaVersion);

/// <summary>Display context, not authority to read another tenant or content locale.</summary>
public sealed record DefinitionProjectionRequest(
    ImmutableArray<DefinitionRevision> ContentTypes, ImmutableArray<DefinitionRevision> Taxonomies,
    string RequestedLocale, string TenantDefaultLocale);

public sealed record TextCardDisplayField(string Name, ResolvedLocalizedText Label);

public sealed record ContentTypeDisplayDefinition(
    Guid Id, DefinitionRevision Revision, DefinitionStatus Status, ResolvedLocalizedText DisplayName,
    string RendererKey, ImmutableArray<TextCardDisplayField> Fields);

public sealed record TaxonomyDisplayBand(string Key, ResolvedLocalizedText DisplayName, short Sort, string? Metadata);

public sealed record TaxonomyDisplayDefinition(
    Guid Id, DefinitionRevision Revision, DefinitionStatus Status, ResolvedLocalizedText DisplayName,
    ImmutableArray<TaxonomyDisplayBand> Bands);

public sealed record DefinitionProjection(
    long? Generation,
    ImmutableDictionary<DefinitionRevision, ContentTypeDisplayDefinition> ContentTypes,
    ImmutableDictionary<DefinitionRevision, TaxonomyDisplayDefinition> Taxonomies,
    ImmutableHashSet<DefinitionRevision> MissingContentTypes,
    ImmutableHashSet<DefinitionRevision> MissingTaxonomies);

/// <summary>
/// Internal batched exact-pin display reads on the announced ambient transaction.
/// Missing members never substitute another revision. No HTTP surface or write eligibility.
/// </summary>
public interface ICustomizationDefinitionProjectionReader
{
    Task<Result<DefinitionProjection>> ReadAsync(
        DefinitionProjectionRequest request, CancellationToken cancellationToken = default);
}
