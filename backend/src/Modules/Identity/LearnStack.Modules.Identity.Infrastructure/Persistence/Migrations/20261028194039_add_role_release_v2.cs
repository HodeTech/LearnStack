using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class add_role_release_v2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "adopted_release_fingerprint",
                table: "roles",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "ba16d2c21f2547f3dc7f9dce24e25a18f30103186739ea00a33e9edf43834794");

            migrationBuilder.AddColumn<int>(
                name: "adopted_release_version",
                table: "roles",
                type: "integer",
                nullable: false,
                defaultValue: 1);
            RoleReleaseDdl.Install(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Forward-only: dropping retained release evidence would permit a
            // previously adopted Role to receive the initial exception again.
            throw new NotSupportedException("Role release adoption requires a reviewed forward migration.");
        }
    }
}
