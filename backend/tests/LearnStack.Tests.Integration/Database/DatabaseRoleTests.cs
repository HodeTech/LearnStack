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
            "SELECT rolbypassrls, rolcanlogin FROM pg_roles WHERE rolname = @role", (NpgsqlConnection)connection);
        command.Parameters.AddWithValue("role", role);

        await using var reader = await command.ExecuteReaderAsync();

        (await reader.ReadAsync()).Should().BeTrue($"{role} must exist");
        reader.GetBoolean(0).Should().Be(expectedBypass);
        reader.GetBoolean(1).Should().BeTrue($"{role} logs in for itself — per-role settings are applied at login and do not follow SET ROLE");
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
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM pg_auth_members m
            JOIN pg_roles member ON member.oid = m.member
            JOIN pg_roles granted ON granted.oid = m.roleid
            WHERE member.rolname = 'learnstack_app'
              AND granted.rolname IN ('learnstack_platform', 'learnstack_outbox_admin')
            """, (NpgsqlConnection)connection);

        (await command.ExecuteScalarAsync()).Should().Be(0L);
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
