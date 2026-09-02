using System.Data.Common;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The trait that partitions this assembly between CI's two backend jobs.
/// </summary>
/// <remarks>
/// <para>
/// <c>LearnStack.Tests.Integration</c> holds two kinds of test. The
/// <c>WebApplicationFactory</c> HTTP tests need no Docker and run in the
/// <c>backend</c> job with everything else. Anything carrying this trait needs a
/// real Docker socket and runs in <c>backend-integration</c>.
/// </para>
/// <para>
/// <b>The two filters are exact complements.</b> `backend` runs
/// <c>--filter "Requires!=Docker"</c> and `backend-integration` runs
/// <c>--filter "Requires=Docker"</c>, so every test in the assembly runs in
/// exactly one of them. No count is written here on purpose: one was, and it
/// went stale in the same commit that added a test.
/// </para>
/// <para>
/// The value is a constant because a mistyped one lands in the <b>wrong</b> job,
/// <b>silently</b>. <c>Requires!=Docker</c> matches every test whose value differs
/// — including <c>Requires=Dockerr</c> — so a mis-traited Docker test runs in
/// <c>backend</c>; and both jobs run on <c>ubuntu-latest</c>, which carries a
/// Docker socket natively, so it starts its container and passes. Nothing goes
/// red; the Docker suite just stops being where the Docker tests live. One
/// constant removes the typo, and
/// <c>Every_Database_Test_Carries_The_Docker_Trait</c> removes the omission.
/// </para>
/// </remarks>
internal static class RequiresDocker
{
    public const string Key = "Requires";
    public const string Value = "Docker";
}

/// <summary>
/// A real PostgreSQL 18 with LearnStack's four-role model provisioned, shared by
/// every test that needs a database.
/// </summary>
/// <remarks>
/// <para>
/// <b>Postgres only.</b> Not Valkey, not Kafka — nothing the backend runs calls
/// either, and both sit behind the gated compose profile
/// (<see href="../../../../docs/decisions/0035-demand-gated-infrastructure.md">ADR-0035</see>).
/// A fixture that started them would make every data test pay for containers no
/// assertion touches.
/// </para>
/// <para>
/// <b>The four roles are the point.</b> The container's own superuser owns
/// nothing LearnStack uses: <see cref="MigrationConnectionString"/> owns every
/// table and <see cref="AppConnectionString"/> is what tests connect as. A test
/// that connected as the owner — or as either <c>BYPASSRLS</c> role — would pass
/// against policies that constrain nothing, which is the failure mode
/// <see href="../../../../docs/decisions/0003-tenant-isolation-defense-in-depth.md">ADR-0003
/// Amendment 3</see> names by hand. That is why this fixture exposes four
/// connection strings and not one.
/// </para>
/// <para>
/// The roles are created by the same SQL the compose stack runs, read from
/// <c>infra/compose/postgres-init/02-create-roles.sql</c> rather than restated
/// here — a second copy is a second thing to keep true, and the copy is what
/// would drift. It is executed through <c>psql</c> inside the container because
/// the script uses psql client directives (<c>\getenv</c>, <c>\gexec</c>) that a
/// Npgsql command cannot interpret.
/// </para>
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string Database = "learnstack";
    private const string MigrationPassword = "migration-test";
    private const string AppPassword = "app-test";
    private const string PlatformPassword = "platform-test";
    private const string OutboxPassword = "outbox-test";
    private const string ContainerScriptPath = "/tmp/02-create-roles.sql";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        // Pinned to the tag infra/compose/dev.yml runs. A fixture on a different
        // major would test a database no deployment uses.
        .WithImage("postgres:18.4-alpine")
        .WithDatabase(Database)
        .WithUsername("postgres")
        .WithPassword("postgres")
        .WithEnvironment("LEARNSTACK_MIGRATION_PW", MigrationPassword)
        .WithEnvironment("LEARNSTACK_APP_PW", AppPassword)
        .WithEnvironment("LEARNSTACK_PLATFORM_PW", PlatformPassword)
        .WithEnvironment("LEARNSTACK_OUTBOX_PW", OutboxPassword)
        .Build();

    /// <summary>Owns every table. `dotnet ef database update` and nothing else.</summary>
    public string MigrationConnectionString => For("learnstack_migration", MigrationPassword);

    /// <summary>What a test connects as. <c>NOBYPASSRLS</c>.</summary>
    public string AppConnectionString => For("learnstack_app", AppPassword);

    /// <summary><c>BYPASSRLS</c>; only <c>PlatformAdminScope</c>'s equivalent.</summary>
    public string PlatformConnectionString => For("learnstack_platform", PlatformPassword);

    /// <summary><c>BYPASSRLS</c>; only the outbox dispatcher's equivalent.</summary>
    public string OutboxConnectionString => For("learnstack_outbox_admin", OutboxPassword);

    /// <summary>
    /// The container's superuser. <b>Not for any test that asserts about isolation.</b>
    /// </summary>
    /// <remarks>
    /// It exists for the one thing no four-role credential can do: change a role's own
    /// attributes, so a test can take <c>BYPASSRLS</c> off <c>learnstack_platform</c> and
    /// prove the guard that refuses a non-bypassing platform credential actually fires.
    /// <c>learnstack_migration</c> cannot — it holds neither <c>CREATEROLE</c> nor
    /// <c>ADMIN</c> on the role, which is itself the four-role model working. Any test
    /// that <i>reads</i> tenant data through this connection would pass with every policy
    /// inert and prove nothing, which is the rule CLAUDE.md states by hand.
    /// </remarks>
    public string SuperuserConnectionString => For("postgres", "postgres");

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var script = await File.ReadAllTextAsync(RepositoryPath.RolesScript());
        await _container.CopyAsync(System.Text.Encoding.UTF8.GetBytes(script), ContainerScriptPath);

        // ON_ERROR_STOP so a failure here fails the fixture rather than leaving a
        // half-provisioned cluster that produces a confusing error in the first
        // test that touches it.
        var result = await _container.ExecAsync(
        [
            "psql", "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", Database, "-f", ContainerScriptPath,
        ]);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Provisioning the four database roles failed (exit {result.ExitCode}).{Environment.NewLine}"
                + $"{result.Stderr}{Environment.NewLine}{result.Stdout}");
        }
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    /// <summary>
    /// Runs the roles script a second time against the already-provisioned
    /// cluster, so a test can assert that a re-run is a no-op rather than an error.
    /// </summary>
    public Task<DotNet.Testcontainers.Containers.ExecResult> RunRolesScriptAgainAsync() =>
        _container.ExecAsync(
        [
            "psql", "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", Database,
            "-f", ContainerScriptPath,
        ]);

    /// <summary>
    /// Runs one statement as the container's superuser.
    /// </summary>
    /// <remarks>
    /// For the few assertions that need to change the cluster itself rather than
    /// its data — <c>ALTER ROLE … BYPASSRLS</c> is the case — which none of the
    /// four LearnStack roles may do: <c>learnstack_migration</c> owns tables, not
    /// roles, and PostgreSQL requires <c>CREATEROLE</c> plus <c>ADMIN OPTION</c>.
    /// Every caller reverts what it did in a <c>finally</c>: the cluster is shared,
    /// and an isolation suite running against a bypass role is vacuous.
    /// </remarks>
    public async Task ExecuteAsSuperuserAsync(string sql)
    {
        var result = await _container.ExecAsync(
            ["psql", "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", Database, "-c", sql]);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Superuser statement failed (exit {result.ExitCode}): {sql}{Environment.NewLine}"
                + $"{result.Stderr}{Environment.NewLine}{result.Stdout}");
        }
    }

    /// <summary>Opens a connection as the given role and returns it open.</summary>
    public static async Task<DbConnection> OpenAsync(
        string connectionString, CancellationToken cancellationToken = default)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private string For(string role, string password) =>
        $"Host={_container.Hostname};Port={_container.GetMappedPublicPort(5432)};"
        + $"Database={Database};Username={role};Password={password};"
        // The fixture's roles are created per container, so pooling across
        // connection strings is safe — but Include Error Detail turns a policy
        // rejection into a message that names the constraint, which is the
        // difference between a useful failure and "23514".
        + "Include Error Detail=true";
}

