using LearnStack.Modules.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations;

[DbContext(typeof(IdentityDbContext))]
[Migration("20261122200000_grant_platform_data_request_operations")]
public sealed class grant_platform_data_request_operations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => DataRequestOperatorDdl.Install(migrationBuilder);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        // Forward-only (ADR-0107 § 8): the platform arm and the artifact read are retired by a reviewed forward
        // migration, so a reversal cannot strand an erasure awaiting the privileged step with no writer to complete it.
        throw new NotSupportedException("The platform data-request operations require a reviewed forward migration.");
}
