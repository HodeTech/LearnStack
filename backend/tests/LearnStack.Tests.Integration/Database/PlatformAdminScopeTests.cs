using FluentAssertions;
using LearnStack.Infrastructure.MultiTenancy;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// <c>PlatformAdminScope</c> against a real database — the one sanctioned bypass.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are provenance tests, not isolation tests, and the distinction is the point.</b>
/// CLAUDE.md forbids running an isolation test as a <c>BYPASSRLS</c> role because such a
/// test passes identically when every policy is inert — so "the scope sees both tenants"
/// proves nothing on its own. What each case below asserts instead is <i>where the
/// visibility comes from</i>: that the same query on an application connection sees less,
/// on the same data, at the same moment. A dropped policy would make both sides equal and
/// turn these red, which is the property an isolation-shaped assertion cannot offer.
/// </para>
/// <para>
/// The scope is constructed directly with a permissive gate double. The shipped gate
/// refuses everyone — correctly, there is no principal — and the property under test here
/// is the connection, not the gate; the gate's own behaviour is asserted separately and
/// without Docker.
/// </para>
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class PlatformAdminScopeTests
{
    private readonly SchemaFixture _schema;

    public PlatformAdminScopeTests(SchemaFixture schema) => _schema = schema;

    [Fact]
    public async Task The_Scope_Sees_What_An_Application_Connection_Cannot()
    {
        // Both counts, on the same table, in the same moment, from the two credentials.
        // The application connection sets no tenant context, so its policy predicate is
        // NULL and it sees nothing; the platform connection bypasses the policy entirely.
        // Asserting the PAIR is what makes this evidence: if the policies were dropped,
        // the application side would rise to match and this fails.
        await using var platformSource = PlatformSource(_schema.Postgres.PlatformConnectionString);
        await using var appSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);

        await using var handle = await Build(platformSource).EnterAsync(
            "test:cross-tenant-visibility", CancellationToken.None);

        var seenByPlatform = await CountOrganizationsAsync(handle.Connection, handle.Transaction);

        await using var appConnection = await appSource.OpenConnectionAsync(CancellationToken.None);
        var seenByApplication = await CountOrganizationsAsync(appConnection, transaction: null);

        seenByApplication.Should().Be(0,
            "with no tenant context the policy predicate is NULL and the row is filtered out");
        seenByPlatform.Should().BeGreaterThan(0,
            "the platform role bypasses the policy that filtered the application connection");
    }

    [Fact]
    public async Task The_Scope_Connects_As_The_Platform_Role_On_Its_Own_Connection()
    {
        // The claim ADR-0003 makes by name: a second connection, never SET ROLE. Asserted
        // by asking the server who it thinks we are, and by checking the backend process
        // id differs from the application connection's — a SET ROLE would share one.
        await using var platformSource = PlatformSource(_schema.Postgres.PlatformConnectionString);
        await using var appSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);

        await using var appConnection = await appSource.OpenConnectionAsync(CancellationToken.None);
        var appBackend = await ScalarAsync<int>(appConnection, null, "SELECT pg_backend_pid()");
        var appUser = await ScalarAsync<string>(appConnection, null, "SELECT current_user");

        await using var handle = await Build(platformSource).EnterAsync(
            "test:provenance", CancellationToken.None);

        var scopeUser = await ScalarAsync<string>(handle.Connection, handle.Transaction, "SELECT current_user");
        var scopeBackend = await ScalarAsync<int>(handle.Connection, handle.Transaction, "SELECT pg_backend_pid()");

        appUser.Should().Be("learnstack_app");
        scopeUser.Should().Be("learnstack_platform");
        scopeBackend.Should().NotBe(appBackend,
            "a second connection, not a role switch on the request's own — a SET ROLE "
            + "would survive COMMIT and ride a pooled connection into the next tenant");
    }

    [Fact]
    public async Task Disposing_Without_Committing_Rolls_Back()
    {
        // A frame that ended without committing has failed, and a half-applied
        // cross-tenant mutation is the worst thing this path could leave behind.
        await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);
        var scope = Build(dataSource);
        var host = $"rollback-{Guid.NewGuid():N}.example.com";

        await using (var handle = await scope.EnterAsync(
            "test:rollback", CancellationToken.None))
        {
            await using var insert = handle.Connection.CreateCommand();
            insert.Transaction = handle.Transaction;
            insert.CommandText =
                """
                INSERT INTO platform_host_to_tenant (host, tenant_id, is_active, is_publicly_live)
                VALUES (@host, @tenant, true, true)
                """;
            insert.Parameters.Add(new NpgsqlParameter("host", host));
            insert.Parameters.Add(new NpgsqlParameter("tenant", SchemaFixture.TenantA));
            await insert.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await using var check = await scope.EnterAsync(
            "test:rollback-check", CancellationToken.None);
        var survivors = await ScalarAsync<long>(
            check.Connection,
            check.Transaction,
            "SELECT count(*) FROM platform_host_to_tenant WHERE host = @host",
            [("host", (object)host)]);

        survivors.Should().Be(0, "disposal without a commit rolls the transaction back");
    }

    [Fact]
    public async Task Two_Concurrent_Entries_Get_Independent_Connections()
    {
        // The scope is a stateless singleton, so per-entry state would put two callers on
        // one BYPASSRLS connection — the one-command-at-a-time hazard the ambient unit of
        // work already documents, made worse by a connection that sees every tenant.
        await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);
        var scope = Build(dataSource);

        await using var first = await scope.EnterAsync("test:one", CancellationToken.None);
        await using var second = await scope.EnterAsync("test:two", CancellationToken.None);

        first.Connection.Should().NotBeSameAs(second.Connection);
        (await ScalarAsync<int>(first.Connection, first.Transaction, "SELECT pg_backend_pid()"))
            .Should().NotBe(
                await ScalarAsync<int>(second.Connection, second.Transaction, "SELECT pg_backend_pid()"));
    }

    [Fact]
    public async Task A_Correctly_Named_Role_That_Lost_Bypass_Is_Refused_On_Connect()
    {
        // The failure that looks like nothing at all. A learnstack_platform which lost
        // BYPASSRLS — a re-created role, a restored dump, an ALTER ROLE — still passes
        // the name check, still connects, and every cross-tenant query it runs comes back
        // filtered to the current tenant context. With no context set that is no rows,
        // so the symptom is missing data rather than a misconfiguration.
        //
        // Driven by taking the attribute off the real role and putting it back, because
        // nothing weaker distinguishes the guard from its absence: measured, with the
        // suite building its own data source, replacing this initializer with the
        // application one — which refuses every bypassing role — left all six cases
        // green. The shared-schema collection serializes its classes, so the window is
        // this method, and the restore is in a finally.
        // The superuser, because no four-role credential may alter a role's attributes —
        // learnstack_migration holds neither CREATEROLE nor ADMIN on learnstack_platform,
        // which is the four-role model working rather than a gap. Nothing here reads
        // tenant data through it.
        await using var admin = NpgsqlDataSource.Create(_schema.Postgres.SuperuserConnectionString);
        await ExecuteAsync(admin, "ALTER ROLE learnstack_platform NOBYPASSRLS");

        try
        {
            await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);

            var act = async () => await dataSource.OpenConnectionAsync(CancellationToken.None);

            (await act.Should().ThrowAsync<InvalidOperationException>())
                .Which.Message.Should().Contain("does not bypass Row Level Security");
        }
        finally
        {
            await ExecuteAsync(admin, "ALTER ROLE learnstack_platform BYPASSRLS");
        }

        // And it connects again once the attribute is back, so the guard is the thing
        // that refused rather than the credential being broken by this test.
        await using var restored = PlatformSource(_schema.Postgres.PlatformConnectionString);
        await using var connection = await restored.OpenConnectionAsync(CancellationToken.None);
        connection.State.Should().Be(System.Data.ConnectionState.Open);
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public void A_Credential_Naming_Another_Role_Is_Refused_Before_Any_Connection()
    {
        // The name is not the privilege, so there are two guards and this is the cheap
        // one — the mistake an operator actually makes, caught without a socket. The
        // server-side half catches a correctly named role that LOST the attribute (a
        // re-created role, a restored dump), which is the failure that looks like
        // nothing at all: every cross-tenant query simply returns fewer rows. That half
        // is evidenced by the cases above running green against the real credential.
        var act = () => LearnStack.Api.Composition.PersistenceCompositionExtensions
            .BuildPlatformDataSource(_schema.Postgres.AppConnectionString);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*learnstack_app*")
            .And.Message.Should().Contain("learnstack_platform");
    }

    [Fact]
    public void An_Absent_Credential_Names_The_Key_Rather_Than_Degrading()
    {
        // It must not fall back to the application role. A bypass that silently became a
        // tenant-scoped read would return nothing and read as missing data — which is the
        // degradation Database Standards rules out by name.
        var act = () => LearnStack.Api.Composition.PersistenceCompositionExtensions
            .BuildPlatformDataSource(null);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ConnectionStrings:PlatformAdmin*");
    }

    /// <summary>
    /// The platform data source as the composition root builds it.
    /// </summary>
    /// <remarks>
    /// Through <c>BuildPlatformDataSource</c> and never <c>NpgsqlDataSource.Create</c>:
    /// the builder installs the physical-connection initializer that asserts the
    /// connected role actually bypasses row security, and a suite that created its own
    /// data source would never run it. Measured — with the suite creating its own,
    /// swapping that initializer for the application one, which refuses every bypassing
    /// role, left all six cases green.
    /// </remarks>
    private static NpgsqlDataSource PlatformSource(string connectionString) =>
        LearnStack.Api.Composition.PersistenceCompositionExtensions
            .BuildPlatformDataSource(connectionString);

    private static PlatformAdminScope Build(NpgsqlDataSource dataSource) =>
        new(new PermissiveGate(),
            new Lazy<NpgsqlDataSource>(() => dataSource),
            NullLogger<PlatformAdminScope>.Instance);

    private static async Task<long> CountOrganizationsAsync(
        System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction? transaction) =>
        await ScalarAsync<long>(connection, transaction, "SELECT count(*) FROM organizations");

    private static async Task<T> ScalarAsync<T>(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction? transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;

        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        return (T)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Permits entry, so the connection is what the case observes.</summary>
    private sealed class PermissiveGate : IPlatformAdminGate
    {
        public ValueTask<bool> IsPermittedAsync(
            string reason, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);
    }
}
