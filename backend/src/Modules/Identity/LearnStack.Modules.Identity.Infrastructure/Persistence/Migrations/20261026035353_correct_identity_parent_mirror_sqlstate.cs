using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class correct_identity_parent_mirror_sqlstate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Preserve the applied schema and function permissions while aligning
            // only missing/mismatched Membership parent scope with Standards 05.
            ContextualIdentityStorageDdl.CorrectParentMirrorSqlState(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ContextualIdentityStorageDdl.RestoreLegacyParentMirrorSqlState(migrationBuilder);
        }
    }
}
