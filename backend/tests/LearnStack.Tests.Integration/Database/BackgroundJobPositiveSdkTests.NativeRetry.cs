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
        }, applicationPoolSize: 24);
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
            ShouldListenTo = activitySource => !lostOutcomeAck && activitySource.Name.Length == 0,
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
    [InlineData("retained")]
    [InlineData("reconcile")]
    [InlineData("no_start")]
    public async Task Native_known_recovery_retries_only_after_positive_checkpoint_and_reuses_the_prepared_operation(string mode)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        var resource = Guid.CreateVersion7();
        var prepared = OrganizationId.From(Guid.CreateVersion7());
        var finalized = OrganizationId.From(Guid.CreateVersion7());
        await provider.SetModeAsync(resource, "repeatable");
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
            report.Observation!.FailurePolicy.Should().Be(BackgroundJobExternalFailurePolicy.TransientRepeatable);
            if (mode == "retained")
            {
                var recorded = await executor.RecordExternalOutcomeAsync(normal, original.Read,
                    normal.Run.OpenBoundary(BackgroundJobCheckpointKind.Outcome), report.Observation, CancellationToken.None);
                recorded.CommitAcknowledged.Should().BeTrue();
                recorded.CleanupFailed.Should().BeFalse();
            }
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
        }, applicationPoolSize: 24);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        using var globals = new NativeSdkGlobalFilterSnapshot();
        var runtime = NativeSdkRuntime(host.Services, source, bounds);
        try
        {
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await runtime.StartAsync(startup.Token);
            using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(80));
            while ((await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, normal.Envelope.JobId))!.Phase != "completed"
                || runtime.Snapshot.Control!.Executions != 0) await Task.Delay(20, completion.Token);
            var row = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, normal.Envelope.JobId))!;
            row.NormalAttempts.Should().Be(2);
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, normal.Envelope.JobId)).Should().Be(3);
            construction.Calls.Should().Be(mode == "reconcile" ? 2 : 1,
                "stored facts and positive NoStart need no provider call; only unresolved intent requires Reconcile");
            (await provider.CountsAsync(operation)).Should().Be((mode == "no_start" ? 1L : 2L, mode == "reconcile" ? 1L : 0L, 1L));
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, prepared.Value)).Should().Be(1);
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, finalized.Value)).Should().Be(1);
        }
        finally
        {
            runtime.RequestStop();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await runtime.StopAsync(cleanup.Token);
            await runtime.Completion.WaitAsync(cleanup.Token);
        }
        runtime.Snapshot.CleanupConfirmed.Should().BeTrue();
        runtime.Snapshot.RetainedInvocations.Should().Be(0);
    }
}
