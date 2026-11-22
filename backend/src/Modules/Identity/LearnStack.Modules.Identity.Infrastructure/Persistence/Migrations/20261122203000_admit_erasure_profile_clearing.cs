using LearnStack.Modules.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations;

/// <summary>
/// ADR-0107 § 5: the contextual mutation guard admits a clearing UPDATE of a Revoked Membership's profile under the
/// erasure witness, the one database exception a tenant erasure needs.
/// </summary>
[DbContext(typeof(IdentityDbContext))]
[Migration("20261122203000_admit_erasure_profile_clearing")]
public sealed class admit_erasure_profile_clearing : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => ContextualIdentityStorageDdl.AdmitErasureProfileClearing(migrationBuilder);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        // Forward-only, like every recent Identity head: an erasure that cleared a Revoked profile under this exception
        // cannot be undone by narrowing the guard again, so a change to it is a reviewed forward migration.
        throw new NotSupportedException("The erasure profile clearing requires a reviewed forward migration.");
}
