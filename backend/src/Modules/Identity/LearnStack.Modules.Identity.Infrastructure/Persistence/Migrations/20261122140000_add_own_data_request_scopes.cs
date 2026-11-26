using LearnStack.Modules.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations;

[DbContext(typeof(IdentityDbContext))]
[Migration("20261122140000_add_own_data_request_scopes")]
public sealed class add_own_data_request_scopes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => OwnDataRequestScopeDdl.Install(migrationBuilder);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        // Forward-only (ADR-0106 § 9), like every recent Identity head: the confined definer and its two owner arms
        // are retired by a reviewed forward migration, so a reversal cannot strand the retained own-scope path.
        throw new NotSupportedException("The retained own data-request scope requires a reviewed forward migration.");
}
