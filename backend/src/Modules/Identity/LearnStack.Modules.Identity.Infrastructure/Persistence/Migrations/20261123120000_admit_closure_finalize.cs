using LearnStack.Modules.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations;

/// <summary>
/// ADR-0107 §§ 7 and 8: the closure's Finalize completes every open closure of the Closed subject, a never-begun one
/// included, under the same witnesses as the closure it finalizes, and reads a begun closure's Begin moment.
/// </summary>
[DbContext(typeof(IdentityDbContext))]
[Migration("20261123120000_admit_closure_finalize")]
public sealed class admit_closure_finalize : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => AccountClosureStorageDdl.InstallFinalize(migrationBuilder);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        // Forward-only, like every recent Identity head: a closure Finalize completed from scheduled has no way back, and
        // narrowing the arm again could strand a closure behind a Closed account; a change to it is a reviewed forward
        // migration.
        throw new NotSupportedException("The closure finalize storage requires a reviewed forward migration.");
}
