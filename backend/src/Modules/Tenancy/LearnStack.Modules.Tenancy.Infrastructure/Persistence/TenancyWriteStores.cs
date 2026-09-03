using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static LearnStack.Modules.Tenancy.Infrastructure.Persistence.WriteStoreTracking;

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence;

/// <summary>
/// The <c>Tenant</c> aggregate's writes, against the module context.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each method saves, and that is load-bearing rather than convenient.</b> The EF model
/// carries no relationship between <c>Tenant</c> and <c>Organization</c>, so batching both
/// into one <c>SaveChanges</c> leaves the order EF sends them unspecified — and
/// provisioning depends on it: the organization's composite foreign key names
/// <c>(tenant_id, id)</c>, so the tenant row has to land first. Saving per call is what
/// makes the handler's statement order the database's statement order.
/// </para>
/// <para>
/// <b><c>UpdateAsync</c> takes a tracked aggregate and nothing else.</b> Under
/// [ADR-0040](../../../../../../docs/decisions/0040-ambient-unit-of-work.md) a scope has
/// one connection, one transaction and one module context, so an aggregate is loaded and
/// saved on the same context by construction — and for a tracked aggregate change
/// tracking already holds the diff, so the save is the whole of the work.
/// </para>
/// <para>
/// <b>Which is why it does not call <c>DbSet.Update</c>.</b> That method traverses the
/// graph and marks what it reaches: on a DETACHED aggregate every child with a set key
/// becomes <c>Modified</c> and every child with an unset key becomes <c>Added</c>, so a
/// tenant carrying <c>Locales</c> and <c>FeatureFlags</c> re-<c>UPDATE</c>s every one of
/// them with whatever the in-memory copy holds — silently overwriting anything written
/// since it was loaded. Marking only the detached root instead is no better: <c>Version</c>
/// is the concurrency token, EF takes a detached entity's original values from its current
/// ones, and a caller that mutated the root first therefore issues
/// <c>WHERE row_version = &lt;the value it just incremented to&gt;</c>, matches nothing, and
/// gets <c>DbUpdateConcurrencyException</c> — measured. There is no correct silent
/// handling of a detached aggregate here, so it is refused.
/// </para>
/// </remarks>
/// <remarks>
/// Both stores are <c>public</c> only so the composition root can name them in a
/// registration; nothing outside that line should. The port is the type callers depend
/// on.
/// </remarks>
public sealed class TenantWriteStore(TenancyDbContext db) : ITenantWriteStore
{
    public Task AddAsync(Tenant aggregate, CancellationToken cancellationToken = default)
    {
        db.Tenants.Add(aggregate);
        return SaveTranslatingConflictsAsync(db, cancellationToken);
    }

    public Task UpdateAsync(Tenant aggregate, CancellationToken cancellationToken = default)
    {
        EnsureTracked(db, aggregate);
        return SaveTranslatingConflictsAsync(db, cancellationToken);
    }
}

/// <summary>The <c>Organization</c> aggregate's writes.</summary>
/// <remarks>
/// Same shape and the same three reasons as <see cref="TenantWriteStore"/>, which
/// carries them: save per call, no <c>DbSet.Update</c>, tracked aggregates only.
/// </remarks>
public sealed class OrganizationWriteStore(TenancyDbContext db) : IOrganizationWriteStore
{
    public Task AddAsync(Organization aggregate, CancellationToken cancellationToken = default)
    {
        db.Organizations.Add(aggregate);
        return SaveTranslatingConflictsAsync(db, cancellationToken);
    }

    public Task UpdateAsync(
        Organization aggregate, CancellationToken cancellationToken = default)
    {
        EnsureTracked(db, aggregate);
        return SaveTranslatingConflictsAsync(db, cancellationToken);
    }
}

/// <summary>The host-resolution index's writes.</summary>
/// <remarks>
/// Add-only for now: nothing in the corpus updates a mapping in place — activation and
/// the publicly-live flip arrive with the Hub-side custom-domain lifecycle in
/// [Phase 02c](../../../../../../docs/roadmap/phase-02c-hub-foundation.md), which owns
/// that transaction and the cache invalidation that goes with it.
/// </remarks>
public sealed class PlatformHostMappingStore(TenancyDbContext db) : IPlatformHostMappingStore
{
    public Task AddAsync(
        PlatformHostMapping mapping, CancellationToken cancellationToken = default)
    {
        db.PlatformHostMappings.Add(mapping);
        return SaveTranslatingConflictsAsync(db, cancellationToken);
    }
}

/// <summary>Shared by the stores; see <see cref="TenantWriteStore"/> for why.</summary>
internal static class WriteStoreTracking
{
    /// <summary>
    /// Saves, turning a uniqueness violation into the port's own conflict type.
    /// </summary>
    /// <remarks>
    /// The translation happens here because here is the only place allowed to name
    /// <c>PostgresException</c>: the repository forbids importing a provider SDK
    /// exception type outside an adapter's namespace, and `Application` cannot reference
    /// this assembly regardless. Untranslated, a reused slug reaches the L1 handler as a
    /// <c>DbUpdateException</c>, which <c>HttpStatusMap</c> has no arm for — a 500 for
    /// something the caller can fix by choosing another slug.
    ///
    /// 23505 only. Every other SQLSTATE is a fault and stays one; a 42501 in particular
    /// means a policy refused the write, which is never something to soften.
    /// </remarks>
    internal static async Task SaveTranslatingConflictsAsync(
        TenancyDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException failure)
            when (failure.InnerException is PostgresException { SqlState: "23505" } conflict)
        {
            // Detach what the database refused, before the exception leaves. EF keeps a
            // failed entry in the state it had — an Added row stays Added — so a caller
            // that turns this into Result.Fail and carries on writing has the rejected
            // INSERT still queued, and the NEXT SaveChanges on this context re-sends it.
            //
            // Reachable through nesting, which is the shape ADR-0040 permits: an outer
            // handler may absorb an inner failure and keep going on the same scope, and
            // the scope is one DbContext. The row is gone from the database either way —
            // the statement was refused — so the tracker holding it is a claim that
            // outlived its subject.
            //
            // Added only. A Modified entry's original values are what the database still
            // holds, so leaving it tracked is correct; detaching it would discard a change
            // the caller may legitimately retry.
            foreach (var entry in failure.Entries)
            {
                if (entry.State == EntityState.Added)
                {
                    entry.State = EntityState.Detached;
                }
            }

            throw new AggregateConflictException(
                conflict.MessageText, conflict.ConstraintName, failure);
        }
    }

    internal static void EnsureTracked<T>(TenancyDbContext db, T aggregate)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(aggregate);

        if (db.Entry(aggregate).State != EntityState.Detached)
        {
            return;
        }

        throw new InvalidOperationException(
            $"The {typeof(T).Name} passed to UpdateAsync is not tracked by this scope's "
            + "context. Load it through the same context that saves it — under the "
            + "ambient unit of work that is the ordinary case, and it is the only one "
            + "with correct concurrency-token semantics.");
    }
}
