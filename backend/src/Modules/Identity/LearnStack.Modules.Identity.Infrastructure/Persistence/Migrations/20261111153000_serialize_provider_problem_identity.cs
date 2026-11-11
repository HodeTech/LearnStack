using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations;

[DbContext(typeof(IdentityDbContext))]
[Migration("20261111153000_serialize_provider_problem_identity")]
public sealed class serialize_provider_problem_identity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(ProviderEventProblemIdentityDdl.ReplaceAcceptance);
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Provider problem identity serialization requires a forward repair.");
}
