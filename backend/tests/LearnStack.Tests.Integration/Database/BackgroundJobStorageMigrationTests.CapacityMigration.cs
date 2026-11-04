using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobStorageMigrationTests
{
    [Fact]
    public async Task Capacity_cleanup_forward_migration_reapplies_and_preserves_the_nonkey_count_lock()
    {
        await using var database = await DisposableSchemaDatabase.CreateAtPlatformMigrationAsync(schema.Postgres,
            "20261017231821_index_background_job_control_quarantines");
        await using var context = Platform(database);
        var migrations = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        var added = migrations.Single(name => name.EndsWith("_add_transactional_job_capacity_cleanup", StringComparison.Ordinal));
        var predecessor = migrations[Array.IndexOf(migrations, added) - 1];
        var current = await BackgroundJobSchemaInventory.ReadAsync(database.MigrationConnectionString);
        current.Should().Contain(item => item.Contains("transactional_cleanup_at", StringComparison.Ordinal));
        current.Single(item => item.StartsWith("function:", StringComparison.Ordinal)
            && item.Contains("FUNCTION public.fn_background_job_attempt_counts()", StringComparison.Ordinal))
            .Should().Contain("FOR NO KEY UPDATE");
        await context.GetService<IMigrator>().MigrateAsync(predecessor);
        var before = await BackgroundJobSchemaInventory.ReadAsync(database.MigrationConnectionString);
        before.Should().NotContain(item => item.Contains("transactional_cleanup_at", StringComparison.Ordinal));
        await context.Database.MigrateAsync();
        AssertCapacityReappliedInventory(await BackgroundJobSchemaInventory.ReadAsync(database.MigrationConnectionString), current);
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transactional_cleanup_and_release_are_atomic_and_migration_reversal_refuses_new_history(bool release)
    {
        await using var database = release
            ? await DisposableSchemaDatabase.CreateAtPlatformMigrationAsync(schema.Postgres,
                "20261016210528_add_transactional_job_capacity_cleanup")
            : await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedAsync(database.AppConnectionString, database.OutboxConnectionString);
        var job = BackgroundJobSchemaSeed.Find(SchemaFixture.TenantA, null);
        await using (var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString))
            await SchemaQueries.ExecuteAsync(dispatcher, null, """
                UPDATE background_jobs SET ownership_until=clock_timestamp()-interval '1 microsecond' WHERE id=@job;
                UPDATE background_jobs SET phase='reconciling', ownership_token=@recovery,
                    ownership_kind='recovery', ownership_until=clock_timestamp()+interval '1 minute' WHERE id=@job;
                """, ("job", job.JobId), ("recovery", Guid.NewGuid()));
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, transaction, job.TenantId, null);
        await SchemaQueries.ExecuteAsync(app, transaction,
            "UPDATE background_job_attempts SET transactional_cleanup_at=clock_timestamp() WHERE job_id=@job", ("job", job.JobId));
        if (!release)
        {
            var commit = async () => await transaction.CommitAsync();
            (await commit.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
            return;
        }
        await SchemaQueries.ExecuteAsync(app, transaction, """
            UPDATE background_job_reservations SET released_at=clock_timestamp(), release_reason='transactional_rollback'
            WHERE job_id=@job;
            """, ("job", job.JobId));
        await transaction.CommitAsync();
        await using var context = Platform(database);
        var migrations = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        var added = migrations.Single(name => name.EndsWith("_add_transactional_job_capacity_cleanup", StringComparison.Ordinal));
        var predecessor = migrations[Array.IndexOf(migrations, added) - 1];
        var reversal = async () => await context.GetService<IMigrator>().MigrateAsync(predecessor);
        (await reversal.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        await using var check = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, check, job.TenantId, null);
        (await SchemaQueries.CountAsync(app,
            "SELECT count(*) FROM background_job_reservations WHERE job_id='" + job.JobId + "' AND release_reason='transactional_rollback'", check))
            .Should().Be(1);
        (await SchemaQueries.CountAsync(app,
            "SELECT count(*) FROM pg_class WHERE relname IN ('background_job_attempts','background_job_reservations') AND relforcerowsecurity", check))
            .Should().Be(2);
        await check.RollbackAsync();
        await context.Database.MigrateAsync();
        (await context.Database.GetAppliedMigrationsAsync()).Should().Equal(context.Database.GetMigrations());
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }
}
