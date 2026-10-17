using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class add_background_job_retirement_index : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE INDEX ix_background_jobs_retirement ON background_jobs (available_after, created_at, id)
                    WHERE processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX ix_background_jobs_retirement;");
        }
    }
}
