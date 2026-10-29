using System.Text.Json;
using System.Text.Json.Nodes;
using LearnStack.Modules.Customization.Application.Contracts.Seeding;
using LearnStack.Modules.Customization.Domain;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace LearnStack.Modules.Customization.Infrastructure.Persistence;

/// <summary>Checks only the fixed completed definitions on the prepared adoption owner's narrow scope.</summary>
public sealed class FixedSeedCustomizationCompletionReader(CustomizationDbContext db, IUnitOfWork unit)
    : ISeedCustomizationCompletionReader
{
    public async Task<bool> VerifyAsync(SeedCompletionReadWitness witness, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(witness);
        RequireCurrent(witness);
        var declaration = witness.GetDeclaration<SeedCustomizationDeclaration>();
        if (declaration.TenantId != witness.TenantId || declaration.BuiltInContentType.Id is not null
            || declaration.BuiltInTaxonomy.Id is not null || declaration.ContentType.Id is not { } contentId
            || contentId == Guid.Empty || declaration.Taxonomy.Id is not { } taxonomyId || taxonomyId == Guid.Empty
            || declaration.BuiltInContentType.Key == declaration.ContentType.Key
            || declaration.BuiltInTaxonomy.Key == declaration.Taxonomy.Key) return false;

        var contentRoot = TenantContentTypeId.From(contentId);
        var taxonomyRoot = TenantLevelTaxonomyId.From(taxonomyId);
        var previousTimeout = db.Database.GetCommandTimeout();
        db.Database.SetCommandTimeout(10);
        try
        {
            var content = await db.TenantContentTypes.AsNoTracking().Where(row =>
                    row.Key == declaration.BuiltInContentType.Key && row.SchemaVersion == declaration.BuiltInContentType.SchemaVersion
                    || row.Key == declaration.ContentType.Key && row.SchemaVersion == declaration.ContentType.SchemaVersion
                    || row.Id == contentRoot)
                .ToListAsync(cancellationToken);
            RequireCurrent(witness);
            var taxonomies = await db.TenantLevelTaxonomies.AsNoTracking().Where(row =>
                    row.Key == declaration.BuiltInTaxonomy.Key && row.SchemaVersion == declaration.BuiltInTaxonomy.SchemaVersion
                    || row.Key == declaration.Taxonomy.Key && row.SchemaVersion == declaration.Taxonomy.SchemaVersion
                    || row.Id == taxonomyRoot)
                .Include(row => row.Items).ToListAsync(cancellationToken);
            RequireCurrent(witness);
            return ContentMatches(content, declaration.BuiltInContentType, declaration.TenantId)
                && ContentMatches(content, declaration.ContentType, declaration.TenantId)
                && TaxonomyMatches(taxonomies, declaration.BuiltInTaxonomy, declaration.TenantId)
                && TaxonomyMatches(taxonomies, declaration.Taxonomy, declaration.TenantId);
        }
        finally { db.Database.SetCommandTimeout(previousTimeout); }
    }

    private void RequireCurrent(SeedCompletionReadWitness witness)
    {
        witness.RequireCurrent();
        if (!unit.HasActiveTransaction || unit.IsRollbackOnly || unit.Mode != TransactionMode.ReadWrite
            || unit is IOperationReadScopeView { IsTenantWideRead: true }
            || unit is IGlobalIdentityUnitOfWork { IsGlobalIdentityTransaction: true }
            || unit.Transaction is not { } transaction
            || unit.GetTenantContextAnnouncement(transaction) is not { Generation: > 0 } announcement
            || announcement.TenantId != witness.TenantId || announcement.OrganizationId is not null
            || witness.OrganizationId is not null || !witness.IsExpectedOn(unit, transaction, announcement.Generation)
            || db.CurrentTenantId != witness.TenantId || db.CurrentOrganizationId is not null || db.CurrentTenantWideRead
            || !ReferenceEquals(db.Database.GetDbConnection(), unit.Connection)
            || !ReferenceEquals(db.Database.CurrentTransaction?.GetDbTransaction(), transaction)
            || db.ChangeTracker.Entries().Any(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Fixed customization completion requires its prepared narrow owner.");
    }

    private static bool ContentMatches(IReadOnlyList<TenantContentType> rows, SeedContentTypeDeclaration expected, TenantId tenant)
    {
        var found = rows.Where(row => row.Key == expected.Key && row.SchemaVersion == expected.SchemaVersion
            || expected.Id is { } id && row.Id.Value == id).ToArray();
        return found.Length == 1 && found[0] is { } row && (expected.Id is null || row.Id.Value == expected.Id)
            && row.TenantId == tenant && row.Key == expected.Key && row.SchemaVersion == expected.SchemaVersion
            && row.SchemaRevision == 0 && row.Status == CustomizationStatus.Active && Pristine(row, 1)
            && Json(row.DisplayName.ToJson(), JsonSerializer.Serialize(expected.DisplayName))
            && Json(row.JsonSchema, expected.JsonSchema) && row.RendererKey == expected.RendererKey;
    }

    private static bool TaxonomyMatches(IReadOnlyList<TenantLevelTaxonomy> rows, SeedTaxonomyDeclaration expected, TenantId tenant)
    {
        var found = rows.Where(row => row.Key == expected.Key && row.SchemaVersion == expected.SchemaVersion
            || expected.Id is { } id && row.Id.Value == id).ToArray();
        return found.Length == 1 && found[0] is { } row && (expected.Id is null || row.Id.Value == expected.Id)
            && row.TenantId == tenant && row.Key == expected.Key && row.SchemaVersion == expected.SchemaVersion
            && row.SchemaRevision == expected.Items.Length && row.Status == CustomizationStatus.Active
            && Pristine(row, expected.Items.Length + 1)
            && Json(row.DisplayName.ToJson(), JsonSerializer.Serialize(expected.DisplayName))
            && row.Items.Count == expected.Items.Length
            && row.Items.All(item => item.TenantId == tenant && item.TaxonomyKey == expected.Key
                && item.SchemaVersion == expected.SchemaVersion && expected.Items.Count(pin => pin.Key == item.Key
                    && pin.Sort == item.Sort && Json(item.DisplayName.ToJson(), JsonSerializer.Serialize(pin.DisplayName))
                    && Json(item.Metadata, pin.Metadata)) == 1);
    }

    private static bool Pristine<TId>(AuditableEntity<TId> row, long version)
        where TId : struct, IStronglyTypedId<Guid>, IEquatable<TId> => row.Version == version
        && row.CreatedAt != default && row.CreatedBy == UserId.SystemActor && row.DeletedAt is null && row.DeletedBy is null
        && row.UpdatedAt is { } updated && updated != default && row.UpdatedBy == UserId.SystemActor;

    private static bool Json(string? left, string? right)
    {
        if (left is null || right is null) return left == right;
        try { return JsonNode.DeepEquals(JsonNode.Parse(left), JsonNode.Parse(right)); }
        catch (JsonException) { return false; }
    }
}
