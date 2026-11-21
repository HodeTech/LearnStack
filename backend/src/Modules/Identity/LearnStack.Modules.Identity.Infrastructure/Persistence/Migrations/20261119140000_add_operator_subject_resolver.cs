using LearnStack.Modules.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations;

[DbContext(typeof(IdentityDbContext))]
[Migration("20261119140000_add_operator_subject_resolver")]
public sealed class add_operator_subject_resolver : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => OperatorSubjectResolverDdl.Add(migrationBuilder);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        // Forward-only (ADR-0102): like every recent Identity head, the resolver is retired by a reviewed forward
        // migration, so a reversal cannot silently remove the confined lookup an operator act depends on.
        throw new NotSupportedException("Operator subject resolution requires a reviewed forward migration.");
}
