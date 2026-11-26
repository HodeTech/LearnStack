using LearnStack.Modules.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations;

/// <summary>
/// ADR-0107 §§ 7 and 9: the delayed tenant mirror of a provider fact reads the affected person's status in the database,
/// under the person's shared User fence, so a delivery after a closure began refuses the person.
/// </summary>
[DbContext(typeof(IdentityDbContext))]
[Migration("20261123110000_refuse_closing_provider_event_delivery")]
public sealed class refuse_closing_provider_event_delivery : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => ProviderEventSubjectStatusDdl.Install(migrationBuilder);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        // Forward-only, like every recent Identity head: removing the definer would let a delayed mirror write a closed
        // person's observation again; a change to it is a reviewed forward migration.
        throw new NotSupportedException("The provider event subject status requires a reviewed forward migration.");
}
