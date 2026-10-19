using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobPositiveSdkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_no_intent_recovery_uses_exact_NoStart_history_after_lost_app_commit_ACK(bool loseAck)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        await using var originalHost = ShortRecoveryOriginalHost(database, provider.Settings);
        var original = await PreparedRecoveryWithoutIntentAsync(database, originalHost);
        var construction = new BackgroundJobPositiveProviderConstructionProbe();
        await using var proxy = new CommitResponseDroppingProxy(database.AppConnectionString,
            commitsToPass: loseAck ? 1 : int.MaxValue);
        await using var host = Fixture.Host(database, provider.Settings, construction)
            .WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Default", proxy.ConnectionString));
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var bounds = new BackgroundJobRuntimeOptions();
        var control = new BackgroundJobControl(source, Fixture.Registry, host.Services.GetRequiredService<JobExecutor>(),
            runtime, bounds, new SystemGuidFactory(), TimeProvider.System);
        var generation = new HangfireGenerationWork(Fixture.Registry.Infrastructure.Pools["external"], runtime);
        var work = generation.TryBegin()!;
        var execution = control.TryBeginRecovery(original.Receipt.Envelope.JobId, await GenerationAsync(database, original.Receipt.Envelope.JobId),
            original.Receipt.Envelope.Queue, work)!;
        work.HoldExecution(execution);
        var clean = false;
        try
        {
            clean = await control.ExecuteExternalRecoveryAsync(execution);
            clean.Should().BeTrue();
            var checkpoint = await (Task<JobExternalCheckpointResult>)NativeSdkField(execution, "_externalOutcome");
            checkpoint.Kind.Should().Be(BackgroundJobCheckpointKind.NoStart);
            checkpoint.Outcome!.IsNoStart.Should().BeTrue();
            checkpoint.Issued.Should().BeTrue();
            checkpoint.CommitStarted.Should().BeTrue();
            checkpoint.CommitAcknowledged.Should().Be(!loseAck);
            checkpoint.KnownRollback.Should().BeFalse();
            proxy.CompletedCommits.Should().Be(2, "one actual app read precedes the actual NoStart writer");
            proxy.DroppedCommitResponses.Should().Be(loseAck ? 1 : 0);
            if (loseAck)
            {
                var history = (Task<OwnedBackgroundJobControl<BackgroundJobCheckpointHistoryObservation>>)
                    NativeSdkField(execution, "_outcomeHistory");
                var confirmed = await history;
                confirmed.CleanupConfirmed.Should().BeTrue();
                confirmed.Failed.Should().BeFalse();
                confirmed.Value!.Disposition.Should().Be(BackgroundJobCheckpointHistoryDisposition.Committed);
                confirmed.Value.Confirmation!.Matches(checkpoint.Outcome).Should().BeTrue();
                checkpoint.Gate.CommitConfirmed.Should().BeTrue();
                checkpoint.CommitAcknowledged.Should().BeFalse();
            }
            execution.LeaseWorkSettled.Should().BeTrue();
            construction.Calls.Should().Be(0);
            (await provider.CountsAsync(original.Operation)).Should().Be((0L, 0L, 0L));
            await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
            await using var transaction = await app.BeginTransactionAsync();
            await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            await using var command = new NpgsqlCommand("""
                SELECT j.normal_attempts,j.finalize_attempts,a.no_start_at IS NOT NULL,
                    a.execute_intent_at IS NULL AND a.outcome IS NULL AND a.failure_policy IS NULL,
                    r.released_at IS NOT NULL AND r.release_reason='never_started',
                    (SELECT count(*) FROM background_job_attempts x WHERE x.job_id=j.id)
                FROM background_jobs j JOIN background_job_attempts a ON a.id=j.current_attempt_id
                    JOIN background_job_reservations r ON r.attempt_id=a.id WHERE j.id=@id
                """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction) { CommandTimeout = 5 };
            command.Parameters.AddWithValue("id", original.Receipt.Envelope.JobId.Value);
            await using var row = await command.ExecuteReaderAsync();
            (await row.ReadAsync()).Should().BeTrue();
            row.GetInt32(0).Should().Be(1);
            row.GetInt32(1).Should().Be(0);
            row.GetBoolean(2).Should().BeTrue();
            row.GetBoolean(3).Should().BeTrue();
            row.GetBoolean(4).Should().BeTrue();
            row.GetInt64(5).Should().Be(1);
        }
        finally
        {
            await work.CompleteAsync(await execution.JoinAsync(clean));
            generation.CloseStarts();
            control.RequestStop();
            await control.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        }
        control.Snapshot.Executions.Should().Be(0);
        await generation.Completion.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, false)]
    public async Task Native_recovery_timer_initialization_keeps_host_stop_independent_and_requires_the_actual_handle(
        bool leaseTimer, bool throwBeforeReturn, bool change, bool stopAfterHold)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        await using var originalHost = ShortRecoveryOriginalHost(database, provider.Settings);
        var original = await PreparedRecoveryWithoutIntentAsync(database, originalHost);
        var construction = new BackgroundJobPositiveProviderConstructionProbe();
        await using var host = Fixture.Host(database, provider.Settings, construction);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var bounds = new BackgroundJobRuntimeOptions();
        await using var time = new UnreturnedRecoveryTimer(leaseTimer, throwBeforeReturn, change);
        var control = new BackgroundJobControl(source, Fixture.Registry, host.Services.GetRequiredService<JobExecutor>(),
            runtime, bounds, new SystemGuidFactory(), time);
        var generation = new HangfireGenerationWork(Fixture.Registry.Infrastructure.Pools["external"], runtime, bounds, time);
        var work = generation.TryBegin()!;
        var execution = control.TryBeginRecovery(original.Receipt.Envelope.JobId, await GenerationAsync(database, original.Receipt.Envelope.JobId),
            original.Receipt.Envelope.Queue, work)!;
        work.HoldExecution(execution);
        Task<bool>? actual = null;
        Task? stopping = null;
        var clean = false;
        try
        {
            actual = Task.Run(() => control.ExecuteExternalRecoveryAsync(execution));
            await time.Entered.WaitAsync(TimeSpan.FromSeconds(15));
            actual.IsCompleted.Should().BeFalse();
            var heldIdentity = execution.Identity;
            if (!change || leaseTimer) heldIdentity.Should().BeNull();
            control.CanRoute(execution.JobId).Should().BeFalse();
            control.Snapshot.Executions.Should().Be(1);
            if (leaseTimer || heldIdentity is not null)
            {
                var acquisition = (Task<OwnedBackgroundJobExternalRecovery>)NativeSdkField(execution, "_recoveryAcquisition");
                var acquired = await acquisition;
                acquired.CommitAcknowledged.Should().BeTrue();
                acquired.CleanupConfirmed.Should().BeTrue();
            }
            control.Snapshot.LeaseLossElapsed.Should().Be(0);
            control.Snapshot.LeaseLossRenewalRefused.Should().Be(0);
            if (stopAfterHold)
            {
                stopping = Task.Run(generation.RequestHostStop);
                await stopping.WaitAsync(TimeSpan.FromSeconds(5));
                execution.RunGate.IsClosed.Should().BeTrue();
                work.ReturnRequested.IsCompleted.Should().BeTrue();
            }
            else
            {
                // The real acquisition has ACKed; only the native monotonic
                // observation clock advances while the actual timer prefix holds.
                time.Advance(TimeSpan.FromSeconds(61));
                execution.RunGate.IsClosed.Should().BeFalse();
            }
            control.Snapshot.Executions.Should().Be(1);
            time.Release();
            clean = await actual.WaitAsync(TimeSpan.FromSeconds(15));
            control.Snapshot.LeaseLossElapsed.Should().Be(stopAfterHold ? 0 : 1);
            control.Snapshot.LeaseLossRenewalRefused.Should().Be(0);
            if (!stopAfterHold) execution.RunGate.IsClosed.Should().BeTrue();
            var faultBeforeJoin = execution.CleanupFault;
            var joined = await execution.JoinAsync(clean);
            await work.CompleteAsync(joined);
            generation.CloseStarts();
            clean.Should().Be(!throwBeforeReturn, "only an actual returned timer handle permits positive cleanup");
            faultBeforeJoin.Should().Be(throwBeforeReturn);
            joined.Should().Be(!throwBeforeReturn);
            execution.Identity.Should().BeSameAs(heldIdentity, "host stop cannot publish a later identity from the held timer call");
            time.ProductionDisposals.Should().Be(throwBeforeReturn ? 0 : 1,
                "the returned handle must actually be disposed before the fixture's private timer cleanup");
            control.Snapshot.Executions.Should().Be(throwBeforeReturn ? 1 : 0);
            generation.Snapshot.Retained.Should().Be(throwBeforeReturn ? 1 : 0);
            if (throwBeforeReturn) control.CanRoute(execution.JobId).Should().BeFalse();
            else await generation.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            construction.Calls.Should().Be(0);
            (await provider.CountsAsync(original.Operation)).Should().Be((0L, 0L, 0L));
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, execution.JobId)).Should().Be(1);
        }
        finally
        {
            time.Release();
            if (stopping is not null) await stopping;
            if (actual is not null) clean = await actual;
            // The fault is intentionally unrecoverable to the production owner.
            // The test knows and disposes the hidden timer for fixture hygiene;
            // this supplies no production join/replacement/release witness.
            control.RequestStop();
            await control.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    private static WebApplicationFactory<Program> ShortRecoveryOriginalHost(DisposableSchemaDatabase database,
        BackgroundJobProviderSettings settings) => Fixture.Host(database, settings, configure: services =>
        {
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(new BackgroundJobRuntimeOptions { ExternalWaitTimeout = TimeSpan.FromSeconds(4) });
        });

    private static async Task<(ConfirmedStartReceipt Receipt, Guid Operation)> PreparedRecoveryWithoutIntentAsync(
        DisposableSchemaDatabase database, WebApplicationFactory<Program> host)
    {
        var submitted = await Fixture.SubmitAsync(host.Services, Fixture.Parameters(Guid.CreateVersion7(),
            OrganizationId.From(Guid.CreateVersion7()), OrganizationId.From(Guid.CreateVersion7())));
        submitted.IsSuccess.Should().BeTrue();
        var claim = await host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>()
            .TryClaimAsync(submitted.Value, await GenerationAsync(database, submitted.Value));
        var executor = host.Services.GetRequiredService<JobExecutor>();
        var admitted = await executor.ExecuteAsync(claim!, BackgroundJobTestRun.Create());
        admitted.Outcome.Should().Be(JobStartExecutionOutcome.Started);
        var receipt = admitted.Receipt!;
        (await executor.ExecuteBusinessAsync(receipt, receipt.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare)))
            .CommitAcknowledged.Should().BeTrue();
        var read = await executor.ReadExternalStateAsync(receipt, CancellationToken.None);
        var operation = read.Handle!.Require(receipt).OperationId!.Value;
        receipt.Run.Close();
        await WaitForDatabaseLeaseAsync(database, receipt.Deadline);
        await using var control = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await SchemaQueries.ExecuteAsync(control, null, """
            UPDATE background_jobs SET phase='reconciling',ownership_token=NULL,ownership_kind=NULL,ownership_until=NULL,probe_after=clock_timestamp()
            WHERE id=@id AND ownership_until<=clock_timestamp()
            """, ("id", receipt.Envelope.JobId.Value));
        return (receipt, operation);
    }

    private sealed class UnreturnedRecoveryTimer(bool leaseTimer, bool throwBeforeReturn, bool change) : TimeProvider, IAsyncDisposable
    {
        private readonly List<ITimer> _timers = [];
        private readonly ManualResetEventSlim _release = new();
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _held, _productionDisposals;
        private long _offset;
        public override long GetTimestamp() => System.GetTimestamp() + Interlocked.Read(ref _offset);
        internal void Advance(TimeSpan value) => Interlocked.Add(ref _offset, (long)(value.TotalSeconds * TimestampFrequency));
        private readonly bool _holdChange = change;
        internal Task Entered => _entered.Task;
        internal int ProductionDisposals => Volatile.Read(ref _productionDisposals);
        internal void Release() => _release.Set();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var actual = System.CreateTimer(callback, state, dueTime, period);
            lock (_timers) _timers.Add(actual);
            var target = leaseTimer ? state is BackgroundJobExecutionControl : state is HangfireGenerationWork.Invocation;
            var observed = new ObservedRecoveryTimer(actual, this, target);
            if (target && !_holdChange) Hold();
            return observed;
        }
        private void Hold()
        {
            if (Interlocked.Exchange(ref _held, 1) != 0) return;
            _entered.TrySetResult();
            if (!_release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("The test timer prefix was not released.");
            if (throwBeforeReturn) throw new InvalidOperationException("The test timer allocated an owner but did not return it.");
        }
        public async ValueTask DisposeAsync()
        {
            Release();
            // Bypass the observed wrappers: fixture hygiene cannot create a
            // production-disposal count or satisfy the assertions above.
            foreach (var timer in _timers) await timer.DisposeAsync();
            _release.Dispose();
        }
        private sealed class ObservedRecoveryTimer(ITimer actual, UnreturnedRecoveryTimer owner, bool target) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (target && owner._holdChange) owner.Hold();
                return actual.Change(dueTime, period);
            }
            public void Dispose()
            {
                actual.Dispose();
                if (target) Interlocked.Increment(ref owner._productionDisposals);
            }
            public async ValueTask DisposeAsync()
            {
                await actual.DisposeAsync();
                if (target) Interlocked.Increment(ref owner._productionDisposals);
            }
        }
    }
}
