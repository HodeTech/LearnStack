using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// <c>outbox_messages</c> and <c>idempotency_keys</c> — the two tables no module
/// owns.
/// </summary>
/// <remarks>
/// They ship in their own migration chain, on their own history table, so a
/// module's migration cannot block the platform's or be blocked by it. Both are
/// tenant-owned and tenant-wide despite living outside a module: every row
/// carries a <c>tenant_id</c> and the ordinary policy applies.
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class PlatformSchemaTests : IClassFixture<PlatformSchemaFixture>
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-7222-8222-222222222222");

    private readonly PlatformSchemaFixture _schema;

    public PlatformSchemaTests(PlatformSchemaFixture schema) => _schema = schema;

    [Theory]
    [InlineData("outbox_messages")]
    [InlineData("idempotency_keys")]
    public async Task RowSecurityIsEnabledAndForced(string table)
    {
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);
        await using var command = new NpgsqlCommand(
            "SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE relname = @table",
            (NpgsqlConnection)connection);
        command.Parameters.AddWithValue("table", table);

        (await command.ExecuteScalarAsync()).Should().Be(true);
    }

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
        await SetTenantAsync(connection, transaction, TenantA);

        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO outbox_messages (tenant_id, correlation_id, type, topic, partition_key, payload)
            VALUES (@tenant, '00-trace-span-01', 'T', 'learnstack.tenancy.tenant', 'k', '{}')
            RETURNING uuid_extract_version(id)
            """, (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        insert.Parameters.AddWithValue("tenant", TenantA);

        (await insert.ExecuteScalarAsync()).Should().Be((short)7);
    }

    [Fact]
    public async Task AnotherTenantCannotSeeOutboxRows()
    {
        await using var seed = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await using (var seedTx = await seed.BeginTransactionAsync())
        {
            await SetTenantAsync(seed, seedTx, TenantA);
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO outbox_messages (tenant_id, correlation_id, type, topic, partition_key, payload)
                VALUES (@tenant, '00-trace-span-02', 'T', 'learnstack.tenancy.tenant', 'k', '{}')
                """, (NpgsqlConnection)seed, (NpgsqlTransaction)seedTx);
            insert.Parameters.AddWithValue("tenant", TenantA);
            await insert.ExecuteNonQueryAsync();
            await seedTx.CommitAsync();
        }

        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, transaction, TenantB);

        await using var read = new NpgsqlCommand(
            "SELECT count(*) FROM outbox_messages",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);

        (await read.ExecuteScalarAsync()).Should().Be(0L);
    }

    [Fact]
    public async Task TheApplicationRoleCanOnlyEnqueue()
    {
        // No UPDATE and no DELETE on the outbox: status transitions belong to the
        // dispatcher and purging to the audited platform scope. A handler that
        // could mark a row processed could make an event vanish.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await using var command = new NpgsqlCommand(
            "DELETE FROM outbox_messages", (NpgsqlConnection)connection);

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
    public async Task TheIdempotencyBodyCapIsEnforcedByTheDatabase()
    {
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, transaction, TenantA);

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO idempotency_keys
                (tenant_id, key, fingerprint, claim_token, state, expires_at, body)
            VALUES (@tenant, 'oversized', 'fp', gen_random_uuid(), 'completed', now(),
                    repeat('x', 262145)::bytea)
            """, (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        command.Parameters.AddWithValue("tenant", TenantA);

        var act = async () => await command.ExecuteNonQueryAsync();

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.ConstraintName.Should().Be("ck_idempotency_keys_body_size");
    }

    [Fact]
    public async Task TheTwoMigrationChainsAdvanceIndependently()
    {
        // Separate history tables, so a module's migration cannot block the
        // platform's or be blocked by it — and, from step 6, so ADR-0040's
        // multi-context property has two contexts to be true of.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);
        await using var command = new NpgsqlCommand(
            """
            SELECT string_agg(tablename, ',' ORDER BY tablename)
            FROM pg_tables WHERE schemaname = 'public' AND tablename LIKE '\_\_ef%'
            """, (NpgsqlConnection)connection);

        (await command.ExecuteScalarAsync()).Should()
            .Be("__ef_migrations_history_platform,__ef_migrations_history_tenancy");
    }

    private static async Task SetTenantAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        Guid tenantId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT set_config('app.tenant_id', @value, true)",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        command.Parameters.AddWithValue("value", tenantId.ToString());
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// Applies both migration chains and seeds the two tenants the isolation cases
/// compare — the platform tables carry a <c>tenant_id</c>, so a tenant must exist
/// for a row to reference even though no foreign key enforces it.
/// </summary>
public sealed class PlatformSchemaFixture : IAsyncLifetime
{
    public PostgresFixture Postgres { get; } = new();

    public async Task InitializeAsync()
    {
        await Postgres.InitializeAsync();

        var tenancy = new DbContextOptionsBuilder<Modules.Tenancy.Infrastructure.Persistence.TenancyDbContext>()
            .UseNpgsql(Postgres.MigrationConnectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__ef_migrations_history_tenancy"))
            .Options;

        await using (var context = new Modules.Tenancy.Infrastructure.Persistence.TenancyDbContext(tenancy))
        {
            await context.Database.MigrateAsync();
        }

        var platform = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(Postgres.MigrationConnectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__ef_migrations_history_platform"))
            .Options;

        await using (var context = new PlatformDbContext(platform))
        {
            await context.Database.MigrateAsync();
        }

        await SeedTenantsAsync();
    }

    public async Task DisposeAsync() => await Postgres.DisposeAsync();

    private async Task SeedTenantsAsync()
    {
        await using var connection = await PostgresFixture.OpenAsync(Postgres.MigrationConnectionString);
        await using var command = new NpgsqlCommand(
            """
            BEGIN;
            SET LOCAL app.tenant_id = '11111111-1111-7111-8111-111111111111';
            INSERT INTO tenants (id, slug, display_name, status, created_at, created_by, row_version)
            VALUES ('11111111-1111-7111-8111-111111111111','alpha','Alpha','Trial', now(),
                    '00000000-0000-7000-8000-000000000001', 0);
            COMMIT;
            BEGIN;
            SET LOCAL app.tenant_id = '22222222-2222-7222-8222-222222222222';
            INSERT INTO tenants (id, slug, display_name, status, created_at, created_by, row_version)
            VALUES ('22222222-2222-7222-8222-222222222222','beta','Beta','Trial', now(),
                    '00000000-0000-7000-8000-000000000001', 0);
            COMMIT;
            """, (NpgsqlConnection)connection);

        await command.ExecuteNonQueryAsync();
    }
}
