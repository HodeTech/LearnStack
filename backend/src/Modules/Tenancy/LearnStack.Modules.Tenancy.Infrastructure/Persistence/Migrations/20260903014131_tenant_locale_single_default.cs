using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class tenant_locale_single_default : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ONE object, additive. The single-default invariant needs a database
            // guarantee because an aggregate invariant does not hold across concurrent
            // transactions: two transactions each promoting a different locale both pass
            // the in-memory guard, and one of them must lose here.
            //
            // The scaffolder ALSO emitted AddForeignKey for fk_tenant_locales_tenant and
            // fk_tenant_feature_flags_tenant, because mapping the two navigations
            // introduced the first relationships into a model that had none. Both
            // constraints already exist — created as raw SQL in the first tenancy
            // migration, where the snapshot cannot see them — so applying those calls
            // fails with "constraint already exists" against any database that has run
            // it. Deleted by hand; the HasConstraintName on each relationship is what
            // keeps the surviving snapshot agreeing with the live schema. This fails at
            // `make migrate`, not at build, so a green local suite would have proved
            // nothing.
            migrationBuilder.CreateIndex(
                name: "ux_tenant_locales_tenant_id_is_default",
                table: "tenant_locales",
                column: "tenant_id",
                unique: true,
                filter: "is_default");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The index and nothing else, matching Up. Dropping either foreign key here
            // would remove a constraint this migration never created.
            migrationBuilder.DropIndex(
                name: "ux_tenant_locales_tenant_id_is_default",
                table: "tenant_locales");
        }
    }
}
