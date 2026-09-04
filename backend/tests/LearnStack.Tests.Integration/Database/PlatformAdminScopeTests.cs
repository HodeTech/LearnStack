using FluentAssertions;
using LearnStack.Infrastructure.MultiTenancy;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.Logging;
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
/// on the same data, at the same moment — a property an isolation-shaped assertion
/// cannot offer.
/// </para>
/// <para>
/// <b>What actually falsifies them, measured — and it is not a dropped policy.</b>
/// Appending <c>DROP POLICY organizations_isolation ON organizations</c> leaves every case
/// here green, because the table is under <c>FORCE ROW LEVEL SECURITY</c> and a
/// policy-less table is default-deny: dropping it makes the application side see
/// <i>less</i>, never more. The two mutations that do turn these red are
/// <c>DISABLE ROW LEVEL SECURITY</c> and a second <b>permissive</b> <c>USING (true)</c>
/// policy — the exact ADR-0003 Amendment 3 defect, where PostgreSQL combines permissive
/// policies with <c>OR</c>. Naming the wrong falsifier is worse than naming none.
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
        // Through the fixture's existing single-statement helper, whose own remarks name
        // ALTER ROLE … BYPASSRLS as the case it exists for. A raw superuser connection
        // string in a fixture would be a strictly wider capability than this needs, and
        // no four-role credential can do it: learnstack_migration owns tables, not roles.
        await _schema.Postgres.ExecuteAsSuperuserAsync("ALTER ROLE learnstack_platform NOBYPASSRLS");

        try
        {
            await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);

            var act = async () => await dataSource.OpenConnectionAsync(CancellationToken.None);

            (await act.Should().ThrowAsync<InvalidOperationException>())
                .Which.Message.Should().Contain("does not bypass Row Level Security");
        }
        finally
        {
            await _schema.Postgres.ExecuteAsSuperuserAsync(
                "ALTER ROLE learnstack_platform BYPASSRLS");
        }

        // And it connects again once the attribute is back, so the guard is the thing
        // that refused rather than the credential being broken by this test.
        await using var restored = PlatformSource(_schema.Postgres.PlatformConnectionString);
        await using var connection = await restored.OpenConnectionAsync(CancellationToken.None);
        connection.State.Should().Be(System.Data.ConnectionState.Open);
    }

    [Fact]
    public async Task A_Role_Promoted_To_Superuser_Is_Refused_On_Connect()
    {
        // The mirror failure, and the one the guard used to admit. A superuser DOES bypass
        // Row Level Security, so `rolbypassrls OR rolsuper` answered the literal question
        // correctly — and that was the trap: a superuser also bypasses the table and
        // schema GRANT matrix, which is what actually BOUNDS this role. Its own creation
        // script writes it `BYPASSRLS NOSUPERUSER` on purpose.
        //
        // So a deployment that promoted learnstack_platform — a restored dump, a hurried
        // ALTER ROLE during an incident — widened the one credential the platform path
        // uses from "reads across tenants" to "does anything", and the startup guard said
        // nothing. The role keeps BYPASSRLS throughout, so nothing but the superuser term
        // can account for the refusal.
        await _schema.Postgres.ExecuteAsSuperuserAsync("ALTER ROLE learnstack_platform SUPERUSER");

        try
        {
            await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);

            var act = async () => await dataSource.OpenConnectionAsync(CancellationToken.None);

            (await act.Should().ThrowAsync<InvalidOperationException>())
                .Which.Message.Should().Contain("does not bypass Row Level Security");
        }
        finally
        {
            await _schema.Postgres.ExecuteAsSuperuserAsync(
                "ALTER ROLE learnstack_platform NOSUPERUSER");
        }

        // And it connects again once the promotion is undone, so the guard refused rather
        // than the credential being broken by this test.
        await using var restored = PlatformSource(_schema.Postgres.PlatformConnectionString);
        await using var connection = await restored.OpenConnectionAsync(CancellationToken.None);
        connection.State.Should().Be(System.Data.ConnectionState.Open);
    }

    [Fact]
    public async Task A_Resolved_Handle_Cannot_Be_Used_Again()
    {
        // Measured before the fence existed: after a successful commit the connection is
        // still Open, so a plain SELECT on it returned every tenant's rows — in
        // autocommit, on a BYPASSRLS credential, with no transaction left to undo
        // anything. A write issued there SURVIVED DisposeAsync, which is the exact
        // opposite of what this type's contract promises.
        await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);
        await using var handle = await Build(dataSource).EnterAsync(
            "test:terminal", CancellationToken.None);

        await handle.CommitAsync(CancellationToken.None);

        var readConnection = () => handle.Connection;
        var readTransaction = () => handle.Transaction;
        var commitAgain = async () => await handle.CommitAsync(CancellationToken.None);

        readConnection.Should().Throw<InvalidOperationException>(
            "fencing Transaction alone would leave the autocommit hole open");
        readTransaction.Should().Throw<InvalidOperationException>();
        await commitAgain.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Committing_Persists_And_Disposal_Leaves_It_Alone()
    {
        // The other half of the handle's contract, and the half both named Packet 9
        // consumers need. Measured: gutting CommitAsync to a no-op left the whole suite
        // green, because every case here only ever checked that things did NOT survive.
        await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);
        var scope = Build(dataSource);
        var host = $"committed-{Guid.NewGuid():N}.example.com";

        try
        {
            await using (var handle = await scope.EnterAsync(
                "test:commit", CancellationToken.None))
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

                await handle.CommitAsync(CancellationToken.None);
            }

            await using var check = await scope.EnterAsync("test:check", CancellationToken.None);
            (await ScalarAsync<long>(
                check.Connection,
                check.Transaction,
                "SELECT count(*) FROM platform_host_to_tenant WHERE host = @host",
                [("host", (object)host)]))
                .Should().Be(1, "a committed write survives the handle that made it");
        }
        finally
        {
            // The schema fixture is shared and other classes assert exact row counts.
            await using var cleanup = await scope.EnterAsync("test:cleanup", CancellationToken.None);
            await using var delete = cleanup.Connection.CreateCommand();
            delete.Transaction = cleanup.Transaction;
            delete.CommandText = "DELETE FROM platform_host_to_tenant WHERE host = @host";
            delete.Parameters.Add(new NpgsqlParameter("host", host));
            await delete.ExecuteNonQueryAsync(CancellationToken.None);
            await cleanup.CommitAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_Faulted_Commit_Returns_The_Connection_And_Is_Left_Indeterminate()
    {
        // The leak. A COMMIT that faults leaves the outcome genuinely unknown — ADR-0033
        // calls that Indeterminate — and the obvious ordering, marking the handle
        // resolved AFTER the await, leaves the flag false. Disposal then issues ROLLBACK
        // on a transaction that is already over, which throws, which skips both
        // disposals, which strands the one BYPASSRLS connection in the process outside
        // the pool.
        //
        // The fault is produced by terminating our own backend: a killed connection is a
        // real failure mode, it makes both the COMMIT and the following ROLLBACK throw,
        // and unlike a constraint violation it is deterministic. A first version tried a
        // deferred foreign key and proved nothing — the constraint is not DEFERRABLE, so
        // the INSERT failed and the commit was never reached.
        var builder = new NpgsqlConnectionStringBuilder(_schema.Postgres.PlatformConnectionString)
        {
            MaxPoolSize = 3,
            Timeout = 5,
        };

        await using var dataSource = PlatformSource(builder.ConnectionString);
        var scope = Build(dataSource);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var handle = await scope.EnterAsync("test:faulted", CancellationToken.None);

            await using (handle)
            {
                await using (var suicide = handle.Connection.CreateCommand())
                {
                    suicide.Transaction = handle.Transaction;
                    suicide.CommandText = "SELECT pg_terminate_backend(pg_backend_pid())";

                    try
                    {
                        await suicide.ExecuteNonQueryAsync(CancellationToken.None);
                    }
                    catch (PostgresException)
                    {
                        // The server hangs up mid-statement; that is the point.
                    }
                    catch (NpgsqlException)
                    {
                    }
                }

                var commit = async () => await handle.CommitAsync(CancellationToken.None);
                await commit.Should().ThrowAsync<Exception>(
                    "the caller sees the connection's failure, not a bookkeeping one");
            }
        }

        // Five entries through a pool of three: without the disposal in a finally, this
        // line blocks until the pool timeout.
        await using var afterwards = await scope.EnterAsync("test:after", CancellationToken.None);
        afterwards.Connection.State.Should().Be(System.Data.ConnectionState.Open);
    }

    [Fact]
    public async Task An_Abandoned_Handle_On_A_Dead_Connection_Still_Returns_It()
    {
        // The branch the fix round ADDED and nothing reached: an abandoned frame whose
        // rollback itself fails. The only other case that kills a backend does so after
        // CommitAsync has already resolved the handle, and the only case that abandons
        // one rolls back a healthy connection — so the catch, its filter, and the
        // LogRollbackFailed swallow were all unexercised. Measured: narrowing the catch
        // filter to an unreachable type left all 1126 tests green.
        //
        // A connection already broken by the failure being cleaned up after is the
        // ordinary way to reach this, which is why it is worth a case rather than a
        // comment.
        var builder = new NpgsqlConnectionStringBuilder(_schema.Postgres.PlatformConnectionString)
        {
            MaxPoolSize = 3,
            Timeout = 5,
        };

        await using var dataSource = PlatformSource(builder.ConnectionString);
        var logger = new CapturingLogger();
        var scope = new PlatformAdminScope(
            new PermissiveGate(), new Lazy<NpgsqlDataSource>(() => dataSource), logger);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var handle = await scope.EnterAsync("test:abandoned", CancellationToken.None);

            await using (var suicide = handle.Connection.CreateCommand())
            {
                suicide.Transaction = handle.Transaction;
                suicide.CommandText = "SELECT pg_terminate_backend(pg_backend_pid())";

                try
                {
                    await suicide.ExecuteNonQueryAsync(CancellationToken.None);
                }
                catch (Exception failure) when (failure is PostgresException or NpgsqlException)
                {
                }
            }

            // No commit: the frame is abandoned, so disposal attempts a rollback that
            // cannot succeed. It must not throw, and must not keep the connection.
            var dispose = async () => await handle.DisposeAsync();
            await dispose.Should().NotThrowAsync(
                "throwing from disposal replaces whatever the caller was actually failing on");
        }

        await using var afterwards = await scope.EnterAsync("test:after", CancellationToken.None);
        afterwards.Connection.State.Should().Be(System.Data.ConnectionState.Open,
            "every abandoned entry returned its connection through the finally");

        logger.Entries.Should().Contain(entry => entry.EventId == 7002,
            "a rollback that could not run is recorded rather than silently dropped");
    }

    [Fact]
    public async Task Entry_Is_Recorded_At_Warning_With_The_Reason_And_The_Caller()
    {
        // The packet's ENTIRE record of a cross-tenant bypass until Packet 9 ships
        // audit_log — and until now every test passed NullLogger, so demoting it to
        // Debug, renumbering the EventId, or gutting ShortPath to a constant each left
        // the whole suite green.
        //
        // Bound through the CONCRETE type deliberately. C# fills [Caller*] from the
        // static type of the receiver, so the attributes have to be on the
        // implementation as well as the interface — they were not, which meant every
        // test constructing PlatformAdminScope directly logged "<unknown>" and the
        // provenance this case exists for was never exercised.
        await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);
        var logger = new CapturingLogger();
        var scope = new PlatformAdminScope(
            new PermissiveGate(), new Lazy<NpgsqlDataSource>(() => dataSource), logger);

        await using var handle = await scope.EnterAsync("test:recorded", CancellationToken.None);

        var entry = logger.Entries.Should().ContainSingle(line => line.EventId == 7001).Subject;

        entry.Level.Should().Be(LogLevel.Warning,
            "an operator filtering at Information must still see a cross-tenant bypass");
        entry.Message.Should().Contain("test:recorded");
        entry.Message.Should().Contain(nameof(Entry_Is_Recorded_At_Warning_With_The_Reason_And_The_Caller),
            "the calling member is how 'the caller' is known at all with no principal");
        // The property, not a substring of one machine's layout. CallerFilePath is the
        // COMPILING machine's absolute path, so logging it whole puts a build-agent
        // directory tree into every forwarded line. A first version asserted
        // NotContain("/Users/") and missed the mutation that keeps every segment, because
        // Split drops the leading slash and the result reads "Users/cemililik/..." —
        // the assertion has to be on the shape, which is at most two segments.
        var logged = entry.Message.Split(" at ")[1].Split(':')[0];

        logged.Should().EndWith("PlatformAdminScopeTests.cs");
        logged.Split('/').Should().HaveCountLessThanOrEqualTo(2,
            "the file is shortened to its last two segments");
    }

    /// <summary>Records what the scope logged, with its level and event id.</summary>
    private sealed class CapturingLogger : ILogger<PlatformAdminScope>
    {
        private readonly List<Captured> _entries = [];

        public IReadOnlyList<Captured> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            _entries.Add(new Captured(logLevel, eventId.Id, formatter(state, exception)));
        }

        internal sealed record Captured(LogLevel Level, int EventId, string Message);
    }

    [Fact]
    public void A_Credential_Naming_Another_Role_Is_Refused_Before_Any_Connection()
    {
        // The name is not the privilege, so there are two guards and this is the cheap
        // one — the mistake an operator actually makes, caught without a socket.
        var act = () => LearnStack.Api.Composition.PersistenceCompositionExtensions
            .BuildPlatformDataSource(_schema.Postgres.AppConnectionString);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*learnstack_app*")
            .And.Message.Should().Contain("learnstack_platform");
    }

    [Fact]
    public void An_Absent_Credential_Names_The_Key_Rather_Than_Degrading()
    {
        // It must not fall back to the application role: a bypass that silently became a
        // tenant-scoped read would return nothing and read as missing data.
        //
        // Asserted on text unique to THIS branch, not on the key prefix both messages
        // share — measured, deleting the blank guard let control fall into
        // ValidatePlatformConnectionString(null), whose message also names the key, so a
        // prefix-only assertion passed against the mutant.
        var act = () => LearnStack.Api.Composition.PersistenceCompositionExtensions
            .BuildPlatformDataSource(null);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ConnectionStrings:PlatformAdmin*")
            .And.Message.Should().Contain("is not configured").And.Contain(".env.example");
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
