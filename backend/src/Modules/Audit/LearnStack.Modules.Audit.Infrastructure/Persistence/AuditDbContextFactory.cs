using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LearnStack.Modules.Audit.Infrastructure.Persistence;

/// <summary>
/// Builds an <see cref="AuditDbContext"/> for <c>dotnet ef</c> at design time.
/// </summary>
/// <remarks>
/// <para>
/// <b>So the tooling never resolves the runtime connection string.</b> Without it,
/// <c>dotnet ef --startup-project backend/src/LearnStack.Api</c> builds the API's
/// service provider and takes <c>ConnectionStrings:Default</c> — the
/// <c>learnstack_app</c> role, which holds <c>USAGE</c> but not <c>CREATE</c> on schema
/// <c>public</c>. The migration then fails with <c>permission denied for schema
/// public</c>, and the obvious local fix for that error — granting the runtime role
/// <c>CREATE</c>, or making it the owner — is exactly the arrangement
/// <c>FORCE ROW LEVEL SECURITY</c> exists to defeat
/// (<see href="../../../../../../docs/standards/05-database.md">Database Standards
/// § Database roles</see>).
/// </para>
/// <para>
/// <b>The fourth chain, and the first one whose order matters.</b> Every earlier chain
/// is independent of every other, so <c>make migrate</c> could apply them in any order.
/// This one is not: <c>audit_config</c> references <c>tenants</c>, which the Tenancy
/// chain creates, and the glob that finds the chains expands alphabetically —
/// <c>Modules/Audit</c> before <c>Modules/Tenancy</c>. The recipe therefore names
/// Tenancy first
/// (<see href="../../../../../../docs/standards/05-database.md">Database Standards
/// § Migrations</see>). <c>audit_log</c> carries no such reference and would not have
/// cared.
/// </para>
/// </remarks>
public sealed class AuditDbContextFactory : IDesignTimeDbContextFactory<AuditDbContext>
{
    private const string ConnectionStringVariable = "ConnectionStrings__Migration";

    /// <summary>
    /// The migration history table for this chain.
    /// </summary>
    /// <remarks>
    /// Public and read by the test fixtures rather than repeated as a literal beside
    /// them: <c>dotnet ef</c> — and therefore <c>make migrate</c> — only ever goes
    /// through this factory, so a fixture with its own copy would assert the name it
    /// chose while the deployment path drifted underneath it.
    /// </remarks>
    public const string HistoryTable = "__ef_migrations_history_audit";

    public AuditDbContext CreateDbContext(string[] args)
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

        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(HistoryTable))
            .Options;

        // Unresolved, because `dotnet ef` has no request and needs no tenant: a global
        // query filter emits no DDL, so the model this builds is byte-identical whatever
        // context it is handed.
        return new AuditDbContext(options, StaticTenantContextAccessor.Unresolved);
    }
}
