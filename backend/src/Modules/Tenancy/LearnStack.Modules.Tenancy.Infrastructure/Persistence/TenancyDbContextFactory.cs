using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence;

/// <summary>
/// Builds a <see cref="TenancyDbContext"/> for <c>dotnet ef</c> at design time.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists so the tooling never resolves the runtime connection string.</b>
/// Without it, <c>dotnet ef --startup-project backend/src/LearnStack.Api</c> builds
/// the API's service provider and takes <c>ConnectionStrings:Default</c> — the
/// <c>learnstack_app</c> role, which holds <c>USAGE</c> but not <c>CREATE</c> on
/// schema <c>public</c>. The migration then fails with
/// <c>permission denied for schema public</c>, and the obvious local fix for that
/// error — granting the runtime role <c>CREATE</c>, or making it the owner — is
/// exactly the arrangement <c>FORCE ROW LEVEL SECURITY</c> exists to defeat
/// (<see href="../../../../../../docs/standards/05-database.md">Database Standards
/// § Database roles</see>).
/// </para>
/// <para>
/// It reads <c>ConnectionStrings__Migration</c> from the environment and nothing
/// else. No <c>appsettings</c>, no user secrets, no fallback to
/// <c>ConnectionStrings__Default</c>: a fallback is how the wrong role gets used
/// by accident, and the whole point of the four-role split is that using the
/// wrong one is loud. <c>make migrate</c> is the sanctioned carrier and exports
/// the value.
/// </para>
/// <para>
/// <b><c>dotnet ef --connection</c> does not reach this method.</b> The tool
/// consumes that option in its own parser and applies it to the context after the
/// factory has returned, so <c>args</c> never carries it and a factory that waited
/// for it would throw first — measured, on a workstation whose value lives only in
/// <c>.env</c>. The environment variable is what lets the context be constructed;
/// the flag is what EF then applies to it.
/// </para>
/// <para>
/// Design-time only. Nothing at runtime constructs a context this way — ADR-0040
/// has every module context built on the connection <c>IUnitOfWork</c> owns.
/// </para>
/// </remarks>
public sealed class TenancyDbContextFactory : IDesignTimeDbContextFactory<TenancyDbContext>
{
    private const string ConnectionStringVariable = "ConnectionStrings__Migration";

    /// <summary>
    /// The migration history table for this chain.
    /// </summary>
    /// <remarks>
    /// Public and referenced by the test fixtures rather than repeated as a
    /// literal beside them. `dotnet ef` — and therefore `make migrate` — only ever
    /// goes through this factory, so a fixture that wrote its own copy would
    /// assert the name it chose while the deployment path drifted underneath it.
    /// </remarks>
    public const string HistoryTable = "__ef_migrations_history_tenancy";

    public TenancyDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"{ConnectionStringVariable} is not set in the environment. "
                + "Migrations run as learnstack_migration, which owns every table; the value "
                + "is in .env.example and `make migrate` is its sanctioned carrier. "
                + "`dotnet ef --connection` does not help here — EF applies it after this "
                + "factory returns. Do not point this at ConnectionStrings__Default — that "
                + "role cannot CREATE in schema public, and granting it that is the ownership "
                + "mistake the four-role split exists to prevent.");
        }

        var options = new DbContextOptionsBuilder<TenancyDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable(HistoryTable))
            .Options;

        return new TenancyDbContext(options);
    }
}
