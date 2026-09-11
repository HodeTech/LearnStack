using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Bars the reserved platform sentinel id from <c>tenants</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>00000000-0000-7000-8000-000000000002</c> is <c>TenantId.PlatformSentinel</c>
    /// (<see href="../../../../../../../docs/decisions/0044-audit-write-path.md">ADR-0044
    /// § 1</see>) — the tenant a platform-scope <c>audit_log</c> row carries when there is
    /// no resolvable tenant to carry. Its whole value is that it names no tenant: nothing
    /// in <c>tenants</c>, so no host maps to it through
    /// <c>fk_platform_host_to_tenant_tenant</c>, no request context resolves it, and no
    /// tenant-keyed policy matches the rows it owns.
    /// </para>
    /// <para>
    /// <b>Why the constraint lives in the Tenancy chain and not the Audit one.</b> The
    /// invariant is about <c>tenants</c>, which this module owns. An Audit-chain
    /// migration reaching across to constrain another module's table would put the
    /// constraint's <c>Down</c> in a chain that can be rolled back independently — and
    /// would drop a Tenancy invariant as a side effect of reverting Audit.
    /// </para>
    /// <para>
    /// <b>It is the backstop, not the control.</b> A constraint on <c>tenants</c> cannot
    /// stop the sentinel from being <i>announced</i> on <c>app.tenant_id</c>, so the
    /// guards sit where the value enters: <c>TenantOwnership.EnsureRealTenant</c> — which
    /// <c>Tenant.Create</c> calls — refuses it in every aggregate factory,
    /// <c>NpgsqlUnitOfWork.SetProvisioningTenantContextAsync</c> refuses it exactly as it
    /// already refuses <c>Guid.Empty</c>, <c>SetTenantContextAsync</c> refuses it at the
    /// one announcement choke point, and <c>EventTenantContext.FromEnvelope</c> refuses an
    /// envelope naming it. This row is the layer that still holds when all four are
    /// bypassed by hand-written SQL.
    /// </para>
    /// <para>
    /// <b>Raw SQL rather than a model-tracked check constraint.</b> <c>tenants</c> already
    /// carries <c>ck_tenants_status</c>, declared this way by the chain's first migration;
    /// adding one constraint to the model while its neighbour on the same table stays out
    /// of it is the arrangement that misleads, because the snapshot would then read as a
    /// complete list of that table's constraints and would not be one. Nothing drifts
    /// either way here: the operand is a fixed constant, not a value list that has to
    /// track a C# enum.
    /// </para>
    /// <para>
    /// <b>Reversible, and it rewrites no rows.</b> <c>ADD CONSTRAINT … CHECK</c> validates
    /// the existing rows and takes an <c>ACCESS EXCLUSIVE</c> lock for the scan; the table
    /// holds one row per tenant, so the scan is bounded by tenant count. No shipped or
    /// seeded row can violate it — the id is refused by every factory that mints one.
    /// </para>
    /// </remarks>
    public partial class tenants_reject_platform_sentinel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE tenants ADD CONSTRAINT ck_tenants_not_platform_sentinel
                    CHECK (id <> '00000000-0000-7000-8000-000000000002');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE tenants DROP CONSTRAINT ck_tenants_not_platform_sentinel;
                """);
        }
    }
}
