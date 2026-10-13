using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class add_outbox_dispatch_state : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ADR-0046: legacy diagnostics are deliberately not retained. This
            // owner-only, transactional backfill temporarily removes FORCE so
            // it reaches every tenant, then restores it before the migration
            // commits. Invalid legacy identities fail the CHECK without repair.
            migrationBuilder.Sql("""
                ALTER TABLE outbox_messages NO FORCE ROW LEVEL SECURITY;
                UPDATE outbox_messages
                    SET last_error = 'migration:legacy:redacted'
                    WHERE last_error IS NOT NULL;
                ALTER TABLE outbox_messages FORCE ROW LEVEL SECURITY;

                ALTER TABLE outbox_messages
                    ADD COLUMN locked_by text NULL,
                    ADD COLUMN locked_until timestamptz NULL,
                    ADD COLUMN terminal_reason text NULL,
                    ADD COLUMN dead_lettered_at timestamptz NULL,
                    ADD CONSTRAINT ck_outbox_messages_identity CHECK (
                        id <> '00000000-0000-0000-0000-000000000000'::uuid
                        AND tenant_id NOT IN ('00000000-0000-0000-0000-000000000000'::uuid,
                                             '00000000-0000-7000-8000-000000000002'::uuid)
                        AND (organization_id IS NULL OR
                             organization_id <> '00000000-0000-0000-0000-000000000000'::uuid)),
                    ADD CONSTRAINT ck_outbox_messages_attempts CHECK (attempts >= 0),
                    ADD CONSTRAINT ck_outbox_messages_last_error_size CHECK (
                        last_error IS NULL OR octet_length(last_error) <= 256),
                    ADD CONSTRAINT ck_outbox_messages_lease CHECK (
                        (locked_by IS NULL AND locked_until IS NULL) OR
                        (locked_by IS NOT NULL AND length(locked_by) > 0 AND locked_until IS NOT NULL)),
                    ADD CONSTRAINT ck_outbox_messages_terminal_reason CHECK (
                        terminal_reason IS NULL OR terminal_reason IN (
                            'unknown_type', 'retired_type', 'invalid_envelope', 'retry_exhausted')),
                    ADD CONSTRAINT ck_outbox_messages_state CHECK (
                        (processed_at IS NULL OR
                            (dead_lettered_at IS NULL AND terminal_reason IS NULL AND locked_by IS NULL))
                        AND (dead_lettered_at IS NULL OR
                            (processed_at IS NULL AND terminal_reason IS NOT NULL AND locked_by IS NULL)));

                DROP INDEX ix_outbox_messages_pending;
                DROP INDEX ix_outbox_messages_tenant_pending;
                CREATE INDEX ix_outbox_messages_pending
                    ON outbox_messages (available_after, occurred_at, id)
                    WHERE processed_at IS NULL AND dead_lettered_at IS NULL
                      AND terminal_reason IS NULL AND attempts < 6 AND locked_by IS NULL;
                CREATE INDEX ix_outbox_messages_tenant_pending
                    ON outbox_messages (tenant_id, available_after)
                    WHERE processed_at IS NULL AND dead_lettered_at IS NULL;
                CREATE INDEX ix_outbox_messages_recovery
                    ON outbox_messages (locked_until, occurred_at, id)
                    WHERE processed_at IS NULL AND dead_lettered_at IS NULL
                      AND terminal_reason IS NULL AND locked_by IS NOT NULL;
                CREATE INDEX ix_outbox_messages_terminal
                    ON outbox_messages (available_after, occurred_at, id)
                    WHERE processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NOT NULL;

                GRANT UPDATE (locked_by, locked_until, terminal_reason, dead_lettered_at)
                    ON outbox_messages TO learnstack_outbox_admin;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Schema reversal for disposable databases only. Sanitized legacy
            // diagnostics cannot be restored; live rollback must be forward-fix.
            migrationBuilder.Sql("""
                REVOKE UPDATE (locked_by, locked_until, terminal_reason, dead_lettered_at)
                    ON outbox_messages FROM learnstack_outbox_admin;
                DROP INDEX ix_outbox_messages_terminal;
                DROP INDEX ix_outbox_messages_recovery;
                DROP INDEX ix_outbox_messages_pending;
                DROP INDEX ix_outbox_messages_tenant_pending;
                ALTER TABLE outbox_messages
                    DROP CONSTRAINT ck_outbox_messages_state,
                    DROP CONSTRAINT ck_outbox_messages_terminal_reason,
                    DROP CONSTRAINT ck_outbox_messages_lease,
                    DROP CONSTRAINT ck_outbox_messages_last_error_size,
                    DROP CONSTRAINT ck_outbox_messages_attempts,
                    DROP CONSTRAINT ck_outbox_messages_identity,
                    DROP COLUMN dead_lettered_at,
                    DROP COLUMN terminal_reason,
                    DROP COLUMN locked_until,
                    DROP COLUMN locked_by;
                CREATE INDEX ix_outbox_messages_pending
                    ON outbox_messages (available_after) WHERE processed_at IS NULL;
                CREATE INDEX ix_outbox_messages_tenant_pending
                    ON outbox_messages (tenant_id, available_after) WHERE processed_at IS NULL;
                """);
        }
    }
}
