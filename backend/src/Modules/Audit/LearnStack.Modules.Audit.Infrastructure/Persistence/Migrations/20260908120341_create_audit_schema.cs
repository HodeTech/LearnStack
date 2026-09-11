using System;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Modules.Audit.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Creates the Audit module's two tables — the fourth migration chain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One chain, two table classes.</b> <c>audit_log</c> carries
    /// <c>organization_id</c> and is therefore tenant-owned <i>org-scoped</i>;
    /// <c>audit_config</c> has no such column and is tenant-owned <i>tenant-wide</i>
    /// (<see href="../../../../../../../docs/decisions/0044-audit-write-path.md">ADR-0044
    /// Amendment 1</see>). Each takes its policy from its class, written in
    /// <see href="../../../../../../../docs/standards/05-database.md">Database Standards
    /// § Tenant-Owned and Organization-Scoped Tables</see> — the one document that owns
    /// the template.
    /// </para>
    /// <para>
    /// <b><c>audit_log</c> ships plain and unpartitioned.</b> Monthly partitioning, the
    /// partition-management job and the retention purge from
    /// <see href="../../../../../../../docs/decisions/0028-audit-log-partition-management.md">ADR-0028</see>
    /// belong to
    /// <see href="../../../../../../../docs/roadmap/phase-11-production-hardening.md">Phase
    /// 11</see> against a measured trigger; nothing in ADR-0028 is executed here. What
    /// this migration does carry forward is the shape that keeps that conversion additive:
    /// the primary key is the composite <c>(id, timestamp)</c>, so Phase 11 attaches this
    /// table to a partitioned parent rather than migrating a key. Audit correctness cannot
    /// be added later; audit scale can, and the platform has no rows yet to scale.
    /// </para>
    /// <para>
    /// <b>Four indexes, and deliberately not the template's fifth.</b> The canonical
    /// org-scoped template carries <c>ix_&lt;table&gt;_tenant_id_organization_id</c> so
    /// the organization arm of the policy has an index to read. This table does not take
    /// it: every read the admin API issues is tenant-scope — <c>audit.event.read</c> is a
    /// Tenant-scope permission — so the arm that fires is
    /// <c>current_setting('app.scope') = 'tenant'</c>, which no index serves, and the four
    /// indexes below all lead with a column those queries filter on. A fifth index on a
    /// high-volume append-only table is a write cost paid on every row for a predicate arm
    /// the shipped readers do not take. The index set is the one
    /// <see href="../../../../../../../docs/architecture/31-audit-subsystem.md">Audit
    /// Subsystem § 7</see> enumerates.
    /// </para>
    /// <para>
    /// <b>Append-only is three layers, and this migration writes all three</b> — the
    /// absent privilege for <c>learnstack_app</c>, the column-restricted <c>UPDATE</c>
    /// for <c>learnstack_platform</c>, and the two trigger functions that are the only
    /// layer binding the table owner. Each stops a different actor; none is redundant
    /// with another.
    /// </para>
    /// </remarks>
    public partial class create_audit_schema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_config",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    module = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    operation = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
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
                    table.PrimaryKey("pk_audit_config", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    module = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    operation = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    operation_type = table.Column<string>(type: "text", nullable: false),
                    operation_class = table.Column<string>(type: "text", nullable: false),
                    entity_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    entity_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    error_key = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    before_state = table.Column<string>(type: "jsonb", nullable: true),
                    after_state = table.Column<string>(type: "jsonb", nullable: true),
                    changes = table.Column<string>(type: "jsonb", nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ip_address = table.Column<IPAddress>(type: "inet", nullable: true),
                    user_agent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("audit_log_pkey", x => new { x.id, x.timestamp });
                    table.CheckConstraint("ck_audit_log_operation_class", "operation_class IN ('Must', 'Should', 'May')");
                    table.CheckConstraint("ck_audit_log_operation_type", "operation_type IN ('Create', 'Update', 'Delete', 'ReadSensitive', 'SecurityEvent', 'PlatformAdmin', 'Action')");
                    table.CheckConstraint("ck_audit_log_outcome", "outcome IN ('success', 'denied', 'failed', 'indeterminate')");
                });

            migrationBuilder.CreateIndex(
                name: "ux_audit_config_tenant_id_module_operation",
                table: "audit_config",
                columns: new[] { "tenant_id", "module", "operation" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_actor_user_id_timestamp",
                table: "audit_log",
                columns: new[] { "actor_user_id", "timestamp" },
                descending: new[] { false, true },
                filter: "actor_user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_correlation_id",
                table: "audit_log",
                column: "correlation_id",
                filter: "correlation_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_module_operation_timestamp",
                table: "audit_log",
                columns: new[] { "module", "operation", "timestamp" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_tenant_id_timestamp",
                table: "audit_log",
                columns: new[] { "tenant_id", "timestamp" },
                descending: new[] { false, true });

            // ── The two server-side defaults, and what they are for ──────────
            //
            // Neither is the path a row normally takes. PostgresAuditStore supplies both
            // values from the pipeline: the id is minted app-side at step 3 because the
            // commit-in-doubt pair has to carry ONE identity across two connections, and
            // a server default would give the two inserts two (ADR-0023 Amendment 9);
            // the timestamp comes from IClock, the intent's DeclaredAt for the
            // in-transaction row and a fresh reading for a standalone re-write, which is
            // what keeps that pair legal under the composite key instead of raising
            // 23505. The defaults stay as the backstop for a row inserted by something
            // other than the store — a psql session, a future job — so that such a row
            // is well-formed rather than rejected.
            //
            // uuidv7() is a PostgreSQL 18 built-in and needs no extension; ADR-0031 pins
            // 18.x across every deployment mode.
            migrationBuilder.Sql("""
                ALTER TABLE audit_log ALTER COLUMN id        SET DEFAULT uuidv7();
                ALTER TABLE audit_log ALTER COLUMN timestamp SET DEFAULT now();
                """);

            // ── The schema's only cross-chain foreign key ────────────────────
            //
            // `tenants` belongs to the Tenancy chain and audit_config to this one, so
            // chain application order is load-bearing here and nowhere else. `make
            // migrate` names Tenancy before the glob for exactly this reason — the glob
            // expands alphabetically and Modules/Audit sorts first. The DO block turns
            // the failure that follows a wrong order into a sentence that says what to
            // do; without it the migration fails with `relation "tenants" does not
            // exist`, which reads like a missing table rather than a sequencing mistake.
            //
            // audit_log deliberately has NO such key: the platform-scope row carries
            // TenantId.PlatformSentinel, which by construction has no `tenants` row, and
            // a foreign key admits no exception — not for learnstack_platform and not
            // under BYPASSRLS, because it is a constraint rather than a policy.
            // Independently, an audit log that cascaded or restricted on tenant deletion
            // would lose the record of what happened to the tenant, which is the case a
            // regulator asks about most often (ADR-0044 § 9).
            //
            // Single-column, under the standard's one written exception: the referenced
            // table is self-keyed, so `tenant_id` referencing `tenants (id)` cannot point
            // at another tenant by construction. RESTRICT rather than CASCADE, because
            // the standing cascade exception is for a child inside an aggregate boundary
            // and audit_config is not inside the Tenant aggregate.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF to_regclass('public.tenants') IS NULL THEN
                        RAISE EXCEPTION
                            'audit_config references tenants, which the Tenancy migration chain creates. Apply that chain first: `make migrate` does, and applies Tenancy before the alphabetical glob for this reason.'
                            USING ERRCODE = 'undefined_table';
                    END IF;
                END;
                $$;

                ALTER TABLE audit_config
                    ADD CONSTRAINT fk_audit_config_tenant FOREIGN KEY (tenant_id)
                    REFERENCES tenants (id) ON DELETE RESTRICT;
                """);

            // ── Row Level Security ───────────────────────────────────────────
            //
            // ENABLE *and* FORCE on both: without FORCE the table owner bypasses its own
            // policies, and migrations run as the owner (ADR-0003 Amendment 3).
            migrationBuilder.Sql("""
                ALTER TABLE audit_log    ENABLE ROW LEVEL SECURITY;
                ALTER TABLE audit_log    FORCE  ROW LEVEL SECURITY;
                ALTER TABLE audit_config ENABLE ROW LEVEL SECURITY;
                ALTER TABLE audit_config FORCE  ROW LEVEL SECURITY;
                """);

            // audit_log is TENANT-OWNED, ORG-SCOPED — it carries organization_id, and the
            // class follows the column. One permissive policy AND-ing the tenant term
            // with the organization term, an explicit WITH CHECK, and both AS RESTRICTIVE
            // guards, exactly as Database Standards § Tenant-Owned and
            // Organization-Scoped Tables writes them.
            //
            // NULLIF(..., '') is not decoration. A customized (dotted) GUC becomes a
            // session placeholder the first time it is assigned, and its reset value is
            // the empty string rather than "undefined". On a pooled connection whose
            // previous transaction set app.tenant_id and whose next one forgets to,
            // current_setting(..., true) returns '' and ''::uuid RAISES instead of
            // filtering. NULLIF turns that into NULL, and a NULL policy result is false
            // for both USING and WITH CHECK — fail-closed for the never-set path and the
            // reset path alike.
            //
            // The WITH CHECK is what makes a MUST-class row writable only inside a
            // transaction that has announced its tenant. It is deliberately NOT the guard
            // that decides which tenant a row carries: the standalone writers announce
            // both GUCs from the draft they were handed, so for those writes the clause
            // is vacuous. The value is decided once, at pipeline step 3, from the tenant
            // context and the provisioning marker — never from the payload the row
            // describes (ADR-0044 § 2).
            migrationBuilder.Sql("""
                CREATE POLICY audit_log_isolation ON audit_log
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

                CREATE POLICY audit_log_org_write_guard ON audit_log
                    AS RESTRICTIVE FOR UPDATE
                    USING (
                        (organization_id IS NULL
                         AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                        OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    );

                CREATE POLICY audit_log_org_delete_guard ON audit_log
                    AS RESTRICTIVE FOR DELETE
                    USING (
                        (organization_id IS NULL
                         AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                        OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    );
                """);

            // audit_config is TENANT-OWNED, TENANT-WIDE. It has no organization_id —
            // nothing asks a tenant to classify one organization's operations differently
            // from another's — so it takes the tenant term alone and no restrictive write
            // guards; there is no organization for them to guard (ADR-0044 Amendment 1).
            migrationBuilder.Sql("""
                CREATE POLICY audit_config_isolation ON audit_config
                    USING      (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);

            // ── GRANTs ───────────────────────────────────────────────────────
            //
            // learnstack_migration owns both tables and needs no grant.
            // learnstack_outbox_admin has no business in either.
            //
            // Layer 1 of append-only: learnstack_app may add rows and read them back and
            // holds nothing else, so an ordinary UPDATE or DELETE fails with 42501 before
            // any trigger runs.
            //
            // Layer 2: learnstack_platform's UPDATE is restricted BY COLUMN to the six a
            // GDPR erasure touches, so an UPDATE naming any other column is refused by
            // the privilege system — again before the trigger. outbox_messages'
            // dispatcher grant is the existing precedent for the form. actor_user_id is
            // deliberately absent: once the users row is erased it is an orphan surrogate
            // with no path back to a natural person, which is what keeps the row's
            // existence auditable after erasure, and redacting it would collapse every
            // erased user's history into one bucket.
            //
            // audit_config has no writer, and that is deliberate rather than an
            // omission. Both runtime roles hold SELECT, so the only role that can insert
            // is the owner and the table is empty until a tenant authors a row. The
            // editor that authors one lands with the Studio tenant-settings screens in
            // Phase 06, on the permission registry Phase 03 brings; INSERT, UPDATE,
            // DELETE for learnstack_app lands in that phase's migration, beside the
            // command that needs it. An absent override reads as "no overrides", which is
            // the safe answer, so nothing is lost by the gap.
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT         ON audit_log TO learnstack_app;
                GRANT SELECT, INSERT, DELETE ON audit_log TO learnstack_platform;
                GRANT UPDATE (actor_email, ip_address, user_agent,
                              before_state, after_state, changes)
                    ON audit_log TO learnstack_platform;

                GRANT SELECT ON audit_config TO learnstack_app;
                GRANT SELECT ON audit_config TO learnstack_platform;
                """);

            // ── Append-only, layer 3 ─────────────────────────────────────────
            //
            // Two triggers, because a row trigger cannot see the one statement that
            // empties a table without touching a row.
            //
            // This layer is the only one that binds the table OWNER. learnstack_app is
            // stopped by the absent privilege and a stray learnstack_platform UPDATE by
            // the column grant, both before any trigger runs. Neither reaches
            // learnstack_migration: ownership carries every privilege implicitly, and
            // while FORCE ROW LEVEL SECURITY does subject the owner to the policy above,
            // what the policy constrains it by is the TENANT, not immutability — measured
            // on PostgreSQL 18.6, an owner's UPDATE returns `UPDATE 0` with no tenant
            // announced and `UPDATE 1` with one. State the bound honestly: what these
            // functions do not stop is an owner who first runs ALTER TABLE audit_log
            // DISABLE TRIGGER. No layer inside the database stops that one, which is why
            // learnstack_migration is a migration credential and never a runtime one.
            //
            // Exactly two mutating paths exist. Both are owned by the Audit module and
            // both run as learnstack_platform through the audited
            // EnterPlatformAdminScope(reason) path: GDPR redaction (UPDATE, restricted to
            // the six redactable columns) and the retention purge (DELETE). After Phase
            // 11 partitioning the purge becomes DETACH + DROP PARTITION and issues no
            // DELETE at all.
            //
            // BEFORE row triggers on partitioned tables are supported from PostgreSQL 13
            // and are inherited by partitions created later, so Phase 11 partitioning
            // stays additive for the append-only guard. The TRUNCATE guard is NOT
            // inherited — a TRUNCATE trigger is a statement trigger on the table it was
            // created on — so Phase 11's partition-management job creates its own on
            // every partition it creates. That is the one place partitioning is not
            // additive for this table.
            migrationBuilder.Sql("""
                CREATE FUNCTION fn_audit_log_append_only()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    IF current_user <> 'learnstack_platform' THEN
                        RAISE EXCEPTION 'audit_log is append-only (attempted % as %)', TG_OP, current_user
                            USING ERRCODE = 'insufficient_privilege';
                    END IF;

                    IF TG_OP = 'DELETE' THEN
                        RETURN OLD;   -- allow the purge; returning NULL here would cancel it
                    END IF;

                    -- UPDATE: every column except the six redactable ones must be
                    -- unchanged. Expressed as a jsonb difference rather than a column
                    -- list so the guard survives every future column addition without an
                    -- edit here.
                    IF (to_jsonb(NEW) - 'actor_email' - 'ip_address' - 'user_agent'
                                      - 'before_state' - 'after_state' - 'changes')
                       IS DISTINCT FROM
                       (to_jsonb(OLD) - 'actor_email' - 'ip_address' - 'user_agent'
                                      - 'before_state' - 'after_state' - 'changes')
                    THEN
                        RAISE EXCEPTION 'audit_log UPDATE may only redact actor_email, ip_address, user_agent, before_state, after_state, changes'
                            USING ERRCODE = 'insufficient_privilege';
                    END IF;

                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER audit_log_append_only_guard
                    BEFORE UPDATE OR DELETE ON audit_log
                    FOR EACH ROW EXECUTE FUNCTION fn_audit_log_append_only();

                -- No role in the GRANT matrix holds TRUNCATE on audit_log —
                -- learnstack_platform included, whose retention purge is a per-tenant,
                -- per-retention-class DELETE and needs none — so this guard takes no
                -- current_user test and admits no exception. Row security does not apply
                -- to TRUNCATE at all, and the row trigger above never sees it.
                CREATE FUNCTION fn_audit_log_no_truncate()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION 'audit_log is append-only (TRUNCATE attempted as %)', current_user
                        USING ERRCODE = 'insufficient_privilege';
                END;
                $$;

                CREATE TRIGGER tg_audit_log_no_truncate
                    BEFORE TRUNCATE ON audit_log
                    FOR EACH STATEMENT EXECUTE FUNCTION fn_audit_log_no_truncate();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_config");

            migrationBuilder.DropTable(
                name: "audit_log");

            // The triggers went with the table; the functions did not. A CREATE FUNCTION
            // that finds a leftover of the same name silently replaces nothing — the
            // signature matches — so re-applying Up after this Down would keep whichever
            // body happened to be there. Dropping them is what makes the pair symmetric.
            migrationBuilder.Sql("""
                DROP FUNCTION fn_audit_log_append_only();
                DROP FUNCTION fn_audit_log_no_truncate();
                """);
        }
    }
}
