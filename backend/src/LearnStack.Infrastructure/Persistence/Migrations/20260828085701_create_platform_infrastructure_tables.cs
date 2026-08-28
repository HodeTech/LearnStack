using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class create_platform_infrastructure_tables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Two tables no module owns, transcribed from the canonical DDL in
            // docs/standards/05-database.md § Outbox and § Idempotency. Written as
            // raw SQL rather than through the model builder because neither is an
            // EF entity: an outbox row is enqueued through IOutbox and read by the
            // dispatcher, and an idempotency claim is one INSERT ... ON CONFLICT
            // that decides five outcomes in a single round trip. Mapping them would
            // add a model nothing queries.

            migrationBuilder.Sql("""
                CREATE TABLE outbox_messages (
                    id              uuid PRIMARY KEY DEFAULT uuidv7(),
                    occurred_at     timestamptz NOT NULL DEFAULT now(),
                    tenant_id       uuid NOT NULL,
                    -- Deliberately unindexed and absent from the policy: the event's
                    -- organization is delivery metadata the consumer restores, not a
                    -- dimension anything filters the outbox by.
                    organization_id uuid NULL,
                    -- text, not uuid: the full W3C traceparent, per ADR-0032.
                    correlation_id  text NOT NULL,
                    causation_id    uuid NULL,
                    actor_user_id   uuid NULL,
                    type            text NOT NULL,
                    topic           text NOT NULL,
                    partition_key   text NOT NULL,
                    payload         jsonb NOT NULL,
                    metadata        jsonb NULL,
                    processed_at    timestamptz NULL,
                    attempts        int NOT NULL DEFAULT 0,
                    last_error      text NULL,
                    available_after timestamptz NOT NULL DEFAULT now()
                );

                -- Partial, because the dispatcher only ever asks for unprocessed
                -- rows and the processed set grows without bound until the purge.
                CREATE INDEX ix_outbox_messages_pending
                    ON outbox_messages (available_after)
                    WHERE processed_at IS NULL;

                CREATE INDEX ix_outbox_messages_tenant_pending
                    ON outbox_messages (tenant_id, available_after)
                    WHERE processed_at IS NULL;
                """);

            migrationBuilder.Sql("""
                CREATE TABLE idempotency_keys (
                    tenant_id    uuid        NOT NULL,
                    key          text        NOT NULL,
                    fingerprint  text        NOT NULL,
                    claim_token  uuid        NOT NULL,
                    state        text        NOT NULL,
                    status_code  int         NULL,
                    content_type text        NULL,
                    headers      jsonb       NULL,
                    body         bytea       NULL,
                    claimed_at   timestamptz NOT NULL DEFAULT now(),
                    -- ONE expiry column: the 5-minute claim lease while in_flight and
                    -- the 24-hour retention window once an outcome is recorded, so the
                    -- claim statement's "the existing row has expired" predicate is one
                    -- comparison at every stage. AbandonAsync sets it to now(), which
                    -- makes the released row satisfy that same predicate — a release
                    -- needs no second code path, and learnstack_app never needs DELETE.
                    expires_at   timestamptz NOT NULL,
                    CONSTRAINT pk_idempotency_keys PRIMARY KEY (tenant_id, key),
                    CONSTRAINT ck_idempotency_keys_state
                        CHECK (state IN ('in_flight', 'completed', 'unreplayable')),
                    -- ADR-0037's replay cap is 256 KiB headers included, so this is a
                    -- floor under it rather than the cap: the database bounds the body
                    -- cheaply and the store enforces the headers-inclusive total.
                    CONSTRAINT ck_idempotency_keys_body_size
                        CHECK (body IS NULL OR octet_length(body) <= 262144),
                    -- Matches [Idempotent]'s header bounds, so a key the API accepted
                    -- always fits.
                    CONSTRAINT ck_idempotency_keys_key_length
                        CHECK (length(key) BETWEEN 8 AND 128),
                    -- The state and the response columns are one fact, not two.
                    -- ADR-0037 Amendment 2's claim statement reports a `completed`
                    -- row as replayable, so a `completed` row with no status code
                    -- and no body makes the caller replay a response that does not
                    -- exist; and the reclaim branch NULLs all four alongside
                    -- `state = 'in_flight'`, so the reverse is equally a lie about
                    -- what the row is. content_type stays free in the completed
                    -- arm — the port defines it as null for an empty body.
                    CONSTRAINT ck_idempotency_keys_outcome CHECK (
                        (state =  'completed' AND status_code IS NOT NULL AND body IS NOT NULL)
                     OR (state <> 'completed' AND status_code IS NULL AND content_type IS NULL
                                              AND headers IS NULL AND body IS NULL))
                );

                -- Serves both the retention sweep and the per-tenant admission count,
                -- tenant first per the composite-index rule.
                CREATE INDEX ix_idempotency_keys_tenant_id_expires_at
                    ON idempotency_keys (tenant_id, expires_at);
                """);

            // Both are tenant-owned, tenant-wide: no organization term, and
            // therefore no restrictive guards — there is no organization to guard.
            migrationBuilder.Sql("""
                ALTER TABLE outbox_messages   ENABLE ROW LEVEL SECURITY;
                ALTER TABLE outbox_messages   FORCE  ROW LEVEL SECURITY;
                ALTER TABLE idempotency_keys  ENABLE ROW LEVEL SECURITY;
                ALTER TABLE idempotency_keys  FORCE  ROW LEVEL SECURITY;

                CREATE POLICY outbox_messages_isolation ON outbox_messages
                    USING      (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                CREATE POLICY idempotency_keys_isolation ON idempotency_keys
                    USING      (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);

            // Application code only ever ENQUEUES, so learnstack_app gets no UPDATE
            // and no DELETE on the outbox: status transitions belong to the
            // dispatcher and purging to the audited platform scope. The dispatcher's
            // BYPASSRLS is what lets it read every tenant's pending rows — and
            // BYPASSRLS bypasses policies, not GRANTs, so the column list below is
            // what actually bounds it. SELECT ... FOR UPDATE SKIP LOCKED works with a
            // column-level UPDATE grant, so no table-wide UPDATE is needed. When
            // locked_by and locked_until land in Phase 02b, that migration extends
            // this grant; a column added without extending it fails at runtime with
            // `permission denied for table`.
            //
            // idempotency_keys gives learnstack_app no DELETE either: a release is an
            // UPDATE that backdates expires_at.
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT         ON outbox_messages  TO learnstack_app;
                GRANT SELECT, DELETE         ON outbox_messages  TO learnstack_platform;
                GRANT SELECT                 ON outbox_messages  TO learnstack_outbox_admin;
                GRANT UPDATE (processed_at, attempts, last_error, available_after)
                                             ON outbox_messages  TO learnstack_outbox_admin;

                GRANT SELECT, INSERT, UPDATE ON idempotency_keys TO learnstack_app;
                GRANT SELECT, DELETE         ON idempotency_keys TO learnstack_platform;
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TABLE IF EXISTS idempotency_keys;
                DROP TABLE IF EXISTS outbox_messages;
                """);

        }
    }
}
