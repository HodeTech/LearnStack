using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Modules.Customization.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class create_customization_schema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "customization_generations",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customization_generations", x => x.tenant_id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_content_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    json_schema = table.Column<string>(type: "jsonb", nullable: false),
                    renderer_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    row_version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    schema_revision = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    status = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_content_types", x => x.id);
                    table.UniqueConstraint("ux_tenant_content_types_tenant_id_key_schema_version", x => new { x.tenant_id, x.key, x.schema_version });
                });

            migrationBuilder.CreateTable(
                name: "tenant_level_taxonomies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    row_version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    schema_revision = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    status = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_level_taxonomies", x => x.id);
                    table.UniqueConstraint("ux_tenant_level_taxonomies_tenant_id_key_schema_version", x => new { x.tenant_id, x.key, x.schema_version });
                });

            migrationBuilder.CreateTable(
                name: "tenant_level_taxonomy_items",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    taxonomy_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    display_name = table.Column<string>(type: "jsonb", nullable: false),
                    sort = table.Column<short>(type: "smallint", nullable: false),
                    metadata = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_level_taxonomy_items", x => new { x.tenant_id, x.taxonomy_key, x.schema_version, x.key });
                    table.ForeignKey(
                        name: "fk_tenant_level_taxonomy_items_taxonomy",
                        columns: x => new { x.tenant_id, x.taxonomy_key, x.schema_version },
                        principalTable: "tenant_level_taxonomies",
                        principalColumns: new[] { "tenant_id", "key", "schema_version" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_tenant_content_types_tenant_id_key_active",
                table: "tenant_content_types",
                columns: new[] { "tenant_id", "key" },
                unique: true,
                filter: "status = 'Active' AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_tenant_level_taxonomies_tenant_id_key_active",
                table: "tenant_level_taxonomies",
                columns: new[] { "tenant_id", "key" },
                unique: true,
                filter: "status = 'Active' AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_tenant_level_taxonomy_items_taxonomy_sort",
                table: "tenant_level_taxonomy_items",
                columns: new[] { "tenant_id", "taxonomy_key", "schema_version", "sort" },
                unique: true);
            // ── Closed-set status columns ────────────────────────────────────
            //
            // `text` with a CHECK, never a PostgreSQL enum type — whose values can
            // only be added, never removed or reordered, and every change is a
            // migration on the type rather than on the table. The CLR side stays a
            // C# enum and maps through a value converter, so the stored value is
            // the CLR name (Database Standards § Constraints).
            //
            // `renderer_key` is a closed set too and is deliberately NOT bounded
            // here. Its nine values live in `CompositeRendererKey.All` and are
            // shared with the frontend's `composites.ts`, which
            // `Composite_Renderer_Keys_Match_The_Frontend_Registry` holds equal; a
            // CHECK would be a third copy that only a migration can correct, and
            // the two it must agree with ship on a different cadence.
            migrationBuilder.Sql("""
                ALTER TABLE tenant_content_types
                    ADD CONSTRAINT ck_tenant_content_types_status
                    CHECK (status IN ('Draft', 'Active', 'Deprecated'));

                ALTER TABLE tenant_level_taxonomies
                    ADD CONSTRAINT ck_tenant_level_taxonomies_status
                    CHECK (status IN ('Draft', 'Active', 'Deprecated'));
                """);

            // ── Row Level Security ───────────────────────────────────────────
            //
            // ENABLE *and* FORCE: without FORCE the table owner bypasses its own
            // policies, and migrations run as the owner (ADR-0003 Amendment 3).
            migrationBuilder.Sql("""
                ALTER TABLE tenant_content_types           ENABLE ROW LEVEL SECURITY;
                ALTER TABLE tenant_content_types           FORCE  ROW LEVEL SECURITY;
                ALTER TABLE tenant_level_taxonomies        ENABLE ROW LEVEL SECURITY;
                ALTER TABLE tenant_level_taxonomies        FORCE  ROW LEVEL SECURITY;
                ALTER TABLE tenant_level_taxonomy_items    ENABLE ROW LEVEL SECURITY;
                ALTER TABLE tenant_level_taxonomy_items    FORCE  ROW LEVEL SECURITY;
                ALTER TABLE customization_generations      ENABLE ROW LEVEL SECURITY;
                ALTER TABLE customization_generations      FORCE  ROW LEVEL SECURITY;
                """);

            // Every table here is TENANT-OWNED, TENANT-WIDE: one permissive policy
            // with the tenant term alone, an explicit WITH CHECK, and NO restrictive
            // guards — those exist to stop an organization-scoped session touching
            // another organization's rows, and no table here carries an
            // organization_id (Database Standards § Table classes).
            //
            // NULLIF(..., '') is not decoration. A customized (dotted) GUC becomes a
            // session placeholder the first time it is assigned, and its reset value
            // is the empty string rather than "undefined". On a pooled connection
            // whose previous transaction set app.tenant_id and whose next one forgets
            // to, current_setting(..., true) returns '' and ''::uuid RAISES instead of
            // filtering. NULLIF turns that into NULL, and a NULL policy result is
            // false for both USING and WITH CHECK — fail-closed for the never-set path
            // and the reset path alike.
            migrationBuilder.Sql("""
                CREATE POLICY tenant_content_types_isolation ON tenant_content_types
                    USING      (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                CREATE POLICY tenant_level_taxonomies_isolation ON tenant_level_taxonomies
                    USING      (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                CREATE POLICY tenant_level_taxonomy_items_isolation ON tenant_level_taxonomy_items
                    USING      (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                CREATE POLICY customization_generations_isolation ON customization_generations
                    USING      (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);

            // ── GRANTs ───────────────────────────────────────────────────────
            //
            // learnstack_migration owns these tables and needs no grant.
            // learnstack_app is the runtime role and holds NOBYPASSRLS.
            // learnstack_platform reads under an audited bypass and does not write:
            // a customization row is a tenant's own declaration, and an operator
            // repairing one by hand is a decision, not a default.
            // learnstack_outbox_admin has no business here at all.
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT, UPDATE, DELETE ON tenant_content_types        TO learnstack_app;
                GRANT SELECT                         ON tenant_content_types        TO learnstack_platform;

                GRANT SELECT, INSERT, UPDATE, DELETE ON tenant_level_taxonomies     TO learnstack_app;
                GRANT SELECT                         ON tenant_level_taxonomies     TO learnstack_platform;

                GRANT SELECT, INSERT, UPDATE, DELETE ON tenant_level_taxonomy_items TO learnstack_app;
                GRANT SELECT                         ON tenant_level_taxonomy_items TO learnstack_platform;

                GRANT SELECT, INSERT, UPDATE         ON customization_generations   TO learnstack_app;
                GRANT SELECT                         ON customization_generations   TO learnstack_platform;
                """);
        }


        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "customization_generations");

            migrationBuilder.DropTable(
                name: "tenant_content_types");

            migrationBuilder.DropTable(
                name: "tenant_level_taxonomy_items");

            migrationBuilder.DropTable(
                name: "tenant_level_taxonomies");
        }
    }
}
