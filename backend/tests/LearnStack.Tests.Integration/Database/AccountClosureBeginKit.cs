using System.Globalization;
using FluentAssertions;
using LearnStack.Api.Composition;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.MultiTenancy;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.Modules.Identity.Application.DataRequests;
using LearnStack.Modules.Identity.Infrastructure.DataRequests;
using LearnStack.Modules.Tenancy.Application.Audit;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using LearnStack.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using static LearnStack.Tests.Integration.Database.DataRequestSchemaSeed;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// A disposable seeded database for the closure's Begin, the real platform scope with the real audit store over it, and the
/// fixture writers a closure needs: fresh persons, Memberships under the canonical fences, closure requests due or still in
/// their window. Fixture rows are written by the application role exactly as the product writes them, and only what no
/// product path can write yet (a backdated window, a suspended tenant) by the owner or the superuser.
/// </summary>
internal sealed class AccountClosureBeginKit : IAsyncDisposable
{
    private readonly PostgresFixture _postgres;
    private readonly NpgsqlDataSource _platform;
    private readonly NpgsqlDataSource _app;
    private readonly NpgsqlUnitOfWork _unused;

    private AccountClosureBeginKit(PostgresFixture postgres, DisposableSchemaDatabase database)
    {
        _postgres = postgres;
        Database = database;
        _platform = LearnStack.Api.Composition.PersistenceCompositionExtensions.BuildPlatformDataSource(database.PlatformConnectionString);
        _app = ApplicationDataSource.Build(database.AppConnectionString);
        // The Tenancy reader is constructed as production constructs it; its platform form never touches this unit.
        _unused = new NpgsqlUnitOfWork(_app, NullLogger<NpgsqlUnitOfWork>.Instance);
    }

    internal DisposableSchemaDatabase Database { get; }

    internal static async Task<AccountClosureBeginKit> CreateAsync(PostgresFixture postgres) =>
        new(postgres, await DataExportTestKit.CreateSeededDatabaseAsync(postgres));

    internal static PlatformAdmission Admission(string? ticket = null) =>
        TestPlatformAdmissions.Admission(
            PlatformOperations.ClosureBegin, ticket, correlationId: "00-closure-begin-" + Guid.NewGuid().ToString("N"));

    internal static long UserKey(Guid user) => IdentityAdvisoryLockKey.ForUser(UserId.From(user));

    internal static long TenantKey(Guid tenant) => IdentityAdvisoryLockKey.ForTenant(TenantId.From(tenant));

    /// <summary>The scope as production builds it, with the real audit store.</summary>
    internal PlatformAdminScope Scope() => new(
        new PrivilegedDataSource(() => _platform), new AuditCatalog([new TenancyAuditCatalogSource()]), AuditServices(),
        new SystemClock(), new SystemGuidFactory(), NullLogger<PlatformAdminScope>.Instance);

    internal AccountClosureOperatorOperations Owner(
        IClosureFanOut? fanOut = null, IDataRequestHoldSource? holds = null, IPlatformAdminScope? scope = null,
        IIdentityProviderAccountRemoval? removal = null, Microsoft.Extensions.Logging.ILogger<AccountClosureOperatorOperations>? logger = null) => new(
        scope ?? Scope(), new ClosureGroupLifecycleReader(new IdentityScopeLifecycleReader(_unused)), holds ?? new NoHoldSource(),
        [fanOut ?? new RecordingFanOut()], removal ?? new RecordingAccountRemoval(),
        logger ?? NullLogger<AccountClosureOperatorOperations>.Instance);

    /// <summary>A fresh Active person with a creation row, as the product binds one.</summary>
    internal async Task<Guid> PersonAsync()
    {
        var person = Guid.CreateVersion7();
        await InsertUserAsync(Database.AppConnectionString, person);
        return person;
    }

