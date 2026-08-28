using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LearnStack.Infrastructure.Persistence;

/// <summary>
/// Builds a <see cref="PlatformDbContext"/> for <c>dotnet ef</c> at design time.
/// </summary>
/// <remarks>
/// Same contract, and same reason, as Tenancy's: it reads
/// <c>ConnectionStrings__Migration</c> and nothing else, with no fallback to
/// <c>ConnectionStrings__Default</c>. A fallback is how the runtime role becomes
/// the table owner by accident, which is the arrangement
/// <c>FORCE ROW LEVEL SECURITY</c> exists to defeat.
/// </remarks>
public sealed class PlatformDbContextFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    private const string ConnectionStringVariable = "ConnectionStrings__Migration";

    public PlatformDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            ReadConnectionArgument(args)
            ?? Environment.GetEnvironmentVariable(ConnectionStringVariable);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"{ConnectionStringVariable} is not set and no --connection was passed. "
                + "Migrations run as learnstack_migration, which owns every table; "
                + "`make migrate` is its sanctioned carrier.");
        }

        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                // Its own history table, so the two chains advance independently:
                // a module's migration must not be blocked by, or block, the
                // platform's.
                npgsql.MigrationsHistoryTable("__ef_migrations_history_platform"))
            .Options;

        return new PlatformDbContext(options);
    }

    private static string? ReadConnectionArgument(string[] args)
    {
        if (args is null)
        {
            return null;
        }

        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], "--connection", StringComparison.Ordinal))
            {
                return args[index + 1];
            }
        }

        return null;
    }
}
