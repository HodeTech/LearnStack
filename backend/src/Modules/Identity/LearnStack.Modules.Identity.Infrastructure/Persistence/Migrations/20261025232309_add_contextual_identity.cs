using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class add_contextual_identity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "memberships",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    row_version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_memberships", x => x.id);
                    table.UniqueConstraint("ux_memberships_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_memberships_lifecycle", "(status IN ('Active','Suspended') AND deleted_at IS NULL AND deleted_by IS NULL) OR (status = 'Revoked' AND deleted_at IS NOT NULL AND deleted_by IS NOT NULL)");
                    table.CheckConstraint("ck_memberships_status", "status IN ('Active','Suspended','Revoked')");
                    table.ForeignKey(
                        name: "fk_memberships_user",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    built_in_key = table.Column<string>(type: "text", nullable: false),
                    permission_scope = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    row_version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_roles", x => x.id);
                    table.UniqueConstraint("ux_roles_tenant_id_id", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "membership_profiles",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    display_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    preferred_locale = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_membership_profiles", x => new { x.tenant_id, x.membership_id });
                    table.ForeignKey(
                        name: "fk_membership_profiles_membership",
                        columns: x => new { x.tenant_id, x.membership_id },
                        principalTable: "memberships",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "membership_role_assignments",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_membership_role_assignments", x => new { x.tenant_id, x.membership_id, x.role_id });
                    table.ForeignKey(
                        name: "fk_membership_role_assignments_membership",
                        columns: x => new { x.tenant_id, x.membership_id },
                        principalTable: "memberships",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_membership_role_assignments_role",
                        columns: x => new { x.tenant_id, x.role_id },
                        principalTable: "roles",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "role_permission_grants",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    permission_scope = table.Column<string>(type: "text", nullable: false),
                    permission_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_role_permission_grants", x => new { x.tenant_id, x.role_id, x.permission_scope, x.permission_key });
                    table.ForeignKey(
                        name: "fk_role_permission_grants_role",
                        columns: x => new { x.tenant_id, x.role_id },
                        principalTable: "roles",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_membership_profiles_tenant_id_organization_id",
                table: "membership_profiles",
                columns: new[] { "tenant_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_membership_role_assignments_tenant_id_organization_id",
                table: "membership_role_assignments",
                columns: new[] { "tenant_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_membership_role_assignments_tenant_id_role_id",
                table: "membership_role_assignments",
                columns: new[] { "tenant_id", "role_id" });

            migrationBuilder.CreateIndex(
                name: "ix_memberships_tenant_id_organization_id",
                table: "memberships",
                columns: new[] { "tenant_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_memberships_user_id",
                table: "memberships",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_memberships_current_scope",
                table: "memberships",
                columns: new[] { "tenant_id", "user_id", "organization_id" },
                unique: true,
                filter: "deleted_at IS NULL")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ux_roles_current_builtin_scope",
                table: "roles",
                columns: new[] { "tenant_id", "permission_scope", "built_in_key" },
                unique: true,
                filter: "deleted_at IS NULL");

            ContextualIdentityStorageDdl.Install(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ContextualIdentityStorageDdl.Remove(migrationBuilder);
            migrationBuilder.DropTable(
                name: "membership_profiles");

            migrationBuilder.DropTable(
                name: "membership_role_assignments");

            migrationBuilder.DropTable(
                name: "role_permission_grants");

            migrationBuilder.DropTable(
                name: "memberships");

            migrationBuilder.DropTable(
                name: "roles");
        }
    }
}
