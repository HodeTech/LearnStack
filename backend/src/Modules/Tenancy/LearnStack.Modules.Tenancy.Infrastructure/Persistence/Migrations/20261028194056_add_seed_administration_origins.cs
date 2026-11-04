using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class add_seed_administration_origins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "adopted_seed_origin_version",
                table: "tenants",
                type: "integer",
                nullable: false,
                defaultValue: 0);
            SeedAdministrationOriginDdl.Install(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Forward-only: origin adoption is monotonic and cannot be reset by
            // removing its evidence while keeping a live host projection.
            throw new NotSupportedException("Seed origin adoption requires a reviewed forward migration.");
        }
    }
}
