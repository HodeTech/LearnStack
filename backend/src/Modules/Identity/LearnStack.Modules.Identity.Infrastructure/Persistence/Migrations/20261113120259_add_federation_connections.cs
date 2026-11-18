using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class add_federation_connections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "federation_connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    protocol = table.Column<string>(type: "text", nullable: false),
                    alias = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false, collation: "C"),
                    display_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    issuer_or_entity_id = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false, collation: "C"),
                    state = table.Column<string>(type: "text", nullable: false),
                    source_kind = table.Column<string>(type: "text", nullable: false),
                    client_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true, collation: "C"),
                    configuration_revision = table.Column<long>(type: "bigint", nullable: false),
                    configuration_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    desired_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    last_provider_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    pending_operation_kind = table.Column<string>(type: "text", nullable: true),
                    pending_operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    pending_actor = table.Column<Guid>(type: "uuid", nullable: true),
                    pending_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    pending_quiet_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    pending_desired_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    test_attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    test_actor = table.Column<Guid>(type: "uuid", nullable: true),
                    test_revision = table.Column<long>(type: "bigint", nullable: true),
                    test_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    test_result = table.Column<string>(type: "text", nullable: true),
                    test_passed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
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
                    table.PrimaryKey("pk_federation_connections", x => x.id);
                    table.UniqueConstraint("ux_federation_connections_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_federation_connections_actors", "(uuid_extract_version(created_by)=7 AND created_by <> '00000000-0000-7000-8000-000000000001'::uuid AND (updated_by IS NULL OR (uuid_extract_version(updated_by)=7 AND updated_by <> '00000000-0000-7000-8000-000000000001'::uuid)) AND (pending_actor IS NULL OR (uuid_extract_version(pending_actor)=7 AND pending_actor <> '00000000-0000-7000-8000-000000000001'::uuid)) AND (test_actor IS NULL OR (uuid_extract_version(test_actor)=7 AND test_actor <> '00000000-0000-7000-8000-000000000001'::uuid)) AND (deleted_by IS NULL OR (uuid_extract_version(deleted_by)=7 AND deleted_by <> '00000000-0000-7000-8000-000000000001'::uuid))) IS TRUE");
                    table.CheckConstraint("ck_federation_connections_alias", "alias = 'lsf-' || replace(id::text, '-', '')");
                    table.CheckConstraint("ck_federation_connections_client", "((protocol <> 'Oidc' OR (client_id IS NOT NULL AND octet_length(client_id) BETWEEN 1 AND 255 AND client_id ~ '^[!-~]+$')) AND (protocol <> 'Saml' OR client_id IS NULL)) IS TRUE");
                    table.CheckConstraint("ck_federation_connections_digests", "(configuration_digest ~ '^[0-9a-f]{64}$' AND desired_digest ~ '^[0-9a-f]{64}$' AND (last_provider_digest IS NULL OR last_provider_digest ~ '^[0-9a-f]{64}$') AND (pending_desired_digest IS NULL OR pending_desired_digest ~ '^[0-9a-f]{64}$')) IS TRUE");
                    table.CheckConstraint("ck_federation_connections_display_name", "char_length(display_name) BETWEEN 1 AND 80 AND display_name = btrim(display_name, ' ') AND display_name IS NFC NORMALIZED AND display_name !~ '[\\u0001-\\u001f\\u007f-\\u00a0\\u00ad\\u0600-\\u0605\\u061c\\u06dd\\u070f\\u0890\\u0891\\u08e2\\u1680\\u180e\\u2000-\\u200f\\u2028-\\u202f\\u205f-\\u206f\\u3000\\ufeff\\ue000-\\uf8ff\\ufff9-\\ufffb\\U000110bd\\U000110cd\\U00013430-\\U0001343f\\U0001bca0-\\U0001bca3\\U0001d173-\\U0001d17a\\U000e0001\\U000e0020-\\U000e007f\\U000f0000-\\U0010ffff\\u115f\\u1160\\u2800\\u3164\\uffa0]' AND display_name !~ '^[\\u0300-\\u036f\\u0483-\\u0489\\u1ab0-\\u1aff\\u1dc0-\\u1dff\\u20d0-\\u20ff\\u302a-\\u302f\\u3099\\u309a\\ufe00-\\ufe0f\\ufe20-\\ufe2f\\U000e0100-\\U000e01ef]'");
                    table.CheckConstraint("ck_federation_connections_identifiers", "(uuid_extract_version(id)=7 AND (pending_operation_id IS NULL OR uuid_extract_version(pending_operation_id)=7) AND (test_attempt_id IS NULL OR uuid_extract_version(test_attempt_id)=7)) IS TRUE");
                    table.CheckConstraint("ck_federation_connections_issuer", "octet_length(issuer_or_entity_id) BETWEEN 1 AND 2048 AND issuer_or_entity_id ~ '^[!-~]+$' AND (protocol <> 'Oidc' OR (issuer_or_entity_id !~ '[?#]' AND issuer_or_entity_id !~ '^[^/]*//[^/]*@'))");
                    table.CheckConstraint("ck_federation_connections_pending", "((pending_operation_kind IS NULL AND pending_operation_id IS NULL AND pending_actor IS NULL AND pending_started_at IS NULL\n    AND pending_quiet_until IS NULL AND pending_desired_digest IS NULL)\nOR (pending_operation_kind IN ('Configure','RotateSecret','Rename','TestStart','Disable','Delete')\n    AND pending_operation_id IS NOT NULL AND pending_actor IS NOT NULL AND pending_started_at IS NOT NULL\n    AND pending_quiet_until IS NOT NULL AND pending_desired_digest IS NOT NULL\n    AND isfinite(pending_started_at) AND pending_quiet_until = pending_started_at + interval '90 seconds'\n    AND state <> 'Deleted')) IS TRUE");
                    table.CheckConstraint("ck_federation_connections_protocol", "protocol IN ('Oidc','Saml')");
                    table.CheckConstraint("ck_federation_connections_revision", "configuration_revision >= 1");
                    table.CheckConstraint("ck_federation_connections_source_kind", "source_kind IN ('MetadataUrl','DiscoveryUrl','Manual')");
                    table.CheckConstraint("ck_federation_connections_state", "state IN ('Draft','TestOpen','TestPassed','Active','Disabled','Deleted')");
                    table.CheckConstraint("ck_federation_connections_tenant", "tenant_id NOT IN ('00000000-0000-0000-0000-000000000000'::uuid,'00000000-0000-7000-8000-000000000002'::uuid)");
                    table.CheckConstraint("ck_federation_connections_test", "((state IN ('TestOpen','TestPassed') AND test_attempt_id IS NOT NULL AND test_actor IS NOT NULL AND test_revision IS NOT NULL\n    AND test_revision = configuration_revision AND test_expires_at IS NOT NULL AND isfinite(test_expires_at)\n    AND ((state='TestOpen' AND test_result='Pending' AND test_passed_at IS NULL)\n        OR (state='TestPassed' AND test_result='Passed' AND test_passed_at IS NOT NULL\n            AND test_expires_at = test_passed_at + interval '10 minutes')))\nOR (state NOT IN ('TestOpen','TestPassed') AND test_attempt_id IS NULL AND test_actor IS NULL AND test_revision IS NULL\n    AND test_expires_at IS NULL AND test_result IS NULL AND test_passed_at IS NULL)) IS TRUE");
                    table.CheckConstraint("ck_federation_connections_tombstone", "((state='Deleted' AND deleted_at IS NOT NULL AND deleted_by IS NOT NULL) OR (state<>'Deleted' AND deleted_at IS NULL AND deleted_by IS NULL)) IS TRUE");
                });

            migrationBuilder.CreateIndex(
                name: "ux_federation_connections_tenant_live",
                table: "federation_connections",
                column: "tenant_id",
                unique: true,
                filter: "deleted_at IS NULL");

            FederationConnectionStorageDdl.Install(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Forward-only (ADR-0099): connection rows and tombstones reserve aliases a provider may still hold.
            throw new NotSupportedException("Federation connection storage requires a reviewed forward migration.");
        }
    }
}
