using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations;

[DbContext(typeof(IdentityDbContext))]
[Migration("20261111150000_reconcile_provider_window_problems")]
public sealed class reconcile_provider_window_problems : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(ProviderEventWindowResolutionDdl.ReplaceAcceptance);
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Provider window reconciliation requires a forward repair.");
}
