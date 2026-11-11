using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations;

[DbContext(typeof(IdentityDbContext))]
[Migration("20261111123000_reconcile_provider_event_receipt_replay")]
public sealed class reconcile_provider_event_receipt_replay : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(ProviderEventReceiptReplayDdl.ReplaceAcceptance);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Retained receipt reconciliation requires a forward repair.");
}
