using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class create_tenancy_schema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "organizations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    custom_subdomain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reporting_parent_id = table.Column<Guid>(type: "uuid", nullable: true),
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
                    table.PrimaryKey("pk_organizations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "platform_entitlement_cache",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    features = table.Column<string>(type: "jsonb", nullable: false),
                    limits = table.Column<string>(type: "jsonb", nullable: false),
                    compliance = table.Column<string>(type: "jsonb", nullable: false),
                    valid_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    grace_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    generation = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    refreshed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_platform_entitlement_cache", x => x.tenant_id);
                });

            migrationBuilder.CreateTable(
                name: "platform_host_to_tenant",
                columns: table => new
                {
                    host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    is_publicly_live = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_platform_host_to_tenant", x => x.host);
                });

            migrationBuilder.CreateTable(
                name: "tenant_domains",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    verification_attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    last_verification_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
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
                    table.PrimaryKey("pk_tenant_domains", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_feature_flags",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    value = table.Column<string>(type: "jsonb", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_feature_flags", x => new { x.tenant_id, x.key });
                });

            migrationBuilder.CreateTable(
                name: "tenant_locales",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    locale = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    sort = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_locales", x => new { x.tenant_id, x.locale });
                });

            migrationBuilder.CreateTable(
                name: "tenant_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    value = table.Column<string>(type: "jsonb", nullable: false),
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
                    table.PrimaryKey("pk_tenant_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    default_organization_id = table.Column<Guid>(type: "uuid", nullable: true),
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
                    table.PrimaryKey("pk_tenants", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_organizations_tenant_id_id",
                table: "organizations",
                columns: new[] { "tenant_id", "id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_organizations_tenant_id_slug",
                table: "organizations",
                columns: new[] { "tenant_id", "slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_platform_host_to_tenant_tenant_id",
                table: "platform_host_to_tenant",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_domains_tenant_id",
                table: "tenant_domains",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ux_tenant_domains_host",
                table: "tenant_domains",
                column: "host",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tenant_settings_tenant_id_organization_id",
                table: "tenant_settings",
                columns: new[] { "tenant_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ux_tenant_settings_tenant_id_organization_id_key",
                table: "tenant_settings",
                columns: new[] { "tenant_id", "organization_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_tenants_slug",
                table: "tenants",
                column: "slug",
                unique: true);

            // ────────────────────────────────────────────────────────────────
            // Everything below is what EF Core cannot express, and it is not
            // optional decoration: without it the eight tables above have no
            // isolation at all. The canonical forms live in
            // docs/standards/05-database.md; this migration transcribes them.
            // ────────────────────────────────────────────────────────────────

            // ── The circular foreign key, composite ──────────────────────────
            // tenants.default_organization_id -> organizations, and organizations
            // -> tenants. Composite on the tenant term because referential-integrity
            // checks run with row security bypassed: single-column, tenant A could
            // commit a permanent pointer at tenant B's organization — a row A cannot
            // even see. Measured. Under MATCH SIMPLE the check is skipped while the
            // column is null, which is what makes the three-statement provisioning
            // sequence work (insert tenant, insert organization, UPDATE tenant).
            migrationBuilder.Sql("""
                ALTER TABLE organizations
                    ADD CONSTRAINT fk_organizations_tenant
                    FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT;

                ALTER TABLE tenants
                    ADD CONSTRAINT fk_tenants_default_organization
                    FOREIGN KEY (id, default_organization_id) REFERENCES organizations (tenant_id, id)
                    ON DELETE RESTRICT;
                """);

            // organizations.reporting_parent_id is reporting-only and NOT an
            // isolation boundary, but the composite rule still applies: it points
            // at another organization in the same tenant, and single-column would
            // reopen the same hole.
            migrationBuilder.Sql("""
                ALTER TABLE organizations
                    ADD CONSTRAINT fk_organizations_reporting_parent
                    FOREIGN KEY (tenant_id, reporting_parent_id) REFERENCES organizations (tenant_id, id)
                    ON DELETE RESTRICT;

                ALTER TABLE tenant_domains
                    ADD CONSTRAINT fk_tenant_domains_tenant
                    FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT;

                ALTER TABLE tenant_locales
                    ADD CONSTRAINT fk_tenant_locales_tenant
                    FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT;

                ALTER TABLE tenant_settings
                    ADD CONSTRAINT fk_tenant_settings_tenant
                    FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT;

                ALTER TABLE tenant_settings
                    ADD CONSTRAINT fk_tenant_settings_organization
                    FOREIGN KEY (tenant_id, organization_id) REFERENCES organizations (tenant_id, id)
                    ON DELETE RESTRICT;

                ALTER TABLE tenant_feature_flags
                    ADD CONSTRAINT fk_tenant_feature_flags_tenant
                    FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT;

                ALTER TABLE platform_entitlement_cache
                    ADD CONSTRAINT fk_platform_entitlement_cache_tenant
                    FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT;

                ALTER TABLE platform_host_to_tenant
                    ADD CONSTRAINT fk_platform_host_to_tenant_tenant
                    FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT;

                ALTER TABLE platform_host_to_tenant
                    ADD CONSTRAINT fk_platform_host_to_tenant_organization
                    FOREIGN KEY (tenant_id, organization_id) REFERENCES organizations (tenant_id, id)
                    ON DELETE RESTRICT;
                """);

            // ── Closed-set columns: text + CHECK ─────────────────────────────
            // Not a PostgreSQL enum type, whose values can only be added and never
            // removed or reordered, and not an int, which makes a dump unreadable.
            migrationBuilder.Sql("""
                ALTER TABLE tenants ADD CONSTRAINT ck_tenants_status
                    CHECK (status IN ('Trial', 'Active', 'Suspended', 'Archived'));

                ALTER TABLE organizations ADD CONSTRAINT ck_organizations_status
                    CHECK (status IN ('Active', 'Suspended', 'Archived'));

                ALTER TABLE tenant_domains ADD CONSTRAINT ck_tenant_domains_kind
                    CHECK (kind IN ('Subdomain', 'Custom'));

                ALTER TABLE tenant_domains ADD CONSTRAINT ck_tenant_domains_status
                    CHECK (status IN ('Requested', 'Verifying', 'Verified', 'Failed'));

                ALTER TABLE platform_entitlement_cache ADD CONSTRAINT ck_platform_entitlement_cache_source
                    CHECK (source IN ('hub', 'signed-license-key', 'null-provider'));
                """);

            // ── Host normalization, as a backstop ────────────────────────────
            // The LDH rule stated positively: every label starts and ends
            // alphanumeric and may carry hyphens between, labels joined by single
            // dots. Written this way rather than as prohibitions because the
            // prohibitions kept missing cases — a `!~ '[^a-z0-9.-]'` form accepted
            // `.example.com`, `a..b.com` and `-example.com`, none of which
            // EffectiveHost.Normalize's IsLdh gate can produce. Lowercase, no
            // trailing dot and no embedded port all fall out of the pattern.
            // `[a-z0-9]+(` rather than `[a-z0-9](`: the latter spells `](`, which
            // the CI link audit greps for as a Markdown link.
            migrationBuilder.Sql("""
                ALTER TABLE platform_host_to_tenant
                    ADD CONSTRAINT ck_platform_host_to_tenant_host_normalized CHECK (
                        host ~ '^[a-z0-9]+([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]+([a-z0-9-]*[a-z0-9])?)*$'
                        AND length(host) <= 253);

                ALTER TABLE tenant_domains
                    ADD CONSTRAINT ck_tenant_domains_host_normalized CHECK (
                        host ~ '^[a-z0-9]+([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]+([a-z0-9-]*[a-z0-9])?)*$'
                        AND length(host) <= 253);
                """);

            // ── tenant_settings uniqueness: NULLS NOT DISTINCT ───────────────
            // EF cannot express it, so the index it generated is dropped and
            // replaced. Load-bearing: organization_id is null on every tenant-wide
            // row, and a standard UNIQUE treats nulls as distinct — so without this
            // a tenant could hold unlimited duplicate tenant-wide rows for one key,
            // which is precisely the set a single-organization tenant creates
            // exclusively, and resolution would pick one arbitrarily.
            migrationBuilder.Sql("""
                DROP INDEX ux_tenant_settings_tenant_id_organization_id_key;

                ALTER TABLE tenant_settings
                    ADD CONSTRAINT ux_tenant_settings_tenant_id_organization_id_key
                    UNIQUE NULLS NOT DISTINCT (tenant_id, organization_id, key);
                """);

            // ── organization_id is immutable after insert ────────────────────
            // IS DISTINCT FROM rather than <>, so a move to or from NULL —
            // tenant-wide to org-scoped, or back — is caught too; <> is NULL when
            // either side is null and the trigger would pass. The restrictive
            // UPDATE guard does not cover this: it admits the row when the NEW
            // organization_id is the caller's own, which is exactly the
            // re-parenting move. A row does not move between organizations because
            // its audit rows, its storage prefix and its cache-key prefix are all
            // organization-qualified.
            migrationBuilder.Sql("""
                CREATE FUNCTION fn_organization_id_immutable() RETURNS trigger AS $$
                BEGIN
                    IF NEW.organization_id IS DISTINCT FROM OLD.organization_id THEN
                        RAISE EXCEPTION
                            'organization_id is immutable after insert (table %, row %)',
                            TG_TABLE_NAME, OLD.id
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER tg_tenant_settings_organization_id_immutable
                    BEFORE UPDATE ON tenant_settings
                    FOR EACH ROW EXECUTE FUNCTION fn_organization_id_immutable();
                """);

            // ── Row Level Security ───────────────────────────────────────────
            // ENABLE *and* FORCE on all eight, with no exception list, so the
            // structural scan needs none. FORCE is what stops the owner bypassing
            // its own policies — and learnstack_migration, which owns every table
            // here, is NOBYPASSRLS precisely so FORCE means something.
            //
            // ONE permissive policy per table with an AND-ed predicate. Two
            // permissive policies are combined with OR, which WIDENS access: that
            // is the defect ADR-0003 Amendment 3 corrects, and it made every
            // tenant-wide row visible to every tenant.
            //
            // NULLIF(..., '') is not decoration. A customized (dotted) GUC becomes a
            // session placeholder the first time it is assigned, and its reset value
            // is the empty string, not "undefined". On a pooled connection whose
            // previous transaction set app.tenant_id and whose next one forgets to,
            // current_setting(..., true) returns '' and ''::uuid RAISES instead of
            // filtering. NULLIF turns that into NULL, and a NULL policy result is
            // false for both USING and WITH CHECK — fail-closed for the never-set
            // path and the reset path alike.
            migrationBuilder.Sql("""
                ALTER TABLE tenants                    ENABLE ROW LEVEL SECURITY;
                ALTER TABLE tenants                    FORCE  ROW LEVEL SECURITY;
                ALTER TABLE organizations              ENABLE ROW LEVEL SECURITY;
                ALTER TABLE organizations              FORCE  ROW LEVEL SECURITY;
                ALTER TABLE tenant_domains             ENABLE ROW LEVEL SECURITY;
                ALTER TABLE tenant_domains             FORCE  ROW LEVEL SECURITY;
                ALTER TABLE tenant_locales             ENABLE ROW LEVEL SECURITY;
                ALTER TABLE tenant_locales             FORCE  ROW LEVEL SECURITY;
                ALTER TABLE tenant_settings            ENABLE ROW LEVEL SECURITY;
                ALTER TABLE tenant_settings            FORCE  ROW LEVEL SECURITY;
                ALTER TABLE tenant_feature_flags       ENABLE ROW LEVEL SECURITY;
                ALTER TABLE tenant_feature_flags       FORCE  ROW LEVEL SECURITY;
                ALTER TABLE platform_entitlement_cache ENABLE ROW LEVEL SECURITY;
                ALTER TABLE platform_entitlement_cache FORCE  ROW LEVEL SECURITY;
                ALTER TABLE platform_host_to_tenant    ENABLE ROW LEVEL SECURITY;
                ALTER TABLE platform_host_to_tenant    FORCE  ROW LEVEL SECURITY;
                """);

            // Class 1 — tenant-owned, SELF-KEYED. `tenants` has no tenant_id
            // column; its id IS the tenant id, so the predicate keys on id.
            migrationBuilder.Sql("""
                CREATE POLICY tenants_isolation ON tenants
                    USING      (id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);

            // Class 2 — tenant-owned, TENANT-WIDE. No organization term, because
            // these tables carry no organization_id column, and therefore no
            // restrictive guards either: there is no organization to guard.
            migrationBuilder.Sql("""
                CREATE POLICY organizations_isolation ON organizations
                    USING      (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                CREATE POLICY tenant_domains_isolation ON tenant_domains
                    USING      (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                CREATE POLICY tenant_locales_isolation ON tenant_locales
                    USING      (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                CREATE POLICY tenant_feature_flags_isolation ON tenant_feature_flags
                    USING      (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                CREATE POLICY platform_entitlement_cache_isolation ON platform_entitlement_cache
                    USING      (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);

            // Class 2b — tenant-owned, ORG-SCOPED. The only one in this set, and
            // therefore the only table taking the full template: the AND-ed
            // organization term, the app.scope read hatch, and the two AS
            // RESTRICTIVE write guards.
            //
            // The hatch widens READS across organizations, which cross-org
            // reporting needs. It must not widen writes — but USING is also what
            // selects the rows an UPDATE may target, and for DELETE it is the ONLY
            // gate, because PostgreSQL has no WITH CHECK for DELETE. Without the
            // restrictive policies a tenant-scope session could delete another
            // organization's rows, or reassign them to itself.
            migrationBuilder.Sql("""
                CREATE POLICY tenant_settings_isolation ON tenant_settings
                    USING (
                        tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                        AND (
                            organization_id IS NULL
                            OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                            OR current_setting('app.scope', true) = 'tenant'
                        )
                    )
                    WITH CHECK (
                        tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                        AND (
                            organization_id IS NULL
                            OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                        )
                    );

                CREATE POLICY tenant_settings_org_write_guard ON tenant_settings
                    AS RESTRICTIVE FOR UPDATE
                    USING (
                        organization_id IS NULL
                        OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    );

                CREATE POLICY tenant_settings_org_delete_guard ON tenant_settings
                    AS RESTRICTIVE FOR DELETE
                    USING (
                        organization_id IS NULL
                        OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    );
                """);

            // Class 3 — PLATFORM-SCOPED. One table, and adding a second is a
            // decision rather than a convenience. IHostToTenantResolver reads it in
            // order to DETERMINE the tenant, so at that moment app.tenant_id is
            // unset and the tenant-owned template would return zero rows forever.
            // The answer is not to drop row security — a table without it is
            // indistinguishable from one nobody thought about — but to give the
            // read an explicitly declared key of its own: the resolver announces
            // the host it is about to resolve, and the policy admits exactly that
            // row.
            //
            // A wide SELECT policy does not widen UPDATE or DELETE: PostgreSQL
            // applies the command's own policies in addition to the SELECT policy,
            // and a row must satisfy both. A session that can see another tenant's
            // host through app.resolving_host still cannot repoint it.
            //
            // Because these are role-qualified TO learnstack_app, no policy applies
            // to the owner, so under FORCE every access by learnstack_migration to
            // this table is denied. Rows arrive through learnstack_app under tenant
            // context or through learnstack_platform.
            migrationBuilder.Sql("""
                CREATE POLICY platform_host_to_tenant_read ON platform_host_to_tenant
                    FOR SELECT TO learnstack_app
                    USING (
                        host = NULLIF(current_setting('app.resolving_host', true), '')
                        OR tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                    );

                CREATE POLICY platform_host_to_tenant_insert ON platform_host_to_tenant
                    FOR INSERT TO learnstack_app
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                CREATE POLICY platform_host_to_tenant_update ON platform_host_to_tenant
                    FOR UPDATE TO learnstack_app
                    USING      (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                CREATE POLICY platform_host_to_tenant_delete ON platform_host_to_tenant
                    FOR DELETE TO learnstack_app
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);

            // ── Grants ───────────────────────────────────────────────────────
            // Written here, in the migration that creates the tables, because
            // there is deliberately no ALTER DEFAULT PRIVILEGES: a table nobody
            // granted fails loudly with `permission denied` rather than silently
            // inheriting DML — and can never silently widen a BYPASSRLS role, whose
            // only bound is this matrix. learnstack_migration owns every table and
            // needs no grant. The matrix is docs/standards/05-database.md § GRANT
            // matrix; this transcribes it.
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT, UPDATE         ON tenants                    TO learnstack_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON tenants                    TO learnstack_platform;

                GRANT SELECT, INSERT, UPDATE, DELETE ON organizations              TO learnstack_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON organizations              TO learnstack_platform;

                GRANT SELECT, INSERT, UPDATE, DELETE ON tenant_domains             TO learnstack_app;
                GRANT SELECT                         ON tenant_domains             TO learnstack_platform;

                GRANT SELECT, INSERT, UPDATE, DELETE ON tenant_locales             TO learnstack_app;
                GRANT SELECT                         ON tenant_locales             TO learnstack_platform;

                GRANT SELECT, INSERT, UPDATE, DELETE ON tenant_settings            TO learnstack_app;
                GRANT SELECT                         ON tenant_settings            TO learnstack_platform;

                GRANT SELECT, INSERT, UPDATE, DELETE ON tenant_feature_flags       TO learnstack_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON tenant_feature_flags       TO learnstack_platform;

                GRANT SELECT, INSERT, UPDATE         ON platform_entitlement_cache TO learnstack_app;
                GRANT SELECT, DELETE                 ON platform_entitlement_cache TO learnstack_platform;

                GRANT SELECT, INSERT, UPDATE, DELETE ON platform_host_to_tenant    TO learnstack_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON platform_host_to_tenant    TO learnstack_platform;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The tables go with DropTable below; what needs explicit removal is
            // the one object that is not owned by a table.
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS fn_organization_id_immutable();");

            migrationBuilder.DropTable(
                name: "organizations");

            migrationBuilder.DropTable(
                name: "platform_entitlement_cache");

            migrationBuilder.DropTable(
                name: "platform_host_to_tenant");

            migrationBuilder.DropTable(
                name: "tenant_domains");

            migrationBuilder.DropTable(
                name: "tenant_feature_flags");

            migrationBuilder.DropTable(
                name: "tenant_locales");

            migrationBuilder.DropTable(
                name: "tenant_settings");

            migrationBuilder.DropTable(
                name: "tenants");
        }
    }
}
