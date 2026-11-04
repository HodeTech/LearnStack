using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using LearnStack.Modules.Customization.Application.Contracts.Seeding;
using LearnStack.Modules.Customization.Domain;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Modules.Customization.Application.Seeding;

/// <summary>Pure retained-state evidence shared by fixed advancement and prerequisite reads.</summary>
internal static class SeedCustomizationEvidence
{
    internal static bool Content(IReadOnlyList<TenantContentType> rows, SeedContentTypeDeclaration expected, TenantId tenant, bool active)
    {
        var found = rows.Where(row => row.Key == expected.Key || row.Id.Value == expected.Id).ToArray();
        return found.Length == 1 && found[0] is { } row && (expected.Id is null || row.Id.Value == expected.Id)
            && row.TenantId == tenant && row.Key == expected.Key && row.SchemaVersion == expected.SchemaVersion && row.SchemaRevision == 0
            && row.Status == (active ? CustomizationStatus.Active : CustomizationStatus.Draft)
            && Pristine(row, active ? 1 : 0) && Json(row.DisplayName.ToJson(), JsonSerializer.Serialize(expected.DisplayName))
            && Json(row.JsonSchema, expected.JsonSchema) && row.RendererKey == expected.RendererKey;
    }

    internal static bool Taxonomy(IReadOnlyList<TenantLevelTaxonomy> rows, SeedTaxonomyDeclaration expected, TenantId tenant, bool active)
    {
        var found = rows.Where(row => row.Key == expected.Key || row.Id.Value == expected.Id).ToArray();
        return found.Length == 1 && found[0] is { } row && (expected.Id is null || row.Id.Value == expected.Id)
            && row.TenantId == tenant && row.Key == expected.Key && row.SchemaVersion == expected.SchemaVersion
            && row.SchemaRevision == expected.Items.Length && row.Status == (active ? CustomizationStatus.Active : CustomizationStatus.Draft)
            && Pristine(row, expected.Items.Length + (active ? 1 : 0))
            && Json(row.DisplayName.ToJson(), JsonSerializer.Serialize(expected.DisplayName)) && row.Items.Count == expected.Items.Length
            && row.Items.All(item => expected.Items.Any(expectedItem => item.Key == expectedItem.Key && item.Sort == expectedItem.Sort
                && Json(item.DisplayName.ToJson(), JsonSerializer.Serialize(expectedItem.DisplayName)) && Json(item.Metadata, expectedItem.Metadata)));
    }

    private static bool Pristine<TId>(AuditableEntity<TId> row, long version)
        where TId : struct, IStronglyTypedId<Guid>, IEquatable<TId> => row.Version == version
        && row.CreatedAt != default && row.CreatedBy == UserId.SystemActor && row.DeletedAt is null && row.DeletedBy is null
        && (version == 0 ? row.UpdatedAt is null && row.UpdatedBy is null : row.UpdatedAt is { } at && at != default && row.UpdatedBy == UserId.SystemActor);

    internal static bool Map(IReadOnlyDictionary<string, string> left, ImmutableDictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && pair.Value == value);

    private static bool Json(string? left, string? right)
    {
        if (left is null || right is null) return left == right;
        try { return JsonNode.DeepEquals(JsonNode.Parse(left), JsonNode.Parse(right)); }
        catch (JsonException) { return false; }
    }
}
