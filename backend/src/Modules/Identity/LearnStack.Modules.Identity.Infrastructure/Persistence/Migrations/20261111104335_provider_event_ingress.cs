using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class provider_event_ingress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "identity_provider_event_targets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    authority_id = table.Column<Guid>(type: "uuid", nullable: false),
                    realm_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    provider_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    affected_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    source_occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source_kind = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    claim_token = table.Column<Guid>(type: "uuid", nullable: true),
                    claim_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    enqueued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_identity_provider_event_targets", x => x.id);
                    table.CheckConstraint("ck_identity_provider_event_targets_claim", "(claim_token IS NULL)=(claim_until IS NULL) AND attempts >= 0");
                    table.CheckConstraint("ck_identity_provider_event_targets_ids", "(uuid_extract_version(id)=7 AND uuid_extract_version(event_id)=7 AND uuid_extract_version(receipt_id)=7) IS TRUE");
                    table.CheckConstraint("ck_identity_provider_event_targets_subject", "octet_length(subject_key)=32 AND affected_user_id <> '00000000-0000-7000-8000-000000000001'::uuid");
                    table.CheckConstraint("ck_identity_provider_event_targets_tenant", "tenant_id NOT IN ('00000000-0000-0000-0000-000000000000'::uuid,'00000000-0000-7000-8000-000000000002'::uuid)");
                    table.CheckConstraint("ck_identity_provider_event_targets_time", "isfinite(received_at) AND isfinite(source_occurred_at) AND (claim_until IS NULL OR isfinite(claim_until)) AND (enqueued_at IS NULL OR isfinite(enqueued_at))");
                });

            migrationBuilder.CreateIndex(
                name: "ix_identity_provider_event_targets_pending",
                table: "identity_provider_event_targets",
                columns: new[] { "received_at", "id" },
                filter: "enqueued_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_identity_provider_event_targets_subject_id",
                table: "identity_provider_event_targets",
                columns: new[] { "affected_user_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_identity_provider_event_targets_tenant_id",
                table: "identity_provider_event_targets",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ux_identity_provider_event_targets_event",
                table: "identity_provider_event_targets",
                column: "event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_identity_provider_event_targets_source_tenant",
                table: "identity_provider_event_targets",
                columns: new[] { "authority_id", "realm_id", "provider_event_id", "tenant_id" },
                unique: true);
            ProviderEventStorageDdl.Install(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException("Retained provider evidence requires reviewed forward repair.");
    }
}
