using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobStorageMigrationTests
{
    [Fact]
    public async Task Quarantine_observation_migration_reverses_only_its_index_and_reapplies_exactly()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var context = Platform(database);
        var head = await BackgroundJobSchemaInventory.ReadAsync(database.MigrationConnectionString);
        var migration = context.Database.GetMigrations().Single(name => name.EndsWith("_add_background_job_quarantine_observation", StringComparison.Ordinal));
        await context.GetService<IMigrator>().MigrateAsync(migration);
        var current = await BackgroundJobSchemaInventory.ReadAsync(database.MigrationConnectionString);
        await context.GetService<IMigrator>().MigrateAsync("20261017111954_add_external_job_recovery_wakes");
        var previous = await BackgroundJobSchemaInventory.ReadAsync(database.MigrationConnectionString);
        current.Except(previous).Should().ContainSingle().Which.Should().Contain("ix_background_jobs_quarantined");
        previous.Except(current).Should().BeEmpty();
        await context.GetService<IMigrator>().MigrateAsync(migration);
        (await BackgroundJobSchemaInventory.ReadAsync(database.MigrationConnectionString)).Should().Equal(current);
        await context.Database.MigrateAsync();
        (await BackgroundJobSchemaInventory.ReadAsync(database.MigrationConnectionString)).Should().Equal(head);
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }
}
