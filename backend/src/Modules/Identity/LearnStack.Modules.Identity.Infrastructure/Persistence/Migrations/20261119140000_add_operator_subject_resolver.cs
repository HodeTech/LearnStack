using LearnStack.Modules.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations;

[DbContext(typeof(IdentityDbContext))]
[Migration("20261119140000_add_operator_subject_resolver")]
public sealed class add_operator_subject_resolver : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => OperatorSubjectResolverDdl.Add(migrationBuilder);

    protected override void Down(MigrationBuilder migrationBuilder) => OperatorSubjectResolverDdl.Remove(migrationBuilder);
}
