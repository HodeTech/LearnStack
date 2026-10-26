using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class serialize_identity_parent_lifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ContextualIdentityStorageDdl.SerializeParentLifecycle(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ContextualIdentityStorageDdl.RestoreLegacyParentLifecycleLock(migrationBuilder);
        }
    }
}
