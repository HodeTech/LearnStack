using LearnStack.Modules.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations;

[DbContext(typeof(IdentityDbContext))]
[Migration("20261120160000_grant_platform_federation_projection")]
public sealed class grant_platform_federation_projection : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => FederationOperatorProjectionDdl.Add(migrationBuilder);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        // Forward-only (ADR-0099 Amendment 11): a grant a reviewed forward migration retires, so a reversal cannot
        // silently leave the operator's report reading a table it no longer has the right to.
        throw new NotSupportedException("The platform federation projection grant requires a reviewed forward migration.");
}
