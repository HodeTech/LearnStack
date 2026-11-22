using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class add_data_subject_requests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "data_subject_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    origin = table.Column<string>(type: "text", nullable: false),
                    subject_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requester_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    membership_snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    execute_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    progress = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    result = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    last_refusal = table.Column<string>(type: "text", nullable: true),
                    failure_reason = table.Column<string>(type: "text", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
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
                    table.PrimaryKey("pk_data_subject_requests", x => x.id);
                    table.UniqueConstraint("ux_data_subject_requests_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_data_subject_requests_identifiers", "(uuid_extract_version(id)=7 AND uuid_extract_version(subject_user_id)=7 AND subject_user_id <> '00000000-0000-7000-8000-000000000001'::uuid AND (requester_user_id IS NULL OR (uuid_extract_version(requester_user_id)=7 AND requester_user_id <> '00000000-0000-7000-8000-000000000001'::uuid))) IS TRUE");
                    table.CheckConstraint("ck_data_subject_requests_kind", "kind IN ('export','erasure','closure')");
                    table.CheckConstraint("ck_data_subject_requests_no_delete", "deleted_at IS NULL AND deleted_by IS NULL");
                    table.CheckConstraint("ck_data_subject_requests_origin", "((kind IN ('export','erasure') AND origin IN ('self','administrator')) OR (kind = 'erasure' AND origin = 'closure_fanout') OR (kind = 'closure' AND origin = 'self')) IS TRUE");
                    table.CheckConstraint("ck_data_subject_requests_outcome", "((status = 'completed') = (completed_at IS NOT NULL)\n    AND (completed_at IS NULL OR (isfinite(completed_at) AND completed_at >= created_at))\n    AND ((status = 'failed' AND failure_reason IN ('export_too_large','execution_failed')\n            AND (failure_reason <> 'export_too_large' OR kind = 'export'))\n        OR (status <> 'failed' AND failure_reason IS NULL))\n    AND (last_refusal IS NULL OR last_refusal IN ('blocked_window','blocked_hold','blocked_last_administrator'))\n    AND (status <> 'blocked' OR last_refusal IN ('blocked_hold','blocked_last_administrator'))) IS TRUE");
                    table.CheckConstraint("ck_data_subject_requests_progress", "CASE WHEN jsonb_typeof(progress) = 'object'\n    THEN jsonb_array_length(jsonb_path_query_array(progress, '$.keyvalue()')) <= 32\n        AND NOT jsonb_path_exists(progress,\n            '$.keyvalue() ? (!(@.key like_regex \"^[a-z][a-z0-9_]{0,31}$\") || @.value.type() != \"string\" || !(@.value like_regex \"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}[.][0-9]{7}Z$\"))')\n    ELSE false END");
                    table.CheckConstraint("ck_data_subject_requests_requester", "((origin = 'self' AND requester_user_id = subject_user_id) OR (origin = 'administrator' AND requester_user_id IS NOT NULL AND requester_user_id <> subject_user_id) OR (origin = 'closure_fanout' AND requester_user_id IS NULL)) IS TRUE");
                    table.CheckConstraint("ck_data_subject_requests_result", "CASE WHEN jsonb_typeof(result) = 'object'\n    THEN NOT jsonb_path_exists(result,\n        '$.keyvalue() ? (!(@.key like_regex \"^(consent_evidence|invitation_record|invitation_envelope|audit_actor_reference|audit_entity_reference|audit_metadata_reference|unscoped_audit_rows|transport_evidence|execution_evidence|provider_event_record|idempotency_cache|attribution_reference|federation_actor_reference|request_receipt|external_copy|unserved_group)$\") || @.value.type() != \"number\" || @.value < 0 || @.value > 9007199254740991 || @.value.floor() != @.value)')\n    ELSE false END");
                    table.CheckConstraint("ck_data_subject_requests_snapshot", "CASE WHEN jsonb_typeof(membership_snapshot) = 'array'\n    THEN jsonb_array_length(membership_snapshot) BETWEEN 1 AND 256\n        AND NOT jsonb_path_exists(membership_snapshot,\n            '$[*] ? (@.type() != \"string\" || !(@ like_regex \"^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$\"))')\n    ELSE false END");
                    table.CheckConstraint("ck_data_subject_requests_status", "((kind = 'export' AND status IN ('requested','fulfilling','completed','failed','cancelled'))\nOR (kind = 'erasure' AND status IN ('scheduled','fulfilling','awaiting_privileged_step','completed','blocked','failed','cancelled'))\nOR (kind = 'closure' AND status IN ('scheduled','fulfilling','completed','cancelled'))) IS TRUE");
                    table.CheckConstraint("ck_data_subject_requests_tenant", "tenant_id NOT IN ('00000000-0000-0000-0000-000000000000'::uuid,'00000000-0000-7000-8000-000000000002'::uuid)");
                    table.CheckConstraint("ck_data_subject_requests_times", "(isfinite(created_at) AND (updated_at IS NULL OR (isfinite(updated_at) AND updated_at >= created_at)) AND (execute_at IS NULL OR isfinite(execute_at))) IS TRUE");
                    table.CheckConstraint("ck_data_subject_requests_window", "((kind IN ('erasure','closure') AND origin <> 'closure_fanout' AND execute_at = created_at + interval '24 hours') OR ((kind = 'export' OR origin = 'closure_fanout') AND execute_at IS NULL)) IS TRUE");
                    table.ForeignKey(
                        name: "fk_data_subject_requests_requester",
                        column: x => x.requester_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_data_subject_requests_subject",
                        column: x => x.subject_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "data_export_artifacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    byte_count = table.Column<long>(type: "bigint", nullable: false),
                    chunk_count = table.Column<int>(type: "integer", nullable: false),
                    key_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    purged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
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
                    table.PrimaryKey("pk_data_export_artifacts", x => x.id);
                    table.UniqueConstraint("ux_data_export_artifacts_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_data_export_artifacts_identifiers", "(uuid_extract_version(id)=7 AND uuid_extract_version(subject_user_id)=7 AND subject_user_id <> '00000000-0000-7000-8000-000000000001'::uuid) IS TRUE");
                    table.CheckConstraint("ck_data_export_artifacts_key_version", "key_version COLLATE \"C\" ~ '^[A-Za-z0-9_-]{1,32}$'");
                    table.CheckConstraint("ck_data_export_artifacts_lifecycle", "((status IN ('revoked','purged')) = (purged_at IS NOT NULL) AND (purged_at IS NULL OR (isfinite(purged_at) AND purged_at >= created_at))) IS TRUE");
                    table.CheckConstraint("ck_data_export_artifacts_no_delete", "deleted_at IS NULL AND deleted_by IS NULL");
                    table.CheckConstraint("ck_data_export_artifacts_size", "byte_count BETWEEN 1 AND 67108864 AND chunk_count BETWEEN 1 AND 512 AND chunk_count = (byte_count + 131071) / 131072");
                    table.CheckConstraint("ck_data_export_artifacts_status", "status IN ('available','revoked','expired','purged')");
                    table.CheckConstraint("ck_data_export_artifacts_tenant", "tenant_id NOT IN ('00000000-0000-0000-0000-000000000000'::uuid,'00000000-0000-7000-8000-000000000002'::uuid)");
                    table.CheckConstraint("ck_data_export_artifacts_times", "(isfinite(created_at) AND expires_at = created_at + interval '7 days' AND (updated_at IS NULL OR (isfinite(updated_at) AND updated_at >= created_at))) IS TRUE");
                    table.ForeignKey(
                        name: "fk_data_export_artifacts_request",
                        columns: x => new { x.tenant_id, x.request_id },
                        principalTable: "data_subject_requests",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "data_export_tickets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    session_binding_digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_export_tickets", x => x.id);
                    table.UniqueConstraint("ux_data_export_tickets_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_data_export_tickets_digests", "octet_length(digest) = 32 AND octet_length(session_binding_digest) = 32");
                    table.CheckConstraint("ck_data_export_tickets_identifiers", "(uuid_extract_version(id)=7 AND uuid_extract_version(created_by)=7 AND created_by <> '00000000-0000-7000-8000-000000000001'::uuid) IS TRUE");
                    table.CheckConstraint("ck_data_export_tickets_tenant", "tenant_id NOT IN ('00000000-0000-0000-0000-000000000000'::uuid,'00000000-0000-7000-8000-000000000002'::uuid)");
                    table.CheckConstraint("ck_data_export_tickets_times", "(isfinite(created_at) AND expires_at = created_at + interval '15 minutes' AND (consumed_at IS NULL OR (consumed_at >= created_at AND consumed_at < expires_at))) IS TRUE");
                    table.ForeignKey(
                        name: "fk_data_export_tickets_artifact",
                        columns: x => new { x.tenant_id, x.artifact_id },
                        principalTable: "data_export_artifacts",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_data_export_artifacts_tenant_id_organization_id",
                table: "data_export_artifacts",
                columns: new[] { "tenant_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ux_data_export_artifacts_available",
                table: "data_export_artifacts",
                columns: new[] { "tenant_id", "organization_id", "subject_user_id" },
                unique: true,
                filter: "status = 'available'")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ux_data_export_artifacts_request",
                table: "data_export_artifacts",
                columns: new[] { "tenant_id", "request_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_data_export_tickets_tenant_id_artifact_id",
                table: "data_export_tickets",
                columns: new[] { "tenant_id", "artifact_id" });

            migrationBuilder.CreateIndex(
                name: "ix_data_export_tickets_tenant_id_organization_id",
                table: "data_export_tickets",
                columns: new[] { "tenant_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ux_data_export_tickets_digest",
                table: "data_export_tickets",
                columns: new[] { "tenant_id", "digest" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_data_subject_requests_requester_user_id",
                table: "data_subject_requests",
                column: "requester_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_data_subject_requests_subject_user_id",
                table: "data_subject_requests",
                column: "subject_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_data_subject_requests_tenant_id_organization_id",
                table: "data_subject_requests",
                columns: new[] { "tenant_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ux_data_subject_requests_active",
                table: "data_subject_requests",
                columns: new[] { "tenant_id", "organization_id", "subject_user_id", "kind" },
                unique: true,
                filter: "status IN ('requested','scheduled','fulfilling','awaiting_privileged_step')")
                .Annotation("Npgsql:NullsDistinct", false);

            DataRequestStorageDdl.Install(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Forward-only (ADR-0107 § 6): requests, artifacts and tickets are retained receipts
            // (`request_receipt`) whose retention and purge belong to Phase 11, so a reversal cannot
            // silently drop the evidence that a person asked and what was done.
            throw new NotSupportedException("Data request storage requires a reviewed forward migration.");
        }
    }
}
