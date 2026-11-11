using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations;

[DbContext(typeof(IdentityDbContext))]
[Migration("20261111180000_classify_retained_provider_collision")]
public sealed class classify_retained_provider_collision : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(ProviderEventReceiptCollisionDdl.ReplaceAcceptance);
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Provider receipt collision classification requires a forward repair.");
}
