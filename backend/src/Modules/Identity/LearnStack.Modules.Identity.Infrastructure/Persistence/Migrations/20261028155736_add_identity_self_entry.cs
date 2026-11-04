using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class add_identity_self_entry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            IdentityEntryStorageDdl.Install(migrationBuilder);
            migrationBuilder.CreateIndex(
                name: "ix_memberships_self_active_user_id_id",
                table: "memberships",
                columns: new[] { "user_id", "id" },
                filter: "status = 'Active' AND deleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            IdentityEntryStorageDdl.Remove(migrationBuilder);
            migrationBuilder.DropIndex(
                name: "ix_memberships_self_active_user_id_id",
                table: "memberships");
        }
    }
}
