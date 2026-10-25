using System;
using LearnStack.Infrastructure.Audit;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class create_identity_schema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "identity_authority",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    authority_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issuer = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_identity_authority", x => x.id);
                    table.UniqueConstraint("ux_identity_authority_authority_id", x => x.authority_id);
                    table.CheckConstraint("ck_identity_authority_assigned", "authority_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_identity_authority_singleton", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    authority_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    provider_subject = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    creation_audit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    creation_audit_timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
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
                    table.PrimaryKey("pk_users", x => x.id);
                    table.CheckConstraint("ck_users_actor", "uuid_extract_version(id) = 7 AND id <> '00000000-0000-7000-8000-000000000001'::uuid");
                    table.CheckConstraint("ck_users_no_soft_delete", "deleted_at IS NULL AND deleted_by IS NULL");
                    table.CheckConstraint("ck_users_status", "status IN ('Active', 'Closing', 'Closed')");
                    table.CheckConstraint("ck_users_subject_digest", "provider_subject IS NULL OR subject_key = sha256(int4send(octet_length(convert_to('learnstack.identity.subject.v1','UTF8'))) || convert_to('learnstack.identity.subject.v1','UTF8') || int4send(16) || uuid_send(authority_id) || int4send(octet_length(convert_to(provider_subject,'UTF8'))) || convert_to(provider_subject,'UTF8'))");
                    table.CheckConstraint("ck_users_subject_key", "octet_length(subject_key) = 32");
                    table.CheckConstraint("ck_users_subject_lifecycle", "(status = 'Active' AND provider_subject IS NOT NULL AND length(provider_subject) BETWEEN 1 AND 255) OR (status = 'Closing' AND (provider_subject IS NULL OR length(provider_subject) BETWEEN 1 AND 255)) OR (status = 'Closed' AND provider_subject IS NULL)");
                    table.ForeignKey(
                        name: "fk_users_identity_authority",
                        column: x => x.authority_id,
                        principalTable: "identity_authority",
                        principalColumn: "authority_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_users_authority_id_subject_key",
                table: "users",
                columns: new[] { "authority_id", "subject_key" },
                unique: true);

            IdentityStorageDdl.Install(migrationBuilder);
            IdentityCreationWitnessInstaller.Install(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Disposable-schema reversal only. Live recovery is forward-only;
            // this is not an admitted binding-retirement operation.
            IdentityCreationWitnessInstaller.Remove(migrationBuilder);
            IdentityStorageDdl.Remove(migrationBuilder);
            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "identity_authority");
        }
    }
}
