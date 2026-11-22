using LearnStack.Infrastructure.Persistence;
using FluentAssertions;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.MultiTenancy;
using LearnStack.Modules.Tenancy.Application.Audit;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;
using LearnStack.SharedKernel.Platform;
using LearnStack.Tests.Fixtures;
using LearnStack.Modules.Tenancy.Infrastructure;
using LearnStack.SharedKernel.Entitlements;
using System.Diagnostics.Metrics;

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
/// refuses everyone — correctly, application permissions await Phase 03 — and the property under test here
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

        await using var handle = await Build(platformSource).EnterAsync(Admission(), CancellationToken.None);

        var seenByPlatform = await ScalarAsync<long>(handle, "SELECT count(*) FROM organizations");

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

        await using var handle = await Build(platformSource).EnterAsync(Admission(), CancellationToken.None);

        var scopeUser = await ScalarAsync<string>(handle, "SELECT current_user");
        var scopeBackend = await ScalarAsync<int>(handle, "SELECT pg_backend_pid()");

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

        await using (var handle = await scope.EnterAsync(Admission(), CancellationToken.None))
        {
            await handle.ExecuteAsync(
                "INSERT INTO platform_host_to_tenant (host, tenant_id, is_active, is_publicly_live) VALUES (@host, @tenant, true, true)",
                [new PlatformSqlParameter("host", host), new PlatformSqlParameter("tenant", SchemaFixture.TenantA)],
                CancellationToken.None);
        }

        await using var check = await scope.EnterAsync(Admission(), CancellationToken.None);
        var survivors = await ScalarAsync<long>(
            check, "SELECT count(*) FROM platform_host_to_tenant WHERE host = @host",
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

        await using var first = await scope.EnterAsync(Admission(), CancellationToken.None);
        await using var second = await scope.EnterAsync(Admission(), CancellationToken.None);

        (await ScalarAsync<int>(first, "SELECT pg_backend_pid()"))
            .Should().NotBe(
                await ScalarAsync<int>(second, "SELECT pg_backend_pid()"));
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
    public async Task A_Resolved_Handle_Cannot_Be_Used_Again_And_Exposes_No_Connection()
    {
        // The hazard the facade closes: a connection reference captured before the commit
        // would run in autocommit afterwards on a role that sees every tenant. The handle
        // therefore has no Connection or Transaction member to capture, refuses every call once
        // resolved, and closes the underlying connection at completion (ADR-0101 § 8).
        typeof(IPlatformAdminScopeHandle).GetProperties().Select(property => property.PropertyType)
            .Should().NotContain(type => typeof(System.Data.Common.DbConnection).IsAssignableFrom(type)
                || typeof(System.Data.Common.DbTransaction).IsAssignableFrom(type),
                "a raw provider object is exactly what could be captured");

        await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);
        await using var handle = await Build(dataSource).EnterAsync(Admission(), CancellationToken.None);
        (await ScalarAsync<int>(handle, "SELECT pg_backend_pid()")).Should().BeGreaterThan(0);

        await CompleteAsync(handle);

        var execute = async () => await handle.ExecuteAsync("SELECT 1", null, CancellationToken.None);
        var query = async () => await ScalarAsync<int>(handle, "SELECT 1");
        var result = async () => await handle.WriteResultAsync(
            new PlatformResult(TenantId.PlatformSentinel, null, null, null, null), CancellationToken.None);
        var commitAgain = async () => await handle.CommitAsync(CancellationToken.None);

        await execute.Should().ThrowAsync<Exception>("a statement after the commit would be an autocommit bypass");
        await query.Should().ThrowAsync<Exception>();
        await result.Should().ThrowAsync<Exception>();
        await commitAgain.Should().ThrowAsync<Exception>();
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
            await using (var handle = await scope.EnterAsync(Admission(), CancellationToken.None))
            {
                await handle.ExecuteAsync(
                "INSERT INTO platform_host_to_tenant (host, tenant_id, is_active, is_publicly_live) VALUES (@host, @tenant, true, true)",
                [new PlatformSqlParameter("host", host), new PlatformSqlParameter("tenant", SchemaFixture.TenantA)],
                CancellationToken.None);

                await CompleteAsync(handle);
            }

            await using var check = await scope.EnterAsync(Admission(), CancellationToken.None);
            (await ScalarAsync<long>(
                check, "SELECT count(*) FROM platform_host_to_tenant WHERE host = @host",
                [("host", (object)host)]))
                .Should().Be(1, "a committed write survives the handle that made it");
        }
        finally
        {
            // The schema fixture is shared and other classes assert exact row counts.
            await using var cleanup = await scope.EnterAsync(Admission(), CancellationToken.None);
            await cleanup.ExecuteAsync("DELETE FROM platform_host_to_tenant WHERE host = @host", [new PlatformSqlParameter("host", host)], CancellationToken.None);
            await CompleteAsync(cleanup);
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
            var handle = await scope.EnterAsync(Admission(), CancellationToken.None);

            await using (handle)
            {
                // The MUST row is written first so the commit is attempted, and the backend is
                // ended from outside the facade, so the statement fault guard does not stop the
                // commit short of the wire: this case is about a commit that itself faults.
                var pid = await ScalarAsync<int>(handle, "SELECT pg_backend_pid()");
                await handle.WriteResultAsync(
                    new PlatformResult(TenantId.PlatformSentinel, "PlatformKillswitch", "k", null, null),
                    CancellationToken.None);
                await using (var killer = await PostgresFixture.OpenAsync(_schema.Postgres.PlatformConnectionString))
                {
                    await ScalarAsync<bool>(killer, null, "SELECT pg_terminate_backend(@pid)", ("pid", pid));
                }

                var commit = async () => await handle.CommitAsync(CancellationToken.None);
                await commit.Should().ThrowAsync<Exception>(
                    "the caller sees the connection's failure, not a bookkeeping one");
            }
        }

        // Five entries through a pool of three: without the disposal in a finally, this
        // line blocks until the pool timeout.
        await using var afterwards = await scope.EnterAsync(Admission(), CancellationToken.None);
        (await ScalarAsync<int>(afterwards, "SELECT 1")).Should().Be(1);
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
        var scope = Build(dataSource, logger);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var handle = await scope.EnterAsync(Admission(), CancellationToken.None);

            var suicide = new Terminator(handle);
            {

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

        await using var afterwards = await scope.EnterAsync(Admission(), CancellationToken.None);
        (await ScalarAsync<int>(afterwards, "SELECT 1")).Should().Be(1,
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
        var scope = Build(dataSource, logger);

        await using var handle = await scope.EnterAsync(Admission(), CancellationToken.None);

        var entry = logger.Entries.Should().ContainSingle(line => line.EventId == 7001).Subject;

        entry.Level.Should().Be(LogLevel.Warning,
            "an operator filtering at Information must still see a cross-tenant bypass");
        entry.Message.Should().Contain(PlatformOperations.KillswitchToggle.ReasonSlug);
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

    [Fact]
    public async Task Entry_Is_On_The_Record_Before_The_Operation_Runs()
    {
        // The packet's whole point at this call site. Read on an INDEPENDENT connection
        // while the handle is still open and before the caller has run anything: the row
        // is committed already, which is what makes an operation that later fails still
        // recorded. The case used to read inside the scope's own transaction, which proves
        // the row was written and says nothing about whether it would survive.
        //
        // Every column is asserted from the catalogue's declaration rather than from a
        // literal repeated here, except the ones that ARE the decision — the sentinel
        // tenant, the outcome, and the provenance.
        await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);
        var correlation = $"corr-{Guid.NewGuid():N}";
        await using var handle = await Build(dataSource).EnterAsync(Admission(correlation));

        await using var independent = await PostgresFixture.OpenAsync(
            _schema.Postgres.PlatformConnectionString);
        await using var read = independent.CreateCommand();
        read.CommandText =
            "SELECT tenant_id, organization_id, module, operation, operation_type, "
            + "operation_class, outcome, actor_user_id, "
            + "metadata #>> '{caller,member}', metadata #>> '{caller,file}' "
            + "FROM audit_log WHERE correlation_id = @correlation";
        read.Parameters.Add(new NpgsqlParameter("correlation", correlation));

        await using var reader = await read.ExecuteReaderAsync();

        (await reader.ReadAsync()).Should().BeTrue(
            "the row is written on entry, before the caller runs anything");

        reader.GetGuid(0).Should().Be(TenantId.PlatformSentinel.Value,
            "a platform-scope operation has no resolvable tenant, and this is the one "
            + "class of row that carries the sentinel");
        reader.IsDBNull(1).Should().BeTrue("the scope belongs to no organization");
        reader.GetString(2).Should().Be("platform");
        reader.GetString(3).Should().Be(PlatformAdminScope.EnterOperation);
        reader.GetString(4).Should().Be(nameof(OperationType.SecurityEvent),
            "Audit Coverage puts every platform-bypass invocation on security-event");
        reader.GetString(5).Should().Be(nameof(OperationClass.Must));
        reader.GetString(6).Should().Be("success", "entry succeeded; what follows it is not this row");
        reader.IsDBNull(7).Should().BeTrue(
            "this scope does not acquire an authenticated tenant principal, and inventing one here "
            + "would put a fabricated actor on a permanent record");

        reader.GetString(8).Should().Be(nameof(Entry_Is_On_The_Record_Before_The_Operation_Runs),
            "with no principal, the compiler-supplied caller is how 'who' is known at all");
        reader.GetString(9).Should().EndWith("PlatformAdminScopeTests.cs");

        (await reader.ReadAsync()).Should().BeFalse("one entry writes one row");
    }

    [Theory]
    [InlineData("read-then-throw")]
    [InlineData("abandoned")]
    [InlineData("committed")]
    public async Task An_Entry_Stays_On_The_Record_Whatever_The_Work_Then_Does(string ending)
    {
        // The review's finding, inverted into the case. The row rode the handle's own
        // transaction, so a caller that read across every tenant and then threw — or simply
        // disposed the handle — took the only record of that access down with the rollback.
        // This case asserted exactly that, as zero survivors, and passed: it proved the
        // implementation and contradicted ADR-0044 § 10, which says an operation that later
        // fails is still recorded. Checked on an independent connection, after the handle
        // is gone, because that is what durable means.
        await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);
        var correlation = $"corr-{Guid.NewGuid():N}";

        var act = async () =>
        {
            await using var handle = await Build(dataSource).EnterAsync(Admission(correlation));

            // Privileged work first, in every ending: the access happened.
            (await ScalarAsync<long>(handle, "SELECT count(*) FROM organizations"))
                .Should().BeGreaterThanOrEqualTo(0);

            switch (ending)
            {
                case "read-then-throw":
                    throw new InvalidOperationException("the work failed after reading");
                case "committed":
                    await CompleteAsync(handle);
                    break;
            }
        };

        if (ending == "read-then-throw")
        {
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        else
        {
            await act.Should().NotThrowAsync();
        }

        await using var afterwards = await PostgresFixture.OpenAsync(
            _schema.Postgres.PlatformConnectionString);

        var survivors = await ScalarAsync<long>(
            afterwards, null,
            "SELECT count(*) FROM audit_log WHERE correlation_id = @correlation AND operation = @entry",
            ("correlation", correlation), ("entry", PlatformAdminScope.EnterOperation));

        survivors.Should().Be(1, "entry into a cross-tenant scope is recorded whatever the work then does");
    }

    [Fact]
    public async Task An_Entry_That_Cannot_Be_Recorded_Is_Not_Entered()
    {
        // The refusal that makes the record a control rather than a side effect, and the
        // catch that has to survive it: the connection is opened and a transaction begun
        // before the row is attempted, so a store failure must return BOTH to the pool.
        // Measured the way the abandoned-rollback case is — a pool of three, entered five
        // times. Dropping either disposal from the catch strands the connections and the
        // sixth entry times out.
        var builder = new NpgsqlConnectionStringBuilder(_schema.Postgres.PlatformConnectionString)
        {
            MaxPoolSize = 3,
            Timeout = 5,
        };

        await using var dataSource = PlatformSource(builder.ConnectionString);
        var scope = Build(dataSource, NullLogger<PlatformAdminScope>.Instance, RefusingStore());

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var act = async () => await scope.EnterAsync(Admission(), CancellationToken.None);

            await act.Should().ThrowAsync<AuditWriteFailedException>(
                "a cross-tenant scope that cannot be recorded must not be entered");
        }

        await using var afterwards = await Build(dataSource).EnterAsync(Admission(), CancellationToken.None);

        (await ScalarAsync<int>(afterwards, "SELECT 1")).Should().Be(1,
            "every refused entry returned its connection through the catch");
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
    [Fact]
    public async Task A_Result_Row_Commits_With_The_Work_And_Carries_The_Operator_But_Not_The_Ticket_For_A_Tenant()
    {
        await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);
        var correlation = $"corr-{Guid.NewGuid():N}";
        var admission = TestPlatformAdmissions.Admission(ticket: "INC-7", correlationId: correlation);

        // A throwaway tenant id: the shared fixture tenants must keep the audit rows other classes
        // count, and audit_log keeps no foreign key to tenants.
        var target = Guid.CreateVersion7();

        await using (var handle = await Build(dataSource).EnterAsync(admission))
        {
            await handle.WriteResultAsync(new PlatformResult(
                TenantId.From(target), "PlatformKillswitch", "key-1", "{\"isEnabled\":true}", "{\"isEnabled\":false}"));
            await handle.CommitAsync();
        }

        await using var read = await PostgresFixture.OpenAsync(_schema.Postgres.PlatformConnectionString);
        var rows = await Rows(read, correlation);

        rows.Should().HaveCount(2, "the entry row and the result row");
        var entry = rows.Single(row => row.Operation == PlatformAdminScope.EnterOperation);
        entry.Tenant.Should().Be(TenantId.PlatformSentinel.Value);
        entry.Metadata.Should().Contain("INC-7", "the sentinel row is invisible to every tenant, so it carries the ticket");
        entry.Actor.Should().BeNull("an operator is not a users row");

        var result = rows.Single(row => row.Operation == PlatformOperations.KillswitchToggle.AuditSlug);
        result.Tenant.Should().Be(target, "a tenant-targeted act is recorded under the real tenant");
        result.Reason.Should().Be(PlatformOperations.KillswitchToggle.ReasonSlug);
        result.Actor.Should().BeNull();
        using var metadata = System.Text.Json.JsonDocument.Parse(result.Metadata);
        var op = metadata.RootElement.GetProperty("operator");
        op.GetProperty("subjectDigest").GetString().Should().Be(admission.Operator.SubjectDigest);
        op.GetProperty("kind").GetString().Should().Be("human");
        op.GetProperty("acr").GetString().Should().Be("mfa");
        op.TryGetProperty("ticket", out _).Should().BeFalse(
            "a row a tenant administrator can read never carries the operator's ticket");
        result.After.Should().Contain("false");
    }

    [Fact]
    public async Task A_Result_Row_Rolls_Back_With_The_Work_While_The_Entry_Stays()
    {
        await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);
        var correlation = $"corr-{Guid.NewGuid():N}";

        await using (var handle = await Build(dataSource).EnterAsync(TestPlatformAdmissions.Admission(correlationId: correlation)))
        {
            await handle.WriteResultAsync(new PlatformResult(TenantId.PlatformSentinel, "PlatformKillswitch", "k", null, null));
            // Disposed without a commit: the work and its MUST row go together.
        }

        await using var read = await PostgresFixture.OpenAsync(_schema.Postgres.PlatformConnectionString);
        var rows = await Rows(read, correlation);

        rows.Should().ContainSingle().Which.Operation.Should().Be(PlatformAdminScope.EnterOperation,
            "the entry is on the record whatever the work then did, and the result is not");
    }

    [Fact]
    public async Task A_Result_Row_That_Cannot_Be_Written_Fails_The_Act_And_Leaves_No_State()
    {
        await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);
        var scope = Build(dataSource, NullLogger<PlatformAdminScope>.Instance, FailingResultStore());
        var key = KillswitchKeys.EmailDispatchEnabled;
        var removals = new RecordingCache();
        await using var read = await PostgresFixture.OpenAsync(_schema.Postgres.PlatformConnectionString);
        var before = await ScalarAsync<string>(read, null,
            "SELECT coalesce((SELECT is_enabled::text FROM platform_killswitches WHERE key = @k), 'absent')", ("k", key.Value));

        var act = async () => await new KillswitchToggle(scope, removals, new SystemClock(), NullLogger<KillswitchToggle>.Instance)
            .ToggleAsync(TestPlatformAdmissions.Admission(), key, before == "false", CancellationToken.None);

        await act.Should().ThrowAsync<AuditWriteFailedException>();
        removals.Removed.Should().Be(0, "nothing committed, so nothing is stale");
        (await ScalarAsync<string>(read, null,
            "SELECT coalesce((SELECT is_enabled::text FROM platform_killswitches WHERE key = @k), 'absent')", ("k", key.Value)))
            .Should().Be(before, "the state change rolled back with the failed MUST row");
    }

    [Fact]
    public async Task The_Killswitch_Toggle_Writes_The_Row_Both_Audit_Rows_And_Then_Drops_The_Cache()
    {
        await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);
        var scope = Build(dataSource);
        var key = KillswitchKeys.AnalyticsIngestEnabled;
        var cache = new RecordingCache(async () =>
        {
            await using var independent = await PostgresFixture.OpenAsync(_schema.Postgres.PlatformConnectionString);
            return await ScalarAsync<string>(independent, null,
                "SELECT is_enabled::text FROM platform_killswitches WHERE key = @k", ("k", key.Value));
        });
        var toggle = new KillswitchToggle(scope, cache, new SystemClock(), NullLogger<KillswitchToggle>.Instance);
        var correlation = $"corr-{Guid.NewGuid():N}";

        try
        {
            var off = await toggle.ToggleAsync(
                TestPlatformAdmissions.Admission(ticket: "INC-9", correlationId: correlation), key, false);
            await using (var midway = await PostgresFixture.OpenAsync(_schema.Postgres.PlatformConnectionString))
            {
                (await ScalarAsync<object>(midway, null,
                    "SELECT coalesce(reason, 'null') FROM platform_killswitches WHERE key = @k", ("k", key.Value)))
                    .Should().Be("null", "the ticket stays out of a table the application role can read");
            }

            var back = await toggle.ToggleAsync(
                TestPlatformAdmissions.Admission(correlationId: correlation), key, true);

            off.Outcome.Should().Be(KillswitchToggleOutcome.Applied);
            (off.Before, off.After).Should().Be((true, false), "no row yet means the registry default");
            (back.Before, back.After).Should().Be((false, true));
            cache.Removed.Should().Be(2, "one removal after each commit");
            cache.Observed.Should().Equal(["false", "true"],
                "each removal happens after its commit, so a concurrent reload cannot repopulate the stale value");

            await using var read = await PostgresFixture.OpenAsync(_schema.Postgres.PlatformConnectionString);
            (await ScalarAsync<bool>(read, null, "SELECT is_enabled FROM platform_killswitches WHERE key = @k", ("k", key.Value)))
                .Should().BeTrue();
            (await ScalarAsync<long>(read, null,
                "SELECT count(*) FROM platform_killswitches WHERE key = @k AND toggled_by IS NULL", ("k", key.Value)))
                .Should().Be(1, "an operator is not a users row, so toggled_by stays NULL");
            var results = (await Rows(read, correlation))
                .Where(row => row.Operation == PlatformOperations.KillswitchToggle.AuditSlug).ToList();
            results.Should().HaveCount(2, "one MUST result row per flip");
            results.Should().OnlyContain(row => row.Tenant == TenantId.PlatformSentinel.Value && row.Actor == null);
            results.Should().OnlyContain(row => row.Reason == PlatformOperations.KillswitchToggle.ReasonSlug);
            results[0].Before.Should().Contain("true");
            results[0].After.Should().Contain("false");
            results[1].Before.Should().Contain("false");
            results[1].After.Should().Contain("true");
            using var firstMetadata = System.Text.Json.JsonDocument.Parse(results[0].Metadata);
            var operatorNode = firstMetadata.RootElement.GetProperty("operator");
            operatorNode.GetProperty("issuer").GetString().Should().Be(TestPlatformAdmissions.Issuer);
            operatorNode.GetProperty("executor").GetString().Should().Be("operator.test");
            operatorNode.GetProperty("ticket").GetString().Should().Be("INC-9",
                "a sentinel-tenant row is unreadable to every tenant, so it carries the ticket");
            using var secondMetadata = System.Text.Json.JsonDocument.Parse(results[1].Metadata);
            secondMetadata.RootElement.GetProperty("operator").TryGetProperty("ticket", out _).Should().BeFalse();
            (await ScalarAsync<System.DateTime>(read, null, "SELECT toggled_at FROM platform_killswitches WHERE key = @k", ("k", key.Value)))
                .Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(30));
            (await ScalarAsync<object>(read, null, "SELECT coalesce(reason, 'null') FROM platform_killswitches WHERE key = @k", ("k", key.Value)))
                .Should().Be("null");
        }
        finally
        {
            await using var cleanup = await Build(dataSource).EnterAsync(TestPlatformAdmissions.Admission());
            await cleanup.ExecuteAsync("DELETE FROM platform_killswitches WHERE key = @k", [new PlatformSqlParameter("k", key.Value)]);
            await CompleteAsync(cleanup);
        }
    }

    [Fact]
    public async Task An_Undeclared_Killswitch_Key_Enters_No_Scope_And_Writes_Nothing()
    {
        await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);
        var correlation = $"corr-{Guid.NewGuid():N}";
        var cache = new RecordingCache();

        var result = await new KillswitchToggle(Build(dataSource), cache, new SystemClock(), NullLogger<KillswitchToggle>.Instance).ToggleAsync(
            TestPlatformAdmissions.Admission(correlationId: correlation), new KillswitchKey("killswitch.not.declared"), false);

        result.Outcome.Should().Be(KillswitchToggleOutcome.UnknownKey);
        cache.Removed.Should().Be(0);
        await using var read = await PostgresFixture.OpenAsync(_schema.Postgres.PlatformConnectionString);
        (await Rows(read, correlation)).Should().BeEmpty("an undeclared key is refused before a cross-tenant scope is entered");
    }

    [Fact]
    public async Task A_Result_Row_Cannot_Differ_From_Its_Admission_In_Any_Bound_Attribute()
    {
        // The audit seam is witness-bound: the operation, the closed reason, the correlation id, the
        // outcome and class, the module, the tenant, the actor and the operator attribution in
        // metadata.operator are all checked against the admission, whatever the caller composed. The
        // accepted control comes first so a refusal below is the attribute's, not a broken fixture.
        await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);
        await using var store = AuditStoreScopeFactory().CreateAsyncScope();
        var audit = store.ServiceProvider.GetRequiredService<IAuditStore>();
        var admission = TestPlatformAdmissions.Admission(ticket: "INC-1");
        var digest = admission.Operator.SubjectDigest;
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        string Metadata(string issuer, string? ticket = null) =>
            "{\"operator\":{\"issuer\":\"" + issuer + "\",\"subjectDigest\":\"" + digest + "\",\"kind\":\"human\""
            + (ticket is null ? string.Empty : ",\"ticket\":\"" + ticket + "\"") + "}}";

        var good = new AuditEntryDraft
        {
            Id = AuditEntryId.From(Guid.CreateVersion7()),
            TenantId = TenantId.PlatformSentinel,
            OrganizationId = null,
            ActorUserId = null,
            ActorEmail = null,
            ModuleName = "tenancy",
            Operation = admission.Operation.AuditSlug,
            OperationType = OperationType.SecurityEvent,
            OperationClass = OperationClass.Must,
            EntityType = null,
            EntityId = null,
            Outcome = AuditOutcome.Success,
            ErrorKey = null,
            Reason = admission.Operation.ReasonSlug,
            BeforeState = null,
            AfterState = null,
            Changes = null,
            CorrelationId = admission.Operator.CorrelationId,
            IpAddress = null,
            UserAgent = null,
            Timestamp = DateTimeOffset.UtcNow,
            Metadata = Metadata(admission.Operator.Issuer),
        };

        await audit.WritePlatformOperatorResultAsync(good, admission, connection, transaction);
        await audit.WritePlatformOperatorResultAsync(
            good with { Id = AuditEntryId.From(Guid.CreateVersion7()), Metadata = Metadata(admission.Operator.Issuer, "INC-1") },
            admission, connection, transaction);

        var refused = new Dictionary<string, AuditEntryDraft>
        {
            ["another operation"] = good with { Operation = "tenancy.other.operation" },
            ["a non-null actor"] = good with { ActorUserId = UserId.From(Guid.CreateVersion7()) },
            ["no operator attribution"] = good with { Metadata = "{}" },
            ["no metadata"] = good with { Metadata = null },
            ["the digest in the wrong place"] = good with
            {
                Metadata = "{\"note\":\"" + digest + "\",\"operator\":{\"issuer\":\"" + admission.Operator.Issuer + "\"}}",
            },
            ["another issuer"] = good with { Metadata = Metadata("https://elsewhere.example.test/realms/x") },
            ["an outcome of failure"] = good with { Outcome = AuditOutcome.Failed },
            ["a SHOULD class"] = good with { OperationClass = OperationClass.Should },
            ["another reason"] = good with { Reason = "other_reason" },
            ["the identity module"] = good with { ModuleName = "identity" },
            ["the nil tenant"] = good with { TenantId = TenantId.From(Guid.Empty) },
            ["another correlation id"] = good with { CorrelationId = "another" },
            ["a ticket on a tenant row"] = good with
            {
                TenantId = TenantId.From(Guid.CreateVersion7()),
                Metadata = Metadata(admission.Operator.Issuer, "INC-1"),
            },
            ["a ticket the admission never held"] = good with { Metadata = Metadata(admission.Operator.Issuer, "INC-2") },
        };

        foreach (var (attribute, draft) in refused)
        {
            var act = async () => await audit.WritePlatformOperatorResultAsync(draft, admission, connection, transaction);
            (await act.Should().ThrowAsync<ArgumentException>(attribute)).Which.Message.Should().Contain("admission");
        }
    }

    [Theory]
    [InlineData("platform.delivery.replay")]
    [InlineData("platform.delivery.reset")]
    public async Task An_Admission_For_A_Recovery_Act_Opens_No_Platform_Scope_And_Writes_No_Platform_Result_Row(string slug)
    {
        // ADR-0103: a replay and a reset run on the dispatcher's outbox role and write their own row through the
        // recovery witness. An admission minted for one is not spendable on the platform scope or on the platform
        // result writer, whose draft below is otherwise exactly what that writer accepts.
        var operation = PlatformOperations.All.Single(candidate => candidate.AuditSlug == slug);
        await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);
        await using var store = AuditStoreScopeFactory().CreateAsyncScope();
        var audit = store.ServiceProvider.GetRequiredService<IAuditStore>();
        var admission = TestPlatformAdmissions.Admission(operation, null, correlationId: "corr-" + Guid.NewGuid().ToString("N"));

        var enter = async () => await Build(dataSource).EnterAsync(admission);
        (await enter.Should().ThrowAsync<ArgumentException>(slug)).Which.Message.Should().Contain("platform role");

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var draft = new AuditEntryDraft
        {
            Id = AuditEntryId.From(Guid.CreateVersion7()),
            TenantId = TenantId.PlatformSentinel,
            OrganizationId = null,
            ActorUserId = null,
            ActorEmail = null,
            ModuleName = "tenancy",
            Operation = operation.AuditSlug,
            OperationType = OperationType.SecurityEvent,
            OperationClass = OperationClass.Must,
            EntityType = null,
            EntityId = null,
            Outcome = AuditOutcome.Success,
            ErrorKey = null,
            Reason = operation.ReasonSlug,
            BeforeState = null,
            AfterState = null,
            Changes = null,
            CorrelationId = admission.Operator.CorrelationId,
            IpAddress = null,
            UserAgent = null,
            Timestamp = DateTimeOffset.UtcNow,
            Metadata = "{\"operator\":{\"issuer\":\"" + admission.Operator.Issuer + "\",\"subjectDigest\":\""
                + admission.Operator.SubjectDigest + "\",\"kind\":\"human\"}}",
        };
        var write = async () => await audit.WritePlatformOperatorResultAsync(draft, admission, connection, transaction);
        (await write.Should().ThrowAsync<ArgumentException>(slug)).Which.Message.Should().Contain("admission");
    }

    private static PlatformAdmission FederationAdmission(PlatformOperation operation, string? ticket, string correlation) =>
        TestPlatformAdmissions.Admission(operation, ticket, correlationId: correlation);

    /// <summary>
    /// The tenant an operator federation row names in these cases. It has no <c>tenants</c> row, as an orphan's owner may
    /// not, and it is not a seeded tenant, because audit_log is append-only and the isolation census that shares this
    /// database counts the rows each seeded tenant sees.
    /// </summary>
    private static readonly Guid UnseededFederationTenant = Guid.CreateVersion7();

    private static PlatformResult FederationIntent(Guid connection, string? after = null) =>
        new(TenantId.From(UnseededFederationTenant), "FederationConnection", connection.ToString("D"), "{\"classes\":[\"orphan\"]}", after);

    private static async Task<List<(string Operation, string Outcome, Guid Tenant, string Metadata)>> Outcomes(
        System.Data.Common.DbConnection connection, string correlation)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT operation, outcome::text, tenant_id, metadata::text FROM audit_log WHERE correlation_id = @c ORDER BY timestamp, id";
        command.Parameters.Add(new NpgsqlParameter("c", correlation));
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<(string, string, Guid, string)>();
        while (await reader.ReadAsync()) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetGuid(2), reader.IsDBNull(3) ? string.Empty : reader.GetString(3)));
        return rows;
    }

    [Fact]
    public async Task An_Intent_Row_Commits_Before_The_Effect_And_Stands_When_The_Act_Is_Abandoned()
    {
        // ADR-0099 Amendment 11: the effect lies outside the database, so the intent must be durable before it starts.
        await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);
        var correlation = "corr-" + Guid.NewGuid().ToString("N");
        var host = $"intent-{Guid.NewGuid():N}.example.com";
        await using (var handle = await Build(dataSource).EnterAsync(
            FederationAdmission(PlatformOperations.FederationRemove, "CHG-7", correlation)))
        {
            await handle.WriteIntentAsync(FederationIntent(Guid.CreateVersion7()));

            // The scope continues in a fresh transaction, and an act that then fails is rolled back by disposal.
            (await InsertHostAsync(handle, host)).Should().Be(1);
        }

        await using var read = await PostgresFixture.OpenAsync(_schema.Postgres.PlatformConnectionString);
        var rows = await Outcomes(read, correlation);
        rows.Select(row => row.Operation).Should().Equal(PlatformAdminScope.EnterOperation, "platform.federation.remove_attempt");
        rows[0].Tenant.Should().Be(TenantId.PlatformSentinel.Value);
        rows[0].Metadata.Should().Contain("CHG-7", "the entry row is a sentinel row, which carries the ticket");
        rows[1].Outcome.Should().Be("indeterminate", "the effect had not happened when the row was written");
        rows[1].Tenant.Should().Be(UnseededFederationTenant, "the row names the tenant the caller named, never the sentinel");
        rows[1].Metadata.Should().NotContain("CHG-7", "a tenant can read this row");
        (await ScalarAsync<long>(read, null, "SELECT count(*) FROM platform_host_to_tenant WHERE host = @h", ("h", host))).Should().Be(0,
            "work after the checkpoint is rolled back with the abandoned scope");
    }

    [Fact]
    public async Task The_Completion_Row_And_Commit_End_An_Act_With_A_Checkpoint_In_The_Order_Intent_Then_Result()
    {
        await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);
        var correlation = "corr-" + Guid.NewGuid().ToString("N");
        var connection = Guid.CreateVersion7();
        await using (var handle = await Build(dataSource).EnterAsync(
            FederationAdmission(PlatformOperations.FederationRemove, null, correlation)))
        {
            await handle.WriteIntentAsync(FederationIntent(connection));
            await handle.WriteResultAsync(FederationIntent(connection, "{\"outcome\":\"applied\"}"));
            await handle.CommitAsync();
        }

        await using var read = await PostgresFixture.OpenAsync(_schema.Postgres.PlatformConnectionString);
        var rows = await Outcomes(read, correlation);
        rows.Select(row => (row.Operation, row.Outcome)).Should().Equal(
            (PlatformAdminScope.EnterOperation, "success"),
            ("platform.federation.remove_attempt", "indeterminate"),
            ("platform.federation.remove", "success"));
        rows.Skip(1).Select(row => row.Tenant).Should().OnlyContain(tenant => tenant == UnseededFederationTenant);
    }

    [Fact]
    public async Task A_Checkpoint_Is_Refused_Where_It_Does_Not_Belong_And_An_Act_That_Has_One_Cannot_Skip_It()
    {
        await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);

        // An operation that declares no attempt slug has no checkpoint.
        await using (var plain = await Build(dataSource).EnterAsync(Admission()))
        {
            var intent = async () => await plain.WriteIntentAsync(FederationIntent(Guid.CreateVersion7()));
            await intent.Should().ThrowAsync<InvalidOperationException>().WithMessage("*declares no intent checkpoint*");
        }

        await using var handle = await Build(dataSource).EnterAsync(
            FederationAdmission(PlatformOperations.FederationDisable, null, "corr-" + Guid.NewGuid().ToString("N")));
        var early = async () => await handle.WriteResultAsync(FederationIntent(Guid.CreateVersion7()));
        await early.Should().ThrowAsync<InvalidOperationException>().WithMessage("*intent row before its result row*");

        await handle.WriteIntentAsync(FederationIntent(Guid.CreateVersion7()));
        var twice = async () => await handle.WriteIntentAsync(FederationIntent(Guid.CreateVersion7()));
        await twice.Should().ThrowAsync<InvalidOperationException>().WithMessage("*written once*");
    }

    [Fact]
    public async Task A_Failed_Intent_Write_Stops_The_Act_Before_Anything_Commits()
    {
        await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);
        var correlation = "corr-" + Guid.NewGuid().ToString("N");
        await using (var handle = await Build(dataSource, NullLogger<PlatformAdminScope>.Instance, IntentFailingStore()).EnterAsync(
            FederationAdmission(PlatformOperations.FederationRemove, null, correlation)))
        {
            var intent = async () => await handle.WriteIntentAsync(FederationIntent(Guid.CreateVersion7()));
            await intent.Should().ThrowAsync<AuditWriteFailedException>();

            var after = async () => await handle.WriteResultAsync(FederationIntent(Guid.CreateVersion7()));
            await after.Should().ThrowAsync<InvalidOperationException>("a faulted scope can only be abandoned");
        }

        await using var read = await PostgresFixture.OpenAsync(_schema.Postgres.PlatformConnectionString);
        (await Outcomes(read, correlation)).Select(row => row.Operation).Should().Equal(
            [PlatformAdminScope.EnterOperation], "only the entry row stands: the effect never started");
    }

    [Fact]
    public async Task An_Intent_Row_Cannot_Differ_From_Its_Admission_In_Any_Bound_Attribute()
    {
        await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);
        await using var store = AuditStoreScopeFactory().CreateAsyncScope();
        var audit = store.ServiceProvider.GetRequiredService<IAuditStore>();
        var admission = FederationAdmission(PlatformOperations.FederationRemove, null, "corr-" + Guid.NewGuid().ToString("N"));
        var digest = admission.Operator.SubjectDigest;
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        string Metadata(string issuer, string? ticket = null) =>
            "{\"operator\":{\"issuer\":\"" + issuer + "\",\"subjectDigest\":\"" + digest + "\",\"kind\":\"human\""
            + (ticket is null ? string.Empty : ",\"ticket\":\"" + ticket + "\"") + "}}";

        var good = new AuditEntryDraft
        {
            Id = AuditEntryId.From(Guid.CreateVersion7()),
            TenantId = TenantId.From(UnseededFederationTenant),
            OrganizationId = null,
            ActorUserId = null,
            ActorEmail = null,
            ModuleName = "platform",
            Operation = "platform.federation.remove_attempt",
            OperationType = OperationType.SecurityEvent,
            OperationClass = OperationClass.Must,
            EntityType = "FederationConnection",
            EntityId = Guid.CreateVersion7().ToString("D"),
            Outcome = AuditOutcome.Indeterminate,
            ErrorKey = null,
            Reason = admission.Operation.ReasonSlug,
            BeforeState = null,
            AfterState = null,
            Changes = null,
            CorrelationId = admission.Operator.CorrelationId,
            IpAddress = null,
            UserAgent = null,
            Timestamp = DateTimeOffset.UtcNow,
            Metadata = Metadata(admission.Operator.Issuer),
        };
        await audit.WritePlatformOperatorIntentAsync(good, admission, connection, transaction);

        var refused = new Dictionary<string, AuditEntryDraft>
        {
            ["the result slug"] = good with { Operation = "platform.federation.remove" },
            ["another operation"] = good with { Operation = "platform.federation.disable_attempt" },
            ["a success outcome"] = good with { Outcome = AuditOutcome.Success },
            ["a non-null actor"] = good with { ActorUserId = UserId.From(Guid.CreateVersion7()) },
            ["no metadata"] = good with { Metadata = null },
            ["no operator attribution"] = good with { Metadata = "{}" },
            ["another issuer"] = good with { Metadata = Metadata("https://elsewhere.example.test/realms/x") },
            ["another reason"] = good with { Reason = "other_reason" },
            ["a SHOULD class"] = good with { OperationClass = OperationClass.Should },
            ["the identity module"] = good with { ModuleName = "identity" },
            ["the nil tenant"] = good with { TenantId = TenantId.From(Guid.Empty) },
            ["another correlation id"] = good with { CorrelationId = "another" },
            ["a ticket on a tenant row"] = good with { Metadata = Metadata(admission.Operator.Issuer, "INC-1") },
        };
        foreach (var (attribute, draft) in refused)
        {
            var act = async () => await audit.WritePlatformOperatorIntentAsync(draft, admission, connection, transaction);
            (await act.Should().ThrowAsync<ArgumentException>(attribute)).Which.Message.Should().Contain("admission");
        }

        // An operation with no attempt slug presents no intent, whatever the draft says.
        var killswitch = Admission();
        var wrong = async () => await audit.WritePlatformOperatorIntentAsync(good, killswitch, connection, transaction);
        await wrong.Should().ThrowAsync<ArgumentException>();

        // The offline rebind has an attempt slug too, and writes it on its own role through its own method: its admission
        // is not one this writer takes, whatever the draft says.
        var rebind = FederationAdmission(PlatformOperations.IdentityAuthorityRebind, null, "corr-" + Guid.NewGuid().ToString("N"));
        var offline = good with
        {
            Operation = "identity.authority.rebind_attempt",
            Reason = rebind.Operation.ReasonSlug,
            CorrelationId = rebind.Operator.CorrelationId,
            Metadata = Metadata(rebind.Operator.Issuer),
        };
        var rebound = async () => await audit.WritePlatformOperatorIntentAsync(offline, rebind, connection, transaction);
        (await rebound.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("admission");
    }

    private static IServiceScopeFactory IntentFailingStore() =>
        new ServiceCollection()
            .AddScoped<IAuditStore, IntentFailingAuditStore>()
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

    /// <summary>Writes the entry row for real and fails the intent row, as an outage at the checkpoint would.</summary>
    private sealed class IntentFailingAuditStore : IAuditStore
    {
        private readonly IAuditStore _inner = AuditStoreScopeFactory().CreateScope().ServiceProvider.GetRequiredService<IAuditStore>();

        public Task WritePlatformScopeAsync(
            AuditEntryDraft entry, System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction,
            CancellationToken cancellationToken = default) =>
            _inner.WritePlatformScopeAsync(entry, connection, transaction, cancellationToken);

        public Task WritePlatformOperatorIntentAsync(
            AuditEntryDraft entry, PlatformAdmission admission, System.Data.Common.DbConnection connection,
            System.Data.Common.DbTransaction transaction, CancellationToken cancellationToken = default) =>
            throw new AuditWriteFailedException("forced intent failure");

        public Task WriteGlobalIdentityPendingAsync(LearnStack.SharedKernel.Persistence.IUnitOfWork unit,
            GlobalIdentityCreationReference creation, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ReconcileGlobalIdentityAsync(AuditIntent intent, IReadOnlyList<CapturedEntityChange> changes,
            AuditIntentState state, DateTimeOffset timestamp, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task WritePendingAsync(LearnStack.SharedKernel.Persistence.IUnitOfWork unitOfWork, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task WriteStandaloneAsync(AuditEntryDraft entry, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task WriteBestEffortAsync(AuditEntryDraft entry, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task WriteJobTerminalAsync(AuditEntryDraft entry, JobTerminalTransition transition,
            System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task WriteEventTerminalAsync(AuditEntryDraft entry, EventTerminalTransition transition,
            System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Commit_Without_A_Result_Row_Is_Refused_And_The_Work_Rolls_Back()
    {
        await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);
        var host = $"norow-{Guid.NewGuid():N}.example.com";

        await using (var handle = await Build(dataSource).EnterAsync(Admission()))
        {
            await InsertHostAsync(handle, host);

            var commit = async () => await handle.CommitAsync();

            await commit.Should().ThrowAsync<InvalidOperationException>().WithMessage("*MUST result row*");
        }

        await using var read = await PostgresFixture.OpenAsync(_schema.Postgres.PlatformConnectionString);
        (await ScalarAsync<long>(read, null, "SELECT count(*) FROM platform_host_to_tenant WHERE host = @h", ("h", host)))
            .Should().Be(0, "a state change with no MUST row never commits");
    }

    [Fact]
    public async Task A_Failed_Statement_Leaves_The_Scope_Only_To_Be_Abandoned_And_The_Work_Rolls_Back()
    {
        await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);
        var host = $"faulted-{Guid.NewGuid():N}.example.com";

        await using (var handle = await Build(dataSource).EnterAsync(Admission()))
        {
            await InsertHostAsync(handle, host);
            var failing = async () => await handle.ExecuteAsync("SELECT 1/0", null, CancellationToken.None);
            await failing.Should().ThrowAsync<PostgresException>();

            // PostgreSQL would answer "current transaction is aborted"; the facade says what to do.
            var next = async () => await ScalarAsync<int>(handle, "SELECT 1");
            var result = async () => await handle.WriteResultAsync(
                new PlatformResult(TenantId.PlatformSentinel, null, null, null, null), CancellationToken.None);
            var commit = async () => await handle.CommitAsync();
            await next.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Dispose the scope*");
            await result.Should().ThrowAsync<InvalidOperationException>();
            await commit.Should().ThrowAsync<InvalidOperationException>();
        }

        await using var read = await PostgresFixture.OpenAsync(_schema.Postgres.PlatformConnectionString);
        (await ScalarAsync<long>(read, null, "SELECT count(*) FROM platform_host_to_tenant WHERE host = @h", ("h", host)))
            .Should().Be(0);
    }

    [Theory]
    [InlineData("COMMIT")]
    [InlineData("ROLLBACK")]
    [InlineData("BEGIN")]
    [InlineData("SET app.tenant_id = 'x'")]
    [InlineData("SELECT 1; COMMIT")]
    [InlineData("SELECT 1; SELECT 2")]
    [InlineData("DROP TABLE platform_killswitches")]
    [InlineData("TRUNCATE platform_killswitches")]
    [InlineData("COPY platform_killswitches TO STDOUT")]
    [InlineData("DO $$ BEGIN END $$")]
    [InlineData("-- comment\nSELECT 1")]
    [InlineData("   ")]
    [InlineData("SELECT count(*) FROM audit_log")]
    [InlineData("DELETE FROM audit_log")]
    [InlineData("UPDATE public.\"Audit_Log\" SET reason = 'x'")]
    public async Task The_Facade_Refuses_Anything_But_One_Dml_Statement_And_Stays_Usable(string sql)
    {
        await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);
        await using var handle = await Build(dataSource).EnterAsync(Admission());

        var refused = async () => await handle.ExecuteAsync(sql, null, CancellationToken.None);

        await refused.Should().ThrowAsync<ArgumentException>();
        (await ScalarAsync<int>(handle, "SELECT 1")).Should().Be(1, "a statement refused before it ran does not fault the scope");
    }

    [Theory]
    [InlineData("select 1")]
    [InlineData("SELECT 1;")]
    [InlineData("  SELECT\n1")]
    [InlineData("WITH x AS (SELECT 1 AS n) SELECT n FROM x")]
    public async Task The_Facade_Accepts_One_Statement_Whatever_Its_Case_Or_Spacing(string sql)
    {
        await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);
        await using var handle = await Build(dataSource).EnterAsync(Admission());

        var rows = await handle.QueryAsync(sql, null, reader => reader.GetInt32(0), CancellationToken.None);

        rows.Should().Equal(1);
    }

    [Fact]
    public async Task The_Result_Row_Is_The_Last_Write_Of_A_Scope()
    {
        await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);
        var host = $"after-{Guid.NewGuid():N}.example.com";
        await using var handle = await Build(dataSource).EnterAsync(Admission());
        await handle.WriteResultAsync(new PlatformResult(TenantId.PlatformSentinel, null, null, null, null));

        var write = async () => await InsertHostAsync(handle, host);
        var read = async () => await ScalarAsync<int>(handle, "SELECT 1");

        await write.Should().ThrowAsync<InvalidOperationException>().WithMessage("*last write*");
        await read.Should().ThrowAsync<InvalidOperationException>("a statement after the row could change state it does not describe");
        var commit = async () => await handle.CommitAsync();
        await commit.Should().NotThrowAsync("the refused statements did not fault the scope");
    }

    [Theory]
    [InlineData("{")]
    [InlineData("not json")]
    [InlineData("")]
    public async Task A_Result_State_That_Is_Not_Json_Is_A_Programming_Error_Not_An_Audit_Outage(string state)
    {
        await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);
        await using var handle = await Build(dataSource).EnterAsync(Admission());

        var bad = async () => await handle.WriteResultAsync(
            new PlatformResult(TenantId.PlatformSentinel, "PlatformKillswitch", "k", null, state));

        await bad.Should().ThrowAsync<ArgumentException>("not an AuditWriteFailedException, which the endpoint would report as a 503");
        await handle.WriteResultAsync(new PlatformResult(TenantId.PlatformSentinel, "PlatformKillswitch", "k", null, "{\"ok\":true}"));
        var commit = async () => await handle.CommitAsync();
        await commit.Should().NotThrowAsync("a refused argument leaves the transaction intact");
    }

    [Fact]
    public async Task An_Oversized_Result_State_Is_Refused_Before_It_Reaches_The_Database()
    {
        await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);
        await using var handle = await Build(dataSource).EnterAsync(Admission());
        var oversized = "{\"padding\":\"" + new string('x', 40 * 1024) + "\"}";

        var bad = async () => await handle.WriteResultAsync(
            new PlatformResult(TenantId.PlatformSentinel, "PlatformKillswitch", "k", oversized, null));

        await bad.Should().ThrowAsync<ArgumentException>().WithMessage("*larger than*");
    }

    [Fact]
    public async Task The_Connection_Is_Returned_At_Commit_Not_At_Disposal()
    {
        // A pool of one: the second entry can only succeed if the first scope's commit already
        // returned its connection. A handle that held it until disposal would block here.
        var builder = new NpgsqlConnectionStringBuilder(_schema.Postgres.PlatformConnectionString)
        {
            MaxPoolSize = 1,
            Timeout = 3,
        };
        await using var dataSource = PlatformSource(builder.ConnectionString);
        var scope = Build(dataSource);

        var first = await scope.EnterAsync(Admission(), CancellationToken.None);
        await CompleteAsync(first);

        await using var second = await scope.EnterAsync(Admission(), CancellationToken.None);
        (await ScalarAsync<int>(second, "SELECT 1")).Should().Be(1);
        await first.DisposeAsync();
    }

    [Fact]
    public async Task The_Result_Counter_Counts_A_Committed_Result_And_Not_A_Rolled_Back_One()
    {
        long total = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == LearnStack.Infrastructure.PlatformOperator.PlatformOperatorMetrics.MeterName
                && instrument.Name == LearnStack.Infrastructure.PlatformOperator.PlatformOperatorMetrics.ResultCounterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref total, value));
        listener.Start();
        await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);
        var scope = Build(dataSource);

        await using (var abandoned = await scope.EnterAsync(Admission()))
        {
            await abandoned.WriteResultAsync(new PlatformResult(TenantId.PlatformSentinel, null, null, null, null));
        }

        Interlocked.Read(ref total).Should().Be(0, "a result that rolled back is not a result");

        await using (var completed = await scope.EnterAsync(Admission()))
        {
            await CompleteAsync(completed);
        }

        Interlocked.Read(ref total).Should().Be(1);
    }

    [Fact]
    public async Task Two_First_Toggles_Of_One_Key_Serialise_On_The_Row_Lock()
    {
        // SELECT ... FOR UPDATE locks nothing when no row exists, so two first toggles used to read the
        // registry default as their before state. The row is created first, so exactly one of two
        // concurrent first toggles sees the default and the other sees the first one's result.
        await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);
        var key = KillswitchKeys.EmailDispatchEnabled;
        var registryDefault = KillswitchKeys.All[key];
        var toggle = new KillswitchToggle(
            Build(dataSource), new RecordingCache(), new SystemClock(), NullLogger<KillswitchToggle>.Instance);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var results = await Task.WhenAll(
                    toggle.ToggleAsync(TestPlatformAdmissions.Admission(), key, !registryDefault),
                    toggle.ToggleAsync(TestPlatformAdmissions.Admission(), key, !registryDefault));

                results.Count(result => result.Before == registryDefault).Should().Be(1,
                    "one toggle saw the default and the other saw the first one's committed state");
                results.Count(result => result.Before == !registryDefault).Should().Be(1);
            }
            finally
            {
                await using var cleanup = await Build(dataSource).EnterAsync(Admission());
                await cleanup.ExecuteAsync("DELETE FROM platform_killswitches WHERE key = @k", [new PlatformSqlParameter("k", key.Value)]);
                await CompleteAsync(cleanup);
            }
        }
    }

    [Fact]
    public async Task Only_The_Erasure_Completion_May_Redact_And_A_Refusal_Runs_Nothing_And_Leaves_The_Scope_Usable()
    {
        // ADR-0108 § 2: the redaction is bound to the admitted operation. Every other operation that enters the platform
        // scope is refused before the executor is resolved, and the refusal is not a failed statement.
        await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);
        var redactor = new RecordingRedactor();
        var scope = Build(dataSource, NullLogger<PlatformAdminScope>.Instance, AuditStoreScopeFactory(redactor));
        var others = PlatformOperations.All
            .Where(operation => operation.NeedsPlatformRole && !ReferenceEquals(operation, PlatformOperations.ErasureComplete))
            .ToList();
        others.Should().NotBeEmpty();

        foreach (var operation in others)
        {
            await using var handle = await scope.EnterAsync(TestPlatformAdmissions.Admission(operation));

            var redact = async () => await handle.RedactAuditAsync(Plan(Guid.CreateVersion7()));

            await redact.Should().ThrowAsync<InvalidOperationException>(operation.AuditSlug).WithMessage("*may not redact*");
            (await ScalarAsync<int>(handle, "SELECT 1")).Should().Be(1, "a refusal before anything ran does not fault the scope");
        }

        redactor.Calls.Should().BeEmpty("no other operation reaches the executor");
    }

    [Fact]
    public async Task The_Redaction_Runs_On_The_Scope_s_Connection_And_Transaction_With_Its_Admission_And_Commits_With_The_Result_Row()
    {
        await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);
        var host = $"redact-{Guid.NewGuid():N}.example.com";
        var redactor = new RecordingRedactor(async (connection, transaction) =>
        {
            // The executor's statements are the scope's: the same backend and transaction, so its write commits or rolls
            // back with the act. A host row stands in for the audit rows it would change.
            await ScalarAsync<int>(connection, transaction,
                "INSERT INTO platform_host_to_tenant (host, tenant_id, is_active, is_publicly_live) VALUES (@h, @t, true, true) RETURNING 1",
                ("h", host), ("t", SchemaFixture.TenantA));
            return await ScalarAsync<long>(connection, transaction, "SELECT txid_current()");
        });
        var scope = Build(dataSource, NullLogger<PlatformAdminScope>.Instance, AuditStoreScopeFactory(redactor));
        var admission = TestPlatformAdmissions.Admission(
            PlatformOperations.ErasureComplete, ticket: "INC-9", correlationId: $"corr-{Guid.NewGuid():N}");
        var target = Guid.CreateVersion7();
        var plan = Plan(target);

        await using (var handle = await scope.EnterAsync(admission))
        {
            var own = await ScalarAsync<long>(handle, "SELECT txid_current()");

            var report = await handle.RedactAuditAsync(plan);

            var call = redactor.Calls.Should().ContainSingle().Which;
            call.Plan.Should().BeSameAs(plan);
            call.Admission.Should().BeSameAs(admission, "the executor checks the admission the scope was entered with");
            redactor.Observed.Should().Equal([own], "the executor ran inside the scope's own work transaction");
            report.RowsSelected.Should().Be(0);
            await handle.WriteResultAsync(new PlatformResult(
                TenantId.From(target), "DataSubjectRequest", plan.Subject.RequestId.ToString("D"), "{}", report.ToClosedJson()));
            await handle.CommitAsync();
        }

        await using var read = await PostgresFixture.OpenAsync(_schema.Postgres.PlatformConnectionString);
        (await ScalarAsync<long>(read, null, "SELECT count(*) FROM platform_host_to_tenant WHERE host = @h", ("h", host)))
            .Should().Be(1, "the redaction committed with the result row");
        var rows = await Rows(read, admission.Operator.CorrelationId!);
        var result = rows.Single(row => row.Operation == PlatformOperations.ErasureComplete.AuditSlug);
        result.Tenant.Should().Be(target, "the result row names the request's real tenant");
        result.Actor.Should().BeNull();
        result.Metadata.Should().NotContain("INC-9", "a row the tenant can read never carries the operator's ticket");
        rows.Single(row => row.Operation == PlatformAdminScope.EnterOperation).Metadata.Should().Contain("INC-9");

        await using var cleanup = await Build(dataSource).EnterAsync(Admission());
        await cleanup.ExecuteAsync("DELETE FROM platform_host_to_tenant WHERE host = @h", [new PlatformSqlParameter("h", host)]);
        await CompleteAsync(cleanup);
    }

    [Fact]
    public async Task A_Redaction_Rolls_Back_With_A_Scope_That_Is_Abandoned_And_Writes_Nothing_After_Its_Result_Row_Or_Its_End()
    {
        await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);
        var host = $"abandon-{Guid.NewGuid():N}.example.com";
        var redactor = new RecordingRedactor(async (connection, transaction) =>
        {
            await ScalarAsync<int>(connection, transaction,
                "INSERT INTO platform_host_to_tenant (host, tenant_id, is_active, is_publicly_live) VALUES (@h, @t, true, true) RETURNING 1",
                ("h", host), ("t", SchemaFixture.TenantA));
            return 0;
        });
        var scope = Build(dataSource, NullLogger<PlatformAdminScope>.Instance, AuditStoreScopeFactory(redactor));
        var target = Guid.CreateVersion7();

        await using (var abandoned = await scope.EnterAsync(TestPlatformAdmissions.Admission(PlatformOperations.ErasureComplete)))
        {
            await abandoned.RedactAuditAsync(Plan(target));
            // Disposed without a commit.
        }

        await using var read = await PostgresFixture.OpenAsync(_schema.Postgres.PlatformConnectionString);
        (await ScalarAsync<long>(read, null, "SELECT count(*) FROM platform_host_to_tenant WHERE host = @h", ("h", host)))
            .Should().Be(0, "an abandoned act rolls its redaction back with it");

        var handle = await scope.EnterAsync(TestPlatformAdmissions.Admission(PlatformOperations.ErasureComplete));
        await handle.WriteResultAsync(new PlatformResult(TenantId.From(target), "DataSubjectRequest", target.ToString("D"), null, "{}"));
        var afterResult = async () => await handle.RedactAuditAsync(Plan(target));
        await afterResult.Should().ThrowAsync<InvalidOperationException>().WithMessage("*last write*",
            "a redaction after the result row would change what the row does not describe");
        await handle.CommitAsync();
        var afterCommit = async () => await handle.RedactAuditAsync(Plan(target));
        await afterCommit.Should().ThrowAsync<InvalidOperationException>("the connection closed at the commit");
        await handle.DisposeAsync();
        var afterDisposal = async () => await handle.RedactAuditAsync(Plan(target));
        await afterDisposal.Should().ThrowAsync<ObjectDisposedException>();
        redactor.Calls.Should().ContainSingle("only the abandoned act reached the executor");
    }

    [Fact]
    public async Task A_Failed_Redaction_Faults_The_Scope_So_Nothing_Of_The_Act_Commits_While_The_Entry_Stays()
    {
        await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);
        var host = $"failed-{Guid.NewGuid():N}.example.com";
        var redactor = new RecordingRedactor(async (connection, transaction) =>
            await ScalarAsync<int>(connection, transaction, "SELECT 1/0"));
        var scope = Build(dataSource, NullLogger<PlatformAdminScope>.Instance, AuditStoreScopeFactory(redactor));
        var admission = TestPlatformAdmissions.Admission(PlatformOperations.ErasureComplete, correlationId: $"corr-{Guid.NewGuid():N}");
        var target = Guid.CreateVersion7();

        await using (var handle = await scope.EnterAsync(admission))
        {
            await InsertHostAsync(handle, host);

            var redact = async () => await handle.RedactAuditAsync(Plan(target));

            await redact.Should().ThrowAsync<PostgresException>("the executor's failure is the caller's, unchanged");
            var result = async () => await handle.WriteResultAsync(
                new PlatformResult(TenantId.From(target), "DataSubjectRequest", target.ToString("D"), null, "{}"));
            await result.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Dispose the scope*");
            var commit = async () => await handle.CommitAsync();
            await commit.Should().ThrowAsync<InvalidOperationException>();
        }

        await using var read = await PostgresFixture.OpenAsync(_schema.Postgres.PlatformConnectionString);
        (await ScalarAsync<long>(read, null, "SELECT count(*) FROM platform_host_to_tenant WHERE host = @h", ("h", host)))
            .Should().Be(0, "the work before the failed redaction rolled back with it");
        (await Rows(read, admission.Operator.CorrelationId!)).Should().ContainSingle()
            .Which.Operation.Should().Be(PlatformAdminScope.EnterOperation, "the entry is on the record and no result row is");
    }

    [Fact]
    public async Task Without_A_Registered_Executor_A_Completion_Cannot_Redact_And_Cannot_Commit()
    {
        await using var dataSource = PlatformSource(_schema.Postgres.PlatformConnectionString);
        var target = Guid.CreateVersion7();
        await using var handle = await Build(dataSource).EnterAsync(TestPlatformAdmissions.Admission(PlatformOperations.ErasureComplete));

        var redact = async () => await handle.RedactAuditAsync(Plan(target));

        await redact.Should().ThrowAsync<InvalidOperationException>().WithMessage("*IAuditRedactor*",
            "a deployment that cannot redact refuses by name rather than completing an erasure with nothing redacted");
        var result = async () => await handle.WriteResultAsync(
            new PlatformResult(TenantId.From(target), "DataSubjectRequest", target.ToString("D"), null, "{}"));
        await result.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Dispose the scope*");
    }

    /// <summary>A plan for an erasure a person filed in a tenant-wide group; its identifiers are fresh.</summary>
    private static AuditRedactionPlan Plan(Guid tenant) => new(new UserDataSubject(
        Guid.CreateVersion7(), DataRequestKind.Erasure, DataRequestOrigin.Self, UserId.From(Guid.CreateVersion7()),
        new DataRequestScopeGroup(TenantId.From(tenant), null), [Guid.CreateVersion7()]));

    /// <summary>Records each call and runs <paramref name="work"/> on the handed-over connection and transaction.</summary>
    private sealed class RecordingRedactor(
        Func<System.Data.Common.DbConnection, System.Data.Common.DbTransaction, Task<long>>? work = null) : IAuditRedactor
    {
        public List<(AuditRedactionPlan Plan, PlatformAdmission Admission, System.Data.Common.DbTransaction Transaction)> Calls { get; } = [];

        public List<long> Observed { get; } = [];

        public async Task<AuditRedactionReport> RedactAsync(
            AuditRedactionPlan plan, PlatformAdmission admission, System.Data.Common.DbConnection connection,
            System.Data.Common.DbTransaction transaction, CancellationToken cancellationToken = default)
        {
            Calls.Add((plan, admission, transaction));
            if (work is not null)
            {
                Observed.Add(await work(connection, transaction));
            }

            return new AuditRedactionReport(0, new Dictionary<AuditRedactedColumn, long>(), []);
        }
    }

    private static Task<int> InsertHostAsync(IPlatformAdminScopeHandle handle, string host) =>
        handle.ExecuteAsync(
            "INSERT INTO platform_host_to_tenant (host, tenant_id, is_active, is_publicly_live) VALUES (@host, @tenant, true, true)",
            [new PlatformSqlParameter("host", host), new PlatformSqlParameter("tenant", SchemaFixture.TenantA)],
            CancellationToken.None);

    private static async Task<List<AuditRow>> Rows(System.Data.Common.DbConnection connection, string correlation)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT tenant_id, operation, reason, actor_user_id, metadata::text, after_state::text, before_state::text "
            + "FROM audit_log WHERE correlation_id = @c ORDER BY timestamp, id";
        command.Parameters.Add(new NpgsqlParameter("c", correlation));
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<AuditRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new AuditRow(
                reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                reader.IsDBNull(6) ? string.Empty : reader.GetString(6)));
        }

        return rows;
    }

    private sealed record AuditRow(Guid Tenant, string Operation, string? Reason, Guid? Actor, string Metadata, string After, string Before);

    private sealed class RecordingCache(Func<Task<string>>? observe = null) : LearnStack.SharedKernel.Caching.ICacheService
    {
        public int Removed { get; private set; }

        /// <summary>What an independent connection saw at the moment of each removal.</summary>
        public List<string> Observed { get; } = [];

        public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            Removed++;
            if (observe is not null)
            {
                Observed.Add(await observe());
            }
        }

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<T> GetOrSetAsync<T>(
            string key, Func<CancellationToken, Task<T>> factory, LearnStack.SharedKernel.Caching.CacheOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SetAsync<T>(
            string key, T value, LearnStack.SharedKernel.Caching.CacheOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static IServiceScopeFactory FailingResultStore() =>
        new ServiceCollection()
            .AddScoped<IAuditStore, ResultFailingAuditStore>()
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

    /// <summary>Writes the entry row for real and fails the result row, as an outage between them would.</summary>
    private sealed class ResultFailingAuditStore : IAuditStore
    {
        private readonly IAuditStore _inner = AuditStoreScopeFactory().CreateScope().ServiceProvider.GetRequiredService<IAuditStore>();

        public Task WritePlatformScopeAsync(
            AuditEntryDraft entry, System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction,
            CancellationToken cancellationToken = default) =>
            _inner.WritePlatformScopeAsync(entry, connection, transaction, cancellationToken);

        public Task WritePlatformOperatorResultAsync(
            AuditEntryDraft entry, PlatformAdmission admission, System.Data.Common.DbConnection connection,
            System.Data.Common.DbTransaction transaction, CancellationToken cancellationToken = default) =>
            throw new AuditWriteFailedException("forced result failure");

        public Task WriteGlobalIdentityPendingAsync(LearnStack.SharedKernel.Persistence.IUnitOfWork unit,
            GlobalIdentityCreationReference creation, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ReconcileGlobalIdentityAsync(AuditIntent intent, IReadOnlyList<CapturedEntityChange> changes,
            AuditIntentState state, DateTimeOffset timestamp, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task WritePendingAsync(LearnStack.SharedKernel.Persistence.IUnitOfWork unitOfWork, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task WriteStandaloneAsync(AuditEntryDraft entry, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task WriteBestEffortAsync(AuditEntryDraft entry, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task WriteJobTerminalAsync(AuditEntryDraft entry, JobTerminalTransition transition,
            System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task WriteEventTerminalAsync(AuditEntryDraft entry, EventTerminalTransition transition,
            System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static NpgsqlDataSource PlatformSource(string connectionString) =>
        LearnStack.Api.Composition.PersistenceCompositionExtensions
            .BuildPlatformDataSource(connectionString);

    private static PlatformAdminScope Build(NpgsqlDataSource dataSource) =>
        Build(dataSource, NullLogger<PlatformAdminScope>.Instance);

    /// <summary>The scope as the composition root builds it, with a permissive gate.</summary>
    /// <remarks>
    /// <para>
    /// The catalogue is the real one, built from the shipped
    /// <c>TenancyAuditCatalogSource</c> rather than a double, because the slug's
    /// declaration is half of what these cases assert: a fake declaring
    /// <c>platform.admin_scope.enter</c> would keep every case green after the source
    /// that has to declare it stopped.
    /// </para>
    /// <para>
    /// The store's <b>application</b> data source is a <see cref="Lazy{T}"/> that throws.
    /// That is the assertion, not a shortcut: <c>WritePlatformScopeAsync</c> writes on the
    /// caller's connection, so an implementation that reached for its own would fail here
    /// rather than quietly write the row as <c>learnstack_app</c> — which cannot announce
    /// the sentinel and would fail the policy anyway, but far from this file.
    /// </para>
    /// </remarks>
    private static PlatformAdminScope Build(
        NpgsqlDataSource dataSource,
        ILogger<PlatformAdminScope> logger,
        IServiceScopeFactory? auditStore = null) =>
        new(new PrivilegedDataSource(() => dataSource),
            new AuditCatalog([new TenancyAuditCatalogSource()]),
            auditStore ?? AuditStoreScopeFactory(),
            new SystemClock(),
            new SystemGuidFactory(),
            logger);

    private static IServiceScopeFactory AuditStoreScopeFactory(IAuditRedactor? redactor = null) =>
        (redactor is null ? new ServiceCollection() : new ServiceCollection().AddSingleton(redactor))
            .AddMetrics()
            .AddLogging()
            .AddScoped<IAuditStateCapture, AuditStateCapture>()
            .AddSingleton<IAuditHealth, AuditHealth>()
            .AddSingleton<IAuditCatalog>(new AuditCatalog([new TenancyAuditCatalogSource()]))
            .AddSingleton<ITenantContextAccessor>(StaticTenantContextAccessor.Unresolved)
            .AddScoped<LearnStack.SharedKernel.Identity.IGlobalIdentityEnrollment, LearnStack.Infrastructure.Persistence.GlobalIdentityEnrollment>()
            .AddSingleton(new Lazy<NpgsqlDataSource>(
                () => throw new InvalidOperationException(
                    "The platform-scope row is written on the scope's own connection, so "
                    + "the store's application data source must never be built here.")))
            .AddScoped<IAuditStore, PostgresAuditStore>()
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

    /// <summary>A store whose platform-scope write always fails, as an outage would.</summary>
    private static IServiceScopeFactory RefusingStore() =>
        new ServiceCollection()
            .AddScoped<IAuditStore, RefusingAuditStore>()
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

    private sealed class RefusingAuditStore : IAuditStore
    {
        public Task WriteGlobalIdentityPendingAsync(LearnStack.SharedKernel.Persistence.IUnitOfWork unit,
            GlobalIdentityCreationReference creation, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Global identity has its own cases.");

        public Task ReconcileGlobalIdentityAsync(AuditIntent intent, IReadOnlyList<CapturedEntityChange> changes,
            AuditIntentState state, DateTimeOffset timestamp, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Global identity has its own cases.");

        public Task WritePendingAsync(
            LearnStack.SharedKernel.Persistence.IUnitOfWork unitOfWork,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task WriteStandaloneAsync(
            AuditEntryDraft entry, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task WriteBestEffortAsync(
            AuditEntryDraft entry, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task WriteJobTerminalAsync(AuditEntryDraft entry, JobTerminalTransition transition,
            System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction,
            CancellationToken cancellationToken = default) => throw new NotSupportedException("The job-terminal path has its own cases.");

        public Task WriteEventTerminalAsync(
            AuditEntryDraft entry,
            EventTerminalTransition transition,
            System.Data.Common.DbConnection connection,
            System.Data.Common.DbTransaction transaction,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The terminal-event path has its own cases.");

        public Task WritePlatformScopeAsync(
            AuditEntryDraft entry,
            System.Data.Common.DbConnection connection,
            System.Data.Common.DbTransaction transaction,
            CancellationToken cancellationToken = default) =>
            throw new AuditWriteFailedException(
                "The platform-scope audit row could not be written, so the scope must not "
                + "be entered.");
    }

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

    /// <summary>Writes a MUST result row under the sentinel and commits, as a completed act does.</summary>
    private static async Task CompleteAsync(IPlatformAdminScopeHandle handle)
    {
        await handle.WriteResultAsync(
            new PlatformResult(TenantId.PlatformSentinel, "PlatformKillswitch", "test", null, null), CancellationToken.None);
        await handle.CommitAsync(CancellationToken.None);
    }

    private static PlatformAdmission Admission(string? correlation = null) =>
        correlation is null
            ? TestPlatformAdmissions.Admission()
            : TestPlatformAdmissions.Admission(correlationId: correlation);

    private static async Task<T> ScalarAsync<T>(
        IPlatformAdminScopeHandle handle, string sql, params (string Name, object Value)[] parameters)
    {
        var rows = await handle.QueryAsync(
            sql,
            parameters.Select(parameter => new PlatformSqlParameter(parameter.Name, parameter.Value)).ToList(),
            reader => (T)reader.GetValue(0),
            CancellationToken.None);
        return rows[0];
    }

    /// <summary>Kills the scope's own backend through the facade, as an operator-side fault would.</summary>
    private sealed class Terminator(IPlatformAdminScopeHandle handle)
    {
        public Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) =>
            handle.ExecuteAsync("SELECT pg_terminate_backend(pg_backend_pid())", null, cancellationToken);
    }
}
