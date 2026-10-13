using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Modules.Audit.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class grant_outbox_terminal_audit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ADR-0046: the confined terminal writer shares the dispatcher's
            // transaction. The role receives no audit read/update/delete access.
            migrationBuilder.Sql("GRANT INSERT ON audit_log TO learnstack_outbox_admin;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Disposable-schema reversal only; live recovery uses a forward fix.
            migrationBuilder.Sql("REVOKE INSERT ON audit_log FROM learnstack_outbox_admin;");
        }
    }
}
