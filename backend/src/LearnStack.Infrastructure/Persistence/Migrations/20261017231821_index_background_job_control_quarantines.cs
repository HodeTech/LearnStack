using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class index_background_job_control_quarantines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE INDEX ix_background_job_recurrences_quarantined ON background_job_recurrences (recurrence_key, revision)
                    WHERE quarantined_at IS NOT NULL;
                CREATE INDEX ix_background_job_occurrences_quarantined ON background_job_occurrences (id)
                    WHERE quarantined_at IS NOT NULL;
                CREATE INDEX ix_background_job_recipients_quarantined ON background_job_recipients (id)
                    WHERE quarantined_at IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX ix_background_job_recipients_quarantined;
                DROP INDEX ix_background_job_occurrences_quarantined;
                DROP INDEX ix_background_job_recurrences_quarantined;
                """);
        }
    }
}
