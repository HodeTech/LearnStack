using System.Collections.Immutable;
using System.Text.Json;
using LearnStack.Modules.Customization.Application.Contracts.Definitions;
using LearnStack.Modules.Customization.Application.Customization;
using LearnStack.Modules.Customization.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LearnStack.Modules.Customization.Infrastructure.Projections;

/// <summary>Generation and both families share one READ COMMITTED statement snapshot.</summary>
public sealed class DefinitionSnapshotStore(CustomizationDbContext db)
{
    // Explicit tenant predicates are required for this unmapped, read-only projection;
    // execution still passes the ambient EF command guard and PostgreSQL RLS.
    private const string SnapshotSql = """
        SELECT
          (SELECT generation FROM customization_generations WHERE tenant_id = @tenant_id) AS "Generation",
          (EXISTS(SELECT 1 FROM tenant_content_types WHERE tenant_id = @tenant_id)
           OR EXISTS(SELECT 1 FROM tenant_level_taxonomies WHERE tenant_id = @tenant_id)) AS "HasDefinitionRows",
          COALESCE((SELECT jsonb_agg(jsonb_build_object(
            'id', c.id, 'key', c.key, 'version', c.schema_version, 'status', c.status,
            'label', c.display_name, 'schema', c.json_schema, 'renderer', c.renderer_key)
            ORDER BY c.key, c.schema_version)
            FROM tenant_content_types c
            WHERE c.tenant_id = @tenant_id AND c.deleted_at IS NULL AND c.status IN ('Active', 'Deprecated')),
            '[]'::jsonb)::text AS "ContentTypes",
          COALESCE((SELECT jsonb_agg(jsonb_build_object(
            'id', t.id, 'key', t.key, 'version', t.schema_version, 'status', t.status, 'label', t.display_name,
            'bands', COALESCE((SELECT jsonb_agg(jsonb_build_object(
              'key', i.key, 'label', i.display_name, 'sort', i.sort, 'metadata', i.metadata) ORDER BY i.sort)
              FROM tenant_level_taxonomy_items i
              WHERE i.tenant_id = @tenant_id AND i.tenant_id = t.tenant_id
                AND i.taxonomy_key = t.key AND i.schema_version = t.schema_version), '[]'::jsonb))
            ORDER BY t.key, t.schema_version)
            FROM tenant_level_taxonomies t
            WHERE t.tenant_id = @tenant_id AND t.deleted_at IS NULL AND t.status IN ('Active', 'Deprecated')),
            '[]'::jsonb)::text AS "Taxonomies"
        """;

    internal Task<long?> ProbeAsync(TenantId tenant, CancellationToken cancellationToken) =>
        db.CustomizationGenerations.AsNoTracking().TagWith("P02d-3 generation probe")
            .Where(row => row.TenantId == tenant).Select(row => (long?)row.Generation)
            .SingleOrDefaultAsync(cancellationToken);

    internal async Task<DefinitionSnapshot> LoadAsync(TenantId tenant, CancellationToken cancellationToken)
    {
        var row = await db.Database.SqlQueryRaw<SnapshotRow>(SnapshotSql,
                new NpgsqlParameter("tenant_id", tenant.Value))
            .TagWith("P02d-3 definition snapshot").SingleAsync(cancellationToken);
        return new DefinitionSnapshot(row.Generation, row.HasDefinitionRows,
            ReadContentTypes(row.ContentTypes), ReadTaxonomies(row.Taxonomies));
    }

    internal bool IsEnlistedOn(System.Data.Common.DbTransaction? transaction) =>
        db.Database.CurrentTransaction is { } current
        && ReferenceEquals(Microsoft.EntityFrameworkCore.Storage.DbContextTransactionExtensions.GetDbTransaction(current), transaction);

    private static ContentTypeFamily ReadContentTypes(string json)
    {
        using var document = JsonDocument.Parse(json);
        var definitions = ImmutableDictionary.CreateBuilder<DefinitionRevision, UntranslatedContentType>();
        foreach (var row in document.RootElement.EnumerateArray())
        {
            try
            {
                var renderer = row.GetProperty("renderer").GetString()!;
                var presentation = TextCardPresentation.Resolve(row.GetProperty("schema").GetRawText(), renderer);
                if (presentation.IsFailure)
                {
                    continue;
                }

                var revision = Revision(row);
                definitions.Add(revision, new UntranslatedContentType(row.GetProperty("id").GetGuid(),
                    Status(row), Label(row), renderer, presentation.Value));
            }
            catch (ArgumentException)
            {
                // An invalid stored label makes this pin missing, not its neighbors.
            }
        }

        return new ContentTypeFamily(definitions.ToImmutable());
    }

    private static TaxonomyFamily ReadTaxonomies(string json)
    {
        using var document = JsonDocument.Parse(json);
        var definitions = ImmutableDictionary.CreateBuilder<DefinitionRevision, UntranslatedTaxonomy>();
        foreach (var row in document.RootElement.EnumerateArray())
        {
            try
            {
                var revision = Revision(row);
                var bands = row.GetProperty("bands").EnumerateArray().Select(band => new TaxonomyBandDto(
                    band.GetProperty("key").GetString()!, Label(band), band.GetProperty("sort").GetInt16(),
                    band.GetProperty("metadata").ValueKind == JsonValueKind.Null
                        ? null : band.GetProperty("metadata").GetRawText())).ToImmutableArray();
                definitions.Add(revision, new UntranslatedTaxonomy(row.GetProperty("id").GetGuid(),
                    Status(row), Label(row), bands));
            }
            catch (ArgumentException)
            {
                // Omit this taxonomy revision if its own or a band's label is
                // malformed; unrelated revisions remain available.
            }
        }

        return new TaxonomyFamily(definitions.ToImmutable());
    }

    private static DefinitionRevision Revision(JsonElement row) =>
        new(row.GetProperty("key").GetString()!, row.GetProperty("version").GetInt32());
    private static LocalizedText Label(JsonElement row) => LocalizedText.FromJson(row.GetProperty("label").GetRawText());
    private static DefinitionStatus Status(JsonElement row) => row.GetProperty("status").GetString() == "Active"
        ? DefinitionStatus.Active : DefinitionStatus.Deprecated;

    private sealed class SnapshotRow
    {
        public long? Generation { get; set; }
        public bool HasDefinitionRows { get; set; }
        public string ContentTypes { get; set; } = "";
        public string Taxonomies { get; set; } = "";
    }
}
