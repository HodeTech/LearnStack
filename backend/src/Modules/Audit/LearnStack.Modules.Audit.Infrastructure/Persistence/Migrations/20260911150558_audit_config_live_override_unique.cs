using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Modules.Audit.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Makes <c>ux_audit_config_tenant_id_module_operation</c> partial on
    /// <c>deleted_at IS NULL</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One live override per tenant and operation, not one ever.</b> <c>AuditConfig</c>
    /// is soft-deletable and its <c>IsEnabled</c> has no setter, so changing an override is a
    /// soft delete followed by a fresh <c>Declare</c>. Counted by the index, the deleted row
    /// refused the new one with <c>23505</c> — permanently, since nothing frees a
    /// soft-deleted row's key. Every other soft-deletable table's natural key is partial the
    /// same way (<c>ux_tenant_domains_host</c>, <c>ux_organizations_tenant_id_slug</c>);
    /// this one was the exception, found by the fifth review of Packet 9. The read already
    /// filters <c>deleted_at IS NULL</c>, so it and the index now agree on which rows count.
    /// </para>
    /// <para>
    /// A new migration rather than an edit to the one that created the index, which a
    /// development database may already hold. Nothing writes <c>audit_config</c> until Phase
    /// 06, so no table has a soft-deleted row yet; the reversal restores the unfiltered index,
    /// and PostgreSQL refuses it, atomically, once a deleted and a live override share a key.
    /// </para>
    /// </remarks>
    public partial class audit_config_live_override_unique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_audit_config_tenant_id_module_operation",
                table: "audit_config");

            migrationBuilder.CreateIndex(
                name: "ux_audit_config_tenant_id_module_operation",
                table: "audit_config",
                columns: new[] { "tenant_id", "module", "operation" },
                unique: true,
                filter: "deleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_audit_config_tenant_id_module_operation",
                table: "audit_config");

            migrationBuilder.CreateIndex(
                name: "ux_audit_config_tenant_id_module_operation",
                table: "audit_config",
                columns: new[] { "tenant_id", "module", "operation" },
                unique: true);
        }
    }
}
