using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
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
    public Task Actual_Outcome_ACK_settles_renewal_before_its_held_cleanup_can_release_the_native_identity(bool knownFailure) =>
        AssertHeldOutcomeSettlementAsync(knownFailure, retainedRecovery: false);

    [Fact]
    public Task Actual_stored_recovery_selection_repairs_the_early_finalize_hint_only_after_its_real_cleanup() =>
        AssertHeldOutcomeSettlementAsync(knownFailure: false, retainedRecovery: true);

    private async Task AssertHeldOutcomeSettlementAsync(bool knownFailure, bool retainedRecovery)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        var prepared = OrganizationId.From(Guid.CreateVersion7());
        var finalized = OrganizationId.From(Guid.CreateVersion7());
        var resource = Guid.CreateVersion7();
        await provider.SetModeAsync(resource, knownFailure ? "not_repeatable" : "succeeded");
        var bounds = new BackgroundJobRuntimeOptions
        {
            RenewalInterval = TimeSpan.FromSeconds(1),
            HandoffRepairGrace = TimeSpan.FromSeconds(15),
        };
        await using var host = Fixture.Host(database, provider.Settings, configure: services =>
        {
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(bounds);
        });
        BackgroundJobId job;
        await using var originalHost = retainedRecovery ? ShortRecoveryOriginalHost(database, provider.Settings) : null;
        if (retainedRecovery)
        {
            var originalFlow = await AdmitPrepareAndArmAsync(database, originalHost!, resource, prepared, finalized);
            var executed = await originalFlow.Executor.ExecuteExternalAsync(originalFlow.Read, originalFlow.Arm, CancellationToken.None);
            executed.Status.Should().Be(JobExternalExecutionStatus.Reported);
            executed.CleanupFailed.Should().BeFalse();
            await PersistRetainedSdkOutcomeAsync(database, originalFlow.Receipt, executed.Observation!);
            originalFlow.Receipt.Run.Close();
            await WaitForDatabaseLeaseAsync(database, originalFlow.Receipt.Deadline);
            job = originalFlow.Receipt.Envelope.JobId;
        }
        else
        {
            var submitted = await Fixture.SubmitAsync(host.Services, Fixture.Parameters(resource, prepared, finalized));
            submitted.IsSuccess.Should().BeTrue();
            job = submitted.Value;
        }
        var original = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
        ActivityContext.TryParse(original.Traceparent, null, out var parent).Should().BeTrue();
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        using var globals = new NativeSdkGlobalFilterSnapshot();
        var time = new NativeLeaseTimerObservation();
        var runtime = NativeSdkRuntime(host.Services, source, bounds, time);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        BackgroundJobExecutionControl? captured = null;
        BackgroundJobCommitGate? outcomeGate = null;
        var held = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = activitySource => activitySource.Name == BackgroundJobObservation.InstrumentationName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName != "job.external_checkpoint" || activity.TraceId != parent.TraceId) return;
                var controller = (BackgroundJobControl)NativeSdkField(runtime, "_jobs");
                BackgroundJobExecutionControl execution;
                lock (NativeSdkField(controller, "_sync"))
                    execution = ((Dictionary<Guid, BackgroundJobExecutionControl>)NativeSdkField(controller, "_executions"))[job.Value];
                if (typeof(BackgroundJobExecutionControl).GetField("_externalOutcomeGate", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?.GetValue(execution) is not BackgroundJobCommitGate { CommitAcknowledged: true } gate
                    || Interlocked.CompareExchange(ref held, 1, 0) != 0) return;
                captured = execution;
                outcomeGate = gate;
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Actual Outcome cleanup was not released.");
            },
        };
        ActivitySource.AddActivityListener(listener);
        try
        {
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await runtime.StartAsync(startup.Token);
            await entered.Task.WaitAsync(startup.Token);
            var execution = captured!;
            execution.Identity!.IsRecovery.Should().Be(retainedRecovery);
            if (retainedRecovery)
                typeof(BackgroundJobExecutionControl).GetField("_externalProvider", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(execution).Should().BeNull("retained completed facts select without a Reconcile/provider scope");
            var actual = (Task<JobExternalCheckpointResult>)NativeSdkField(execution, "_externalOutcome");
            actual.IsCompleted.Should().BeFalse("the actual Activity cleanup remains blocked after the physical COMMIT ACK");
            outcomeGate!.Kind.Should().Be(BackgroundJobCheckpointKind.Outcome);
            outcomeGate.CommitAcknowledged.Should().BeTrue();
            execution.LeaseWorkSettled.Should().BeTrue();
            execution.RenewalPermitted.Should().BeFalse();
            execution.RunGate.IsClosed.Should().BeFalse();
            runtime.Snapshot.Control!.Executions.Should().Be(1);
            var changes = time.Timer(execution).Changes;
            var controller = (BackgroundJobControl)NativeSdkField(runtime, "_jobs");
            var progress = NativeSdkRenewalProgress(controller);
            using var observer = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            while (NativeSdkRenewalProgress(controller) == progress) await Task.Delay(10, observer.Token);
            execution.RenewalRefused.Should().BeFalse("the real renewal loop must not interpret settled ownership as a failed renewal");
            execution.RunGate.IsClosed.Should().BeFalse();
            time.Timer(execution).Changes.Should().Be(changes);
            actual.IsCompleted.Should().BeFalse();
            runtime.Snapshot.Control.Executions.Should().Be(1);
            var stored = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
            stored.Phase.Should().Be("finalize_pending");
            stored.OwnershipKind.Should().BeNull();
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, finalized.Value)).Should().Be(0);
            controller.CanRoute(job).Should().BeFalse("the original acknowledged Outcome still owns actual cleanup");
            // Force the legal interleaving: a new-generation opaque hint has
            // already completed while the old native identity forbids routing.
            // Its ACK postpones repair; vendor success is not domain completion.
            using (var early = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                NativeFinalizeHint? hint = null;
                try
                {
                    while ((hint = await ReadEarlyFinalizeHintAsync(source, job, early.Token)) is null
                        || hint.Generation != execution.Identity!.WakeGeneration + 1 || hint.Phase != "finalize_pending"
                        || hint.NormalAttempts != 1 || hint.FinalizeAttempts != 0
                        || hint.OwnershipToken is not null || hint.OwnershipKind is not null
                        || hint.BridgeToken is not null || hint.BridgeUntil is not null
                        || hint.VendorState != "Succeeded" || hint.HandoffAfter <= hint.DatabaseNow)
                        await Task.Delay(10, early.Token);
                }
                catch (OperationCanceledException) when (early.IsCancellationRequested)
                {
                    throw new TimeoutException("The early finalize hint did not reach its actual completed state: " + hint);
                }
            }
            actual.IsCompleted.Should().BeFalse();
            controller.CanRoute(job).Should().BeFalse();
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
            release.Set();
            using var completed = new CancellationTokenSource(TimeSpan.FromSeconds(80));
            while ((await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!.Phase
                    != (knownFailure ? "terminal_pending" : "completed")
                || runtime.Snapshot.Control!.Executions != 0)
                await Task.Delay(20, completed.Token);
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(2);
            (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!.NormalAttempts.Should().Be(1);
            (await GenerationAsync(database, job)).Should().Be(execution.Identity!.WakeGeneration + 1,
                "repair resends the same eligibility generation instead of admitting another external attempt");
            var acknowledged = await actual;
            acknowledged.Outcome!.IsStoredCompleted.Should().Be(retainedRecovery);
            (await provider.CountsAsync(acknowledged.Outcome.OperationId)).Should().Be((1L, 0L, knownFailure ? 0L : 1L));
        }
        finally
        {
            release.Set();
            runtime.RequestStop();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await runtime.StopAsync(cleanup.Token);
            await runtime.Completion.WaitAsync(cleanup.Token);
        }
        held.Should().Be(1);
        runtime.Snapshot.Control!.Executions.Should().Be(0);
    }

    private static async Task PersistRetainedSdkOutcomeAsync(DisposableSchemaDatabase database,
        ConfirmedStartReceipt receipt, BackgroundJobRetainedExternalObservation observation)
    {
        receipt.SourceOwner.CleanupConfirmed.Should().BeTrue();
        observation.State.Should().Be(BackgroundJobExternalState.Succeeded);
        observation.QuiescenceKind.Should().Be(BackgroundJobQuiescenceKind.ProviderFenced);
        observation.JobId.Should().Be(receipt.Envelope.JobId);
        observation.AttemptId.Should().Be(receipt.AttemptId);
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        await SchemaQueries.ExecuteAsync(app, transaction, """
            UPDATE background_job_attempts SET outcome=@outcome,outcome_kind='succeeded',outcome_at=clock_timestamp(),
                quiescence_id=@evidence,quiescence_kind='provider_fenced',quiescent_at=clock_timestamp()
                WHERE id=@attempt;
            UPDATE background_job_reservations SET released_at=clock_timestamp(),release_reason='quiescent'
                WHERE attempt_id=@attempt AND released_at IS NULL
            """, ("attempt", receipt.AttemptId.Value), ("outcome", observation.OutcomeJson!),
            ("evidence", observation.QuiescenceId!.Value));
        await transaction.CommitAsync();
    }

    private sealed record NativeFinalizeHint(string? VendorId, long Generation, string Phase, int NormalAttempts,
        int FinalizeAttempts, Guid? OwnershipToken, string? OwnershipKind, DateTimeOffset HandoffAfter,
        DateTimeOffset DatabaseNow, string? VendorState, Guid? BridgeToken, DateTimeOffset? BridgeUntil);

    private static async Task<NativeFinalizeHint?> ReadEarlyFinalizeHintAsync(PrivilegedDataSource source, BackgroundJobId job,
        CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT j.vendor_job_id,j.wake_generation,j.phase,j.normal_attempts,j.finalize_attempts,
                j.ownership_token,j.ownership_kind,j.handoff_check_at,clock_timestamp(),h.statename,j.bridge_token,j.bridge_until
            FROM background_jobs j LEFT JOIN public.job h ON h.id::text=j.vendor_job_id WHERE j.id=@id
            """, connection) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("id", job.Value);
        await using var row = await command.ExecuteReaderAsync(token);
        return await row.ReadAsync(token) ? new(row.IsDBNull(0) ? null : row.GetString(0), row.GetInt64(1), row.GetString(2),
            row.GetInt32(3), row.GetInt32(4), row.IsDBNull(5) ? null : row.GetGuid(5), row.IsDBNull(6) ? null : row.GetString(6),
            row.GetFieldValue<DateTimeOffset>(7), row.GetFieldValue<DateTimeOffset>(8), row.IsDBNull(9) ? null : row.GetString(9),
            row.IsDBNull(10) ? null : row.GetGuid(10), row.IsDBNull(11) ? null : row.GetFieldValue<DateTimeOffset>(11)) : null;
    }

    private static long? NativeSdkRenewalProgress(BackgroundJobControl control)
    {
        lock (NativeSdkField(control, "_sync"))
            return (long?)typeof(BackgroundJobControl).GetField("_renewalProgress", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(control);
    }
}
