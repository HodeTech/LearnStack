using LearnStack.Modules.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations;

[DbContext(typeof(IdentityDbContext))]
[Migration("20261120090000_add_identity_authority_rebind")]
public sealed class add_identity_authority_rebind : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => IdentityAuthorityRebindDdl.Add(migrationBuilder);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        // Forward-only (ADR-0104): like every recent Identity head, the rebind grants and the key trigger are
        // retired by a reviewed forward migration, so a reversal cannot silently leave an issuer writable.
        throw new NotSupportedException("The issuer-rebind storage requires a reviewed forward migration.");
}
