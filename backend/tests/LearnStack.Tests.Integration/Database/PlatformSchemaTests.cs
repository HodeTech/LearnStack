using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// <c>outbox_messages</c> and <c>idempotency_keys</c> — the two tables no module
/// owns — and the behaviour that is specific to them.
/// </summary>
/// <remarks>
/// <para>
/// The structural sweeps are <b>not</b> here: they live in
/// <see cref="TenancySchemaTests"/> and, since both suites share
/// <see cref="SchemaFixture"/>, they now enumerate a catalogue that includes these
/// two tables. That is the fix for the shape this class used to have — a
/// two-entry <c>[InlineData]</c> row-security check beside sweeps that could not
/// see either table, which let a second permissive policy on the outbox pass the
/// whole suite.
/// </para>
/// <para>
/// What is left here is what only these tables can be asked: the identifier
/// generator, the dispatcher's column-scoped grant, the enqueue-only bound on the
/// application role, and the idempotency constraints.
/// </para>
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class PlatformSchemaTests
{
    private readonly SchemaFixture _schema;

    public PlatformSchemaTests(SchemaFixture schema) => _schema = schema;

    [Fact]
    public async Task TheOutboxIdDefaultsToAVersion7Uuid()
    {
        // The corrected function name. `gen_uuid_v7()` — which six documents named
        // before Packet 6 — does not exist in PostgreSQL 18; the built-in is
        // `uuidv7()`, and a migration carrying the wrong one fails on every insert.
        // Asserting the VERSION rather than that the insert succeeded, because
        // gen_random_uuid() would also have succeeded and produced a v4 with none
        // of the index locality ADR-0023 adopted v7 for.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);

        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO outbox_messages (tenant_id, correlation_id, type, topic, partition_key, payload)
            VALUES (@tenant, '00-trace-span-01', 'T', 'learnstack.tenancy.tenant', 'k', '{}')
            RETURNING uuid_extract_version(id)
            """, (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        insert.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);

        (await insert.ExecuteScalarAsync()).Should().Be((short)7);
    }

    [Theory]
    [InlineData("UPDATE outbox_messages SET processed_at = now()")]
    [InlineData("DELETE FROM outbox_messages")]
    public async Task TheApplicationRoleCanOnlyEnqueue(string statement)
    {
        // No UPDATE and no DELETE on the outbox: status transitions belong to the
        // dispatcher and purging to the audited platform scope. Both halves are
        // asserted, because the UPDATE is the one that matters most and was the one
        // missing — measured, granting learnstack_app table-wide UPDATE let a
        // handler run `SET processed_at = now()` over every pending row, making
        // each event permanently undeliverable, while the whole suite stayed green.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await using var command = new NpgsqlCommand(statement, (NpgsqlConnection)connection);

        var act = async () => await command.ExecuteNonQueryAsync();

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task TheDispatcherHoldsExactlyTheFourColumnUpdateGrant()
    {
        // BYPASSRLS bypasses policies, not GRANTs, so this column list is the
        // whole of the bound on learnstack_outbox_admin. SELECT ... FOR UPDATE
        // SKIP LOCKED works with a column-level grant, so no table-wide UPDATE is
        // needed — and when locked_by / locked_until land in Phase 02b, that
        // migration extends this grant or the dispatcher fails at runtime with
        // `permission denied for table`.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);
        await using var command = new NpgsqlCommand(
            """
            SELECT string_agg(column_name, ',' ORDER BY column_name)
            FROM information_schema.column_privileges
            WHERE table_name = 'outbox_messages'
              AND grantee = 'learnstack_outbox_admin'
              AND privilege_type = 'UPDATE'
            """, (NpgsqlConnection)connection);

        (await command.ExecuteScalarAsync()).Should()
            .Be("attempts,available_after,last_error,processed_at");
    }

    [Fact]
    public async Task TheDispatcherReadsEveryTenantWithNoTenantContext()
    {
        // The one thing BYPASSRLS is for here. The dispatcher polls without a
        // tenant — it does not know whose event is next — so a policy applied to
        // it would return zero rows forever and the outbox would never drain.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.OutboxConnectionString);
        await using var command = new NpgsqlCommand(
            "SELECT count(DISTINCT tenant_id) FROM outbox_messages", (NpgsqlConnection)connection);

        (await command.ExecuteScalarAsync()).Should().Be(2L,
            "the fixture seeds one row per tenant and the dispatcher sees both");
    }

    [Theory]
    // The accepting half as well as the rejecting one, so the assertion pins the
    // BOUND rather than the constraint's name: with only the rejecting case, a cap
    // of zero passed. 262144 is ADR-0037's 256 KiB replay cap.
    [InlineData("repeat('x', 262144)::bytea", null)]
    [InlineData("repeat('x', 262145)::bytea", "ck_idempotency_keys_body_size")]
    public async Task TheIdempotencyBodyCapIsEnforcedByTheDatabase(string body, string? violated)
    {
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);

        var act = async () => await SchemaQueries.ExecuteAsync(connection, transaction,
            $"""
             INSERT INTO idempotency_keys
                 (tenant_id, key, fingerprint, claim_token, state, expires_at,
                  status_code, body)
             VALUES (@tenant, 'body-cap-probe', 'fp', uuidv7(), 'completed',
                     now() + interval '1 day', 200, {body})
             """,
            ("tenant", SchemaFixture.TenantA));

        if (violated is null)
        {
            await act.Should().NotThrowAsync();
        }
        else
        {
            (await act.Should().ThrowAsync<PostgresException>())
                .Which.ConstraintName.Should().Be(violated);
        }
    }

    [Theory]
    // The closed set Standards 05 § Column types names as its worked example, and
    // the key bound the migration claims matches [Idempotent]'s header bounds
    // (MinKeyLength 8, MaxKeyLength 128). Both were unasserted; each value below
    // is one side of a boundary the API already enforces.
    [InlineData("'in_flight'", "'valid-key'", null)]
    [InlineData("'bogus_state'", "'valid-key'", "ck_idempotency_keys_state")]
    [InlineData("'in_flight'", "'sevench'", "ck_idempotency_keys_key_length")]
    [InlineData("'in_flight'", "repeat('k', 129)", "ck_idempotency_keys_key_length")]
    [InlineData("'in_flight'", "repeat('k', 128)", null)]
    public async Task TheIdempotencyClosedSetsAreEnforcedByTheDatabase(
        string state, string key, string? violated)
    {
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);

        var act = async () => await SchemaQueries.ExecuteAsync(connection, transaction,
            $"""
             INSERT INTO idempotency_keys
                 (tenant_id, key, fingerprint, claim_token, state, expires_at)
             VALUES (@tenant, {key}, 'fp', uuidv7(), {state}, now() + interval '5 minutes')
             """,
            ("tenant", SchemaFixture.TenantA));

        if (violated is null)
        {
            await act.Should().NotThrowAsync();
        }
        else
        {
            (await act.Should().ThrowAsync<PostgresException>())
                .Which.ConstraintName.Should().Be(violated);
        }
    }

    [Theory]
    // ck_idempotency_keys_outcome, which ties `state` to the four response
    // columns. Without it a `completed` row could carry no status code and no
    // body, and ADR-0037's claim statement would report it as replayable — the
    // caller then replays a response that does not exist.
    [InlineData("'completed'", "200", "'x'::bytea", null)]
    [InlineData("'completed'", "NULL", "NULL", "ck_idempotency_keys_outcome")]
    [InlineData("'in_flight'", "NULL", "NULL", null)]
    [InlineData("'in_flight'", "201", "'x'::bytea", "ck_idempotency_keys_outcome")]
    public async Task TheIdempotencyOutcomeShapeMatchesItsState(
        string state, string statusCode, string body, string? violated)
    {
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);

        var act = async () => await SchemaQueries.ExecuteAsync(connection, transaction,
            $"""
             INSERT INTO idempotency_keys
                 (tenant_id, key, fingerprint, claim_token, state, expires_at,
                  status_code, body)
             VALUES (@tenant, 'outcome-probe', 'fp', uuidv7(), {state},
                     now() + interval '1 day', {statusCode}, {body})
             """,
            ("tenant", SchemaFixture.TenantA));

        if (violated is null)
        {
            await act.Should().NotThrowAsync();
        }
        else
        {
            (await act.Should().ThrowAsync<PostgresException>())
                .Which.ConstraintName.Should().Be(violated);
        }
    }

    [Fact]
    public async Task EachChainHasItsOwnHistoryTable()
    {
        // Separate history tables, so a module's migration cannot block the
        // platform's or be blocked by it. Compared against the constants the
        // DESIGN-TIME FACTORIES declare, because those are the only objects
        // `dotnet ef` — and therefore `make migrate` — ever uses: an earlier
        // version compared against literals the fixture had written itself, so the
        // deployment path could have drifted underneath a green assertion.
        //
        // That the chains actually advance independently is measured in
        // MigrationRollbackTests, which reverses them in the opposite order to the
        // one that applied them.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);
        await using var command = new NpgsqlCommand(
            """
            SELECT string_agg(tablename, ',' ORDER BY tablename)
            FROM pg_tables WHERE schemaname = 'public' AND tablename LIKE '\_\_ef%'
            """, (NpgsqlConnection)connection);

        var expected = string.Join(',', new[]
        {
            PlatformDbContextFactory.HistoryTable,
            TenancyDbContextFactory.HistoryTable,
        }.Order(StringComparer.Ordinal));

        (await command.ExecuteScalarAsync()).Should().Be(expected);
    }
}
