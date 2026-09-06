using LearnStack.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LearnStack.Infrastructure.Persistence;

/// <summary>
/// What every module's write stores do around a save.
/// </summary>
/// <remarks>
/// Here rather than once per module for the reason
/// <see cref="AuditColumnMapping"/> and <see cref="SnakeCaseNaming"/> are here:
/// both members below encode a measured lesson, and a module that reimplements
/// them reimplements the bug they close. A module keeps only what is genuinely
/// its own — Tenancy's two-pass default-locale save names <c>TenantLocale</c> and
/// stays there.
/// </remarks>
public static class WriteStoreTracking
{
    /// <summary>
    /// Saves, turning a uniqueness violation into the port's own conflict type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The translation happens in Infrastructure because this is the only layer
    /// allowed to name <c>PostgresException</c>: the repository forbids importing a
    /// provider SDK exception type outside an adapter's namespace, and
    /// <c>Application</c> cannot reference this assembly regardless. Untranslated, a
    /// reused key reaches the L1 handler as a <c>DbUpdateException</c>, which
    /// <c>HttpStatusMap</c> has no arm for — a 500 for something the caller can fix
    /// by choosing another key.
    /// </para>
    /// <para>
    /// 23505 only. Every other SQLSTATE is a fault and stays one; a 42501 in
    /// particular means a policy refused the write, which is never something to
    /// soften.
    /// </para>
    /// </remarks>
    public static async Task SaveTranslatingConflictsAsync(
        DbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

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

    /// <summary>
    /// Refuses an aggregate this scope's context does not track.
    /// </summary>
    /// <remarks>
    /// There is no correct silent handling of a detached aggregate: attaching the
    /// graph re-writes every child from a stale in-memory copy, and marking only the
    /// root takes the concurrency token's original value from its current one, so
    /// the next save matches nothing.
    /// </remarks>
    public static void EnsureTracked<T>(DbContext db, T aggregate)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(db);
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
