using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Modules.Education.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class add_course_content_access : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ADD COLUMN's default backfills legacy rows without a row UPDATE or
            // an RLS bypass. Publication, translations, pins and scope stay intact.
            migrationBuilder.AddColumn<string>(
                name: "content_access",
                table: "courses",
                type: "text",
                nullable: false,
                defaultValue: "enrollment_required");

            migrationBuilder.AddCheckConstraint(
                name: "ck_courses_content_access",
                table: "courses",
                sql: "content_access IN ('public', 'enrollment_required')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Technical reversal for disposable migration tests only. A live
            // rollback to ADR-0048 readers would expose restricted content; keep
            // this column or stop public Education reads (ADR-0050).
            migrationBuilder.DropCheckConstraint(
                name: "ck_courses_content_access",
                table: "courses");

            migrationBuilder.DropColumn(
                name: "content_access",
                table: "courses");
        }
    }
}
