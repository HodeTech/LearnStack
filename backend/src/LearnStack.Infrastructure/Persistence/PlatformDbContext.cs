using Microsoft.EntityFrameworkCore;

namespace LearnStack.Infrastructure.Persistence;

/// <summary>
/// The platform's own tables — the ones no module owns.
/// </summary>
/// <remarks>
/// <para>
/// <c>outbox_messages</c> and <c>idempotency_keys</c> belong to no module: they
/// are reached through SharedKernel ports by any module's handler and read by
/// infrastructure that belongs to none of them. <c>IIdempotencyStore</c> ships
/// today, behind <c>InMemoryIdempotencyStore</c> until its ADR-0035 trigger
/// fires; <c>IOutbox</c> enqueues immediately on the ambient transaction under
/// <see href="../../../../docs/decisions/0057-transactional-event-production-and-completion.md">ADR-0057</see>.
/// Dispatcher leases and terminal state are decided by ADR-0046. Putting
/// them in a module's context would make every other module's use of the outbox a
/// dependency on that module — the shape
/// <see href="../../../../docs/architecture/15-event-and-outbox.md">15-event-and-outbox.md</see>
/// rules out when it says LearnStack uses a single shared table, not one per
/// module.
/// </para>
/// <para>
/// This migration-only context has no tracked entities. Runtime outbox SQL uses
/// the announced <c>IUnitOfWork</c> connection, while confined dispatch uses its
/// own role and short transactions. Module contexts enlist through the shared
/// <see href="../../../../docs/decisions/0040-ambient-unit-of-work.md">ADR-0040</see>
/// registration and never acquire a dispatcher connection.
/// </para>
/// <para>
/// Both tables are <b>tenant-owned, tenant-wide</b> despite living outside a
/// module: every row carries a <c>tenant_id</c>, and the ordinary policy applies.
/// The dispatcher reads across tenants through <c>learnstack_outbox_admin</c>'s
/// <c>BYPASSRLS</c>, bounded by a column-scoped grant rather than by the policy.
/// </para>
/// </remarks>
public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // No entity types. Both tables are written through their ports with
        // parameterised SQL, not through a change tracker: an outbox row is
        // enqueued and never updated by application code, and an idempotency
        // claim is a single INSERT ... ON CONFLICT that decides five outcomes in
        // one round trip (ADR-0037 Amendment 2). Mapping them as entities would
        // add a model nothing queries and invite exactly the row-by-row use both
        // designs avoid.
        //
        // The context exists to own the MIGRATION for these tables — that is what
        // needs a model root.
        base.OnModelCreating(modelBuilder);
    }
}
