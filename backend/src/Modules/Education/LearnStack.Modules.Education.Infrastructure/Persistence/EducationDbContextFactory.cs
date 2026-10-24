using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LearnStack.Modules.Education.Infrastructure.Persistence;

/// <summary>Builds the Education migration chain without runtime configuration.</summary>
public sealed class EducationDbContextFactory : IDesignTimeDbContextFactory<EducationDbContext>
{
    private const string ConnectionStringVariable = "ConnectionStrings__Migration";

    /// <summary>The independent Education chain's migration history table.</summary>
    public const string HistoryTable = "__ef_migrations_history_education";

    public EducationDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // This const is the public environment key name, never its credential value.
#pragma warning disable LS0002
            throw new InvalidOperationException(
                $"{ConnectionStringVariable} is not set in the environment. "
                + "Migrations run as learnstack_migration; use `make migrate`. "
                + "Runtime connection strings are never a migration fallback.");
#pragma warning restore LS0002
        }

        var options = new DbContextOptionsBuilder<EducationDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(HistoryTable))
            .Options;
        return new EducationDbContext(options, StaticTenantContextAccessor.Unresolved);
    }
}
