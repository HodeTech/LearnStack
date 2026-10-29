using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class HangfireStorageTests
{
    [Xunit.Fact]
    public async Task Queued_schedule_guard_reverses_and_reapplies_on_an_already_migrated_store()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres, applyMigrations: false);
        await MigrationChains.ApplyTenancyAsync(database.MigrationConnectionString);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        await using var context = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(database.MigrationConnectionString,
                options => options.MigrationsHistoryTable(PlatformDbContextFactory.HistoryTable)).Options);
        const string queuedSchedule = "20261016170000_allow_queued_hangfire_schedule";
        await context.GetService<IMigrator>().MigrateAsync(queuedSchedule);
        (await context.Database.GetAppliedMigrationsAsync()).Last().Should().Be(queuedSchedule);
        await using var connection = await source.OpenConnectionAsync();
        const string queued = "SELECT public.fn_hangfire_metadata_valid('set','{\"key\":\"schedule\",\"score\":0,\"value\":\"transactional:1\"}')";
        const string legacy = "SELECT public.fn_hangfire_metadata_valid('set','{\"key\":\"schedule\",\"score\":0,\"value\":\"1\"}')";
        (await ScalarAsync(connection, queued)).Should().Be(true);
        await context.GetService<IMigrator>().MigrateAsync("20261015213241_add_guarded_hangfire_storage");
        (await ScalarAsync(connection, queued)).Should().Be(false);
        (await ScalarAsync(connection, legacy)).Should().Be(true);
        var outdated = () => HangfireStorageBootstrap.Create(source, BackgroundJobInfrastructureRegistry.CreateDefault(), Diagnostics());
        outdated.Should().Throw<HangfireStorageRefusedException>();
        await context.GetService<IMigrator>().MigrateAsync();
        (await ScalarAsync(connection, queued)).Should().Be(true);
        var upgraded = () => HangfireStorageBootstrap.Create(source, BackgroundJobInfrastructureRegistry.CreateDefault(), Diagnostics());
        upgraded.Should().NotThrow();
    }
}
