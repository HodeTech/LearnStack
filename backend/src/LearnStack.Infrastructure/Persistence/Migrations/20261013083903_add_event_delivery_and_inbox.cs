using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class add_event_delivery_and_inbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Shared transport infrastructure has no EF entity model. Canonical
            // DDL: Standards 05, Inbox. Tenancy owns the shared organization guard
            // function and runs first in the sanctioned migration sequence.
            migrationBuilder.Sql("""
                CREATE TABLE event_deliveries (
                    tenant_id       uuid NOT NULL,
                    subscription_id text NOT NULL,
                    event_id        uuid NOT NULL,
                    organization_id uuid NULL,
                    occurred_at     timestamptz NOT NULL,
                    correlation_id  text NOT NULL,
                    causation_id    uuid NULL,
                    actor_user_id   uuid NULL,
                    type            text NOT NULL,
                    topic           text NOT NULL,
                    partition_key   text NOT NULL,
                    payload         jsonb NOT NULL,
                    processed_at    timestamptz NULL,
                    attempts        int NOT NULL DEFAULT 0,
                    last_error      text NULL,
                    available_after timestamptz NOT NULL DEFAULT now(),
                    locked_by       text NULL,
                    locked_until    timestamptz NULL,
                    terminal_reason text NULL,
                    dead_lettered_at timestamptz NULL,
                    replay_count    int NOT NULL DEFAULT 0,
                    replayed_at     timestamptz NULL,
                    CONSTRAINT pk_event_deliveries PRIMARY KEY (tenant_id, subscription_id, event_id),
                    CONSTRAINT ck_event_deliveries_identity CHECK (
                        tenant_id NOT IN ('00000000-0000-0000-0000-000000000000'::uuid,
                                         '00000000-0000-7000-8000-000000000002'::uuid)
                        AND event_id <> '00000000-0000-0000-0000-000000000000'::uuid
                        AND (organization_id IS NULL OR
                             organization_id <> '00000000-0000-0000-0000-000000000000'::uuid)
                        AND (causation_id IS NULL OR causation_id <> '00000000-0000-0000-0000-000000000000'::uuid)
                        AND (actor_user_id IS NULL OR actor_user_id <> '00000000-0000-0000-0000-000000000000'::uuid)),
                    CONSTRAINT ck_event_deliveries_subscription CHECK (
                        subscription_id COLLATE "C" ~ '^[a-z][a-z0-9]*\.[a-z][a-z0-9-]*\.v[1-9][0-9]*$'),
                    CONSTRAINT ck_event_deliveries_attempts CHECK (attempts >= 0),
                    CONSTRAINT ck_event_deliveries_replay CHECK (
                        replay_count >= 0 AND ((replay_count = 0 AND replayed_at IS NULL)
                                              OR (replay_count > 0 AND replayed_at IS NOT NULL))),
                    CONSTRAINT ck_event_deliveries_last_error_size CHECK (
                        last_error IS NULL OR octet_length(last_error) <= 256),
                    CONSTRAINT ck_event_deliveries_lease CHECK (
                        (locked_by IS NULL AND locked_until IS NULL) OR
                        (locked_by IS NOT NULL AND locked_until IS NOT NULL
                         AND locked_by COLLATE "C" ~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
                         AND locked_by <> '00000000-0000-0000-0000-000000000000')),
                    CONSTRAINT ck_event_deliveries_terminal_reason CHECK (
                        terminal_reason IS NULL OR terminal_reason IN (
                            'unknown_type', 'retired_type', 'invalid_envelope',
                            'invalid_subscription', 'retry_exhausted')),
                    CONSTRAINT ck_event_deliveries_state CHECK (
                        (processed_at IS NULL OR (dead_lettered_at IS NULL AND terminal_reason IS NULL))
                        AND (dead_lettered_at IS NULL OR
                             (processed_at IS NULL AND terminal_reason IS NOT NULL AND locked_by IS NULL)))
                );
                
                CREATE INDEX ix_event_deliveries_tenant_id_organization_id
                    ON event_deliveries (tenant_id, organization_id);
                CREATE INDEX ix_event_deliveries_pending
                    ON event_deliveries (available_after, occurred_at, event_id, tenant_id, subscription_id)
                    WHERE processed_at IS NULL AND dead_lettered_at IS NULL
                      AND terminal_reason IS NULL AND attempts < 6 AND locked_by IS NULL;
                CREATE INDEX ix_event_deliveries_recovery
                    ON event_deliveries (available_after, occurred_at, event_id, tenant_id, subscription_id)
                    WHERE processed_at IS NULL AND dead_lettered_at IS NULL
                      AND terminal_reason IS NULL AND (locked_by IS NOT NULL OR attempts >= 6);
                CREATE INDEX ix_event_deliveries_terminal
                    ON event_deliveries (available_after, occurred_at, event_id, tenant_id, subscription_id)
                    WHERE processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NOT NULL;
                
                ALTER TABLE event_deliveries ENABLE ROW LEVEL SECURITY;
                ALTER TABLE event_deliveries FORCE ROW LEVEL SECURITY;
                CREATE POLICY event_deliveries_isolation ON event_deliveries
                    USING (
                        tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                        AND (organization_id IS NULL
                             OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                             OR current_setting('app.scope', true) = 'tenant'))
                    WITH CHECK (
                        tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                        AND ((organization_id IS NULL
                              AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                             OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid));
                CREATE POLICY event_deliveries_org_write_guard ON event_deliveries
                    AS RESTRICTIVE FOR UPDATE
                    USING ((organization_id IS NULL
                            AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                           OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid);
                CREATE POLICY event_deliveries_org_delete_guard ON event_deliveries
                    AS RESTRICTIVE FOR DELETE
                    USING ((organization_id IS NULL
                            AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                           OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid);
                CREATE TRIGGER tg_event_deliveries_organization_id_immutable
                    BEFORE UPDATE ON event_deliveries
                    FOR EACH ROW EXECUTE FUNCTION fn_organization_id_immutable();
                
                CREATE TABLE inbox_messages (
                    tenant_id       uuid NOT NULL,
                    subscription_id text NOT NULL,
                    event_id        uuid NOT NULL,
                    recorded_at     timestamptz NOT NULL DEFAULT now(),
                    CONSTRAINT pk_inbox_messages PRIMARY KEY (tenant_id, subscription_id, event_id),
                    CONSTRAINT ck_inbox_messages_identity CHECK (
                        tenant_id NOT IN ('00000000-0000-0000-0000-000000000000'::uuid,
                                         '00000000-0000-7000-8000-000000000002'::uuid)
                        AND event_id <> '00000000-0000-0000-0000-000000000000'::uuid),
                    CONSTRAINT ck_inbox_messages_subscription CHECK (
                        subscription_id COLLATE "C" ~ '^[a-z][a-z0-9]*\.[a-z][a-z0-9-]*\.v[1-9][0-9]*$')
                );
                ALTER TABLE inbox_messages ENABLE ROW LEVEL SECURITY;
                ALTER TABLE inbox_messages FORCE ROW LEVEL SECURITY;
                CREATE POLICY inbox_messages_isolation ON inbox_messages
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                
                CREATE FUNCTION fn_inbox_messages_immutable() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'inbox_messages is immutable (attempted % as %)', TG_OP, current_user
                        USING ERRCODE = 'insufficient_privilege';
                END;
                $$;
                CREATE TRIGGER tg_inbox_messages_immutable
                    BEFORE UPDATE OR DELETE ON inbox_messages
                    FOR EACH ROW EXECUTE FUNCTION fn_inbox_messages_immutable();
                CREATE TRIGGER tg_inbox_messages_no_truncate
                    BEFORE TRUNCATE ON inbox_messages
                    FOR EACH STATEMENT EXECUTE FUNCTION fn_inbox_messages_immutable();
                
                GRANT SELECT ON event_deliveries, inbox_messages TO learnstack_app;
                GRANT INSERT (tenant_id, subscription_id, event_id, organization_id, occurred_at,
                              correlation_id, causation_id, actor_user_id, type, topic, partition_key, payload)
                    ON event_deliveries TO learnstack_app;
                GRANT UPDATE (processed_at) ON event_deliveries TO learnstack_app;
                GRANT INSERT (tenant_id, subscription_id, event_id) ON inbox_messages TO learnstack_app;
                GRANT SELECT (tenant_id, subscription_id, event_id, organization_id, occurred_at,
                              type, topic, partition_key, processed_at, attempts, last_error,
                              available_after, locked_by, locked_until, terminal_reason,
                              dead_lettered_at, replay_count, replayed_at)
                    ON event_deliveries TO learnstack_outbox_admin;
                GRANT UPDATE (attempts, last_error, available_after, locked_by, locked_until,
                              terminal_reason, dead_lettered_at, replay_count, replayed_at)
                    ON event_deliveries TO learnstack_outbox_admin;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Disposable-schema reversal only; live rollback uses a forward fix.
            // Neither table has producer/business foreign keys or cascade retention.
            migrationBuilder.Sql("""
                DROP TABLE inbox_messages;
                DROP TABLE event_deliveries;
                DROP FUNCTION fn_inbox_messages_immutable();
                """);
        }
    }
}
