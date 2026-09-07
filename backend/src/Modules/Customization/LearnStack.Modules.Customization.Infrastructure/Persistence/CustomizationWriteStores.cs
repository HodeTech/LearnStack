using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Customization.Application.Abstractions;
using LearnStack.Modules.Customization.Domain;
using Microsoft.EntityFrameworkCore;
using static LearnStack.Infrastructure.Persistence.WriteStoreTracking;

namespace LearnStack.Modules.Customization.Infrastructure.Persistence;

/// <summary>
/// The <c>TenantContentType</c> aggregate's reads and writes, against the module
/// context.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each method saves.</b> The two aggregates carry no EF relationship to each
/// other, so batching into one <c>SaveChanges</c> leaves the order EF sends them
/// unspecified — and publishing depends on it: the incumbent's deprecation has to
/// reach PostgreSQL before the successor's publication, or the partial index
/// refuses the pair. Saving per call is what makes the handler's statement order
/// the database's.
/// </para>
/// <para>
/// <b>Both reads exclude soft-deleted rows.</b> A retired definition is not an
/// incumbent to succeed and not a draft to publish; the partial index says the
/// first in the schema, and a lookup by id that ignored it would let a handler
/// publish something the tenant has already thrown away.
/// </para>
/// <para>
/// <b>The reads are tracked, deliberately.</b> A publish loads a revision in order
/// to change it, and <c>UpdateAsync</c> refuses anything this context does not
/// track — so <c>AsNoTracking</c> here would make every read useless to the only
/// caller there is. The read path Phase 02d needs is a projection keyed on the
/// generation counter, not these.
/// </para>
/// <para>
/// Both stores are <c>public</c> only so the composition root can name them in a
/// registration; nothing outside that line should. The port is the type callers
/// depend on.
/// </para>
/// </remarks>
public sealed class TenantContentTypeStore(CustomizationDbContext db) : ITenantContentTypeStore
{
    public Task AddAsync(
        TenantContentType aggregate, CancellationToken cancellationToken = default)
    {
        db.TenantContentTypes.Add(aggregate);
        return SaveTranslatingConflictsAsync(db, cancellationToken);
    }

    public Task UpdateAsync(
        TenantContentType aggregate, CancellationToken cancellationToken = default)
    {
        EnsureTracked(db, aggregate);
        return SaveTranslatingConflictsAsync(db, cancellationToken);
    }

    public Task<TenantContentType?> FindAsync(
        TenantContentTypeId id, CancellationToken cancellationToken = default) =>
        db.TenantContentTypes.SingleOrDefaultAsync(
            contentType => contentType.Id == id && contentType.DeletedAt == null,
            cancellationToken);

    public Task<TenantContentType?> FindActiveAsync(
        string key, CancellationToken cancellationToken = default) =>
        db.TenantContentTypes.SingleOrDefaultAsync(
            contentType => contentType.Key == key
                && contentType.Status == CustomizationStatus.Active
                && contentType.DeletedAt == null,
            cancellationToken);
}

/// <summary>The <c>TenantLevelTaxonomy</c> aggregate's reads and writes.</summary>
/// <remarks>
/// Same shape and the same reasons as <see cref="TenantContentTypeStore"/>. The
/// one difference is <c>Include</c>: a taxonomy's bands are inside the aggregate,
/// and a publish asks whether it has any — a question a lazy-loading-free context
/// answers with zero for every taxonomy unless the collection is loaded.
/// </remarks>
public sealed class TenantLevelTaxonomyStore(CustomizationDbContext db) : ITenantLevelTaxonomyStore
{
    public Task AddAsync(
        TenantLevelTaxonomy aggregate, CancellationToken cancellationToken = default)
    {
        db.TenantLevelTaxonomies.Add(aggregate);
        return SaveTranslatingConflictsAsync(db, cancellationToken);
    }

    public Task UpdateAsync(
        TenantLevelTaxonomy aggregate, CancellationToken cancellationToken = default)
    {
        EnsureTracked(db, aggregate);
        return SaveTranslatingConflictsAsync(db, cancellationToken);
    }

    public Task<TenantLevelTaxonomy?> FindAsync(
        TenantLevelTaxonomyId id, CancellationToken cancellationToken = default) =>
        WithItems().SingleOrDefaultAsync(
            taxonomy => taxonomy.Id == id && taxonomy.DeletedAt == null, cancellationToken);

    public Task<TenantLevelTaxonomy?> FindActiveAsync(
        string key, CancellationToken cancellationToken = default) =>
        WithItems().SingleOrDefaultAsync(
            taxonomy => taxonomy.Key == key
                && taxonomy.Status == CustomizationStatus.Active
                && taxonomy.DeletedAt == null,
            cancellationToken);

    private IQueryable<TenantLevelTaxonomy> WithItems() =>
        db.TenantLevelTaxonomies.Include(taxonomy => taxonomy.Items);
}

/// <summary>
/// The key lookup an <c>x-taxonomy</c> resolves through.
/// </summary>
/// <remarks>
/// <c>AnyAsync</c> rather than a <c>Find</c>: the question is whether the tenant
/// has ever declared the vocabulary, so loading a revision — and, for this
/// aggregate, its bands — would fetch a graph to throw away. It reads no items and
/// tracks nothing, which is also why it does not belong on the write store.
/// </remarks>
public sealed class TenantLevelTaxonomyCatalog(CustomizationDbContext db) : ITenantLevelTaxonomyCatalog
{
    public Task<bool> ContainsAsync(string key, CancellationToken cancellationToken = default) =>
        db.TenantLevelTaxonomies.AnyAsync(
            taxonomy => taxonomy.Key == key && taxonomy.DeletedAt == null, cancellationToken);
}
