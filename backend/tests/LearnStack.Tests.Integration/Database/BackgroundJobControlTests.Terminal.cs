using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobControlTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_terminal_owner_counts_committed_audit_after_scope_cleanup_even_when_cleanup_refuses_release(bool failCleanup)
    {
        await using var database = await DatabaseAsync();
        await using var host = Fixture.Host(database, new BackgroundJobFixtureProbe());
        await using var source = Source(database.OutboxConnectionString);
        var job = await RequestInvalidAsync(database, host.Services, source);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scopes = new HoldingScopes(host.Services.GetRequiredService<IServiceScopeFactory>(), entered, release, failCleanup);
        var bounds = new BackgroundJobRuntimeOptions { ControlPoll = TimeSpan.FromMilliseconds(50) };
        var executor = new JobExecutor(scopes, host.Services.GetRequiredService<ITenantContextAccessor>(),
            host.Services.GetRequiredService<BackgroundJobExecutionAdmission>(), bounds,
            host.Services.GetRequiredService<BackgroundJobObservation>());
        var gate = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var controller = new BackgroundJobControl(source, Fixture.Registry, executor, gate, bounds,
            new SystemGuidFactory(), TimeProvider.System);
        controller.Start();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await entered.Task.WaitAsync(deadline.Token);
            (await ReadDeadAsync(database, job)).Should().NotBeNull();
            (await AuditAsync(database, job)).Count.Should().Be(1);
            controller.Snapshot.Terminalized.Should().Be(0,
                "the actual terminal task still owns the held post-COMMIT scope cleanup");
            release.TrySetResult();
            while (controller.Snapshot.Terminalized != 1) await Task.Delay(10, deadline.Token);
            (await AuditAsync(database, job)).Count.Should().Be(1);
            if (failCleanup)
            {
                controller.Snapshot.TerminalHealthy.Should().BeFalse();
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var retained = (OwnedBackgroundJobControl<BackgroundJobControlChange>)typeof(BackgroundJobControl)
                    .GetField("_terminalOperation", flags)!.GetValue(controller)!;
                retained.CommitAcknowledged.Should().BeTrue();
                retained.CleanupConfirmed.Should().BeFalse();
                retained.KnownRollback.Should().BeFalse();
            }
        }
        finally
        {
            release.TrySetResult();
            gate.StopStarts();
            controller.RequestStop();
            // The finite observer joins the actual lane/stop tasks even when
            // the public cleanup witness must remain deliberately unconfirmed.
            var joined = (Task)typeof(BackgroundJobControl)
                .GetField("_completionObservation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(controller)!;
            await joined.WaitAsync(TimeSpan.FromSeconds(10));
        }
        controller.Snapshot.Terminalized.Should().Be(1);
        controller.Snapshot.CleanupConfirmed.Should().Be(!failCleanup);
        controller.Completion.IsCompletedSuccessfully.Should().Be(!failCleanup);
        (await AuditAsync(database, job)).Count.Should().Be(1);
    }

    [Fact]
    public async Task Invalid_never_started_org_job_terminalizes_with_one_actual_row_bound_MUST_and_no_causal_payload_access()
    {
        await using var database = await DatabaseAsync();
        await using var host = Fixture.Host(database, new BackgroundJobFixtureProbe());
        await using var source = Source(database.OutboxConnectionString);
        var job = await RequestInvalidAsync(database, host.Services, source, SchemaFixture.OrgA1);
        var terminal = Terminal(source, host.Services.GetRequiredService<IServiceScopeFactory>());
        var acquired = await terminal.TryClaimTerminalAsync([]);
        acquired.ThrowIfFailed();
        var claim = acquired.Value!;
        claim.NormalAttempts.Should().Be(0);
        claim.AttemptId.Should().BeNull();
        var completed = await terminal.TryTerminalizeAsync(claim, BackgroundJobRuntimeFixture.Traceparent);
        completed.ThrowIfFailed();
        completed.CommitAcknowledged.Should().BeTrue();
        completed.Value.Should().NotBeNull();
        var audit = await AuditAsync(database, job, SchemaFixture.OrgA1);
        audit.Count.Should().Be(1);
        audit.Actor.Should().Be(UserId.SystemActor.Value);
        audit.Operation.Should().Be("platform.job.dead_letter");
        audit.Metadata.Should().NotContain("secret").And.NotContain("causal_actor").And.NotContain("causation");
        using var json = JsonDocument.Parse(audit.Metadata!);
        json.RootElement.GetProperty("job_id").GetGuid().Should().Be(job.Value);
        json.RootElement.GetProperty("acquisition_token").GetGuid().Should().Be(claim.Token,
            "the trigger clears NEW ownership, so the witness must use the actual returned OLD recovery token");
        json.RootElement.GetProperty("reason").GetString().Should().Be("invalid_envelope");
        json.RootElement.GetProperty("normal_attempts").GetInt32().Should().Be(0);
        (await terminal.TryClaimTerminalAsync([])).Value.Should().BeNull();
        var duplicate = await terminal.TryTerminalizeAsync(claim, BackgroundJobRuntimeFixture.Traceparent);
        duplicate.ThrowIfFailed();
        duplicate.Value.Should().BeNull();
        (await AuditAsync(database, job, SchemaFixture.OrgA1)).Count.Should().Be(1);
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job, SchemaFixture.OrgA1)).Should().Be(0);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("organization")]
    [InlineData("job")]
    [InlineData("definition")]
    [InlineData("token")]
    [InlineData("reason")]
    [InlineData("trace")]
    [InlineData("timestamp")]
    [InlineData("actor")]
    [InlineData("operation")]
    [InlineData("class")]
    [InlineData("metadata")]
    [InlineData("pair")]
    public async Task Actual_fenced_job_witness_refuses_every_draft_or_pair_substitution_and_rolls_back_state(string substitution)
    {
        await using var database = await DatabaseAsync();
        await using var host = Fixture.Host(database, new BackgroundJobFixtureProbe());
        await using var source = Source(database.OutboxConnectionString);
        var job = await RequestInvalidAsync(database, host.Services, source, SchemaFixture.OrgA1);
        var scopes = new DecoratingScopes(host.Services.GetRequiredService<IServiceScopeFactory>(), inner => new JobAuditDecorator(inner,
            async (draft, witness, connection, transaction, token) =>
            {
                var changed = substitution switch
                {
                    "tenant" => draft with { TenantId = TenantId.From(SchemaFixture.TenantB) },
                    "organization" => draft with { OrganizationId = OrganizationId.From(SchemaFixture.OrgA2) },
                    "job" => draft with { Metadata = draft.Metadata!.Replace(job.Value.ToString("D"), Guid.CreateVersion7().ToString("D"), StringComparison.Ordinal) },
                    "definition" => draft with { Metadata = draft.Metadata!.Replace(witness.DefinitionKey, "fixture.substituted.v1", StringComparison.Ordinal) },
                    "token" => draft with { Metadata = draft.Metadata!.Replace(witness.AcquisitionToken.ToString("D"), Guid.NewGuid().ToString("D"), StringComparison.Ordinal) },
                    "reason" => draft with { Metadata = draft.Metadata!.Replace("invalid_envelope", "permanent_failure", StringComparison.Ordinal) },
                    "trace" => draft with { CorrelationId = "00-33333333333333333333333333333333-4444444444444444-01" },
                    "timestamp" => draft with { Timestamp = draft.Timestamp.AddTicks(10) },
                    "actor" => draft with { ActorUserId = UserId.From(BackgroundJobRuntimeFixture.Human) },
                    "operation" => draft with { Operation = "platform.outbox.dead_letter" },
                    "class" => draft with { OperationClass = OperationClass.Should },
                    "metadata" => draft with { Metadata = "{}" },
                    _ => draft,
                };
                if (substitution == "pair")
                {
                    await using var foreign = await PostgresFixture.OpenAsync(database.OutboxConnectionString, token);
                    await using var foreignTransaction = await foreign.BeginTransactionAsync(token);
                    await inner.WriteJobTerminalAsync(changed, witness, foreign, foreignTransaction, token);
                }
                else await inner.WriteJobTerminalAsync(changed, witness, connection, transaction, token);
            }));
        var store = Terminal(source, scopes);
        var claim = (await store.TryClaimTerminalAsync([])).Value!;
        var refused = await store.TryTerminalizeAsync(claim, BackgroundJobRuntimeFixture.Traceparent);
        refused.Failed.Should().BeTrue();
        refused.KnownRollback.Should().BeTrue();
        refused.CommitAcknowledged.Should().BeFalse();
        refused.CleanupConfirmed.Should().BeTrue();
        (await AuditAsync(database, job, SchemaFixture.OrgA1)).Count.Should().Be(0);
        (await ReadDeadAsync(database, job, SchemaFixture.OrgA1)).Should().BeNull();
    }

    [Fact]
    public async Task Missing_terminal_catalogue_refuses_the_real_writer_and_the_bound_witness_cannot_be_reused_after_COMMIT()
    {
        await using var database = await DatabaseAsync();
        await using var host = Fixture.Host(database, new BackgroundJobFixtureProbe());
        await using var source = Source(database.OutboxConnectionString);
        var job = await RequestInvalidAsync(database, host.Services, source);
        // The normal host catalogue is used for admission. A distinct real
        // terminal audit scope removes only this off-path operation.
        await using var missingHost = Fixture.Host(database, new BackgroundJobFixtureProbe(), configure: services =>
        {
            services.AddSingleton<IAuditCatalog>(new LearnStack.Infrastructure.Audit.AuditCatalog([]));
        });
        var missing = Terminal(source, missingHost.Services.GetRequiredService<IServiceScopeFactory>());
        var claim = (await missing.TryClaimTerminalAsync([])).Value!;
        var refused = await missing.TryTerminalizeAsync(claim, BackgroundJobRuntimeFixture.Traceparent);
        refused.Failed.Should().BeTrue();
        refused.KnownRollback.Should().BeTrue();
        (await ReadDeadAsync(database, job)).Should().BeNull();

        AuditEntryDraft? capturedDraft = null;
        JobTerminalTransition? capturedWitness = null;
        DbConnection? capturedConnection = null;
        DbTransaction? capturedTransaction = null;
        var scopes = new DecoratingScopes(host.Services.GetRequiredService<IServiceScopeFactory>(), inner => new JobAuditDecorator(inner,
            async (draft, witness, connection, transaction, token) =>
            {
                capturedDraft = draft;
                capturedWitness = witness;
                capturedConnection = connection;
                capturedTransaction = transaction;
                await inner.WriteJobTerminalAsync(draft, witness, connection, transaction, token);
            }));
        var completed = await Terminal(source, scopes).TryTerminalizeAsync(claim, BackgroundJobRuntimeFixture.Traceparent);
        completed.ThrowIfFailed();
        completed.CommitAcknowledged.Should().BeTrue();
        capturedWitness.Should().NotBeNull();
        var reuse = () => capturedWitness!.EnsureMatches(capturedDraft!, capturedConnection!, capturedTransaction!);
        reuse.Should().Throw<ArgumentException>();
        (await AuditAsync(database, job)).Count.Should().Be(1);
    }

    [Fact]
    public async Task Failed_terminal_audit_backoff_releases_only_the_exact_request_and_advances_past_the_poison_head()
    {
        await using var database = await DatabaseAsync();
        await using var host = Fixture.Host(database, new BackgroundJobFixtureProbe());
        await using var source = Source(database.OutboxConnectionString);
        var first = await RequestInvalidAsync(database, host.Services, source);
        var second = await RequestInvalidAsync(database, host.Services, source);
        var failures = new DecoratingScopes(host.Services.GetRequiredService<IServiceScopeFactory>(), inner => new JobAuditDecorator(inner,
            (_, _, _, _, _) => throw new AuditWriteFailedException("secret provider message")));
        var terminal = Terminal(source, failures);
        var claim = (await terminal.TryClaimTerminalAsync([])).Value!;
        claim.JobId.Should().Be(first.Value);
        var failed = await terminal.TryTerminalizeAsync(claim, BackgroundJobRuntimeFixture.Traceparent);
        failed.KnownRollback.Should().BeTrue();
        failed.CleanupConfirmed.Should().BeTrue();
        var backoff = await terminal.BackoffKnownAuditRollbackAsync(claim);
        backoff.ThrowIfFailed();
        backoff.Value.Should().NotBeNull();
        var next = (await terminal.TryClaimTerminalAsync([])).Value!;
        next.JobId.Should().Be(second.Value);
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, first)).Should().Be(0);
        (await ReadDeadAsync(database, first)).Should().BeNull();
        (await AuditAsync(database, first)).Count.Should().Be(0);
    }

    [Fact]
    public async Task Terminal_lock_wait_crossing_expiry_refuses_both_terminal_state_and_audit()
    {
        await using var database = await DatabaseAsync();
        await using var host = Fixture.Host(database, new BackgroundJobFixtureProbe());
        await using var source = Source(database.OutboxConnectionString);
        var job = await RequestInvalidAsync(database, host.Services, source);
        var store = Terminal(source, host.Services.GetRequiredService<IServiceScopeFactory>());
        var claim = (await store.TryClaimTerminalAsync([])).Value!;
        await using var locker = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await using var transaction = await locker.BeginTransactionAsync();
        await SchemaQueries.ExecuteAsync(locker, transaction,
            "UPDATE background_jobs SET ownership_until=clock_timestamp()+interval '150 milliseconds' WHERE id=@id", ("id", job.Value));
        var terminalizing = store.TryTerminalizeAsync(claim, BackgroundJobRuntimeFixture.Traceparent);
        await WaitForDispatcherLockAsync(database.OutboxConnectionString, ((NpgsqlConnection)locker).ProcessID);
        await SchemaQueries.ExecuteAsync(locker, transaction, "SELECT pg_sleep(0.25)");
        await transaction.CommitAsync();
        var result = await terminalizing.WaitAsync(TimeSpan.FromSeconds(5));
        result.ThrowIfFailed();
        result.Value.Should().BeNull();
        (await ReadDeadAsync(database, job)).Should().BeNull();
        (await AuditAsync(database, job)).Count.Should().Be(0);
    }

    [Fact]
    public async Task Real_terminal_COMMIT_ack_loss_locks_durable_completion_and_cannot_append_a_second_audit()
    {
        await using var database = await DatabaseAsync();
        await using var host = Fixture.Host(database, new BackgroundJobFixtureProbe());
        await using var healthy = Source(database.OutboxConnectionString);
        var job = await RequestInvalidAsync(database, host.Services, healthy);
        var claim = (await Terminal(healthy, host.Services.GetRequiredService<IServiceScopeFactory>()).TryClaimTerminalAsync([])).Value!;
        await using var proxy = new CommitResponseDroppingProxy(database.OutboxConnectionString);
        await using var faulty = Source(proxy.ConnectionString);
        var unknown = await Terminal(faulty, host.Services.GetRequiredService<IServiceScopeFactory>())
            .TryTerminalizeAsync(claim, BackgroundJobRuntimeFixture.Traceparent);
        proxy.DroppedCommitResponses.Should().Be(1);
        unknown.CommitUncertain.Should().BeTrue();
        unknown.Value.Should().BeNull();
        var observed = await Control(healthy).ProbeCommitAsync(unknown.Probes.Single());
        observed.ThrowIfFailed();
        observed.Value!.Disposition.Should().Be(BackgroundJobProbeDisposition.TerminalCompleted);
        (await AuditAsync(database, job)).Count.Should().Be(1);
        var repeat = await Terminal(healthy, host.Services.GetRequiredService<IServiceScopeFactory>())
            .TryTerminalizeAsync(claim, BackgroundJobRuntimeFixture.Traceparent);
        repeat.ThrowIfFailed();
        repeat.Value.Should().BeNull();
        (await AuditAsync(database, job)).Count.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Acknowledged_terminal_durability_survives_held_or_failed_actual_scope_cleanup_while_other_control_progresses(bool failCleanup)
    {
        await using var database = await DatabaseAsync();
        await using var host = Fixture.Host(database, new BackgroundJobFixtureProbe());
        await using var source = Source(database.OutboxConnectionString);
        var terminalJob = await RequestInvalidAsync(database, host.Services, source);
        var runningJob = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        var route = (await host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>().TryClaimAsync(runningJob, 1))!;
        var admitted = await host.Services.GetRequiredService<JobExecutor>().ExecuteAsync(route, BackgroundJobTestRun.Create());
        var identity = BackgroundJobExecutionIdentity.FromReceipt(admitted.Receipt!);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = Terminal(source, new HoldingScopes(host.Services.GetRequiredService<IServiceScopeFactory>(), entered, release, failCleanup));
        var claim = (await terminal.TryClaimTerminalAsync([])).Value!;
        var actual = terminal.TryTerminalizeAsync(claim, BackgroundJobRuntimeFixture.Traceparent);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            actual.IsCompleted.Should().BeFalse("the actual post-COMMIT audit scope is still held");
            (await ReadDeadAsync(database, terminalJob)).Should().NotBeNull();
            (await AuditAsync(database, terminalJob)).Count.Should().Be(1);
            var renewal = await Control(source).RenewBatchAsync([identity]);
            renewal.ThrowIfFailed();
            renewal.Value.Should().ContainSingle();
            var routingJob = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
            var routingClaim = (await host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>().TryClaimAsync(routingJob, 1))!;
            await DispatchAsync(database, "UPDATE background_jobs SET ownership_until=clock_timestamp()-interval '1 microsecond' WHERE id=@id", ("id", routingJob.Value));
            var recovered = await Control(source).RunRecoveryQuantumAsync(BackgroundJobControlLane.Recovery, [terminalJob.Value]);
            recovered.ThrowIfFailed();
            recovered.Value.Should().Contain(change => change.JobId == routingClaim.JobId.Value);
        }
        finally { release.TrySetResult(); }
        var completed = await actual.WaitAsync(TimeSpan.FromSeconds(5));
        completed.CommitAcknowledged.Should().BeTrue();
        completed.CommitUncertain.Should().BeFalse();
        completed.Value.Should().NotBeNull();
        completed.CleanupConfirmed.Should().Be(!failCleanup);
        completed.KnownRollback.Should().BeFalse();
        (await AuditAsync(database, terminalJob)).Count.Should().Be(1);
    }

    private static async Task<BackgroundJobId> RequestInvalidAsync(DisposableSchemaDatabase database,
        IServiceProvider services, PrivilegedDataSource source, Guid? organization = null)
    {
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            organization is null ? BackgroundJobRuntimeFixture.TenantKey : BackgroundJobRuntimeFixture.OrganizationKey,
            organization, parameters: "{\"ResourceId\":\"12345678-1234-7123-8123-123456789abc\",\"secret\":\"secret-envelope-value\"}");
        var claim = (await services.GetRequiredService<PostgresBackgroundJobRoutingStore>().TryClaimAsync(job, 1))!;
        var read = await services.GetRequiredService<JobExecutor>().ExecuteAsync(claim, BackgroundJobTestRun.Create());
        read.Outcome.Should().Be(JobStartExecutionOutcome.InvalidEnvelope);
        var request = await Control(source).RequestInvalidEnvelopeAsync(claim);
        request.ThrowIfFailed();
        request.Value!.TerminalReason.Should().Be("invalid_envelope");
        return job;
    }

    private static PostgresBackgroundJobTerminalStore Terminal(PrivilegedDataSource source, IServiceScopeFactory scopes) =>
        new(source, scopes, new(), new SystemGuidFactory());

    private static async Task<(long Count, Guid? Actor, string? Operation, string? Metadata)> AuditAsync(
        DisposableSchemaDatabase database, BackgroundJobId job, Guid? organization = null)
    {
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, organization);
        await using var command = new NpgsqlCommand("SELECT actor_user_id, operation, metadata::text FROM audit_log WHERE operation='platform.job.dead_letter' AND metadata->>'job_id'=@job", (NpgsqlConnection)app, (NpgsqlTransaction)transaction);
        command.Parameters.AddWithValue("job", job.Value.ToString("D"));
        await using var row = await command.ExecuteReaderAsync();
        long count = 0;
        Guid? actor = null;
        string? operation = null, metadata = null;
        while (await row.ReadAsync()) { count++; actor = row.GetGuid(0); operation = row.GetString(1); metadata = row.GetString(2); }
        return (count, actor, operation, metadata);
    }

    private static async Task<DateTimeOffset?> ReadDeadAsync(DisposableSchemaDatabase database, BackgroundJobId job, Guid? organization = null)
    {
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, organization);
        await using var command = new NpgsqlCommand("SELECT dead_lettered_at FROM background_jobs WHERE id=@id", (NpgsqlConnection)app, (NpgsqlTransaction)transaction);
        command.Parameters.AddWithValue("id", job.Value);
        var result = await command.ExecuteScalarAsync();
        return result is DateTime date ? new DateTimeOffset(date) : null;
    }

    private static async Task WaitForDispatcherLockAsync(string connectionString, int blockingPid)
    {
        await using var connection = await PostgresFixture.OpenAsync(connectionString);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (true)
        {
            // pg_stat_activity may truncate a longer predicate before its
            // FOR UPDATE suffix. Observe the real blocking session instead.
            await using var command = new NpgsqlCommand("""
                SELECT count(*) FROM pg_stat_activity WHERE datname=current_database()
                    AND usename='learnstack_outbox_admin' AND wait_event_type='Lock'
                    AND @blocker=ANY(pg_blocking_pids(pid))
                """, (NpgsqlConnection)connection);
            command.Parameters.AddWithValue("blocker", blockingPid);
            if ((long)(await command.ExecuteScalarAsync(deadline.Token))! > 0) return;
            await Task.Delay(10, deadline.Token);
        }
    }

    private sealed class DecoratingScopes(IServiceScopeFactory inner, Func<IAuditStore, IAuditStore> decorate) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new DecoratingScope(inner.CreateScope(), decorate);
    }
    private sealed class DecoratingScope(IServiceScope inner, Func<IAuditStore, IAuditStore> decorate) : IServiceScope, IServiceProvider, IAsyncDisposable
    {
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type type) => type == typeof(IAuditStore) ? decorate(inner.ServiceProvider.GetRequiredService<IAuditStore>()) : inner.ServiceProvider.GetService(type);
        public void Dispose() => inner.Dispose();
        public ValueTask DisposeAsync()
        {
            if (inner is IAsyncDisposable asyncScope) return asyncScope.DisposeAsync();
            inner.Dispose();
            return ValueTask.CompletedTask;
        }
    }
    private sealed class JobAuditDecorator(IAuditStore inner,
        Func<AuditEntryDraft, JobTerminalTransition, DbConnection, DbTransaction, CancellationToken, Task> terminal) : IAuditStore
    {
        public Task WriteGlobalIdentityPendingAsync(LearnStack.SharedKernel.Persistence.IUnitOfWork unit,
            GlobalIdentityCreationReference creation, CancellationToken cancellationToken = default) =>
            inner.WriteGlobalIdentityPendingAsync(unit, creation, cancellationToken);

        public Task ReconcileGlobalIdentityAsync(AuditIntent intent, IReadOnlyList<CapturedEntityChange> changes,
            AuditIntentState state, DateTimeOffset timestamp, CancellationToken cancellationToken = default) =>
            inner.ReconcileGlobalIdentityAsync(intent, changes, state, timestamp, cancellationToken);

        public Task WritePendingAsync(IUnitOfWork unit, CancellationToken token = default) => inner.WritePendingAsync(unit, token);
        public Task WriteStandaloneAsync(AuditEntryDraft entry, CancellationToken token = default) => inner.WriteStandaloneAsync(entry, token);
        public Task WriteBestEffortAsync(AuditEntryDraft entry, CancellationToken token = default) => inner.WriteBestEffortAsync(entry, token);
        public Task WritePlatformScopeAsync(AuditEntryDraft entry, DbConnection connection, DbTransaction transaction, CancellationToken token = default) => inner.WritePlatformScopeAsync(entry, connection, transaction, token);
        public Task WriteEventTerminalAsync(AuditEntryDraft entry, EventTerminalTransition witness, DbConnection connection, DbTransaction transaction, CancellationToken token = default) => inner.WriteEventTerminalAsync(entry, witness, connection, transaction, token);
        public Task WriteJobTerminalAsync(AuditEntryDraft entry, JobTerminalTransition witness, DbConnection connection, DbTransaction transaction, CancellationToken token = default) => terminal(entry, witness, connection, transaction, token);
    }

    private sealed class HoldingScopes(IServiceScopeFactory inner, TaskCompletionSource entered, TaskCompletionSource release, bool fail) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new HoldingScope(inner.CreateScope(), entered, release, fail);
    }
    private sealed class HoldingScope(IServiceScope inner, TaskCompletionSource entered, TaskCompletionSource release, bool fail) : IServiceScope, IAsyncDisposable
    {
        public IServiceProvider ServiceProvider => inner.ServiceProvider;
        public void Dispose() => throw new InvalidOperationException("The actual terminal scope requires its async cleanup.");
        public async ValueTask DisposeAsync()
        {
            entered.TrySetResult();
            await release.Task;
            if (inner is IAsyncDisposable asyncScope) await asyncScope.DisposeAsync(); else inner.Dispose();
            if (fail) throw new InvalidOperationException("Deliberate actual terminal scope cleanup fault.");
        }
    }
}
