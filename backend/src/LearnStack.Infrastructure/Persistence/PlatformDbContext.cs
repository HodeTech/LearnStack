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
/// fires; <c>IOutbox</c> does not exist yet and lands with the dispatcher in
/// <see href="../../../../docs/roadmap/phase-02b-events-auth.md">Phase 02b</see>.
/// Nothing writes either table at runtime — Packet 6 shipped the tables. Putting
/// them in a module's context would make every other module's use of the outbox a
/// dependency on that module — the shape
/// <see href="../../../../docs/architecture/15-event-and-outbox.md">15-event-and-outbox.md</see>
/// rules out when it says LearnStack uses a single shared table, not one per
/// module.
/// </para>
/// <para>
/// It is the second <c>DbContext</c>, and that does <b>not</b> yet make
/// <see href="../../../../docs/decisions/0040-ambient-unit-of-work.md">ADR-0040</see>'s
/// central property testable. That property is several contexts enlisted on one
/// connection so <c>SET LOCAL</c> protects every statement, and the enlistment
/// machinery — <c>IUnitOfWork</c>, the shared registration helper, the
/// <c>TransactionBehavior</c> body — lands in step 6; ADR-0040 § What Packet 6
/// can and cannot prove says the property becomes observable in Phase 03, with
/// the second <b>module</b> context. This context exists for the reason below and
/// no other.
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