    /// <summary>One Membership with one Role, written by the application role under the person's and the tenant's fences.</summary>
    internal async Task<Guid> MemberAsync(Guid tenant, Guid? organization, Guid person, BuiltInRoleKey role)
    {
        var member = Guid.CreateVersion7();
        await using var app = await DataExportTestKit.OpenAppAsync(Database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant, organization);
        await LockAsync(app, transaction, tenant, person, exclusive: role == BuiltInRoleKey.TenantAdmin);
        await InsertMemberAsync(app, transaction, tenant, organization, member, person,
            RoleId(tenant, role, organization is null ? PermissionScope.Tenant : PermissionScope.Organization));
        await ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
        await transaction.CommitAsync();
        return member;
    }

    /// <summary>
    /// A closure the person files in the group, its snapshot every Membership they hold there; a due one is backdated past
    /// its 24 hours by the owner with the one guard paused, as a closure filed a day ago looks.
    /// </summary>
    internal async Task<Guid> ClosureAsync(Guid tenant, Guid? organization, Guid person, bool due = true)
    {
        var id = Guid.CreateVersion7();
        var memberships = await MembershipsAsync(tenant, organization, person);
        await using (var app = await DataExportTestKit.OpenAppAsync(Database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await FenceAsync(app, transaction, tenant, organization, person);
            await InsertRequestAsync(app, transaction, id, tenant, organization, person, [.. memberships], "closure");
            await transaction.CommitAsync();
        }

        if (due)
        {
            await BackdateAsync(id, tenant, organization);
        }

        return id;
    }

    internal async Task BackdateAsync(Guid request, Guid tenant, Guid? organization)
    {
        await using var owner = await OpenAsync(Database.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        await ExecuteAsync(owner, transaction, "ALTER TABLE public.data_subject_requests DISABLE TRIGGER tg_data_subject_requests_mutation_guard");
        await ScopeAsync(owner, transaction, tenant, organization);
        await ExecuteAsync(owner, transaction, """
            UPDATE public.data_subject_requests SET created_at=created_at - interval '25 hours',
                execute_at=execute_at - interval '25 hours' WHERE id=@id
            """, ("id", request));
        await ExecuteAsync(owner, transaction, "ALTER TABLE public.data_subject_requests ENABLE TRIGGER tg_data_subject_requests_mutation_guard");
        await transaction.CommitAsync();
    }

    /// <summary>A request of another kind or status for the person, written with the guards paused.</summary>
    internal async Task<Guid> RequestRowAsync(Guid tenant, Guid person, string kind, string status)
    {
        var id = Guid.CreateVersion7();
        var membership = (await MembershipsAsync(tenant, null, person))[0];
        var window = kind != "export";
        await SqlAsync($"""
            SET session_replication_role = replica;
            INSERT INTO data_subject_requests (id, tenant_id, organization_id, kind, origin, subject_user_id, requester_user_id,
                status, membership_snapshot, execute_at, completed_at, created_at, created_by, updated_at, updated_by, row_version)
            SELECT '{id}', '{tenant}', NULL, '{kind}', 'self', '{person}', '{person}', '{status}', '["{membership}"]'::jsonb,
                {(window ? "at + interval '24 hours'" : "NULL")}, {(status == "completed" ? "now()" : "NULL")}, at, '{person}',
                {(status is "scheduled" or "requested" ? "NULL" : "now()")},
                {(status is "scheduled" or "requested" ? "NULL" : $"'{UserId.SystemActor.Value}'")},
                {(status is "scheduled" or "requested" ? 0 : 2)}
            FROM (SELECT now() - interval '2 days' AS at) AS created
            """);
        return id;
    }

    internal async Task<IReadOnlyList<Guid>> MembershipsAsync(Guid tenant, Guid? organization, Guid person)
    {
        await using var platform = await OpenAsync(Database.PlatformConnectionString);
        await using var command = new NpgsqlCommand("""
            SELECT id FROM public.memberships
            WHERE tenant_id=@tenant AND organization_id IS NOT DISTINCT FROM @organization AND user_id=@user ORDER BY id
            """, platform);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.Add(new NpgsqlParameter("organization", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)organization ?? DBNull.Value });
        command.Parameters.AddWithValue("user", person);
        await using var reader = await command.ExecuteReaderAsync();
        var ids = new List<Guid>();
        while (await reader.ReadAsync()) ids.Add(reader.GetGuid(0));
        return ids;
    }

    /// <summary>Sets a tenant's status, as an operator's lifecycle act would, with no trigger to stop a fixture.</summary>
    internal Task TenantStatusAsync(Guid tenant, string status) =>
        SqlAsync($"SET session_replication_role = replica; UPDATE public.tenants SET status = '{status}' WHERE id = '{tenant}'");

    internal Task OrganizationStatusAsync(Guid tenant, Guid organization, string status) =>
        SqlAsync($"SET session_replication_role = replica; UPDATE public.organizations SET status = '{status}' WHERE tenant_id = '{tenant}' AND id = '{organization}'");

    internal Task SqlAsync(string sql) => _postgres.ExecuteAsSuperuserAsync(sql, RebindProofSupport.DatabaseOf(Database.MigrationConnectionString));

    internal sealed record UserRow(string Status, long Version, bool SubjectPresent);

    /// <summary>The User as the migration owner reads it: the platform role is not granted its version.</summary>
    internal async Task<UserRow> UserAsync(Guid user)
    {
        await using var owner = await OpenAsync(Database.MigrationConnectionString);
        await using var command = new NpgsqlCommand(
            "SELECT status, row_version, provider_subject IS NOT NULL FROM public.users WHERE id=@user", owner);
        command.Parameters.AddWithValue("user", user);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return new UserRow(reader.GetString(0), reader.GetInt64(1), reader.GetBoolean(2));
    }

    internal sealed record RequestRow(string Status, string? LastRefusal, long Version);

    internal async Task<RequestRow> RequestAsync(Guid id)
    {
        await using var platform = await OpenAsync(Database.PlatformConnectionString);
        await using var command = new NpgsqlCommand(
            "SELECT status, last_refusal, row_version FROM public.data_subject_requests WHERE id=@id", platform);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return new RequestRow(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt64(2));
    }

    internal sealed record AuditRow(
        Guid Tenant, string Module, string Operation, string? Reason, Guid? Actor, string? EntityType, string? EntityId,
        string Before, string After, string Metadata);

    internal async Task<List<AuditRow>> AuditAsync(string correlation)
    {
        await using var platform = await OpenAsync(Database.PlatformConnectionString);
        await using var command = new NpgsqlCommand("""
            SELECT tenant_id, module, operation, reason, actor_user_id, entity_type, entity_id, before_state::text,
                after_state::text, metadata::text
            FROM audit_log WHERE correlation_id = @c ORDER BY timestamp, id
            """, platform);
        command.Parameters.AddWithValue("c", correlation);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<AuditRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new AuditRow(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                reader.IsDBNull(8) ? string.Empty : reader.GetString(8), reader.IsDBNull(9) ? string.Empty : reader.GetString(9)));
        }

