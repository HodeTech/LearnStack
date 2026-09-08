using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Audit.Domain;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LearnStack.Modules.Audit.Infrastructure.Persistence;

/// <summary>
/// The Audit module's unit of persistence — one <c>DbContext</c> per module, per
/// <see href="../../../../../../docs/decisions/0002-initial-architecture.md">ADR-0002</see>.
/// </summary>
/// <remarks>
/// <para>
/// <b>It does not own a connection</b>, like every other module context:
/// <see href="../../../../../../docs/decisions/0040-ambient-unit-of-work.md">ADR-0040</see>
/// puts the connection on <c>IUnitOfWork</c>, one per scope. A context that opened its
/// own would never see the <c>SET LOCAL app.tenant_id</c> issued on the ambient one and
/// would read zero rows under the corrected policy — silently.
/// </para>
/// <para>
/// <b>It is not the write path.</b> Audit rows are written by <c>PostgresAuditStore</c>
/// as parameterised SQL on the ambient transaction, never through this context. What
/// this context is for is the model — the mapping that gives <c>audit_log</c> and
/// <c>audit_config</c> their query filters, their place in the isolation sweep and their
/// migration — and, from
/// <see href="../../../../../../docs/roadmap/phase-03-identity-admin.md">Phase 03</see>,
/// the read side of the audit admin API. That separation is deliberate: mapping
/// <c>AuditEntry</c> into every module's context would need SharedKernel to reference
/// this assembly, which is the circular reference
/// <see href="../../../../../../docs/decisions/0033-audit-durability-model.md">ADR-0033</see>
/// rejects.
/// </para>
/// <para>
/// <b>Two entities, two table classes.</b> <c>AuditEntry</c> carries
/// <c>organization_id</c> and is therefore tenant-owned <i>org-scoped</i>;
/// <c>AuditConfig</c> has no such column and is tenant-owned <i>tenant-wide</i>
/// (<see href="../../../../../../docs/decisions/0044-audit-write-path.md">ADR-0044
/// Amendment 1</see>). The base's sweep applies the shape each one's interfaces declare.
/// </para>
/// </remarks>
public sealed class AuditDbContext(
    DbContextOptions<AuditDbContext> options, ITenantContextAccessor accessor)
    : TenantScopedDbContext(options, accessor)
{
    /// <summary>
    /// The append-only log.
    /// </summary>
    /// <remarks>
    /// Exposed for the Phase 03 read API. Nothing writes through it: <c>learnstack_app</c>
    /// holds <c>SELECT, INSERT</c> and the insert is the store's SQL, so a
    /// <c>SaveChanges</c> that tried to add one would be a bug this <c>DbSet</c> cannot
    /// prevent and <c>AuditEntry_Is_AppendOnly</c> is what catches.
    /// </remarks>
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    /// <summary>Per-tenant classification overrides.</summary>
    public DbSet<AuditConfig> AuditConfigs => Set<AuditConfig>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AuditDbContext).Assembly);
        modelBuilder.ApplySnakeCaseNames();

        // LAST, and that ordering is load-bearing: the base sweeps
        // modelBuilder.Model.GetEntityTypes(), so anything a fluent configuration
        // introduces is only in the model once the line above has run.
        base.OnModelCreating(modelBuilder);
    }
}
