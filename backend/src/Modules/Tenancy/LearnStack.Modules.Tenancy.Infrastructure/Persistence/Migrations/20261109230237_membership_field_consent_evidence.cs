using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class membership_field_consent_evidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "membership_field_consents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    definition_version = table.Column<int>(type: "integer", nullable: false),
                    purpose_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    notice_locale = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: false),
                    notice_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    predecessor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    predecessor_sequence = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    row_version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_membership_field_consents", x => x.id);
                    table.CheckConstraint("ck_membership_field_consents_actor", "actor_user_id = subject_user_id AND created_by = actor_user_id");
                    table.CheckConstraint("ck_membership_field_consents_immutable_audit", "updated_at IS NULL AND updated_by IS NULL AND deleted_at IS NULL AND deleted_by IS NULL AND row_version = 0");
                    table.CheckConstraint("ck_membership_field_consents_pins", "definition_version > 0 AND length(purpose_key) BETWEEN 1 AND 64 AND purpose_key ~ '^[a-z0-9-]+$' AND length(notice_locale) BETWEEN 1 AND 35 AND notice_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_membership_field_consents_sequence", "sequence BETWEEN 1 AND 9007199254740991 AND predecessor_sequence = sequence - 1 AND ((predecessor_sequence = 0 AND predecessor_id IS NULL) OR (predecessor_sequence > 0 AND predecessor_id IS NOT NULL))");
                    table.CheckConstraint("ck_membership_field_consents_state", "state IN ('Granted','Withdrawn')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_membership_field_consents_subject_membership",
                table: "membership_field_consents",
                columns: new[] { "subject_user_id", "membership_id" });

            migrationBuilder.CreateIndex(
                name: "ix_membership_field_consents_tenant_organization",
                table: "membership_field_consents",
                columns: new[] { "tenant_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ux_membership_field_consents_tuple_sequence",
                table: "membership_field_consents",
                columns: new[] { "tenant_id", "organization_id", "membership_id", "subject_user_id", "definition_id", "definition_version", "purpose_key", "sequence" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
            MembershipFieldConsentDdl.Install(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException("Retained consent evidence requires a reviewed forward migration.");

    }
}