        return rows;
    }

    /// <summary>The Begin result rows written for a request, by any operator.</summary>
    internal async Task<long> ResultRowsAsync(Guid request)
    {
        await using var platform = await OpenAsync(Database.PlatformConnectionString);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM audit_log WHERE operation = 'platform.closure.begin' AND entity_id = @id", platform);
        command.Parameters.AddWithValue("id", request.ToString("D", CultureInfo.InvariantCulture));
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// The advisory keys held, granted, by the other platform-role backends of this database, with their mode: what a Begin
    /// holds while a gate keeps it open. The observer's own backend is left out.
    /// </summary>
    internal async Task<List<(long Key, string Mode)>> PlatformAdvisoryLocksAsync()
    {
        await using var observer = await OpenAsync(Database.PlatformConnectionString);
        await using var command = new NpgsqlCommand("""
            SELECT ((l.classid::bigint << 32) | l.objid::bigint), l.mode
            FROM pg_catalog.pg_locks AS l JOIN pg_catalog.pg_stat_activity AS a ON a.pid = l.pid
            WHERE l.locktype = 'advisory' AND l.granted AND l.objsubid = 1 AND a.datname = current_database()
              AND a.usename = 'learnstack_platform' AND l.pid <> pg_backend_pid()
            ORDER BY 1
            """, observer);
        await using var reader = await command.ExecuteReaderAsync();
        var held = new List<(long, string)>();
        while (await reader.ReadAsync()) held.Add((reader.GetInt64(0), reader.GetString(1)));
        return held;
    }

    /// <summary>Waits, from <c>pg_locks</c>, until a backend of the given role in this database waits on a lock.</summary>
    internal async Task WaitUntilALockIsAwaitedAsync(string role, Task contender, string because)
    {
        await using var observer = await OpenAsync(Database.PlatformConnectionString);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            contender.IsCompleted.Should().BeFalse(because);
            await using var command = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_locks l JOIN pg_catalog.pg_stat_activity a ON a.pid = l.pid "
                + "WHERE a.datname = current_database() AND a.usename = @role AND NOT l.granted)", observer);
            command.Parameters.AddWithValue("role", role);
            if ((bool)(await command.ExecuteScalarAsync())!) return;
            DateTime.UtcNow.Should().BeBefore(deadline, because);
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }
    }

    internal static async Task<NpgsqlConnection> OpenAsync(string connectionString) =>
        (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);

    /// <summary>Every module's reference locator, as the production composition registers them.</summary>
    internal static IReadOnlyList<IUserReferenceLocator> Locators()
    {
        var services = new ServiceCollection();
        services.AddIdentityContextualFoundation();
        services.AddDataExportCustody(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        return [.. provider.GetServices<IUserReferenceLocator>()];
    }

    public async ValueTask DisposeAsync()
    {
        await _unused.DisposeAsync();
        await _app.DisposeAsync();
        await _platform.DisposeAsync();
        await Database.DisposeAsync();
    }

    /// <summary>The audit store and the real redaction executor over the production locators, as the scope resolves them.</summary>
    private static IServiceScopeFactory AuditServices() => new ServiceCollection()
        .AddSingleton<IAuditRedactor>(new AuditRedactionExecutor(Locators()))
        .AddMetrics()
        .AddLogging()
        .AddScoped<IAuditStateCapture, AuditStateCapture>()
        .AddSingleton<IAuditHealth, AuditHealth>()
        .AddSingleton<IAuditCatalog>(new AuditCatalog([new TenancyAuditCatalogSource()]))
        .AddSingleton<ITenantContextAccessor>(StaticTenantContextAccessor.Unresolved)
        .AddScoped<IGlobalIdentityEnrollment, GlobalIdentityEnrollment>()
        .AddSingleton(new Lazy<NpgsqlDataSource>(() => throw new InvalidOperationException("The scope's rows are written on its own connection.")))
        .AddScoped<IAuditStore, PostgresAuditStore>()
        .BuildServiceProvider()
        .GetRequiredService<IServiceScopeFactory>();
}

