using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class membership_revision_pinned_custom_fields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "custom_fields",
                table: "membership_profiles",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddCheckConstraint(
                name: "ck_membership_profiles_custom_fields_shape",
                table: "membership_profiles",
                sql: "CASE WHEN jsonb_typeof(custom_fields) = 'array' THEN jsonb_array_length(custom_fields) <= 20 ELSE false END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_membership_profiles_custom_fields_shape",
                table: "membership_profiles");

            migrationBuilder.DropColumn(
                name: "custom_fields",
                table: "membership_profiles");
        }
    }
}
