using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class add_invitation_protected_receipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE public.idempotency_keys DROP CONSTRAINT ck_idempotency_keys_replay_target;
                ALTER TABLE public.idempotency_keys ADD CONSTRAINT ck_idempotency_keys_replay_target CHECK (
                    (replay_target_kind IS NULL AND replay_target_id IS NULL)
                    OR (replay_target_kind IS NOT NULL AND replay_target_id IS NOT NULL
                        AND replay_target_kind IN ('Membership','Role','Organization','Tenant','Invitation','TenantSetting')
                        AND replay_target_id <> '00000000-0000-0000-0000-000000000000'::uuid
                        AND state = 'completed' AND status_code BETWEEN 200 AND 299
                        AND body IS NOT NULL AND octet_length(body) <= 1024));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Retained invitation receipts require a reviewed forward migration.");
        }
    }
}