/// <summary>
/// Locates repository files from a test host whose working directory is
/// <c>bin/Debug/net10.0</c>.
/// </summary>
/// <remarks>
/// <para>
/// A relative path resolves against that directory, where nothing exists — the
/// query silently yields nothing and the assertion over it passes. Walking up to
/// a marker is the only form that fails loudly when it is wrong.
/// </para>
/// <para>
/// <c>LearnStack.Tests.Architecture</c> has its own <c>RepositoryPaths</c> doing
/// the same walk. This is a deliberate duplicate rather than a shared helper: the
/// two assemblies reference nothing in common, and a shared test-utility project
/// referenced by both would be a fourth test project to justify against
/// <see href="../../../../docs/standards/06-testing.md">Standards 06</see>'s three.
/// Both markers are files that only exist at the root.
/// </para>
/// </remarks>
internal static class RepositoryPath
{
    public static string RolesScript() =>
        Path.Combine(Root(), "infra", "compose", "postgres-init", "02-create-roles.sql");

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        // Two markers, because either alone is reachable in a layout that is not
        // the repository: a published output can carry a stray CLAUDE.md, and a
        // git worktree of a submodule can carry a .git file. Both together, at the
        // same level, are the root.
        while (directory is not null
               && !(File.Exists(Path.Combine(directory.FullName, "CLAUDE.md"))
                    && Directory.Exists(Path.Combine(directory.FullName, "infra", "compose"))))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException(
                $"Repository root not found above {AppContext.BaseDirectory}. "
                + "This fixture reads infra/compose/postgres-init/02-create-roles.sql "
                + "from the working tree; it cannot run from a published layout.");
    }
}
