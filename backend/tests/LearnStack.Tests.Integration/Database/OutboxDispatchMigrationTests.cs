using System.Data.Common;
using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Audit.Infrastructure.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class OutboxDispatchMigrationTests(SchemaFixture schema)
{
    private const string PlatformPredecessor = "20260828085701_create_platform_infrastructure_tables";
    private const string AuditPredecessor = "20260914114341_audit_log_org_insert_scope";
    private const string SanitizedLegacy = "migration:legacy:redacted";

    [Fact]
    public async Task Legacy_facts_survive_up_down_reapply_and_diagnostics_are_redacted_across_tenants()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres, applyMigrations: false);
        await using var context = Platform(database);
        await context.GetService<IMigrator>().MigrateAsync(PlatformPredecessor);
        await InsertLegacyAsync(database, Guid.NewGuid(), SchemaFixture.TenantA, null, processed: false);
        await InsertLegacyAsync(database, Guid.NewGuid(), SchemaFixture.TenantB, SchemaFixture.OrgB1, processed: true);
        var facts = await FactsAsync(database);

        await context.Database.MigrateAsync();
        context.Database.HasPendingModelChanges().Should().BeFalse();
        (await FactsAsync(database)).Should().Be(facts, "migration must not change attempts, availability or event facts");
        await AssertLegacyDiagnosticsAndRlsAsync(database);
        await AssertIndexAsync(database, "ix_outbox_messages_pending",
            ["available_after, occurred_at, id", "dead_lettered_at IS NULL", "terminal_reason IS NULL", "attempts < 6", "locked_by IS NULL"]);
        await AssertIndexAsync(database, "ix_outbox_messages_recovery", ["locked_until, occurred_at, id", "locked_by IS NOT NULL"]);
        await AssertIndexAsync(database, "ix_outbox_messages_terminal", ["terminal_reason IS NOT NULL"]);

        await context.GetService<IMigrator>().MigrateAsync(PlatformPredecessor);
        (await FactsAsync(database)).Should().Be(facts);
        await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
        {
            (await SchemaQueries.CountAsync(owner, """
                SELECT count(*) FROM information_schema.columns
                WHERE table_name = 'outbox_messages'
                  AND column_name IN ('locked_by','locked_until','terminal_reason','dead_lettered_at')
                """)).Should().Be(0);
            (await SchemaQueries.CountAsync(owner, """
                SELECT count(*) FROM pg_constraint
                WHERE conrelid = 'outbox_messages'::regclass AND conname LIKE 'ck_outbox_messages_%'
                """)).Should().Be(0);
        }

        await context.Database.MigrateAsync();
        (await FactsAsync(database)).Should().Be(facts);
        await AssertLegacyDiagnosticsAndRlsAsync(database);
    }

    [Theory]
    [InlineData("id", "00000000-0000-0000-0000-000000000000")]
    [InlineData("tenant", "00000000-0000-0000-0000-000000000000")]
    [InlineData("tenant", "00000000-0000-7000-8000-000000000002")]
    [InlineData("organization", "00000000-0000-0000-0000-000000000000")]
    public async Task Invalid_legacy_identity_refuses_migration_without_repair(string member, string invalid)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres, applyMigrations: false);
        await using var context = Platform(database);
        await context.GetService<IMigrator>().MigrateAsync(PlatformPredecessor);
        await InsertLegacyAsync(database,
            member == "id" ? Guid.Parse(invalid) : Guid.NewGuid(),
            member == "tenant" ? Guid.Parse(invalid) : SchemaFixture.TenantA,
            member == "organization" ? Guid.Parse(invalid) : null,
            processed: false);
        var facts = await FactsAsync(database);
        var apply = async () => await context.Database.MigrateAsync();
        var error = (await apply.Should().ThrowAsync<PostgresException>()).Which;
        error.ConstraintName.Should().Be("ck_outbox_messages_identity");
        (await FactsAsync(database)).Should().Be(facts);
        (await context.Database.GetAppliedMigrationsAsync()).Last().Should().Be(PlatformPredecessor);
        await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var check = new NpgsqlCommand("SELECT relforcerowsecurity FROM pg_class WHERE oid = 'outbox_messages'::regclass", (NpgsqlConnection)owner);
        (await check.ExecuteScalarAsync()).Should().Be(true, "the failed transactional backfill must restore FORCE RLS");
        await using var outbox = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        (await SchemaQueries.ReadStringsAsync(outbox, "SELECT last_error FROM outbox_messages"))
            .Should().ContainSingle().Which.Should().StartWith("private legacy");
    }

    [Theory]
    [InlineData("last_error = repeat('é',128)", null)]
    [InlineData("last_error = repeat('é',129)", "ck_outbox_messages_last_error_size")]
    [InlineData("locked_by = 'token'", "ck_outbox_messages_lease")]
    [InlineData("locked_by = 'token', locked_until = now() + interval '30 seconds'", null)]
    [InlineData("locked_by = '', locked_until = now()", "ck_outbox_messages_lease")]
    [InlineData("terminal_reason = 'unknown_type'", null)]
    [InlineData("terminal_reason = 'not_declared'", "ck_outbox_messages_terminal_reason")]
    [InlineData("dead_lettered_at = now()", "ck_outbox_messages_state")]
    [InlineData("dead_lettered_at = now(), terminal_reason = 'retry_exhausted'", null)]
    [InlineData("processed_at = now(), terminal_reason = 'unknown_type'", "ck_outbox_messages_state")]
    [InlineData("attempts = -1", "ck_outbox_messages_attempts")]
    [InlineData("attempts = 7", null)]
    public async Task Dispatcher_state_checks_have_accepting_and_refusing_controls(string assignment, string? constraint)
    {
        // Literal theory statements are test-owned, never interpolated caller input.
        // Legacy exhausted counts above six remain facts; normal claiming enforces
        // the six-attempt ceiling, rather than a migration rewriting that history.
        await using var connection = await PostgresFixture.OpenAsync(schema.Postgres.OutboxConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await using var update = new NpgsqlCommand(
            $"UPDATE outbox_messages SET {assignment} WHERE tenant_id = @tenant",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        update.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);
        var act = async () => (await update.ExecuteNonQueryAsync()).Should().BeGreaterThan(0, "a CHECK proof must touch a real row");
        if (constraint is null)
        {
            await act.Should().NotThrowAsync();
        }
        else
        {
            (await act.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be(constraint);
        }
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData("SELECT * FROM audit_log")]
    [InlineData("UPDATE audit_log SET outcome = 'success'")]
    [InlineData("DELETE FROM audit_log")]
    [InlineData("SELECT * FROM tenants")]
    [InlineData("UPDATE outbox_messages SET type = 'other'")]
    [InlineData("UPDATE outbox_messages SET payload = '{}'::jsonb")]
    [InlineData("DELETE FROM outbox_messages")]
    public async Task Outbox_role_has_no_audit_read_mutation_or_business_access(string sql)
    {
        await using var connection = await PostgresFixture.OpenAsync(schema.Postgres.OutboxConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        var act = async () => await command.ExecuteNonQueryAsync();
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Audit_grant_has_its_own_chain_and_is_removed_by_down_then_reapplied()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var context = new AuditDbContext(new DbContextOptionsBuilder<AuditDbContext>()
            .UseNpgsql(database.MigrationConnectionString,
                provider => provider.MigrationsHistoryTable(AuditDbContextFactory.HistoryTable)).Options,
            StaticTenantContextAccessor.Unresolved);
        context.Database.HasPendingModelChanges().Should().BeFalse();
        await AssertAuditInsertGrantAsync(database, expected: true);
        await context.GetService<IMigrator>().MigrateAsync(AuditPredecessor);
        await AssertAuditInsertGrantAsync(database, expected: false);
        await context.Database.MigrateAsync();
        await AssertAuditInsertGrantAsync(database, expected: true);
    }

    private static PlatformDbContext Platform(DisposableSchemaDatabase database) =>
        new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(database.MigrationConnectionString,
            provider => provider.MigrationsHistoryTable(PlatformDbContextFactory.HistoryTable)).Options);

    private static async Task InsertLegacyAsync(DisposableSchemaDatabase database, Guid id, Guid tenant, Guid? organization, bool processed)
    {
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await SchemaQueries.SetTenantAsync(app, transaction, tenant);
        await SchemaQueries.ExecuteAsync(app, transaction, """
            INSERT INTO outbox_messages
                (id, tenant_id, organization_id, correlation_id, type, topic, partition_key,
                 payload, metadata, attempts, processed_at, available_after, last_error)
            VALUES (@id, @tenant, @organization, 'bad-legacy-trace', 'legacy-contract',
                    'learnstack.tenancy.tenant', @partition, '{"legacy":true}', '{"legacy":true}',
                    7, CASE WHEN @processed THEN now() ELSE NULL END, now() + interval '5 minutes',
                    'private legacy ' || repeat('é',1000))
            """, ("id", id), ("tenant", tenant), ("organization", organization.HasValue ? organization.Value : DBNull.Value),
            ("partition", tenant.ToString()), ("processed", processed));
        await transaction.CommitAsync();
    }

    private static async Task<string> FactsAsync(DisposableSchemaDatabase database)
    {
        await using var outbox = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await using var command = new NpgsqlCommand("""
            SELECT jsonb_agg(to_jsonb(o) - 'last_error' - 'locked_by' - 'locked_until'
                - 'terminal_reason' - 'dead_lettered_at' ORDER BY id)::text FROM outbox_messages o
            """, (NpgsqlConnection)outbox);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task AssertLegacyDiagnosticsAndRlsAsync(DisposableSchemaDatabase database)
    {
        await using var outbox = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        (await SchemaQueries.ReadStringsAsync(outbox, "SELECT last_error FROM outbox_messages ORDER BY id"))
            .Should().Equal(SanitizedLegacy, SanitizedLegacy);
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        (await SchemaQueries.CountAsync(app, "SELECT count(*) FROM outbox_messages")).Should().Be(0,
            "unresolved app context must remain fail-closed after the owner backfill");
        await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var command = new NpgsqlCommand("SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid = 'outbox_messages'::regclass", (NpgsqlConnection)owner);
        (await command.ExecuteScalarAsync()).Should().Be(true);
    }

    private static async Task AssertIndexAsync(DisposableSchemaDatabase database, string name, string[] fragments)
    {
        await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var command = new NpgsqlCommand("SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' AND indexname = @name", (NpgsqlConnection)owner);
        command.Parameters.AddWithValue("name", name);
        var definition = (string)(await command.ExecuteScalarAsync())!;
        foreach (var fragment in fragments)
        {
            definition.Should().Contain(fragment);
        }
    }

    private static async Task AssertAuditInsertGrantAsync(DisposableSchemaDatabase database, bool expected)
    {
        await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var command = new NpgsqlCommand("SELECT has_table_privilege('learnstack_outbox_admin', 'audit_log', 'INSERT')", (NpgsqlConnection)owner);
        (await command.ExecuteScalarAsync()).Should().Be(expected);
    }
}
