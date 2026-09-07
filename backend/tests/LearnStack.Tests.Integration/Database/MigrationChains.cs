using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Customization.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// Every migration chain this repository deploys, applied in one place.
/// </summary>
/// <remarks>
/// <para>
/// <b>One place, because three fixtures had their own copy and the third was
/// wrong.</b> A fixture that applies a subset does not fail — it produces a
/// smaller schema, and every catalogue sweep, every count and every seed silently
/// narrows to it. That happened twice: <c>SchemaFixture</c> applied two of three
/// chains and eight assertions stopped covering the tables Packet 8 added, and
/// <c>TenantIsolationFixture</c> did the same and its seed then failed on a table
/// that did not exist. A chain that ships is a chain this applies.
/// </para>
/// <para>
/// The history table names come from the design-time factories, which are what
/// <c>dotnet ef</c> — and therefore <c>make migrate</c> — actually use. A fixture
/// repeating the literal would assert the name it wrote itself, and the deployment
/// path could drift underneath a green suite.
/// </para>
/// </remarks>
internal static class MigrationChains
{
    /// <summary>Applies every chain, as <c>learnstack_migration</c>.</summary>
    public static async Task ApplyAllAsync(string migrationConnectionString)
    {
        await using (var tenancy = new TenancyDbContext(
            Options<TenancyDbContext>(migrationConnectionString, TenancyDbContextFactory.HistoryTable),
            StaticTenantContextAccessor.Unresolved))
        {
            await tenancy.Database.MigrateAsync();
        }

        await using (var platform = new PlatformDbContext(
            Options<PlatformDbContext>(migrationConnectionString, PlatformDbContextFactory.HistoryTable)))
        {
            await platform.Database.MigrateAsync();
        }

        await using var customization = new CustomizationDbContext(
            Options<CustomizationDbContext>(
                migrationConnectionString, CustomizationDbContextFactory.HistoryTable),
            StaticTenantContextAccessor.Unresolved);

        await customization.Database.MigrateAsync();
    }

    /// <summary>The history table every chain declares, for a fixture that counts them.</summary>
    public static readonly string[] HistoryTables =
    [
        TenancyDbContextFactory.HistoryTable,
        PlatformDbContextFactory.HistoryTable,
        CustomizationDbContextFactory.HistoryTable,
    ];

    private static DbContextOptions<TContext> Options<TContext>(
        string connectionString, string historyTable)
        where TContext : DbContext =>
        new DbContextOptionsBuilder<TContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(historyTable))
            .Options;
}
