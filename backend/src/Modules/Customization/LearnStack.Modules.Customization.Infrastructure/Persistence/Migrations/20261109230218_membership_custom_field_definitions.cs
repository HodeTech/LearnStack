using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Modules.Customization.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class membership_custom_field_definitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenant_custom_field_defs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_entity = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    json_schema = table.Column<string>(type: "jsonb", nullable: false),
                    labels_json = table.Column<string>(type: "jsonb", nullable: false),
                    pii_category = table.Column<string>(type: "text", nullable: false),
                    is_required = table.Column<bool>(type: "boolean", nullable: false),
                    sort = table.Column<short>(type: "smallint", nullable: false),
                    purpose_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    notices_json = table.Column<string>(type: "jsonb", nullable: true),
                    form_labels_json = table.Column<string>(type: "jsonb", nullable: true),
                    maximum_value_bytes = table.Column<int>(type: "integer", nullable: false),
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
                    table.PrimaryKey("pk_tenant_custom_field_defs", x => x.id);
                    table.CheckConstraint("ck_tenant_custom_field_defs_category", "pii_category IN ('None', 'Identity', 'Behaviour', 'Sensitive')");
                    table.CheckConstraint("ck_tenant_custom_field_defs_purpose", "(pii_category = 'Sensitive' AND purpose_key IS NOT NULL AND notices_json IS NOT NULL) OR (pii_category <> 'Sensitive' AND purpose_key IS NULL AND notices_json IS NULL)");
                    table.CheckConstraint("ck_tenant_custom_field_defs_status", "status IN ('Draft', 'Active', 'Deprecated')");
                    table.CheckConstraint("ck_tenant_custom_field_defs_target", "target_entity = 'Membership'");
                    table.CheckConstraint("ck_tenant_custom_field_defs_version", "schema_version >= 1 AND sort >= 0 AND maximum_value_bytes BETWEEN 1 AND 4096");
                });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_custom_field_defs_tenant_target_status",
                table: "tenant_custom_field_defs",
                columns: new[] { "tenant_id", "target_entity", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_tenant_custom_field_defs_tenant_target_key_active",
                table: "tenant_custom_field_defs",
                columns: new[] { "tenant_id", "target_entity", "key" },
                unique: true,
                filter: "status = 'Active' AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_tenant_custom_field_defs_tenant_target_key_version",
                table: "tenant_custom_field_defs",
                columns: new[] { "tenant_id", "target_entity", "key", "schema_version" },
                unique: true);
            CustomFieldDefinitionDdl.Install(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            CustomFieldDefinitionDdl.Remove(migrationBuilder);
            migrationBuilder.DropTable(
                name: "tenant_custom_field_defs");
        }
    }
}
