using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class align_outbox_recovery_index : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ADR-0046 requires the same total selection order for both recovery
            // branches. Keep ordinary unleased Pending backlog outside the index.
            migrationBuilder.Sql("""
                DROP INDEX ix_outbox_messages_recovery;
                CREATE INDEX ix_outbox_messages_recovery
                    ON outbox_messages (available_after, occurred_at, id)
                    WHERE processed_at IS NULL AND dead_lettered_at IS NULL
                      AND terminal_reason IS NULL AND (locked_by IS NOT NULL OR attempts >= 6);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX ix_outbox_messages_recovery;
                CREATE INDEX ix_outbox_messages_recovery
                    ON outbox_messages (locked_until, occurred_at, id)
                    WHERE processed_at IS NULL AND dead_lettered_at IS NULL
                      AND terminal_reason IS NULL AND locked_by IS NOT NULL;
                """);
        }
    }
}
