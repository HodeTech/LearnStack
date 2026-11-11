using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations;

[DbContext(typeof(IdentityDbContext))]
[Migration("20261111143000_index_provider_problem_health")]
public sealed class index_provider_problem_health : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE INDEX ix_identity_provider_event_problems_health
        ON public.identity_provider_event_problems(reason,range_from) WHERE resolved_at IS NULL
        """);
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Provider health indexing requires a forward repair.");
}
