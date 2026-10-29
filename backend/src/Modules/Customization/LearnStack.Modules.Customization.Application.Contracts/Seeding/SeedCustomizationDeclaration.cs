using System.Collections.Immutable;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Modules.Customization.Application.Contracts.Seeding;

/// <summary>Independent fixed-v1 declarations, never values copied from a request.</summary>
public sealed record SeedCustomizationDeclaration(TenantId TenantId,
    SeedContentTypeDeclaration BuiltInContentType, SeedTaxonomyDeclaration BuiltInTaxonomy,
    SeedContentTypeDeclaration ContentType, SeedTaxonomyDeclaration Taxonomy);

public sealed record SeedContentTypeDeclaration(Guid? Id, string Key, int SchemaVersion,
    ImmutableDictionary<string, string> DisplayName, string JsonSchema, string RendererKey);

public sealed record SeedTaxonomyItemDeclaration(string Key, ImmutableDictionary<string, string> DisplayName,
    short Sort, string? Metadata);

public sealed record SeedTaxonomyDeclaration(Guid? Id, string Key, int SchemaVersion,
    ImmutableDictionary<string, string> DisplayName, ImmutableArray<SeedTaxonomyItemDeclaration> Items);

public interface ISeedCustomizationContinuationSource
{
    SeedCustomizationDeclaration? ForTenant(TenantId tenantId);
}

public sealed class DenyAllSeedCustomizationContinuationSource : ISeedCustomizationContinuationSource
{
    public SeedCustomizationDeclaration? ForTenant(TenantId tenantId) => null;
}

/// <summary>Fixed immutable customization prerequisites on the caller's already fenced ordinary owner.</summary>
public interface ISeedCustomizationPrerequisiteReader
{
    Task<bool> IsCompleteAsync(CancellationToken cancellationToken = default);
}
