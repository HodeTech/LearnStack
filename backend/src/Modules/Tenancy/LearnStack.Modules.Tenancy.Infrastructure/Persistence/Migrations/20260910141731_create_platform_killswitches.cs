using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The killswitch overlay: one platform-wide switch per key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Platform-scoped, and the class is a fit for the stated reason rather than by
    /// analogy.</b> The rows belong to no tenant, so there is nothing for a tenant
    /// predicate to isolate. Database Standards says a second platform-scoped table "is a
    /// decision, not a convenience"; <see href="../../../../../../../docs/decisions/0045-entitlement-and-feature-flag-socket.md">ADR-0045
    /// § 5</see> is that decision, and the DDL below is transcribed from
    /// <see href="../../../../../../../docs/standards/05-database.md">Database
    /// Standards</see> rather than re-derived — that document is the only sanctioned copy
    /// of the policy shapes, and the last time a template was duplicated it shipped broken
    /// into four files at once.
    /// </para>
    /// <para>
    /// <b>The alternative could not be written at all.</b> A killswitch held in
    /// <c>tenant_feature_flags</c> "for the sentinel platform tenant" fails
    /// <c>fk_tenant_feature_flags_tenant REFERENCES tenants (id)</c>, because the sentinel
    /// has no <c>tenants</c> row by CHECK — and a foreign key is a constraint, so no role
    /// and no <c>BYPASSRLS</c> attribute moves it.
    /// </para>
    /// <para>
    /// <b>The read is unconditional, and that is the point.</b> The switch is global by
    /// construction, so hiding it from the role that has to honour it would only fail
    /// open. <c>USING (true)</c> widens nothing — there is no tenant term to widen — and
    /// the GRANT bounds the role instead: <c>learnstack_app</c> holds <c>SELECT</c> and
    /// nothing else, so the read policy is the only policy it can exercise. The owner is
    /// denied by the same mechanism as on <c>platform_host_to_tenant</c>: the policy names
    /// <c>learnstack_app</c>, so under <c>FORCE</c> none applies to
    /// <c>learnstack_migration</c>.
    /// </para>
    /// <para>
    /// <b>This is the first table for which "no tenant context ⇒ zero rows" is
    /// deliberately false</b>, and the whole-schema sweep is taught the difference without
    /// a name-based inclusion list — a sweep over a hand-written list of names fails open,
    /// which is how a second permissive policy on <c>outbox_messages</c> once passed the
    /// whole suite.
    /// </para>
    /// <para>
    /// <b>No writer ships with it.</b> The <c>learnstack_platform</c> write grant is
    /// written ahead of its caller: every toggle runs inside
    /// <c>EnterPlatformAdminScope(reason)</c> whose registered gate is
    /// <c>DenyAllPlatformAdminGate</c>, so no runtime path puts a row here until
    /// <see href="../../../../../../../docs/roadmap/phase-03-identity-admin.md">Phase
    /// 03</see> ships the Platform-scope permission.
    /// </para>
    /// </remarks>
    public partial class create_platform_killswitches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "platform_killswitches",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    toggled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    toggled_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_platform_killswitches", x => x.key);
                });

            // Transcribed from Database Standards § platform_killswitches. ENABLE and
            // FORCE both: ENABLE alone leaves the owner exempt, and the owner is
            // learnstack_migration, which runs this very file.
            migrationBuilder.Sql(
                """
                ALTER TABLE platform_killswitches ENABLE ROW LEVEL SECURITY;
                ALTER TABLE platform_killswitches FORCE  ROW LEVEL SECURITY;

                -- READ is unconditional, and that is the point: the switch is global by
                -- construction, so hiding it from the role that has to honour it would
                -- only fail open.
                CREATE POLICY platform_killswitches_read ON platform_killswitches
                    FOR SELECT TO learnstack_app
                    USING (true);

                -- No write policy for learnstack_app, and no write privilege either.
                -- Every toggle runs as learnstack_platform inside
                -- EnterPlatformAdminScope(reason), which is what gives
                -- tenancy.killswitch.toggle a real actor and a MUST-class audit row.
                GRANT SELECT ON platform_killswitches TO learnstack_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON platform_killswitches TO learnstack_platform;
                """);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Dropping the table takes its policies and grants with it; PostgreSQL does not
        /// leave either behind. Every gated read falls back to the key's default — the
        /// enabled state — so a rolled-back deployment is one with no switch flipped,
        /// which is the state it was in before this migration ran.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "platform_killswitches");
        }
    }
}
