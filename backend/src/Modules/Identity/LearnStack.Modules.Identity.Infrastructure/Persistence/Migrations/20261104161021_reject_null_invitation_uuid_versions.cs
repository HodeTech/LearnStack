using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class reject_null_invitation_uuid_versions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_invitations_identifiers",
                table: "invitations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_invitations_identifiers",
                table: "invitations",
                sql: "(uuid_extract_version(id)=7 AND uuid_extract_version(generation_id)=7 AND uuid_extract_version(current_delivery_job_id)=7) IS TRUE");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_invitations_identifiers",
                table: "invitations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_invitations_identifiers",
                table: "invitations",
                sql: "uuid_extract_version(id)=7 AND uuid_extract_version(generation_id)=7 AND uuid_extract_version(current_delivery_job_id)=7");
        }
    }
}
