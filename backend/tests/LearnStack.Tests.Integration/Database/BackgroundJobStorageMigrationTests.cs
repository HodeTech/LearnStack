using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Applied storage/grant proofs for Accepted ADR-0047; tenant behavior authenticates as learnstack_app.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed partial class BackgroundJobStorageMigrationTests(SchemaFixture schema)
{
    private const string Predecessor = "20261013083903_add_event_delivery_and_inbox";

    [Fact]
    public async Task Fresh_down_reapply_restores_every_job_object_and_retains_existing_event_storage()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var context = Platform(database);
        context.Database.HasPendingModelChanges().Should().BeFalse();
        var before = await BackgroundJobSchemaInventory.ReadAsync(database.MigrationConnectionString);
        before.Should().HaveCountGreaterThan(200,
            "the fingerprint covers actual columns, constraints, indexes, grants, policies, triggers and functions");
        await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
        {
            await SchemaTableClasses.AssertCompleteAsync(owner);
        }

        await context.GetService<IMigrator>().MigrateAsync(Predecessor);
        (await BackgroundJobSchemaInventory.ReadAsync(database.MigrationConnectionString)).Should().BeEmpty(
            "Down removes every authoritative job object, including table-owned functions");
        await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
        {
            (await SchemaQueries.ReadStringsAsync(owner, SchemaQueries.TableNames)).Should()
                .BeEquivalentTo(SchemaTableClasses.Existing, "all preexisting data tables survive this additive migration reversal");
        }

        await context.Database.MigrateAsync();
        (await BackgroundJobSchemaInventory.ReadAsync(database.MigrationConnectionString)).Should().Equal(before);
        await using var restored = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await SchemaTableClasses.AssertCompleteAsync(restored);
    }

    [Fact]
    public async Task Canonical_document_ddl_produces_the_same_job_inventory_as_the_migration()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres, applyMigrations: false);
        await MigrationChains.ApplyTenancyAsync(database.MigrationConnectionString);
        await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
        {
            await SchemaQueries.ExecuteAsync(owner, null, await BackgroundJobSchemaInventory.CanonicalSqlAsync());
        }
        (await BackgroundJobSchemaInventory.ReadAsync(database.MigrationConnectionString)).Should()
            .Equal(await BackgroundJobSchemaInventory.ReadAsync(schema.Postgres.MigrationConnectionString),
                "Standards 05 must produce the shipped DDL and exact grants without copied test SQL");
    }

    private static PlatformDbContext Platform(DisposableSchemaDatabase database) => new(
        new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(database.MigrationConnectionString, options =>
                options.MigrationsHistoryTable(PlatformDbContextFactory.HistoryTable)).Options);
}
