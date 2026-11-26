using LearnStack.Modules.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations;

/// <summary>
/// ADR-0107 §§ 7 and 8: the platform role's closed-column grants, the one <c>users</c> trigger exception, the request
/// guard's closure arrows and the INSERT arm's User status witness that a global account closure needs.
/// </summary>
[DbContext(typeof(IdentityDbContext))]
[Migration("20261123100000_admit_account_closure")]
public sealed class admit_account_closure : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => AccountClosureStorageDdl.Install(migrationBuilder);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        // Forward-only, like every recent Identity head: a User a closure made Closing or Closed has no way back, so
        // narrowing the exception again could strand a closure half done; a change to it is a reviewed forward migration.
        throw new NotSupportedException("The account closure storage requires a reviewed forward migration.");
}
