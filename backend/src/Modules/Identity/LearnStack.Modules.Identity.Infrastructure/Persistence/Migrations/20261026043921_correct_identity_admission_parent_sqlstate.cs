using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class correct_identity_admission_parent_sqlstate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The admission trigger runs before the mutation guard for assignment
            // inserts. Keep its missing/hidden parent refusal identical to that guard.
            ContextualIdentityStorageDdl.CorrectAdmissionParentMirrorSqlState(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ContextualIdentityStorageDdl.RestoreLegacyAdmissionParentMirrorSqlState(migrationBuilder);
        }
    }
}
