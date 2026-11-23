using LearnStack.Modules.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations;

/// <summary>
/// ADR-0107 §§ 7 and 8: the request guard's application arms refuse every write of a closure's row but one that keeps it
/// scheduled or cancels it, so a begun closure is written by the platform's arrows alone, and the INSERT arm asks the
/// subject's status only after the snapshot.
/// </summary>
[DbContext(typeof(IdentityDbContext))]
[Migration("20261123130000_harden_closure_request_guard")]
public sealed class harden_closure_request_guard : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => AccountClosureStorageDdl.InstallHardening(migrationBuilder);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        // Forward-only, like every recent Identity head: reopening a begun closure to the application role would let a write
        // move the Begin moment Finalize reads; a change to the guard is a reviewed forward migration.
        throw new NotSupportedException("The closure request guard hardening requires a reviewed forward migration.");
}
