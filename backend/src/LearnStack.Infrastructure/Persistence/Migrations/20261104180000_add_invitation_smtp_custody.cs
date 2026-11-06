using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Infrastructure.Persistence.Migrations;

public sealed partial class AddInvitationSmtpCustody : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(InvitationSmtpStorageDdl.Sql);
    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("Provider custody requires a reviewed forward migration.");
}
