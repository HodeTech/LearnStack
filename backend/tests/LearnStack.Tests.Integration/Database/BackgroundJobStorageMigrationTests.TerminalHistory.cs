using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobStorageMigrationTests
{
    [Fact]
    public async Task Terminal_history_guard_down_restores_only_the_preceding_effective_function_and_reapplies_exactly()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var context = Platform(database);
        var latest = await BackgroundJobSchemaInventory.ReadAsync(database.MigrationConnectionString);
        var migrator = context.GetService<IMigrator>();
        const string target = "20261017163051_add_external_job_terminal_history_guard";
        await migrator.MigrateAsync(target);
        var current = await BackgroundJobSchemaInventory.ReadAsync(database.MigrationConnectionString);
        await migrator.MigrateAsync("20261017134946_add_background_job_quarantine_observation");
        var prior = await BackgroundJobSchemaInventory.ReadAsync(database.MigrationConnectionString);
        var added = current.Except(prior).Should().ContainSingle().Which;
        var removed = prior.Except(current).Should().ContainSingle().Which;
        added.Should().Contain("FUNCTION public.fn_background_jobs_transition()");
        removed.Should().Contain("FUNCTION public.fn_background_jobs_transition()");
        added.Should().Contain("a terminal request retains its original reason")
            .And.Contain("no-plan terminal completion requires complete positive no-start history");
        removed.Should().NotContain("a terminal request retains its original reason")
            .And.NotContain("no-plan terminal completion requires complete positive no-start history");
        removed.Should().Contain("an unowned recovery wake retains its scheduling cycle",
            "Down restores the effective recovery-wake function, preserving its later policy and scheduling guards");
        await migrator.MigrateAsync(target);
        (await BackgroundJobSchemaInventory.ReadAsync(database.MigrationConnectionString)).Should().Equal(current,
            "the function-only forward migration preserves every RLS, grant, index and trigger fact");
        await context.Database.MigrateAsync();
        (await BackgroundJobSchemaInventory.ReadAsync(database.MigrationConnectionString)).Should().Equal(latest,
            "later migrations and their complete job inventory must also return unchanged");
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }
}
