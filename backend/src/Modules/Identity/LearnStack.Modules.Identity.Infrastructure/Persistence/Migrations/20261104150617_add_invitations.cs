using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class add_invitations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "invitations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recipient_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false, collation: "C"),
                    inviter_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pinned_role_version = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    generation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    generation_created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    verifier_digest = table.Column<byte[]>(type: "bytea", nullable: true),
                    consumed_verifier_digest = table.Column<byte[]>(type: "bytea", nullable: true),
                    entry_origin = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    entry_locale = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: false),
                    current_delivery_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    submission_status = table.Column<string>(type: "text", nullable: false),
                    submission_receipt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    accepted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    result_membership_id = table.Column<Guid>(type: "uuid", nullable: true),
                    acceptance_disposition = table.Column<string>(type: "text", nullable: true),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
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
                    table.PrimaryKey("pk_invitations", x => x.id);
                    table.UniqueConstraint("ux_invitations_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_invitations_generation", "generation > 0 AND pinned_role_version >= 0");
                    table.CheckConstraint("ck_invitations_identifiers", "uuid_extract_version(id)=7 AND uuid_extract_version(generation_id)=7 AND uuid_extract_version(current_delivery_job_id)=7");
                    table.CheckConstraint("ck_invitations_lifecycle", "((status='Pending' AND octet_length(verifier_digest)=32 AND consumed_verifier_digest IS NULL\n    AND accepted_by IS NULL AND result_membership_id IS NULL AND acceptance_disposition IS NULL AND accepted_at IS NULL)\nOR (status='Accepted' AND verifier_digest IS NULL AND octet_length(consumed_verifier_digest)=32\n    AND accepted_by IS NOT NULL AND result_membership_id IS NOT NULL\n    AND acceptance_disposition IN ('Created','AlreadyMember') AND accepted_at IS NOT NULL\n    AND isfinite(accepted_at) AND accepted_at >= generation_created_at AND accepted_at < expires_at)\nOR (status='Revoked' AND verifier_digest IS NULL AND consumed_verifier_digest IS NULL\n    AND accepted_by IS NULL AND result_membership_id IS NULL AND acceptance_disposition IS NULL AND accepted_at IS NULL)) IS TRUE");
                    table.CheckConstraint("ck_invitations_mailbox", "octet_length(recipient_email) BETWEEN 3 AND 254 AND recipient_email=lower(recipient_email COLLATE \"C\") AND recipient_email ~ '^[!-~]+@[!-~]+$'");
                    table.CheckConstraint("ck_invitations_no_delete", "deleted_at IS NULL AND deleted_by IS NULL");
                    table.CheckConstraint("ck_invitations_submission", "((submission_status='Queued' AND submission_receipt_id IS NULL) OR (submission_status IN ('SubmissionAccepted','Failed') AND submission_receipt_id IS NOT NULL AND submission_receipt_id <> '00000000-0000-0000-0000-000000000000'::uuid)) IS TRUE");
                    table.CheckConstraint("ck_invitations_tenant", "tenant_id NOT IN ('00000000-0000-0000-0000-000000000000'::uuid,'00000000-0000-7000-8000-000000000002'::uuid)");
                    table.CheckConstraint("ck_invitations_times", "isfinite(created_at) AND isfinite(generation_created_at) AND isfinite(expires_at) AND generation_created_at >= created_at AND expires_at >= generation_created_at + interval '1 day' AND expires_at <= generation_created_at + interval '30 days'");
                    table.ForeignKey(
                        name: "fk_invitations_acceptor",
                        column: x => x.accepted_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_invitations_inviter",
                        column: x => x.inviter_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_invitations_result_membership",
                        columns: x => new { x.tenant_id, x.result_membership_id },
                        principalTable: "memberships",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_invitations_role",
                        columns: x => new { x.tenant_id, x.role_id },
                        principalTable: "roles",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_invitations_accepted_by",
                table: "invitations",
                column: "accepted_by");

            migrationBuilder.CreateIndex(
                name: "ix_invitations_inviter_user_id",
                table: "invitations",
                column: "inviter_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_invitations_tenant_id_organization_id",
                table: "invitations",
                columns: new[] { "tenant_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_invitations_tenant_id_result_membership_id",
                table: "invitations",
                columns: new[] { "tenant_id", "result_membership_id" });

            migrationBuilder.CreateIndex(
                name: "ix_invitations_tenant_id_role_id",
                table: "invitations",
                columns: new[] { "tenant_id", "role_id" });

            migrationBuilder.CreateIndex(
                name: "ux_invitations_pending_recipient",
                table: "invitations",
                columns: new[] { "tenant_id", "organization_id", "recipient_email" },
                unique: true,
                filter: "status = 'Pending' AND deleted_at IS NULL")
                .Annotation("Npgsql:NullsDistinct", false);

            InvitationStorageDdl.Install(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            InvitationStorageDdl.Remove(migrationBuilder);
            migrationBuilder.DropTable(
                name: "invitations");
        }
    }
}
