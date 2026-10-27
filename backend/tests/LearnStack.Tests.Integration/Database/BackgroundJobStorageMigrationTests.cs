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
    public async Task Forward_count_lock_correction_reverts_and_reapplies_only_its_function()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var context = Platform(database);
        var head = await BackgroundJobSchemaInventory.ReadAsync(database.MigrationConnectionString);
        const string Correction = "20261016173813_allow_nonkey_job_count_validation";
        // Later additive capacity guards replace this function again. Qualify
        // the one-function correction at its own historical schema boundary.
        await context.GetService<IMigrator>().MigrateAsync(Correction);
        var current = await BackgroundJobSchemaInventory.ReadAsync(database.MigrationConnectionString);
        var changed = current.Single(item => item.StartsWith("function:", StringComparison.Ordinal)
            && item.Contains("FUNCTION public.fn_background_job_attempt_counts()", StringComparison.Ordinal));
        changed.Should().Contain("target_job FOR NO KEY UPDATE;");
        await context.GetService<IMigrator>().MigrateAsync("20261016170000_allow_queued_hangfire_schedule");
        var previous = await BackgroundJobSchemaInventory.ReadAsync(database.MigrationConnectionString);
        previous.Except(current).Should().ContainSingle().Which.Should()
            .Be(changed.Replace("target_job FOR NO KEY UPDATE;", "target_job FOR UPDATE;", StringComparison.Ordinal));
        current.Except(previous).Should().ContainSingle().Which.Should().Be(changed);
        await context.GetService<IMigrator>().MigrateAsync(Correction);
        (await BackgroundJobSchemaInventory.ReadAsync(database.MigrationConnectionString)).Should().Equal(current);
        await context.Database.MigrateAsync();
        AssertCapacityReappliedInventory(await BackgroundJobSchemaInventory.ReadAsync(database.MigrationConnectionString), head);
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }

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
                .BeEquivalentTo(SchemaTableClasses.Existing.Concat(SchemaTableClasses.GlobalIdentity)
                    .Concat(SchemaTableClasses.IdentityPlatform).Concat(SchemaTableClasses.IdentityContextual),
                    "all event-era and independently added Identity data tables survive this Platform-only reversal");
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

    private static void AssertCapacityReappliedInventory(IReadOnlyList<string> actual, IReadOnlyList<string> expected)
    {
        // PostgreSQL retains dropped physical attnums. Reapplying these two
        // additive migrations advances only their named column positions. Every
        // other column and every security/function/constraint fact remains exact.
        const string Prefix = "column:background_job_attempts:";
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var column in new[] { ":transactional_cleanup_at:", ":failure_policy:" })
        {
            var before = expected.Single(item => item.StartsWith(Prefix, StringComparison.Ordinal)
                && item.Contains(column, StringComparison.Ordinal));
            var after = actual.Single(item => item.StartsWith(Prefix, StringComparison.Ordinal)
                && item.Contains(column, StringComparison.Ordinal));
            var beforePosition = int.Parse(before[Prefix.Length..before.IndexOf(column, StringComparison.Ordinal)],
                System.Globalization.CultureInfo.InvariantCulture);
            var afterPosition = int.Parse(after[Prefix.Length..after.IndexOf(column, StringComparison.Ordinal)],
                System.Globalization.CultureInfo.InvariantCulture);
            afterPosition.Should().BeGreaterThanOrEqualTo(beforePosition);
            replacements.Add(after, Prefix + beforePosition.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + after[after.IndexOf(column, StringComparison.Ordinal)..]);
        }
        actual.Select(item => replacements.GetValueOrDefault(item, item)).Order(StringComparer.Ordinal).Should().Equal(expected);
    }
}