/// <summary>
/// The identity provider's stand-in: it records each subject it is asked to delete, may run a probe first (which sees the
/// database while the act is between its two transactions), and answers what a test sets, removed by default, or throws.
/// </summary>
internal sealed class RecordingAccountRemoval : IIdentityProviderAccountRemoval
{
    public List<string> Subjects { get; } = [];

    public Func<int, AccountRemovalOutcome> Answer { get; set; } = _ => AccountRemovalOutcome.Removed;

    public Func<Task>? Probe { get; set; }

    public Exception? Failure { get; set; }

    public async Task<AccountRemovalOutcome> RemoveAsync(string providerSubject, CancellationToken cancellationToken = default)
    {
        Subjects.Add(providerSubject);
        if (Probe is { } probe) await probe();
        if (Failure is { } failure) throw failure;
        return Answer(Subjects.Count);
    }
}

/// <summary>
/// The fan-out's stand-in: it records each call, answers each group with the outcome a test sets (started by default), or
/// throws, and may run a probe first, which sees the database as the fan-out would.
/// </summary>
internal sealed class RecordingFanOut : IClosureFanOut
{
    public List<(PlatformOperatorWitness Operator, string? Ticket, ClosureFanOutRequest Request)> Calls { get; } = [];

    public Func<ClosureParticipantGroup, ClosureGroupOutcome> Outcome { get; set; } = _ => ClosureGroupOutcome.Started;

