using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobPositiveSdkTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Native_known_repeatable_outcome_retries_a_distinct_attempt_with_the_same_operation(
        bool cancelled, bool lostOutcomeAck)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        var resource = Guid.CreateVersion7();
        var prepared = OrganizationId.From(Guid.CreateVersion7());
        var finalized = OrganizationId.From(Guid.CreateVersion7());
        await provider.SetModeAsync(resource, cancelled ? "cancelled_repeatable" : "repeatable");
        BackgroundJobId job;
        await using (var submit = Fixture.Host(database, provider.Settings))
        {
            var submitted = await Fixture.SubmitAsync(submit.Services, Fixture.Parameters(resource, prepared, finalized));
            submitted.IsSuccess.Should().BeTrue();
            job = submitted.Value;
        }
        var original = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
        ActivityContext.TryParse(original.Traceparent, null, out var parent).Should().BeTrue();
        var bounds = new BackgroundJobRuntimeOptions { HandoffRepairGrace = TimeSpan.FromSeconds(15) };
        await using var proxy = new CommitResponseDroppingProxy(database.AppConnectionString,
            commitsToPass: 5, holdCommitResponse: true, lifetime: TimeSpan.FromMinutes(2));
        var hostBuilder = Fixture.Host(database, provider.Settings, configure: services =>
        {
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(bounds);
        }, applicationPoolSize: 32);
        await using var host = lostOutcomeAck ? hostBuilder.WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Default", proxy.ConnectionString)) : hostBuilder;
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        using var globals = new NativeSdkGlobalFilterSnapshot();
        var runtime = NativeSdkRuntime(host.Services, source, bounds);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        BackgroundJobExecutionControl? captured = null;
        BackgroundJobCommitGate? capturedGate = null;
        var held = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = activitySource => !lostOutcomeAck && activitySource.Name == BackgroundJobObservation.InstrumentationName,
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
                capturedGate = gate;
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("First retry Outcome cleanup was not released.");
            },
        };
        ActivitySource.AddActivityListener(listener);
        try
        {
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await runtime.StartAsync(startup.Token);
            if (lostOutcomeAck)
            {
                await proxy.CommitResponseHeld.WaitAsync(startup.Token);
                proxy.CompletedCommits.Should().Be(6, "the held response belongs to the real Outcome transaction");
                var controller = (BackgroundJobControl)NativeSdkField(runtime, "_jobs");
                lock (NativeSdkField(controller, "_sync"))
                    captured = ((Dictionary<Guid, BackgroundJobExecutionControl>)NativeSdkField(controller, "_executions"))[job.Value];
                capturedGate = (BackgroundJobCommitGate)NativeSdkField(captured, "_externalOutcomeGate");
                capturedGate.Kind.Should().Be(BackgroundJobCheckpointKind.Outcome);
                capturedGate.CommitAcknowledged.Should().BeFalse();
            }
            else await entered.Task.WaitAsync(startup.Token);
            var first = captured!;
            var outcomeTask = (Task<JobExternalCheckpointResult>)NativeSdkField(first, "_externalOutcome");
            outcomeTask.IsCompleted.Should().BeFalse("the first actual writer/cleanup is still retained");
            runtime.Snapshot.Control!.Retries.Should().Be(0);
            typeof(BackgroundJobExecutionControl).GetField("_externalRetry", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(first).Should().BeNull("no disposition source may start before the actual writer has cleaned");
            var known = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
            known.Phase.Should().Be("executing");
            known.NormalAttempts.Should().Be(1);
            known.CurrentAttempt.Should().Be(first.Identity!.AttemptId.Value);
            known.OwnershipUntil.Should().BeAfter(DateTimeOffset.UtcNow);
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, finalized.Value)).Should().Be(0);
            // Change only the independent provider's next-attempt behavior
            // while the first accepted report and its durable checkpoint are fixed.
            await provider.SetModeAsync(resource, "succeeded");
            release.Set();
            proxy.ReleaseHeldResponse();
            using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(80));
            while ((await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!.Phase != "completed"
                || runtime.Snapshot.Control!.Executions != 0) await Task.Delay(20, completion.Token);
            var outcome = await outcomeTask;
            outcome.CleanupFailed.Should().BeFalse();
            outcome.CommitAcknowledged.Should().Be(!lostOutcomeAck);
            capturedGate!.CommitConfirmed.Should().Be(lostOutcomeAck);
            if (lostOutcomeAck)
            {
                proxy.DroppedCommitResponses.Should().Be(1);
                var history = await (Task<OwnedBackgroundJobControl<BackgroundJobCheckpointHistoryObservation>>)NativeSdkField(first, "_outcomeHistory");
                history.CleanupConfirmed.Should().BeTrue();
                history.Failed.Should().BeFalse();
                history.Value!.Disposition.Should().Be(BackgroundJobCheckpointHistoryDisposition.Committed);
                history.Value.Confirmation!.Matches(outcome.Outcome!).Should().BeTrue();
            }
            var retry = await (Task<OwnedBackgroundJobControl<BackgroundJobControlChange>>)NativeSdkField(first, "_externalRetry");
            retry.ThrowIfFailed();
            retry.CleanupConfirmed.Should().BeTrue();
            retry.CommitAcknowledged.Should().BeTrue();
            retry.Value!.WakeGeneration.Should().Be(first.Identity.WakeGeneration + 1);
            runtime.Snapshot.Control!.Retries.Should().Be(1, "the actual completed retry disposition is counted once");
            var final = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
            final.NormalAttempts.Should().Be(2);
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(3,
                "two real normal admissions and one fresh finalizer are the complete ledger");
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, prepared.Value)).Should().Be(1);
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, finalized.Value)).Should().Be(1);
            (await provider.CountsAsync(outcome.Outcome!.OperationId)).Should().Be((2L, 0L, 1L));
            await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
            await using var transaction = await app.BeginTransactionAsync();
            await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            await using var evidence = new NpgsqlCommand("""
                SELECT count(DISTINCT a.operation_id),count(DISTINCT a.plan_fingerprint),
                    count(*) FILTER(WHERE a.stage='normal' AND r.released_at IS NOT NULL),
                    count(*) FILTER(WHERE a.stage='normal' AND a.failure_policy='transient_repeatable'),
                    count(*) FILTER(WHERE a.stage='normal' AND a.outcome_kind='succeeded')
                FROM background_job_attempts a LEFT JOIN background_job_reservations r ON r.attempt_id=a.id WHERE a.job_id=@id
                """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction) { CommandTimeout = 5 };
            evidence.Parameters.AddWithValue("id", job.Value);
            await using var row = await evidence.ExecuteReaderAsync();
            (await row.ReadAsync()).Should().BeTrue();
            row.GetInt64(0).Should().Be(1);
            row.GetInt64(1).Should().Be(1);
            row.GetInt64(2).Should().Be(2);
            row.GetInt64(3).Should().Be(1);
            row.GetInt64(4).Should().Be(1);
        }
        finally
        {
            release.Set();
            proxy.ReleaseHeldResponse();
            runtime.RequestStop();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await runtime.StopAsync(cleanup.Token);
            await runtime.Completion.WaitAsync(cleanup.Token);
            if (!ReferenceEquals(hostBuilder, host)) await hostBuilder.DisposeAsync();
        }
        runtime.Snapshot.CleanupConfirmed.Should().BeTrue();
        runtime.Snapshot.RetainedInvocations.Should().Be(0);
    }

    [Theory]
    [InlineData("retained", "repeatable", null)]
    [InlineData("reconcile", "repeatable", null)]
    [InlineData("no_start", "repeatable", null)]
    [InlineData("reconcile", "cancelled_repeatable", null)]
    [InlineData("reconcile", "not_repeatable", "external_retry_forbidden")]
    public async Task Native_known_recovery_preserves_exact_policy_before_retry_or_failure_finalization(
        string mode, string providerMode, string? terminalReason)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        var resource = Guid.CreateVersion7();
        var prepared = OrganizationId.From(Guid.CreateVersion7());
        var finalized = OrganizationId.From(Guid.CreateVersion7());
        await provider.SetModeAsync(resource, providerMode);
        await using var originalHost = ShortRecoveryOriginalHost(database, provider.Settings);
        var executor = originalHost.Services.GetRequiredService<JobExecutor>();
        ConfirmedStartReceipt normal;
        Guid operation;
        if (mode == "no_start")
        {
            var submitted = await Fixture.SubmitAsync(originalHost.Services, Fixture.Parameters(resource, prepared, finalized));
            submitted.IsSuccess.Should().BeTrue();
            var claim = await originalHost.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>()
                .TryClaimAsync(submitted.Value, await GenerationAsync(database, submitted.Value));
            claim.Should().NotBeNull();
            var admitted = await executor.ExecuteAsync(claim!, BackgroundJobTestRun.Create());
            admitted.CleanupFailed.Should().BeFalse();
            normal = admitted.Receipt!;
            var plan = await executor.ExecuteBusinessAsync(normal, normal.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare));
            plan.CommitAcknowledged.Should().BeTrue();
            plan.CleanupFailed.Should().BeFalse();
            operation = (await executor.ReadExternalStateAsync(normal, CancellationToken.None)).Handle!.Snapshot.OperationId!.Value;
        }
        else
        {
            var original = await AdmitPrepareAndArmAsync(database, originalHost, resource, prepared, finalized);
            normal = original.Receipt;
            operation = original.Operation;
            var report = await executor.ExecuteExternalAsync(original.Read, original.Arm, CancellationToken.None);
            report.Status.Should().Be(JobExternalExecutionStatus.Reported);
            report.CleanupFailed.Should().BeFalse();
            report.Observation!.State.Should().Be(providerMode == "cancelled_repeatable"
                ? BackgroundJobExternalState.Cancelled : BackgroundJobExternalState.Failed);
            report.Observation.FailurePolicy.Should().Be(terminalReason is null
                ? BackgroundJobExternalFailurePolicy.TransientRepeatable : BackgroundJobExternalFailurePolicy.TransientNotRepeatable);
            if (mode == "retained")
            {
                var recorded = await executor.RecordExternalOutcomeAsync(normal, original.Read,
                    normal.Run.OpenBoundary(BackgroundJobCheckpointKind.Outcome), report.Observation, CancellationToken.None);
                recorded.CommitAcknowledged.Should().BeTrue();
                recorded.CleanupFailed.Should().BeFalse();
            }
        }
        var qualifyReconcilePolicy = mode == "reconcile" && providerMode != "repeatable";
        BackgroundJobProviderCausalFacts? originalProvider = null;
        if (qualifyReconcilePolicy)
        {
            originalProvider = await provider.CausalFactsAsync(new(normal.Envelope.JobId, normal.AttemptId, operation, normal.Deadline));
            originalProvider.State.Should().Be(providerMode == "cancelled_repeatable" ? "cancelled" : "failed");
            originalProvider.Policy.Should().Be(terminalReason is null ? "transient_repeatable" : "transient_not_repeatable");
            originalProvider.Closed.Should().BeTrue();
            originalProvider.Evidence.Should().NotBeNull();
            originalProvider.Effects.Should().Be(0);
        }
        normal.SourceOwner.CleanupConfirmed.Should().BeTrue();
        normal.Run.Close();
        await WaitForDatabaseLeaseAsync(database, normal.Deadline);
        await provider.SetModeAsync(resource, "succeeded");
        var construction = new BackgroundJobPositiveProviderConstructionProbe();
        var bounds = new BackgroundJobRuntimeOptions { HandoffRepairGrace = TimeSpan.FromSeconds(15) };
        await using var host = Fixture.Host(database, provider.Settings, construction, configure: services =>
        {
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(bounds);
        }, applicationPoolSize: 32);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        using var globals = new NativeSdkGlobalFilterSnapshot();
        var runtime = NativeSdkRuntime(host.Services, source, bounds);
        var outcomeCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseOutcomeCleanup = new ManualResetEventSlim();
        var observedOutcome = 0;
        var storedEnvelope = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, normal.Envelope.JobId))!;
        ActivityContext.TryParse(storedEnvelope.Traceparent, null, out var trace).Should().BeTrue();
        using var listener = new ActivityListener
        {
            ShouldListenTo = activitySource => qualifyReconcilePolicy && activitySource.Name == BackgroundJobObservation.InstrumentationName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName != "job.external_checkpoint" || activity.TraceId != trace.TraceId) return;
                var controller = (BackgroundJobControl)NativeSdkField(runtime, "_jobs");
                BackgroundJobExecutionControl execution;
                lock (NativeSdkField(controller, "_sync"))
                    execution = ((Dictionary<Guid, BackgroundJobExecutionControl>)NativeSdkField(controller, "_executions"))[normal.Envelope.JobId.Value];
                if (typeof(BackgroundJobExecutionControl).GetField("_externalOutcomeGate", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?.GetValue(execution) is not BackgroundJobCommitGate { CommitAcknowledged: true }
                    || Interlocked.CompareExchange(ref observedOutcome, 1, 0) != 0) return;
                execution.Identity!.IsRecovery.Should().BeTrue();
                outcomeCommitted.TrySetResult();
                if (!releaseOutcomeCleanup.Wait(TimeSpan.FromSeconds(30)))
                    throw new TimeoutException("The reconciled Outcome cleanup was not released.");
            },
        };
        ActivitySource.AddActivityListener(listener);
        try
        {
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await runtime.StartAsync(startup.Token);
            if (qualifyReconcilePolicy)
            {
                await outcomeCommitted.Task.WaitAsync(TimeSpan.FromSeconds(30));
                // The actual recovered Outcome COMMIT has landed, while its task
                // still owns cleanup. No retry/finalizer disposition can issue yet.
                await AssertRecoveredPolicyBeforeDispositionAsync(database, normal, operation, originalProvider!, terminalReason);
                (await provider.CausalFactsAsync(new(normal.Envelope.JobId, normal.AttemptId, operation, normal.Deadline)))
                    .Should().Be(originalProvider, "Reconcile cannot relabel the independent provider's immutable completed facts");
                (await provider.CountsAsync(operation)).Should().Be((1L, 1L, 0L),
                    "Reconcile must preserve the completed old attempt before any later Execute or Finalize");
                releaseOutcomeCleanup.Set();
            }
            using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(80));
            while ((await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, normal.Envelope.JobId))!.Phase
                    != (terminalReason is null ? "completed" : "terminal_pending")
                || runtime.Snapshot.Control!.Executions != 0) await Task.Delay(20, completion.Token);
            var row = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, normal.Envelope.JobId))!;
            row.NormalAttempts.Should().Be(terminalReason is null ? 2 : 1);
            runtime.Snapshot.Control!.Retries.Should().Be(terminalReason is null ? 1 : 0);
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, normal.Envelope.JobId))
                .Should().Be(terminalReason is null ? 3 : 2);
            construction.Calls.Should().Be(mode == "reconcile" && terminalReason is null ? 2 : 1,
                "stored facts and positive NoStart need no provider call; only unresolved intent requires Reconcile");
            (await provider.CountsAsync(operation)).Should().Be((mode == "no_start" || terminalReason is not null ? 1L : 2L,
                mode == "reconcile" ? 1L : 0L, terminalReason is null ? 1L : 0L));
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, prepared.Value)).Should().Be(1);
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, finalized.Value)).Should().Be(1);
            if (terminalReason is not null)
                await AssertRecoveredForbiddenTerminalAsync(database, normal, terminalReason);
        }
        finally
        {
            releaseOutcomeCleanup.Set();
            runtime.RequestStop();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await runtime.StopAsync(cleanup.Token);
            await runtime.Completion.WaitAsync(cleanup.Token);
        }
        runtime.Snapshot.CleanupConfirmed.Should().BeTrue();
        runtime.Snapshot.RetainedInvocations.Should().Be(0);
    }

    private static async Task AssertRecoveredPolicyBeforeDispositionAsync(DisposableSchemaDatabase database,
        ConfirmedStartReceipt original, Guid operation, BackgroundJobProviderCausalFacts provider, string? terminalReason)
    {
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, original.Envelope.TenantId.Value, null);
        await using var command = new NpgsqlCommand("""
            SELECT a.outcome_kind,a.failure_policy,a.operation_id,a.plan_fingerprint,j.plan_fingerprint,
                a.quiescence_id,a.quiescence_kind,a.outcome_at,a.quiescent_at,r.released_at,r.release_reason,
                j.phase,j.normal_attempts,j.finalize_attempts,j.final_outcome_attempt_id,j.finalization_status,
                j.finalization_reason,j.terminal_reason,
                (SELECT count(*) FROM background_job_attempts x WHERE x.job_id=j.id)
            FROM background_jobs j JOIN background_job_attempts a ON a.tenant_id=j.tenant_id AND a.job_id=j.id
                AND a.id=@attempt AND a.stage='normal'
            JOIN background_job_reservations r ON r.tenant_id=a.tenant_id AND r.job_id=a.job_id AND r.attempt_id=a.id
                AND r.capacity_group=j.capacity_group
            WHERE j.id=@id AND a.admission_token=@admission AND a.deadline=@deadline
            """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("id", original.Envelope.JobId.Value);
        command.Parameters.AddWithValue("attempt", original.AttemptId.Value);
        command.Parameters.AddWithValue("admission", original.Claim.Token);
        command.Parameters.AddWithValue("deadline", original.Deadline);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue("the actual original report and exact reservation must remain available");
        row.GetString(0).Should().Be(provider.State);
        row.GetString(1).Should().Be(provider.Policy);
        row.GetGuid(2).Should().Be(operation);
        row.GetString(3).Should().Be(row.GetString(4));
        BackgroundJobExternalIdentity.OperationId(original.Envelope.JobId, row.GetString(3)).Should().Be(operation);
        row.GetGuid(5).Should().Be(provider.Evidence!.Value);
        row.GetString(6).Should().Be("provider_fenced");
        row.GetFieldValue<DateTimeOffset>(9).Should().BeOnOrAfter(row.GetFieldValue<DateTimeOffset>(7));
        row.GetFieldValue<DateTimeOffset>(9).Should().BeOnOrAfter(row.GetFieldValue<DateTimeOffset>(8));
        row.GetString(10).Should().Be("quiescent");
        row.GetString(11).Should().Be(terminalReason is null ? "reconciling" : "finalize_pending");
        row.GetInt32(12).Should().Be(1);
        row.GetInt32(13).Should().Be(0);
        if (terminalReason is null)
        {
            row.IsDBNull(14).Should().BeTrue("repeatable cancellation must not select a failure finalizer early");
            row.IsDBNull(15).Should().BeTrue();
            row.IsDBNull(16).Should().BeTrue();
        }
        else
        {
            row.GetGuid(14).Should().Be(original.AttemptId.Value);
            row.GetString(15).Should().Be("failed");
            row.GetString(16).Should().Be("external_failed");
        }
        row.IsDBNull(17).Should().BeTrue("the prepared domain failure finalizer has not run");
        row.GetInt64(18).Should().Be(1);
    }

    private static async Task AssertRecoveredForbiddenTerminalAsync(DisposableSchemaDatabase database,
        ConfirmedStartReceipt original, string terminalReason)
    {
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, original.Envelope.TenantId.Value, null);
        await using var command = new NpgsqlCommand("""
            SELECT j.terminal_reason,j.business_finalization_incomplete,j.final_outcome_attempt_id,
                (SELECT count(*) FROM audit_log x WHERE x.operation='platform.job.dead_letter'
                    AND x.metadata->>'job_id'=@text AND x.metadata->>'reason'=@reason
                    AND x.metadata->>'external_state'='failed'
                    AND x.metadata->>'external_failure_policy'='transient_not_repeatable'
                    AND x.metadata->>'finalization_reason'='external_failed'),
                (SELECT count(*) FROM audit_log x WHERE x.operation='platform.job.dead_letter'
                    AND x.metadata->>'job_id'=@text)
            FROM background_jobs j WHERE j.id=@id
            """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("id", original.Envelope.JobId.Value);
        command.Parameters.AddWithValue("text", original.Envelope.JobId.Value.ToString("D"));
        command.Parameters.AddWithValue("reason", terminalReason);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        row.GetString(0).Should().Be(terminalReason);
        row.GetBoolean(1).Should().BeFalse("the actual domain failure finalizer committed");
        row.GetGuid(2).Should().Be(original.AttemptId.Value);
        row.GetInt64(3).Should().Be(1);
        row.GetInt64(4).Should().Be(1);
    }
}
