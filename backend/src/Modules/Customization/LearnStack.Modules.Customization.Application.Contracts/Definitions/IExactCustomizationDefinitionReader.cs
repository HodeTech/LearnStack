using System.Collections.Immutable;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;

namespace LearnStack.Modules.Customization.Application.Contracts.Definitions;

/// <summary>Eligibility is evaluated on the exact revision, never on the current live key.</summary>
public enum DefinitionReadPurpose
{
    NewBinding = 0,
    ExistingPin = 1,
}

public enum DefinitionStatus
{
    Active = 1,
    Deprecated = 2,
}

public sealed record TextCardFieldDto(string Name, LocalizedText Label, bool IsRequired = false);

public sealed record ContentTypeDefinitionDto(
    Guid Id, string Key, int SchemaVersion, DefinitionStatus Status,
    string JsonSchema, string RendererKey, ImmutableArray<TextCardFieldDto> Fields);

public sealed record TaxonomyBandDto(string Key, LocalizedText DisplayName, short Sort, string? Metadata);

public sealed record TaxonomyDefinitionDto(
    Guid Id, string Key, int SchemaVersion, DefinitionStatus Status, ImmutableArray<TaxonomyBandDto> Bands);

/// <summary>
/// Uncached, tenant-filtered reads on the caller's ambient transaction. A miss or
/// ineligible revision returns the same bounded validation refusal. Validity is
/// at read time; immutable pins survive a concurrent deprecation (P02d-2).
/// </summary>
public interface IExactCustomizationDefinitionReader
{
    Task<Result<ContentTypeDefinitionDto>> ReadContentTypeAsync(
        string key, int schemaVersion, DefinitionReadPurpose purpose, CancellationToken cancellationToken);

    Task<Result<TaxonomyDefinitionDto>> ReadTaxonomyAsync(
        string key, int schemaVersion, DefinitionReadPurpose purpose, CancellationToken cancellationToken);
}
