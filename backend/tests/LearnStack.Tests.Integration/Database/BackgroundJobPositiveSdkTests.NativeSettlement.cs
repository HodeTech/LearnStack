using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Time;
using Microsoft.AspNetCore.Hosting;
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
    public async Task Actual_Outcome_ACK_settles_renewal_before_its_held_cleanup_can_release_the_native_identity(bool knownFailure)
    {
        await AssertHeldOutcomeSettlementAsync(knownFailure, retainedRecovery: false);
        if (knownFailure)
        {
            await AssertExplicitOwnerLaterFinalizeAttributionAsync();
            await AssertExplicitOwnerPrepareRenewalRefusalAsync();
        }
    }

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
            // A distinct validated cadence identifies only the actual renewal
            // pacing timers. Every deadline and lease duration stays unchanged.
            RenewalInterval = NativeSettlementTimeProvider.RenewalPacing,
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
        var time = new NativeSettlementTimeProvider(retainedRecovery);
        var runtime = NativeSdkRuntime(host.Services, source, bounds, time);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        BackgroundJobExecutionControl? captured = null;
        BackgroundJobExecutionControl? finalizer = null;
        BackgroundJobCommitGate? outcomeGate = null;
        var held = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = activitySource => activitySource.Name == BackgroundJobObservation.InstrumentationName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
            {
                if (activity.OperationName != "job.execution" || activity.TraceId != parent.TraceId) return;
                var controller = (BackgroundJobControl)NativeSdkField(runtime, "_jobs");
                lock (NativeSdkField(controller, "_sync"))
                    if (((Dictionary<Guid, BackgroundJobExecutionControl>)NativeSdkField(controller, "_executions"))
                        .TryGetValue(job.Value, out var execution) && execution.Gate.Kind == BackgroundJobCheckpointKind.Finalize)
                        finalizer = execution;
            },
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
        var boundary = "start";
        long? observedAttempts = null;
        Task<JobExternalCheckpointResult>? outcomeTask = null;
        ExceptionDispatchInfo? failure = null;
        var pacingReported = false;
        void ReportPacing()
        {
            if (pacingReported) return;
            pacingReported = true;
            try { output.WriteLine(time.Diagnostic); }
            catch (Exception) { /* The original failure owns the result. */ }
        }
        try
        {
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await runtime.StartAsync(startup.Token);
            if (!retainedRecovery)
            {
                boundary = "deadline_armed";
                await time.ProveNormalDeadlineArmedBeforePacingAsync(startup.Token);
                boundary = "start";
            }
            await entered.Task.WaitAsync(startup.Token);
            boundary = "held_outcome";
            var execution = captured!;
            execution.Identity!.IsRecovery.Should().Be(retainedRecovery);
            if (retainedRecovery)
                typeof(BackgroundJobExecutionControl).GetField("_externalProvider", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(execution).Should().BeNull("retained completed facts select without a Reconcile/provider scope");
            var actual = (Task<JobExternalCheckpointResult>)NativeSdkField(execution, "_externalOutcome");
            outcomeTask = actual;
            actual.IsCompleted.Should().BeFalse("the actual Activity cleanup remains blocked after the physical COMMIT ACK");
            outcomeGate!.Kind.Should().Be(BackgroundJobCheckpointKind.Outcome);
            outcomeGate.CommitAcknowledged.Should().BeTrue();
            execution.LeaseWorkSettled.Should().BeTrue();
            execution.RenewalPermitted.Should().BeFalse();
            execution.RunGate.IsClosed.Should().BeFalse();
            runtime.Snapshot.Control!.Executions.Should().Be(1);
            var changes = time.Timer(execution).Changes;
            var controller = (BackgroundJobControl)NativeSdkField(runtime, "_jobs");
            using var observer = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            boundary = "renewal_first";
            await time.WaitForFirstCallbackAsync(observer.Token);
            var progress = NativeSdkRenewalProgress(controller);
            time.ReleaseFirst();
            boundary = "renewal_progress";
            while (NativeSdkRenewalProgress(controller) == progress) await Task.Delay(10, observer.Token);
            boundary = "renewal_second";
            await time.WaitForSecondTimerAsync(observer.Token);
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
            controller.Snapshot.LeaseLossElapsed.Should().Be(0);
            controller.Snapshot.LeaseLossRenewalRefused.Should().Be(0);
            // Force the legal interleaving: a new-generation opaque hint has
            // already completed while the old native identity forbids routing.
            // Its ACK postpones repair; vendor success is not domain completion.
            boundary = "early_hint";
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
            boundary = "held_count_read";
            observedAttempts = await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job);
            boundary = "held_count_assert";
            observedAttempts.Value.Should().Be(1);
            time.AssertSecondHeld();
            release.Set();
            boundary = "completion_wait";
            using var completed = new CancellationTokenSource(TimeSpan.FromSeconds(80));
            while ((await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!.Phase
                    != (knownFailure ? "terminal_pending" : "completed")
                || runtime.Snapshot.Control!.Executions != 0)
                await Task.Delay(20, completed.Token);
            time.AssertSecondHeld();
            time.ReleaseAll();
            boundary = "count_read";
            observedAttempts = null;
            observedAttempts = await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job);
            boundary = "count_assert";
            observedAttempts.Value.Should().Be(2);
            boundary = "normal_attempts";
            (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!.NormalAttempts.Should().Be(1);
            boundary = "generation";
            (await GenerationAsync(database, job)).Should().Be(execution.Identity!.WakeGeneration + 1,
                "repair resends the same eligibility generation instead of admitting another external attempt");
            boundary = "outcome";
            var acknowledged = await actual;
            acknowledged.Outcome!.IsStoredCompleted.Should().Be(retainedRecovery);
            boundary = "provider_counts";
            (await provider.CountsAsync(acknowledged.Outcome.OperationId)).Should().Be((1L, 0L, knownFailure ? 0L : 1L));
        }
        catch (Exception error)
        {
            failure = ExceptionDispatchInfo.Capture(error);
            await WriteNativeSettlementFailureAsync(database, job, finalized, boundary, observedAttempts,
                captured ?? time.Original, finalizer, outcomeTask, runtime.Snapshot.Control);
        }
        finally
        {
            // Seal and release both real callback holds before any stop/join.
            time.ReleaseAll();
            release.Set();
            var runtimeJoined = false;
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            try
            {
                runtime.RequestStop();
                await runtime.StopAsync(cleanup.Token);
                await runtime.Completion.WaitAsync(cleanup.Token);
                runtimeJoined = true;
            }
            catch (Exception error)
            {
                if (failure is null)
                {
                    failure = ExceptionDispatchInfo.Capture(error);
                    await WriteNativeSettlementFailureAsync(database, job, finalized, "cleanup", observedAttempts,
                        captured ?? time.Original, finalizer, outcomeTask, runtime.Snapshot.Control);
                }
            }
            try
            {
                await time.JoinAsync(runtimeJoined).WaitAsync(cleanup.Token);
                time.ThrowIfFaulted();
            }
            catch (Exception error)
            {
                if (failure is null)
                {
                    failure = ExceptionDispatchInfo.Capture(error);
                    await WriteNativeSettlementFailureAsync(database, job, finalized, "timer_cleanup", observedAttempts,
                        captured ?? time.Original, finalizer, outcomeTask, runtime.Snapshot.Control);
                }
            }
            if (failure is not null) ReportPacing();
        }
        failure?.Throw();
        try
        {
            held.Should().Be(1);
            runtime.Snapshot.Control!.Executions.Should().Be(0);
            finalizer.Should().NotBeNull();
            finalizer.Should().NotBeSameAs(captured);
            // Process totals include later Finalize identities. The held
            // Outcome's sticky first cause survives settlement and removal.
            NativeSettlementCause(captured).Should().Be("none");
        }
        catch
        {
            await WriteNativeSettlementFailureAsync(database, job, finalized, "post_cleanup", observedAttempts,
                captured, finalizer, outcomeTask, runtime.Snapshot.Control);
            ReportPacing();
            throw;
        }
    }

    private async Task AssertExplicitOwnerLaterFinalizeAttributionAsync()
    {
        // Independent counterexample: real PostgreSQL/publication/control owners
        // are invoked explicitly. This phase does not claim stock Hangfire handoff.
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        var prepared = OrganizationId.From(Guid.CreateVersion7());
        var finalized = OrganizationId.From(Guid.CreateVersion7());
        var resource = Guid.CreateVersion7();
        await provider.SetModeAsync(resource, "not_repeatable");
        await using var host = Fixture.Host(database, provider.Settings);
        var submitted = await Fixture.SubmitAsync(host.Services, Fixture.Parameters(resource, prepared, finalized));
        submitted.IsSuccess.Should().BeTrue();
        var job = submitted.Value;
        var executor = host.Services.GetRequiredService<JobExecutor>();
        var routing = host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>();
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var bounds = new BackgroundJobRuntimeOptions { RenewalInterval = TimeSpan.FromSeconds(1) };
        var controller = new BackgroundJobControl(source, Fixture.Registry, executor, runtime, bounds,
            new SystemGuidFactory(), TimeProvider.System);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, bounds, new SystemGuidFactory(), runtime);
        var generation = new HangfireGenerationWork(Fixture.Registry.Infrastructure.Pools["external"], runtime);
        BackgroundJobExecutionControl? original = null, finalizer = null;
        await using var proxy = new CommitResponseDroppingProxy(database.AppConnectionString,
            holdCommitRequestWhen: () => finalizer?.Gate is { Kind: BackgroundJobCheckpointKind.Finalize, CommitStarted: true },
            lifetime: TimeSpan.FromMinutes(2));
        await using var finalizeHost = Fixture.Host(database, provider.Settings)
            .WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Default", proxy.ConnectionString));
        var finalizeExecutor = finalizeHost.Services.GetRequiredService<JobExecutor>();
        using var releaseCallback = new ManualResetEventSlim();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration? callback = null;
        var callbackIssued = false;
        HangfireGenerationWork.Invocation? originalWork = null, finalizeWork = null;
        TaskCompletionSource<Task<JobStartExecutionResult>>? originalAdmissionFlight = null, finalizeAdmissionFlight = null;
        TaskCompletionSource<Task<bool>>? externalFlight = null, dispositionFlight = null;
        TaskCompletionSource<Task<JobBusinessExecutionResult>>? businessFlight = null;
        Task<JobBusinessExecutionResult>? business = null;
        Task<JobExternalCheckpointResult>? outcome = null;
        Task<bool>? originalJoin = null;
        Task? originalPrivateCompletion = null, finalizePrivateCompletion = null;
        var originalJoined = false;
        var originalCompleted = false;
        var finalizerJoined = false;
        var boundary = "later_original";
        long? observedAttempts = null;
        ExceptionDispatchInfo? failure = null;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var claim = await routing.TryClaimAsync(job, await GenerationAsync(database, job));
            claim.Should().NotBeNull();
            originalWork = generation.TryBegin()!;
            original = controller.TryBegin(claim!, originalWork)!;
            originalWork.HoldExecution(original);
            originalAdmissionFlight = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var admission = IssueNativeSettlementFlight(originalAdmissionFlight,
                () => executor.ExecutePublishedAsync(claim!, original.Publication, original.AdmissionGate, null, originalWork.Token));
            originalWork.HoldAdmission(admission);
            original.HoldAdmission(admission);
            (await admission.WaitAsync(deadline.Token)).Outcome.Should().Be(JobStartExecutionOutcome.Started);
            await original.WaitForPublicationAsync().WaitAsync(deadline.Token);
            externalFlight = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var external = IssueNativeSettlementFlight(externalFlight, () => controller.ExecuteExternalNormalAsync(original));
            (await external.WaitAsync(deadline.Token)).Should().BeTrue();
            outcome = (Task<JobExternalCheckpointResult>)NativeSdkField(original, "_externalOutcome");
            var acknowledged = await outcome;
            acknowledged.CommitAcknowledged.Should().BeTrue();
            original.LeaseWorkSettled.Should().BeTrue();
            NativeSettlementCause(original).Should().Be("none");
            originalJoin = original.JoinAsync(operationCleanupConfirmed: true);
            originalJoined = await originalJoin.WaitAsync(deadline.Token);
            originalJoined.Should().BeTrue();
            originalPrivateCompletion = originalWork.CompleteAsync(originalJoined);
            await originalPrivateCompletion.WaitAsync(deadline.Token);
            originalCompleted = true;
            controller.Snapshot.Executions.Should().Be(0);

            boundary = "later_admission";
            var finalizeClaim = await routing.TryClaimAsync(job, await GenerationAsync(database, job));
            finalizeClaim.Should().NotBeNull();
            finalizeWork = generation.TryBegin()!;
            finalizer = controller.TryBegin(finalizeClaim!, finalizeWork)!;
            finalizeWork.HoldExecution(finalizer);
            finalizeAdmissionFlight = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var finalizeAdmission = IssueNativeSettlementFlight(finalizeAdmissionFlight,
                () => finalizeExecutor.ExecutePublishedAsync(finalizeClaim!, finalizer.Publication,
                    finalizer.AdmissionGate, null, finalizeWork.Token));
            finalizeWork.HoldAdmission(finalizeAdmission);
            finalizer.HoldAdmission(finalizeAdmission);
            var admitted = await finalizeAdmission.WaitAsync(deadline.Token);
            admitted.Outcome.Should().Be(JobStartExecutionOutcome.Started);
            admitted.CleanupFailed.Should().BeFalse();
            await finalizer.WaitForPublicationAsync().WaitAsync(deadline.Token);
            finalizer.Gate.Kind.Should().Be(BackgroundJobCheckpointKind.Finalize);
            finalizer.Identity!.SelectedNormalAttemptId.Should().Be(original.Identity!.AttemptId);
            finalizer.Identity.AttemptId.Should().NotBe(original.Identity.AttemptId);
            businessFlight = new(TaskCreationOptions.RunContinuationsAsynchronously);
            business = IssueNativeSettlementFlight(businessFlight,
                () => finalizeExecutor.ExecuteBusinessAsync(admitted.Receipt!, finalizer.Gate, finalizeWork.Token));
            finalizer.HoldBusiness(business);
            finalizeWork.HoldBusiness(business);
            boundary = "later_commit_held";
            await proxy.CommitRequestHeld.WaitAsync(deadline.Token);
            proxy.HeldCommitRequests.Should().Be(1);
            var completedCommitsBeforeRelease = proxy.CompletedCommits;
            finalizer.Gate.CommitStarted.Should().BeTrue();
            finalizer.Gate.CommitAcknowledged.Should().BeFalse();
            finalizer.RenewalPermitted.Should().BeTrue();
            await using (var connection = await source.OpenConnectionAsync(deadline.Token))
            await using (var command = new NpgsqlCommand(
                "SELECT id FROM background_jobs WHERE id=@id FOR NO KEY UPDATE SKIP LOCKED", connection)
            { CommandTimeout = 5 })
            {
                command.Parameters.AddWithValue("id", job.Value);
                (await command.ExecuteScalarAsync(deadline.Token)).Should().BeNull("the actual finalizer still owns its row lock");
            }
            // Register after Npgsql issued its COMMIT. The owned callback holds
            // cancellation propagation while the already-admitted COMMIT settles.
            callback = finalizeWork.Token.Register(() =>
            {
                callbackEntered.TrySetResult();
                if (!releaseCallback.Wait(TimeSpan.FromSeconds(30)))
                    throw new TimeoutException("Finalize cancellation callback was not released.");
            });
            callbackIssued = true;
            boundary = "later_refusal";
            controller.Start();
            await callbackEntered.Task.WaitAsync(deadline.Token);
            business.IsCompleted.Should().BeFalse("the actual COMMIT request and cancellation callback remain held");
            NativeSettlementCause(original).Should().Be("none");
            NativeSettlementCause(finalizer).Should().Be("renewal_refused");
            controller.Snapshot.LeaseLossRenewalRefused.Should().Be(1, "the real renewal lane refused the held Finalize identity");
            controller.Snapshot.LeaseLossElapsed.Should().Be(0);
            finalizer.Gate.CommitAcknowledged.Should().BeFalse("the actual Finalize COMMIT request remains held before ACK");
            finalizer.RunGate.IsClosed.Should().BeTrue();
            boundary = "later_ack";
            proxy.ReleaseHeldRequest();
            while (!finalizer.Gate.CommitAcknowledged) await Task.Delay(10, deadline.Token);
            proxy.CompletedCommits.Should().Be(completedCommitsBeforeRelease + 1);
            proxy.DroppedCommitRequests.Should().Be(0);
            proxy.DroppedCommitResponses.Should().Be(0);
            releaseCallback.Set();
            await callback.Value.DisposeAsync();
            callback = null;
            var completed = await business.WaitAsync(deadline.Token);
            completed.CommitAcknowledged.Should().BeTrue();
            completed.CleanupFailed.Should().BeFalse();
            dispositionFlight = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var disposition = IssueNativeSettlementFlight(dispositionFlight, () => finalizer.FinishExternalFinalizeAsync(completed, store));
            (await disposition.WaitAsync(deadline.Token)).Should().BeTrue();
            boundary = "later_facts";
            observedAttempts = await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job);
            observedAttempts.Value.Should().Be(2);
            var facts = await ReadNativeSettlementFactsAsync(database, job, finalized, original.Identity.WakeGeneration);
            facts.State.Should().Be("available");
            facts.Normal.Should().Be(1);
            facts.Finalize.Should().Be(1);
            facts.NormalCounter.Should().Be(1);
            facts.FinalizeCounter.Should().Be(1);
            facts.Finalized.Should().Be(1);
            facts.GenerationDelta.Should().Be(1);
            (await provider.CountsAsync(acknowledged.Outcome!.OperationId)).Should().Be((1L, 0L, 0L));
        }
        catch (Exception error)
        {
            failure = ExceptionDispatchInfo.Capture(error);
            await WriteNativeSettlementFailureAsync(database, job, finalized, boundary, observedAttempts,
                original, finalizer, outcome, controller.Snapshot);
        }
        finally
        {
            // Stop future issuance and release every test-owned barrier before
            // awaiting the actual flights. A returned waiter is not cleanup.
            generation.RequestHostStop();
            controller.RequestStop();
            proxy.ReleaseHeldRequest();
            releaseCallback.Set();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            async Task<bool> CleanupStepAsync(Func<Task> step)
            {
                try { await step().WaitAsync(cleanup.Token); return true; }
                catch (Exception error)
                {
                    if (failure is null)
                    {
                        failure = ExceptionDispatchInfo.Capture(error);
                        await WriteNativeSettlementFailureAsync(database, job, finalized, "later_cleanup", observedAttempts,
                            original, finalizer, outcome, controller.Snapshot);
                    }
                    return false;
                }
            }
            var callbackJoined = callback is not { } registration
                || await CleanupStepAsync(() => registration.DisposeAsync().AsTask());
            // Attempt every independent join, even when a preceding flight or
            // callback failed. Never call Join(true) on an unfinished operation.
            var originalAdmissionJoined = await CleanupStepAsync(() => originalAdmissionFlight?.Task.Unwrap() ?? Task.CompletedTask);
            var finalizeAdmissionJoined = await CleanupStepAsync(() => finalizeAdmissionFlight?.Task.Unwrap() ?? Task.CompletedTask);
            var externalJoined = await CleanupStepAsync(() => externalFlight?.Task.Unwrap() ?? Task.CompletedTask);
            var businessJoined = await CleanupStepAsync(() => businessFlight?.Task.Unwrap() ?? Task.CompletedTask);
            var dispositionJoined = await CleanupStepAsync(() => dispositionFlight?.Task.Unwrap() ?? Task.CompletedTask);
            var originalAdmissionClean = originalAdmissionJoined && NativeSettlementAdmissionClean(originalAdmissionFlight);
            var finalizeAdmissionClean = finalizeAdmissionJoined && NativeSettlementAdmissionClean(finalizeAdmissionFlight);
            var externalClean = externalJoined && (externalFlight is null || externalFlight.Task.Result.Result);
            var businessClean = businessJoined && (businessFlight is null || businessFlight.Task.Result.Result is
            { CleanupFailed: false, SourceOwner.CleanupConfirmed: true });
            var dispositionClean = dispositionJoined && (dispositionFlight is null || dispositionFlight.Task.Result.Result);
            var originalClean = originalAdmissionClean && externalClean;
            var finalizeClean = callbackJoined && finalizeAdmissionClean && businessClean && dispositionClean;
            if (finalizer is not null && finalizeClean)
                await CleanupStepAsync(async () =>
                {
                    finalizerJoined = await finalizer.JoinAsync(operationCleanupConfirmed: true);
                    if (finalizerJoined)
                    {
                        finalizePrivateCompletion = finalizeWork!.CompleteAsync(cleanupConfirmed: true);
                        await finalizePrivateCompletion;
                    }
                });
            if (original is not null && !originalCompleted && originalClean)
                await CleanupStepAsync(async () =>
                {
                    originalJoin ??= original.JoinAsync(operationCleanupConfirmed: true);
                    if (!originalJoined) originalJoined = await originalJoin;
                    if (originalJoined)
                    {
                        originalPrivateCompletion ??= originalWork!.CompleteAsync(cleanupConfirmed: true);
                        await originalPrivateCompletion;
                        originalCompleted = true;
                    }
                });
            var controlJoined = await CleanupStepAsync(() => controller.Completion);
            var generationJoined = await CleanupStepAsync(() => generation.Completion);
            if (failure is not null)
            {
                // The first failure marker describes its pre-cleanup boundary;
                // this separate closed witness records actual cleanup afterwards.
                try
                {
                    output.WriteLine("Native settlement cleanup;originalAdmission={0};finalizeAdmission={1};external={2};business={3};disposition={4};callback={5};original={6};finalize={7};control={8};generation={9};executions={10};running={11};retained={12};privateWork={13}.",
                        NativeSettlementCleanupState(originalAdmissionFlight, originalAdmissionClean),
                        NativeSettlementCleanupState(finalizeAdmissionFlight, finalizeAdmissionClean),
                        NativeSettlementCleanupState(externalFlight, externalClean),
                        NativeSettlementCleanupState(businessFlight, businessClean),
                        NativeSettlementCleanupState(dispositionFlight, dispositionClean),
                        !callbackIssued ? "not_issued" : callbackJoined ? "joined" : "incomplete", NativeSettlementCleanupState(original, originalJoined && originalCompleted),
                        NativeSettlementCleanupState(finalizer, finalizerJoined), NativeSettlementCleanupState(controller, controlJoined),
                        NativeSettlementCleanupState(generation, generationJoined), NativeSettlementNumber(controller.Snapshot.Executions, 64),
                        NativeSettlementNumber(generation.Snapshot.Running, 64), NativeSettlementNumber(generation.Snapshot.Retained, 64),
                        generationJoined && (originalWork is null || originalPrivateCompletion?.IsCompletedSuccessfully == true)
                            && (finalizeWork is null || finalizePrivateCompletion?.IsCompletedSuccessfully == true) ? "joined" : "incomplete");
                }
                catch (Exception) { /* Cleanup diagnostics cannot replace the original failure. */ }
            }
        }
        failure?.Throw();
        try
        {
            originalJoined.Should().BeTrue();
            finalizerJoined.Should().BeTrue();
            controller.Snapshot.Executions.Should().Be(0);
            NativeSettlementCause(finalizer).Should().Be("renewal_refused");
            controller.Snapshot.LeaseLossRenewalRefused.Should().Be(1);
            controller.Snapshot.LeaseLossElapsed.Should().Be(0);
            NativeSettlementCause(original).Should().Be("none", "the original Outcome identity remains settled despite the later Finalize refusal");
        }
        catch
        {
            await WriteNativeSettlementFailureAsync(database, job, finalized, "later_post_cleanup", observedAttempts,
                original, finalizer, outcome, controller.Snapshot);
            throw;
        }
    }

    private static Task<T> IssueNativeSettlementFlight<T>(TaskCompletionSource<Task<T>> owner, Func<Task<T>> issue)
    {
        // The caller publishes this outer owner before entering any synchronous
        // operation prefix. Production controls still hold the actual Task.
        try
        {
            var actual = issue();
            owner.SetResult(actual);
            return actual;
        }
        catch (Exception error) { owner.SetException(error); throw; }
    }

    private static bool NativeSettlementAdmissionClean(TaskCompletionSource<Task<JobStartExecutionResult>>? flight) =>
        flight is not null && flight.Task.IsCompletedSuccessfully && flight.Task.Result.IsCompletedSuccessfully
            && flight.Task.Result.Result is { CleanupFailed: false, SourceOwner.CleanupConfirmed: true };

    private static string NativeSettlementCleanupState(object? owner, bool joined) => owner is null ? "not_issued"
        : joined ? "joined" : "incomplete";

    private async Task WriteNativeSettlementFailureAsync(DisposableSchemaDatabase database, BackgroundJobId job,
        OrganizationId finalized, string boundary, long? observedAttempts, BackgroundJobExecutionControl? original,
        BackgroundJobExecutionControl? finalizer, Task? outcomeTask, BackgroundJobControlHealth? health)
    {
        // Output is case-bound and finite. Neither a failed read nor output may
        // replace the original exception, including a failure during cleanup.
        try
        {
            var facts = await ReadNativeSettlementFactsAsync(database, job, finalized, original?.Identity?.WakeGeneration);
            var outcome = outcomeTask is null ? "unknown" : !outcomeTask.IsCompleted ? "pending"
                : outcomeTask.IsCompletedSuccessfully ? "succeeded" : outcomeTask.IsCanceled ? "cancelled" : "failed";
            output.WriteLine("Native settlement failure;boundary={0};observedAttempts={1};snapshot={2};total={3};normal={4};finalize={5};normalCounter={6};finalizeCounter={7};phase={8};terminal={9};generationDelta={10};executions={11};outcome={12};finalized={13};originalCause={14};finalizeCause={15};renewalLosses={16};elapsedLosses={17}.",
                boundary, NativeSettlementNumber(observedAttempts, 12), facts.State,
                NativeSettlementNumber(facts.Total, 12), NativeSettlementNumber(facts.Normal, 6), NativeSettlementNumber(facts.Finalize, 6),
                NativeSettlementNumber(facts.NormalCounter, 6), NativeSettlementNumber(facts.FinalizeCounter, 6), facts.Phase, facts.Terminal,
                NativeSettlementNumber(facts.GenerationDelta, 12, -12), NativeSettlementNumber(health?.Executions, 64),
                outcome, NativeSettlementNumber(facts.Finalized, 1), NativeSettlementCause(original), NativeSettlementCause(finalizer),
                NativeSettlementNumber(health?.LeaseLossRenewalRefused, 64), NativeSettlementNumber(health?.LeaseLossElapsed, 64));
        }
        catch (Exception) { /* Diagnostics cannot replace the original failure. */ }
    }

    private static string NativeSettlementCause(BackgroundJobExecutionControl? execution)
    {
        if (execution is null) return "unknown";
        lock (NativeSdkField(execution, "_sync"))
            return (BackgroundJobLeaseLossCause)(int)NativeSdkField(execution, "_firstLeaseLoss") switch
            {
                BackgroundJobLeaseLossCause.None => "none",
                BackgroundJobLeaseLossCause.RenewalRefused => "renewal_refused",
                BackgroundJobLeaseLossCause.Elapsed => "elapsed",
                _ => "other",
            };
    }

    private static async Task<NativeSettlementFacts> ReadNativeSettlementFactsAsync(DisposableSchemaDatabase database,
        BackgroundJobId job, OrganizationId finalized, long? originalGeneration)
    {
        try
        {
            // A separate bounded observation after failure is not the failed
            // assertion's count. The marker retains both values distinctly.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var connection = new NpgsqlConnection(database.AppConnectionString);
            await connection.OpenAsync(deadline.Token);
            await using var transaction = await connection.BeginTransactionAsync(deadline.Token);
            await using (var announce = new NpgsqlCommand("""
                SELECT set_config('app.tenant_id',@tenant,true),set_config('app.organization_id','',true),set_config('app.scope','',true)
                """, connection, transaction) { CommandTimeout = 5 })
            {
                announce.Parameters.AddWithValue("tenant", SchemaFixture.TenantA.ToString("D"));
                await announce.ExecuteNonQueryAsync(deadline.Token);
            }
            await using var command = new NpgsqlCommand("""
                SELECT current_user::text,j.phase,j.terminal_reason,j.normal_attempts,j.finalize_attempts,j.wake_generation,
                    (SELECT count(*) FROM background_job_attempts a WHERE a.job_id=j.id),
                    (SELECT count(*) FROM background_job_attempts a WHERE a.job_id=j.id AND a.stage='normal'),
                    (SELECT count(*) FROM background_job_attempts a WHERE a.job_id=j.id AND a.stage='finalize'),
                    (SELECT count(*) FROM organizations o WHERE o.id=@finalized)
                FROM background_jobs j WHERE j.id=@job
                """, connection, transaction) { CommandTimeout = 5 };
            command.Parameters.AddWithValue("job", job.Value);
            command.Parameters.AddWithValue("finalized", finalized.Value);
            await using var row = await command.ExecuteReaderAsync(deadline.Token);
            if (!await row.ReadAsync(deadline.Token)) return new("missing");
            if (row.GetString(0) != "learnstack_app") return new("unavailable");
            var phase = row.GetString(1) switch
            {
                "pending" or "preparing" or "prepared" or "executing" or "reconciling" or "finalize_pending"
                    or "finalizing" or "terminal_pending" or "completed" or "quarantined" => row.GetString(1),
                _ => "other",
            };
            var terminal = row.IsDBNull(2) ? "none" : row.GetString(2) switch
            {
                "external_retry_forbidden" or "permanent_failure" or "retry_exhausted" or "finalize_refused"
                    or "finalize_exhausted" or "retired_definition" or "invalid_envelope" => row.GetString(2),
                _ => "other",
            };
            return new("available", phase, terminal, row.GetInt64(6), row.GetInt64(7), row.GetInt64(8),
                row.GetInt32(3), row.GetInt32(4), originalGeneration is { } generation ? checked(row.GetInt64(5) - generation) : null,
                row.GetInt64(9));
        }
        catch (Exception) { return new("unavailable"); }
    }

    private static string NativeSettlementNumber(long? value, long maximum, long minimum = 0) => value is null ? "unknown"
        : value < minimum || value > maximum ? "out_of_range" : value.Value.ToString(CultureInfo.InvariantCulture);

    private sealed record NativeSettlementFacts(string State, string Phase = "unknown", string Terminal = "unknown",
        long? Total = null, long? Normal = null, long? Finalize = null, long? NormalCounter = null,
        long? FinalizeCounter = null, long? GenerationDelta = null, long? Finalized = null);

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
