using FluentAssertions;
using FluentAssertions.Execution;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobPositiveSdkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_external_publication_uses_its_exact_native_stage_and_Prepare_ACK_does_not_end_renewal(bool finalization)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        var resource = Guid.CreateVersion7();
        var prepared = OrganizationId.From(Guid.CreateVersion7());
        var finalized = OrganizationId.From(Guid.CreateVersion7());
        await using var host = Fixture.Host(database, provider.Settings);
        var executor = host.Services.GetRequiredService<JobExecutor>();
        LearnStack.SharedKernel.Jobs.BackgroundJobId job;
        if (finalization)
        {
            var flow = await AdmitPrepareAndArmAsync(database, host, resource, prepared, finalized);
            var report = await executor.ExecuteExternalAsync(flow.Read, flow.Arm, CancellationToken.None);
            report.Status.Should().Be(JobExternalExecutionStatus.Reported);
            (await executor.RecordExternalOutcomeAsync(flow.Receipt, flow.Read,
                flow.Receipt.Run.OpenBoundary(BackgroundJobCheckpointKind.Outcome), report.Observation!, CancellationToken.None))
                .CommitAcknowledged.Should().BeTrue();
            job = flow.Receipt.Envelope.JobId;
        }
        else
        {
            var submitted = await Fixture.SubmitAsync(host.Services, Fixture.Parameters(resource, prepared, finalized));
            submitted.IsSuccess.Should().BeTrue();
            job = submitted.Value;
        }
        var routing = host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>();
        var claim = await routing.TryClaimAsync(job, await GenerationAsync(database, job));
        claim.Should().NotBeNull();
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var bounds = new BackgroundJobRuntimeOptions { RenewalInterval = TimeSpan.FromSeconds(1) };
        var time = new NativeLeaseTimerObservation();
        var controller = new BackgroundJobControl(source, Fixture.Registry, executor, runtime, bounds,
            new SystemGuidFactory(), time);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, bounds, new SystemGuidFactory(), runtime);
        var generation = new HangfireGenerationWork(Fixture.Registry.Infrastructure.Pools["external"], runtime);
        var work = generation.TryBegin()!;
        var execution = controller.TryBegin(claim!, work)!;
        work.HoldExecution(execution);
        try
        {
            // Real publication and native control owners; the test invokes each
            // store explicitly and does not claim the complete Hangfire handoff.
            var admission = executor.ExecutePublishedAsync(claim!, execution.Publication, execution.AdmissionGate, null, work.Token);
            work.HoldAdmission(admission);
            execution.HoldAdmission(admission);
            var admitted = await admission;
            admitted.Outcome.Should().Be(JobStartExecutionOutcome.Started);
            admitted.CleanupFailed.Should().BeFalse();
            await execution.WaitForPublicationAsync();
            execution.Identity.Should().NotBeNull("the actual External receipt must establish its native control identity");
            execution.Identity!.Profile.Should().Be("external");
            execution.Identity.LedgerStage.Should().Be(admitted.Receipt!.LedgerStage);
            execution.Identity.FinalizeAttemptOrdinal.Should().Be(finalization ? 1 : 0);
            execution.Identity.SelectedNormalAttemptId.Should().Be(admitted.Receipt.SelectedNormalAttemptId);
            execution.Gate.Kind.Should().Be(finalization ? BackgroundJobCheckpointKind.Finalize : BackgroundJobCheckpointKind.Prepare);
            execution.RenewalPermitted.Should().BeTrue();
            var beforeBusiness = await store.RenewBatchAsync([execution.Identity]);
            beforeBusiness.ThrowIfFailed();
            beforeBusiness.Value.Should().ContainSingle("both actual preparing and finalizing execution leases are renewable");
            var exactProbe = beforeBusiness.Probes.Single();
            var confirmed = await store.ProbeCommitAsync(exactProbe);
            confirmed.ThrowIfFailed();
            confirmed.Value!.Disposition.Should().Be(BackgroundJobProbeDisposition.Renewed);
            foreach (var wrong in new[]
            {
                exactProbe with { Profile = "transactional" },
                exactProbe with { LedgerStage = finalization ? LearnStack.SharedKernel.Jobs.BackgroundJobLedgerStage.Normal
                    : LearnStack.SharedKernel.Jobs.BackgroundJobLedgerStage.Finalize },
                exactProbe with { FinalizeOrdinal = exactProbe.FinalizeOrdinal + 1 },
                exactProbe with { SelectedNormalAttemptId = Guid.CreateVersion7() },
                exactProbe with { Deadline = exactProbe.Deadline!.Value.AddTicks(10) },
                exactProbe with { AdmittedAt = exactProbe.AdmittedAt!.Value.AddTicks(10) },
                exactProbe with { OriginalDueAt = exactProbe.OriginalDueAt!.Value.AddTicks(10) },
            })
            {
                var rejected = await store.ProbeCommitAsync(wrong);
                rejected.ThrowIfFailed();
                rejected.Value!.Disposition.Should().Be(BackgroundJobProbeDisposition.ChangedOwner,
                    "a status-only copied probe must not borrow the current execution's distinct profile/ledger facts");
            }
            var business = executor.ExecuteBusinessAsync(admitted.Receipt!, execution.Gate, work.Token);
            execution.HoldBusiness(business);
            work.HoldBusiness(business);
            var completed = await business;
            completed.CommitAcknowledged.Should().BeTrue();
            completed.CleanupFailed.Should().BeFalse();
            execution.RenewalPermitted.Should().Be(!finalization,
                "Prepare ACK retains external lease work, while Finalize ACK ends that stage's lease work");
            if (!finalization)
            {
                var issuedAt = time.GetTimestamp();
                var renewed = await store.RenewBatchAsync([execution.Identity!]);
                renewed.ThrowIfFailed();
                renewed.CleanupConfirmed.Should().BeTrue();
                renewed.Value.Should().ContainSingle();
                execution.RecordRenewal(renewed.Value!.Single(), issuedAt).Should().BeTrue();
                execution.RenewalPermitted.Should().BeTrue();
                execution.RunGate.IsClosed.Should().BeFalse();
                var changes = time.Timer(execution).Changes;
                var previousLease = await NativeLeaseAsync(database, job);
                controller.Start();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (!deadline.IsCancellationRequested
                    && (time.Timer(execution).Changes <= changes || controller.Snapshot.Renewals == 0))
                    await Task.Delay(10);
                using var observations = new AssertionScope();
                time.Timer(execution).Changes.Should().BeGreaterThan(changes,
                    "the actual renewal lane must rearm its owned local lease timer after Prepare ACK");
                (await NativeLeaseAsync(database, job)).Should().BeAfter(previousLease,
                    "the same lane must advance the real SQL lease, not merely count a loop iteration");
                (await provider.CountsAsync((await executor.ReadExternalStateAsync(admitted.Receipt!, CancellationToken.None))
                    .Handle!.Require(admitted.Receipt!).OperationId!.Value)).Should().Be((0L, 0L, 0L));
            }
        }
        finally
        {
            var joined = await execution.JoinAsync(operationCleanupConfirmed: true);
            await work.CompleteAsync(joined);
            generation.CloseStarts();
            controller.RequestStop();
            await controller.Completion.WaitAsync(TimeSpan.FromSeconds(15));
            if (joined) await generation.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        }
        controller.Snapshot.Executions.Should().Be(0, "positive actual private cleanup releases the same native identity");
    }

    private static async Task<DateTimeOffset> NativeLeaseAsync(DisposableSchemaDatabase database,
        LearnStack.SharedKernel.Jobs.BackgroundJobId job)
    {
        await using var connection = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await using var command = new NpgsqlCommand("SELECT ownership_until FROM background_jobs WHERE id=@id",
            (NpgsqlConnection)connection)
        { CommandTimeout = 5 };
        command.Parameters.AddWithValue("id", job.Value);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        return row.GetFieldValue<DateTimeOffset>(0);
    }

    private sealed class NativeLeaseTimerObservation : TimeProvider
    {
        private readonly Dictionary<BackgroundJobExecutionControl, ObservedNativeLeaseTimer> _timers = [];
        internal ObservedNativeLeaseTimer Timer(BackgroundJobExecutionControl execution)
        {
            lock (_timers) return _timers[execution];
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var actual = System.CreateTimer(callback, state, dueTime, period);
            if (state is not BackgroundJobExecutionControl execution) return actual;
            var observed = new ObservedNativeLeaseTimer(actual);
            lock (_timers) _timers.Add(execution, observed);
            return observed;
        }
    }

    private sealed class ObservedNativeLeaseTimer(ITimer actual) : ITimer
    {
        private int _changes;
        internal int Changes => Volatile.Read(ref _changes);
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            var changed = actual.Change(dueTime, period);
            if (changed) Interlocked.Increment(ref _changes);
            return changed;
        }
        public void Dispose() => actual.Dispose();
        public ValueTask DisposeAsync() => actual.DisposeAsync();
    }
}
