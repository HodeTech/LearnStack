using Microsoft.EntityFrameworkCore;

namespace LearnStack.Infrastructure.Persistence;

/// <summary>
/// The platform's own tables — the ones no module owns.
/// </summary>
/// <remarks>
/// <para>
/// <c>outbox_messages</c> and <c>idempotency_keys</c> are written through
/// SharedKernel ports (<c>IOutbox</c>, <c>IIdempotencyStore</c>) by any module's
/// handler and read by infrastructure that belongs to none of them. Putting them
/// in a module's context would make every other module's use of the outbox a
/// dependency on that module — the shape
/// <see href="../../../../docs/architecture/15-event-and-outbox.md">15-event-and-outbox.md</see>
/// rules out when it says LearnStack uses a single shared table, not one per
/// module.
/// </para>
/// <para>
/// <b>It is the second <c>DbContext</c>, and that is useful rather than
/// incidental.</b>
/// <see href="../../../../docs/decisions/0040-ambient-unit-of-work.md">ADR-0040</see>
/// says its central property — several contexts enlisted on one connection, so
/// <c>SET LOCAL</c> protects every statement — becomes testable only when a
/// second context exists, and expected that in Phase 03. It exists here, so the
/// property is testable a phase earlier.
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
        // needs a model root — and to be the second context ADR-0040's property
        // requires.
        base.OnModelCreating(modelBuilder);
    }
}
