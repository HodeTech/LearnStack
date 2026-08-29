using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LearnStack.Infrastructure.Persistence;

/// <summary>
/// Builds a <see cref="PlatformDbContext"/> for <c>dotnet ef</c> at design time.
/// </summary>
/// <remarks>
/// Same contract, and same reason, as Tenancy's: it reads
/// <c>ConnectionStrings__Migration</c> from the environment and nothing else, with
/// no fallback to <c>ConnectionStrings__Default</c>. A fallback is how the runtime
/// role becomes the table owner by accident, which is the arrangement
/// <c>FORCE ROW LEVEL SECURITY</c> exists to defeat. <c>dotnet ef --connection</c>
/// never reaches <c>args</c> — EF applies it after the factory returns — so the
/// exported variable is the only thing that lets this construct.
/// </remarks>
public sealed class PlatformDbContextFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    private const string ConnectionStringVariable = "ConnectionStrings__Migration";

    /// <summary>
    /// The migration history table for this chain — its own, so the two chains
    /// advance independently.
    /// </summary>
    /// <remarks>
    /// Public for the same reason as Tenancy's: the fixtures reference it instead
    /// of repeating the literal, so the assertion is against the name the
    /// deployment path uses.
    /// </remarks>
    public const string HistoryTable = "__ef_migrations_history_platform";

    public PlatformDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"{ConnectionStringVariable} is not set in the environment. "
                + "Migrations run as learnstack_migration, which owns every table; "
                + "`make migrate` is its sanctioned carrier.");
        }

        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                // Its own history table, so the two chains advance independently:
                // a module's migration must not be blocked by, or block, the
                // platform's.
                npgsql.MigrationsHistoryTable(HistoryTable))
            .Options;

        return new PlatformDbContext(options);
    }
}
