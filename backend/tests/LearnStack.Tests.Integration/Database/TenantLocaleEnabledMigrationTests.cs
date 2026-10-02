using FluentAssertions;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class TenantLocaleEnabledMigrationTests(SchemaFixture schema)
{
    private const string Predecessor = "20260914114306_org_insert_scope_and_keyless_guard";

    [Fact]
    public async Task Invalid_legacy_default_blocks_migration_without_repair_and_valid_rows_survive_down_reapply()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres, applyMigrations: false);
        await using var migration = new TenancyDbContext(new DbContextOptionsBuilder<TenancyDbContext>()
            .UseNpgsql(database.MigrationConnectionString,
                provider => provider.MigrationsHistoryTable(TenancyDbContextFactory.HistoryTable)).Options,
            StaticTenantContextAccessor.Unresolved);
        await migration.GetService<IMigrator>().MigrateAsync(Predecessor);
        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
        await using (var insert = new NpgsqlCommand("""
            BEGIN;
            SET LOCAL app.tenant_id = '11111111-1111-7111-8111-111111111111';
            INSERT INTO tenants (id, slug, display_name, status, created_at, created_by, row_version)
            VALUES ('11111111-1111-7111-8111-111111111111', 'locale-migration', 'Locale', 'Trial', now(),
                    '00000000-0000-7000-8000-000000000001', 7);
            INSERT INTO tenant_locales (tenant_id, locale, is_default, is_enabled, sort)
            VALUES ('11111111-1111-7111-8111-111111111111', 'en', true, false, 0);
            COMMIT;
            """, (NpgsqlConnection)app))
        {
            await insert.ExecuteNonQueryAsync();
        }

        var invalid = await SnapshotAsync(database.AppConnectionString);
        var apply = async () => await migration.Database.MigrateAsync();
        var refused = (await apply.Should().ThrowAsync<PostgresException>()).Which;
        refused.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        refused.ConstraintName.Should().Be("ck_tenant_locales_default_enabled");
        (await SnapshotAsync(database.AppConnectionString)).Should().Be(invalid);
        (await migration.Database.GetAppliedMigrationsAsync()).Last().Should().Be(Predecessor);

        // Explicit test-owned operator remediation; migration never chooses a locale.
        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
        await using (var repair = new NpgsqlCommand("""
            BEGIN;
            SET LOCAL app.tenant_id = '11111111-1111-7111-8111-111111111111';
            UPDATE tenant_locales SET is_enabled = true;
            COMMIT;
            """, (NpgsqlConnection)app))
        {
            await repair.ExecuteNonQueryAsync();
        }

        var valid = await SnapshotAsync(database.AppConnectionString);
        await migration.Database.MigrateAsync();
        (await SnapshotAsync(database.AppConnectionString)).Should().Be(valid);
        migration.Database.HasPendingModelChanges().Should().BeFalse();
        await migration.GetService<IMigrator>().MigrateAsync(Predecessor);
        (await SnapshotAsync(database.AppConnectionString)).Should().Be(valid);
        await migration.Database.MigrateAsync();
        (await SnapshotAsync(database.AppConnectionString)).Should().Be(valid);
        await using var connection = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(connection, transaction, SchemaFixture.TenantA, null);
        await using var invalidUpdate = new NpgsqlCommand("UPDATE tenant_locales SET is_enabled = false",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        var update = async () => await invalidUpdate.ExecuteNonQueryAsync();
        (await update.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("ck_tenant_locales_default_enabled");
        await transaction.RollbackAsync();
    }

    private static async Task<string> SnapshotAsync(string connectionString)
    {
        await using var app = await PostgresFixture.OpenAsync(connectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        await using var command = new NpgsqlCommand("""
            SELECT jsonb_build_object('tenant', (SELECT to_jsonb(t) FROM tenants t),
                'locales', (SELECT jsonb_agg(to_jsonb(l) ORDER BY locale) FROM tenant_locales l))::text
            """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction);
        var snapshot = (string)(await command.ExecuteScalarAsync())!;
        await transaction.RollbackAsync();
        return snapshot;
    }
}