    public Exception? Failure { get; set; }

    public Func<Task>? Probe { get; set; }

    public async Task<IReadOnlyList<ClosureGroupReport>> FanOutAsync(
        PlatformOperatorWitness operatorWitness, string? ticket, ClosureFanOutRequest request, CancellationToken cancellationToken = default)
    {
        Calls.Add((operatorWitness, ticket, request));
        if (Probe is { } probe) await probe();
        if (Failure is { } failure) throw failure;
        return [.. request.Groups.Select(group => new ClosureGroupReport(group, Outcome(group)))];
    }
}

/// <summary>
/// The hold seam's stand-in: it reports a hold or none, records each query, and may hold the act open at the call, when
/// the Begin holds every fence and its request lock and has written nothing.
/// </summary>
internal sealed class GatedHolds(bool hold = false, bool gate = false) : IDataRequestHoldSource
{
    public List<DataRequestHoldQuery> Queries { get; } = [];

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<bool> HasHoldAsync(DataRequestHoldQuery query, CancellationToken cancellationToken)
    {
        Queries.Add(query);
        if (gate && Queries.Count == 1)
        {
            Entered.SetResult();
            await Release.Task;
        }

        return hold;
    }
}

/// <summary>The real scope with a handle whose commit is never sent and whose answer is a timeout, as a lost reply.</summary>
internal sealed class LostCommitScope(IPlatformAdminScope inner) : IPlatformAdminScope
{
    public async Task<IPlatformAdminScopeHandle> EnterAsync(
        PlatformAdmission admission, CancellationToken cancellationToken = default,
        [System.Runtime.CompilerServices.CallerMemberName] string? callerMember = null,
        [System.Runtime.CompilerServices.CallerFilePath] string? callerFile = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int callerLine = 0) =>
        new Handle(await inner.EnterAsync(admission, cancellationToken, callerMember, callerFile, callerLine));

    private sealed class Handle(IPlatformAdminScopeHandle real) : IPlatformAdminScopeHandle
    {
        public Task<int> ExecuteAsync(string sql, IReadOnlyList<PlatformSqlParameter>? parameters = null, CancellationToken cancellationToken = default) =>
            real.ExecuteAsync(sql, parameters, cancellationToken);

        public Task<IReadOnlyList<T>> QueryAsync<T>(
            string sql, IReadOnlyList<PlatformSqlParameter>? parameters, Func<System.Data.Common.DbDataReader, T> map,
            CancellationToken cancellationToken = default) => real.QueryAsync(sql, parameters, map, cancellationToken);

        public Task WriteIntentAsync(PlatformResult intent, CancellationToken cancellationToken = default) =>
            real.WriteIntentAsync(intent, cancellationToken);

        public Task<AuditRedactionReport> RedactAuditAsync(AuditRedactionPlan plan, CancellationToken cancellationToken = default) =>
            real.RedactAuditAsync(plan, cancellationToken);

        public Task WriteResultAsync(PlatformResult result, CancellationToken cancellationToken = default) =>
            real.WriteResultAsync(result, cancellationToken);

        public Task CommitAsync(CancellationToken cancellationToken = default) =>
            Task.FromException(new TimeoutException("forced lost commit"));

        public ValueTask DisposeAsync() => real.DisposeAsync();
    }
}
