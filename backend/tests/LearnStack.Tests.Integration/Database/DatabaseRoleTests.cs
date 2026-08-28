using FluentAssertions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The four-role model of
/// <see href="../../../../docs/decisions/0003-tenant-isolation-defense-in-depth.md">ADR-0003
/// Amendment 3</see>, asserted against the script the compose stack actually runs.
/// </summary>
/// <remarks>
/// <para>
/// These exist because the roles are the layer everything else rests on and the
/// only one whose absence is silent. With one shared superuser the schema still
/// builds, the migrations still apply, and every isolation test still passes —
/// against policies that constrain nothing, because the connecting role owns the
/// tables. There is no failure to observe until a tenant sees another tenant's
/// rows in production.
/// </para>
/// <para>
/// They assert the script's <b>effects</b> rather than its text. A test that
/// grepped the SQL for <c>NOBYPASSRLS</c> would pass on a script that never ran.
/// </para>
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class DatabaseRoleTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _postgres;

    public DatabaseRoleTests(PostgresFixture postgres) => _postgres = postgres;

    [Theory]
    [InlineData("learnstack_migration", false)]
    [InlineData("learnstack_app", false)]
    [InlineData("learnstack_platform", true)]
    [InlineData("learnstack_outbox_admin", true)]
    public async Task EachRoleExistsWithItsDeclaredBypassPosture(string role, bool expectedBypass)
    {
        await using var connection = await PostgresFixture.OpenAsync(_postgres.MigrationConnectionString);
        await using var command = new NpgsqlCommand(
            "SELECT rolbypassrls, rolcanlogin, rolsuper, rolcreatedb, rolcreaterole FROM pg_roles WHERE rolname = @role",
            (NpgsqlConnection)connection);
        command.Parameters.AddWithValue("role", role);

        await using var reader = await command.ExecuteReaderAsync();

        (await reader.ReadAsync()).Should().BeTrue($"{role} must exist");
        reader.GetBoolean(0).Should().Be(expectedBypass);
        reader.GetBoolean(1).Should().BeTrue($"{role} logs in for itself — per-role settings are applied at login and do not follow SET ROLE");

        // rolbypassrls alone is not the question. A SUPERUSER bypasses row security
        // whatever that column says, so asserting only the attribute would let the
        // whole model be defeated by one CREATE ROLE … SUPERUSER and still pass.
        reader.GetBoolean(2).Should().BeFalse($"{role} must not be a superuser: a superuser bypasses RLS regardless of rolbypassrls");
        reader.GetBoolean(3).Should().BeFalse($"{role} has no reason to create databases");
        reader.GetBoolean(4).Should().BeFalse($"{role} creating roles could grant itself the bypass");
    }

    [Fact]
    public async Task TheApplicationRoleIsNotAMemberOfTheBypassRoles()
    {
        // Membership would make BYPASSRLS a standing capability of the request-path
        // role, reachable from any code path that can execute SET ROLE — and a plain
        // SET ROLE survives COMMIT on a transaction-pooled connection, into the next
        // tenant's request. EnterPlatformAdminScope uses a second credentialed
        // connection instead.
        await using var connection = await PostgresFixture.OpenAsync(_postgres.MigrationConnectionString);
        // pg_has_role, not pg_auth_members: membership is TRANSITIVE, and a chain
        // through a third role would satisfy a direct-membership query while still
        // handing learnstack_app the bypass. The catalogue join tests the shape of
        // the graph; this tests the question actually being asked.
        await using var command = new NpgsqlCommand(
            """
            SELECT pg_has_role('learnstack_app', 'learnstack_platform', 'USAGE')
                OR pg_has_role('learnstack_app', 'learnstack_outbox_admin', 'USAGE')
                OR pg_has_role('learnstack_app', 'learnstack_migration', 'USAGE')
            """, (NpgsqlConnection)connection);

        (await command.ExecuteScalarAsync()).Should().Be(false);
    }

    [Fact]
    public async Task OnlyTheMigrationRoleMayCreateInSchemaPublic()
    {
        // The asymmetry the first migration depends on. Since PostgreSQL 15 the
        // public schema grants CREATE to nobody by default; without the grant the
        // migration fails with "permission denied for schema public", and the
        // tempting fix — granting the application role CREATE, or making it the
        // owner — reinstates the arrangement FORCE ROW LEVEL SECURITY defeats.
        await using var migration = await PostgresFixture.OpenAsync(_postgres.MigrationConnectionString);
        await using var create = new NpgsqlCommand(
            "CREATE TABLE role_probe (id int)", (NpgsqlConnection)migration);
        await create.ExecuteNonQueryAsync();

        await using var app = await PostgresFixture.OpenAsync(_postgres.AppConnectionString);
        await using var denied = new NpgsqlCommand(
            "CREATE TABLE app_probe (id int)", (NpgsqlConnection)app);

        var act = async () => await denied.ExecuteNonQueryAsync();

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task TheMigrationRoleOwnsWhatItCreates_AndOwnershipGrantsNoBypass()
    {
        // No ALTER TABLE ... OWNER TO appears in any migration: the role that runs
        // them IS the owner, and seeing an explicit OWNER TO is a sign the migration
        // ran as the wrong role. Ownership still buys no bypass, because the
        // migration role is NOBYPASSRLS and every table declares FORCE.
        await using var migration = await PostgresFixture.OpenAsync(_postgres.MigrationConnectionString);
        await using var create = new NpgsqlCommand(
            "CREATE TABLE owner_probe (id int)", (NpgsqlConnection)migration);
        await create.ExecuteNonQueryAsync();

        await using var owner = new NpgsqlCommand(
            "SELECT tableowner FROM pg_tables WHERE tablename = 'owner_probe'",
            (NpgsqlConnection)migration);

        (await owner.ExecuteScalarAsync()).Should().Be("learnstack_migration");

        // The second half of the name, which the first version of this test did not
        // assert. FORCE ROW LEVEL SECURITY is what makes ownership grant no bypass,
        // and the owner being NOBYPASSRLS is what makes FORCE meaningful — so the
        // owner is subjected to its own policies like anyone else.
        await using var force = new NpgsqlCommand(
            """
            ALTER TABLE owner_probe ENABLE ROW LEVEL SECURITY;
            ALTER TABLE owner_probe FORCE  ROW LEVEL SECURITY;
            CREATE POLICY owner_probe_never ON owner_probe USING (false) WITH CHECK (false);
            INSERT INTO owner_probe VALUES (1);
            """, (NpgsqlConnection)migration);

        var act = async () => await force.ExecuteNonQueryAsync();

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "the owner is refused by its own policy — that is what FORCE buys");
    }

    [Fact]
    public async Task ANewTableGrantsTheApplicationRoleNothingUntilItsMigrationSaysSo()
    {
        // There is deliberately no ALTER DEFAULT PRIVILEGES. A table nobody granted
        // fails loudly with `permission denied` rather than silently inheriting DML
        // — and can never silently widen a BYPASSRLS role, whose only bound is the
        // grant matrix.
        await using var migration = await PostgresFixture.OpenAsync(_postgres.MigrationConnectionString);
        await using var create = new NpgsqlCommand(
            "CREATE TABLE ungranted_probe (id int)", (NpgsqlConnection)migration);
        await create.ExecuteNonQueryAsync();

        await using var app = await PostgresFixture.OpenAsync(_postgres.AppConnectionString);
        await using var read = new NpgsqlCommand(
            "SELECT count(*) FROM ungranted_probe", (NpgsqlConnection)app);

        var act = async () => await read.ExecuteScalarAsync();

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task ABypassRoleWithNoGrantIsStillRefused()
    {
        // BYPASSRLS bypasses policies, not GRANTs. Stating it as a test because the
        // grant matrix is the whole of the bound on both bypass roles, and a reader
        // who believes the attribute is the bound will not maintain the matrix.
        await using var migration = await PostgresFixture.OpenAsync(_postgres.MigrationConnectionString);
        await using var create = new NpgsqlCommand(
            "CREATE TABLE bypass_probe (id int)", (NpgsqlConnection)migration);
        await create.ExecuteNonQueryAsync();

        await using var platform = await PostgresFixture.OpenAsync(_postgres.PlatformConnectionString);
        await using var read = new NpgsqlCommand(
            "SELECT count(*) FROM bypass_probe", (NpgsqlConnection)platform);

        var act = async () => await read.ExecuteScalarAsync();

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task EveryRoleCredentialActuallyLogsIn()
    {
        // All four, including learnstack_outbox_admin, whose connection string no
        // other case opens. A password the script never set, or set to something
        // else, is invisible until the dispatcher tries to start — in Phase 02b,
        // far from here.
        foreach (var connectionString in new[]
                 {
                     _postgres.MigrationConnectionString,
                     _postgres.AppConnectionString,
                     _postgres.PlatformConnectionString,
                     _postgres.OutboxConnectionString,
                 })
        {
            await using var connection = await PostgresFixture.OpenAsync(connectionString);
            await using var who = new NpgsqlCommand("SELECT current_user", (NpgsqlConnection)connection);

            var actual = (string?)await who.ExecuteScalarAsync();

            connectionString.Should().Contain($"Username={actual}",
                "the role that authenticated must be the role the connection string named");
        }
    }

    [Fact]
    public async Task TheRolesScriptIsIdempotent()
    {
        // The compose init directory runs once per fresh volume, but a developer
        // re-running it by hand against an existing cluster must not get
        // `role "learnstack_app" already exists`, which under ON_ERROR_STOP aborts
        // the rest of the script. CREATE ROLE has no IF NOT EXISTS, so the guard is
        // the \gexec form — and the only way to know it works is to run it twice.
        //
        // Asserted by re-running rather than by grepping the file for the guard: a
        // text assertion passes on a script that never executes, and the first
        // version of this test failed on the script's own COMMENT about a thing it
        // does not do.
        var result = await _postgres.RunRolesScriptAgainAsync();

        result.ExitCode.Should().Be(0, "a re-run must be a no-op, not an error: {0}", result.Stderr);
    }

    [Fact]
    public async Task NoDefaultPrivilegesExist_SoAnUngrantedTableIsAlwaysLoud()
    {
        // The effect of there being no ALTER DEFAULT PRIVILEGES anywhere.
        // pg_default_acl holds one row per default-privilege rule; empty means a
        // table nobody granted inherits nothing, so it fails with
        // `permission denied` instead of silently widening a BYPASSRLS role —
        // whose only bound is the grant matrix.
        await using var connection = await PostgresFixture.OpenAsync(_postgres.MigrationConnectionString);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_default_acl", (NpgsqlConnection)connection);

        (await command.ExecuteScalarAsync()).Should().Be(0L);
    }
}
