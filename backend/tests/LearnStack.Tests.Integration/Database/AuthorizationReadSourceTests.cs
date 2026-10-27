using System.Data;
using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class AuthorizationReadSourceTests(SchemaFixture schema)
{
    [Fact]
    public async Task Four_retained_physical_owners_refuse_excess_immediately_and_recover_only_after_disposal()
    {
        await using var source = await SourceAsync();
        using var trusted = new TestSource();
        var candidate = trusted.Candidate();
        var owners = new List<AuthorizationReadOwner>();
        try
        {
            for (var index = 0; index < 4; index++) owners.Add((await source.OpenAsync(candidate))!);
            owners.Select(owner => ((NpgsqlConnection)owner.Unit.Connection).ProcessID).Distinct().Should().HaveCount(4);
            var excess = source.OpenAsync(candidate);
            excess.IsCompleted.Should().BeTrue("the source has no admission wait queue");
            (await excess).Should().BeNull();
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await FluentActions.Awaiting(() => source.OpenAsync(candidate, canceled.Token))
                .Should().ThrowAsync<OperationCanceledException>();
            await owners[0].CompleteAsync();
            (await source.OpenAsync(candidate)).Should().BeNull("COMMIT does not confirm resource cleanup");
            await owners[0].DisposeAsync();
            var replacement = (await source.OpenAsync(candidate))!;
            owners.Add(replacement);
            replacement.Should().NotBeNull();
        }
        finally { foreach (var owner in owners) await owner.DisposeAsync(); }
    }

    [Fact]
    public async Task Candidate_owner_is_physically_read_only_exactly_announced_and_cannot_join_or_retarget()
    {
        await using var source = await SourceAsync(capacity: 1);
        using var trusted = new TestSource();
        await using var owner = (await source.OpenAsync(trusted.Candidate()))!;
        var transaction = owner.Unit.Transaction;
        var announcement = owner.Unit.GetTenantContextAnnouncement(transaction)!;
        announcement.Generation.Should().BePositive();
        announcement.TenantId.Should().Be(TenantId.From(SchemaFixture.TenantA));
        announcement.OrganizationId.Should().Be(OrganizationId.From(SchemaFixture.OrgA1));
        (await ScalarAsync(owner, "SHOW transaction_read_only")).Should().Be("on");
        (await ScalarAsync(owner, "SHOW transaction_isolation")).Should().Be("read committed");
        foreach (var name in new[] { "scope", "resolving_host", "identity_authority_id", "identity_subject_key" })
            (await ScalarAsync(owner, $"SELECT current_setting('app.{name}', true)")).Should().Be(string.Empty);
        var join = () => owner.Unit.BeginTransactionAsync(TransactionMode.ReadOnly);
        await join.Should().ThrowAsync<InvalidOperationException>();
        var provision = () => owner.Unit.SetProvisioningTenantContextAsync(TenantId.From(SchemaFixture.TenantB));
        await provision.Should().ThrowAsync<InvalidOperationException>();
        var independentCommit = () => owner.Unit.CommitAsync();
        await independentCommit.Should().ThrowAsync<InvalidOperationException>();
        await owner.CompleteAsync();
        owner.Unit.GetTenantContextAnnouncement(transaction).Should().BeNull();
        await owner.DisposeAsync();
        await using var next = (await source.OpenAsync(trusted.Candidate()))!;
        next.Unit.GetTenantContextAnnouncement(next.Unit.Transaction)!.Generation.Should().BeGreaterThan(announcement.Generation);
        owner.Unit.GetTenantContextAnnouncement(next.Unit.Transaction).Should().BeNull();
    }

    [Fact]
    public async Task Cancellation_during_actual_setup_keeps_capacity_and_shutdown_join_until_the_operation_really_finishes()
    {
        using var pause = new SetupPause();
        using var logs = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(pause));
        var source = await SourceAsync(1, logs);
        using var trusted = new TestSource();
        using var cancellation = new CancellationTokenSource();
        pause.Arm();
        var opening = Task.Run(() => source.OpenAsync(trusted.Candidate(), cancellation.Token));
        Task? shutdown = null;
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            opening.IsCompleted.Should().BeFalse("the real driver setup is still retained");
            (await source.OpenAsync(trusted.Candidate())).Should().BeNull();
            shutdown = source.DisposeAsync().AsTask();
            shutdown.IsCompleted.Should().BeFalse("shutdown joins actual cleanup, regardless of request cancellation");
        }
        finally { pause.Release(); }
        await FluentActions.Awaiting(() => opening).Should().ThrowAsync<OperationCanceledException>();
        await (shutdown ?? source.DisposeAsync().AsTask());
        source.IsQualified.Should().BeFalse();
    }

    [Fact]
    public async Task Reported_command_cleanup_failure_retains_capacity_even_after_transaction_and_connection_disposal()
    {
        var source = await SourceAsync(capacity: 1);
        using var trusted = new TestSource();
        var owner = (await source.OpenAsync(trusted.Candidate()))!;
        // This is the reader's denial-only report after its actual command/reader DisposeAsync failed.
        owner.MarkCleanupFailed();
        await FluentActions.Awaiting(() => owner.DisposeAsync().AsTask()).Should().ThrowAsync<InvalidOperationException>();
        (await source.OpenAsync(trusted.Candidate())).Should().BeNull();
        await FluentActions.Awaiting(() => source.DisposeAsync().AsTask()).Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(() => source.DisposeAsync().AsTask()).Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_connection_disposal_retains_source_and_job_ownership_until_its_positive_completion(bool dirty)
    {
        var source = await SourceAsync(capacity: 1);
        using var trusted = new TestSource();
        var job = JobApplicationSourceOwner.Create();
        using var binding = job.Enter();
        var owner = (await source.OpenAsync(trusted.Candidate()))!;
        var connection = (NpgsqlConnection)owner.Unit.Connection;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        StateChangeEventHandler disposal = (_, change) =>
        {
            if (change.CurrentState != ConnectionState.Closed) return;
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            if (dirty) throw new InvalidOperationException("Deliberate actual connection disposal failure.");
        };
        connection.StateChange += disposal;
        var cleanup = Task.Run(() => owner.DisposeAsync().AsTask());
        Task? shutdown = null;
        Task<bool>? jobJoin = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cleanup.IsCompleted.Should().BeFalse();
            (await source.OpenAsync(trusted.Candidate())).Should().BeNull();
            job.CleanupConfirmed.Should().BeFalse();
            job.CloseIssuance();
            jobJoin = job.JoinAuxiliaryCleanupAsync();
            shutdown = source.DisposeAsync().AsTask();
            jobJoin.IsCompleted.Should().BeFalse();
            shutdown.IsCompleted.Should().BeFalse();
        }
        finally { release.TrySetResult(); }
        if (dirty)
        {
            await FluentActions.Awaiting(() => cleanup).Should().ThrowAsync<InvalidOperationException>();
            await FluentActions.Awaiting(() => shutdown!).Should().ThrowAsync<InvalidOperationException>();
        }
        else { await cleanup; await shutdown!; }
        (await jobJoin!).Should().Be(!dirty);
        connection.StateChange -= disposal;
        job.CleanupConfirmed.Should().Be(!dirty);
    }

    [Fact]
    public async Task One_deadline_covers_statements_and_completion_without_a_fresh_commit_budget()
    {
        await using var source = await SourceAsync(capacity: 1);
        using var trusted = new TestSource();
        await using var owner = (await source.OpenAsync(trusted.Candidate()))!;
        var transaction = owner.Unit.Transaction;
        await ScalarAsync(owner, "SELECT pg_sleep(2.6)");
        var second = () => ScalarAsync(owner, "SELECT pg_sleep(3)");
        await second.Should().ThrowAsync<OperationCanceledException>();
        owner.DeadlineToken.IsCancellationRequested.Should().BeTrue();
        owner.Unit.HasActiveTransaction.Should().BeFalse();
        owner.Unit.Transaction.Should().BeNull();
        owner.Unit.GetTenantContextAnnouncement(transaction).Should().BeNull();
        var reuse = () => owner.Unit.Connection;
        reuse.Should().Throw<OperationCanceledException>();
        (await source.OpenAsync(trusted.Candidate())).Should().BeNull("deadline expiry never releases the retained owner slot");
        await FluentActions.Awaiting(owner.CompleteAsync).Should().ThrowAsync<OperationCanceledException>();
        await owner.DisposeAsync();
        await using var replacement = (await source.OpenAsync(trusted.Candidate()))!;
        replacement.Should().NotBeNull();
    }

    [Fact]
    public async Task Qualification_refuses_another_installed_authority_and_never_enables_candidate_issuance()
    {
        await using var source = new IdentityAuthorizationReadSource(schema.Postgres.AppConnectionString, 1);
        await FluentActions.Awaiting(() => source.QualifyAsync(IdentityAuthorityId.From(Guid.CreateVersion7()),
            IdentityMembershipFixture.Authority.Issuer)).Should().ThrowAsync<InvalidOperationException>();
        source.IsQualified.Should().BeFalse();
        using var trusted = new TestSource();
        await FluentActions.Awaiting(() => source.OpenAsync(trusted.Candidate())).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Cancellation_bounds_the_actual_role_guard_statement_before_any_candidate_owner_can_be_published()
    {
        await using var source = await SourceAsync(capacity: 1);
        using var trusted = new TestSource();
        var warm = (await source.OpenAsync(trusted.Candidate()))!;
        var backend = ((NpgsqlConnection)warm.Unit.Connection).ProcessID;
        await warm.DisposeAsync();
        // Both clients exist before the shared catalog is locked. The source reuses its
        // qualified physical connector, so this isolates guard SQL from login cancellation.
        await using var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(schema.Postgres.AppConnectionString);
        var heldLock = schema.Postgres.ExecuteAsSuperuserAsync("""
            BEGIN;
            LOCK TABLE pg_catalog.pg_authid IN ACCESS EXCLUSIVE MODE;
            SELECT pg_sleep(8);
            ROLLBACK;
            """);
        Task<AuthorizationReadOwner?>? blocked = null;
        using var cancellation = new CancellationTokenSource();
        try
        {
            await WaitForRoleCatalogLockAsync(observer, backend: null, granted: true);
            blocked = source.OpenAsync(trusted.Candidate(), cancellation.Token);
            await WaitForRoleCatalogLockAsync(observer, backend, granted: false);
            blocked.IsCompleted.Should().BeFalse();
            cancellation.Cancel();
            await FluentActions.Awaiting(() => blocked.WaitAsync(TimeSpan.FromSeconds(3)))
                .Should().ThrowAsync<OperationCanceledException>();
            heldLock.IsCompleted.Should().BeFalse("the actual role-guard SQL must cancel before its blocker releases");
        }
        finally
        {
            cancellation.Cancel();
            await heldLock;
            if (blocked is not null)
                await ((Task)blocked).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        }
        await using var recovered = (await source.OpenAsync(trusted.Candidate()))!;
        recovered.Should().NotBeNull();
    }

    private async Task<IdentityAuthorizationReadSource> SourceAsync(int capacity = 4, ILoggerFactory? logs = null)
    {
        var source = new IdentityAuthorizationReadSource(schema.Postgres.AppConnectionString, capacity, logs);
        await source.QualifyAsync(IdentityMembershipFixture.Authority.AuthorityId, IdentityMembershipFixture.Authority.Issuer);
        source.QualifiedDatabaseName.Should().Be(new NpgsqlConnectionStringBuilder(schema.Postgres.AppConnectionString).Database);
        return source;
    }

    private static async Task<object?> ScalarAsync(AuthorizationReadOwner owner, string sql)
    {
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)owner.Unit.Connection,
            (NpgsqlTransaction?)owner.Unit.Transaction);
        return await command.ExecuteScalarAsync(owner.DeadlineToken);
    }

    private static async Task WaitForRoleCatalogLockAsync(NpgsqlConnection observer, int? backend, bool granted)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            await using var query = new NpgsqlCommand("""
                SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_locks
                    WHERE relation = 'pg_catalog.pg_authid'::regclass
                      AND granted = @granted AND (@backend = 0 OR pid = @backend))
                """, observer);
            query.Parameters.AddWithValue("granted", granted);
            query.Parameters.AddWithValue("backend", backend ?? 0);
            if (await query.ExecuteScalarAsync(deadline.Token) is true) return;
            await Task.Delay(10, deadline.Token);
        }
    }

    private sealed class TestSource : AuthorizationCandidate.MintingSource
    {
        public AuthorizationCandidate Candidate() => ForCandidate(AuthorizationSourceKind.Http, UserId.From(IdentityMembershipFixture.AdminA),
            TenantId.From(SchemaFixture.TenantA), OrganizationId.From(SchemaFixture.OrgA1));
    }

    private sealed class SetupPause : ILoggerProvider, ILogger
    {
        private readonly ManualResetEventSlim _release = new(false);
        private int _armed;
        private int _fired;
        private int _disposed;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Arm() => Volatile.Write(ref _armed, 1);
        internal void Release() => _release.Set();
        public ILogger CreateLogger(string categoryName) => this;
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (Volatile.Read(ref _armed) == 0
                || !formatter(state, exception).StartsWith("Executing command: SET TRANSACTION READ ONLY", StringComparison.Ordinal)
                || Interlocked.Exchange(ref _fired, 1) != 0) return;
            Entered.TrySetResult();
            if (!_release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Setup gate was not released.");
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _release.Set();
            _release.Dispose();
        }
    }
}
