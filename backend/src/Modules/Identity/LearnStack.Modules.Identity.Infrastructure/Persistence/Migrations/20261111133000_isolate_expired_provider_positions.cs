using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations;

[DbContext(typeof(IdentityDbContext))]
[Migration("20261111133000_isolate_expired_provider_positions")]
public sealed class isolate_expired_provider_positions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(ProviderEventWindowDdl.ReplaceAcceptance);
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Provider poison-position isolation requires a forward repair.");
}
