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
using Npgsql;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobPositiveSdkTests
{
    private async Task AssertExplicitOwnerPrepareRenewalRefusalAsync()
    {
        // This independent real-owner counterexample does not claim a stock
        // handoff or identify the missing historical CI interleaving.
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        var prepared = OrganizationId.From(Guid.CreateVersion7());
        var finalized = OrganizationId.From(Guid.CreateVersion7());
        var resource = Guid.CreateVersion7();
        await provider.SetModeAsync(resource, "succeeded");
        BackgroundJobExecutionControl? original = null;
        await using var prepareProxy = new CommitResponseDroppingProxy(database.AppConnectionString,
            holdCommitRequestWhen: () => original?.Gate is
            { Kind: BackgroundJobCheckpointKind.Prepare, CommitStarted: true, CommitAcknowledged: false },
            lifetime: TimeSpan.FromMinutes(2));
        await using var renewalProxy = new CommitResponseDroppingProxy(database.OutboxConnectionString,
            commitsToPass: int.MaxValue, lifetime: TimeSpan.FromMinutes(2));
        renewalProxy.HoldNextRenewalCommitResponse(loseResponse: false);
        await using var host = Fixture.Host(database, provider.Settings)
            .WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Default", prepareProxy.ConnectionString));
        var submitted = await Fixture.SubmitAsync(host.Services, Fixture.Parameters(resource, prepared, finalized));
        submitted.IsSuccess.Should().BeTrue();
        var job = submitted.Value;
        var executor = host.Services.GetRequiredService<JobExecutor>();
        var routing = host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>();
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(renewalProxy.ConnectionString));
        var gate = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var bounds = new BackgroundJobRuntimeOptions { RenewalInterval = TimeSpan.FromSeconds(1) };
        var controller = new BackgroundJobControl(source, Fixture.Registry, executor, gate, bounds,
            new SystemGuidFactory(), TimeProvider.System);
        var generation = new HangfireGenerationWork(Fixture.Registry.Infrastructure.Pools["external"], gate);
        HangfireGenerationWork.Invocation? work = null;
        TaskCompletionSource<Task<JobStartExecutionResult>>? admissionFlight = null;
        TaskCompletionSource<Task<bool>>? externalFlight = null;
        Task<JobBusinessExecutionResult>? business = null;
        BackgroundJobRenewalOperation? renewal = null;
        CancellationTokenRegistration? callback = null;
        var callbackIssued = false;
        using var releaseCallback = new ManualResetEventSlim();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? originalJoin = null;
        Task? privateCompletion = null;
        var originalJoined = false;
        var boundary = "prepare_admission";
        long? observedAttempts = null;
        ExceptionDispatchInfo? failure = null;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var claim = await routing.TryClaimAsync(job, await GenerationAsync(database, job));
            claim.Should().NotBeNull();
            work = generation.TryBegin()!;
            original = controller.TryBegin(claim!, work)!;
            work.HoldExecution(original);
            admissionFlight = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var admission = IssueNativeSettlementFlight(admissionFlight,
                () => executor.ExecutePublishedAsync(claim!, original.Publication, original.AdmissionGate, null, work.Token));
            work.HoldAdmission(admission);
            original.HoldAdmission(admission);
            (await admission.WaitAsync(deadline.Token)).Outcome.Should().Be(JobStartExecutionOutcome.Started);
            await original.WaitForPublicationAsync().WaitAsync(deadline.Token);
            original.Gate.Kind.Should().Be(BackgroundJobCheckpointKind.Prepare);
            externalFlight = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var external = IssueNativeSettlementFlight(externalFlight, () => controller.ExecuteExternalNormalAsync(original));
            boundary = "prepare_commit_held";
            await prepareProxy.CommitRequestHeld.WaitAsync(deadline.Token);
            business = (Task<JobBusinessExecutionResult>)NativeSdkField(original, "_business");
            var completedCommitsBeforeRelease = prepareProxy.CompletedCommits;
            prepareProxy.HeldCommitRequests.Should().Be(1);
            original.Gate.CommitStarted.Should().BeTrue();
            original.Gate.CommitAcknowledged.Should().BeFalse();
            original.RenewalPermitted.Should().BeTrue();
            await using (var connection = await source.OpenConnectionAsync(deadline.Token))
            await using (var command = new NpgsqlCommand(
                "SELECT id FROM background_jobs WHERE id=@id FOR NO KEY UPDATE SKIP LOCKED", connection)
            { CommandTimeout = 5 })
            {
                command.Parameters.AddWithValue("id", job.Value);
                (await command.ExecuteScalarAsync(deadline.Token)).Should().BeNull("the actual Prepare transaction owns its row lock");
            }
            // Register after the original COMMIT is issued. Holding this actual
            // callback prevents cancellation overtaking the admitted COMMIT ACK.
            callback = work.Token.Register(() =>
            {
                callbackEntered.TrySetResult();
                if (!releaseCallback.Wait(TimeSpan.FromSeconds(30)))
                    throw new TimeoutException("Prepare cancellation callback was not released.");
            });
            callbackIssued = true;
            boundary = "prepare_renewal_held";
            controller.Start();
            await renewalProxy.RenewalCommitHeld.WaitAsync(deadline.Token);
            renewal = (BackgroundJobRenewalOperation)NativeSdkField(controller, "_renewalFlight");
            renewal.Completion.IsCompleted.Should().BeFalse();
            renewalProxy.ReleaseRenewalCommitResponse();
            boundary = "prepare_refusal";
            var actualRenewal = await renewal.Completion.WaitAsync(deadline.Token);
            (await renewal.TimelyReturn.WaitAsync(deadline.Token)).Should().BeTrue();
            actualRenewal.CommitAcknowledged.Should().BeTrue();
            actualRenewal.CommitUncertain.Should().BeFalse();
            actualRenewal.Failed.Should().BeFalse();
            actualRenewal.CleanupConfirmed.Should().BeTrue();
            actualRenewal.Value.Should().NotBeNull().And.BeEmpty("the real locked row is omitted by the successful renewal batch");
            var renewalProbe = actualRenewal.Probes.Should().ContainSingle().Which;
            renewalProbe.Boundary.Should().Be(BackgroundJobCommitBoundary.Renewal);
            renewalProbe.JobId.Should().Be(job.Value);
            renewalProbe.AttemptId.Should().Be(original.Identity!.AttemptId.Value);
            renewalProbe.Token.Should().Be(original.Identity.Token);
            await callbackEntered.Task.WaitAsync(deadline.Token);
            NativeSettlementCause(original).Should().Be("renewal_refused");
            original.RunGate.IsClosed.Should().BeTrue();
            controller.Snapshot.LeaseLossRenewalRefused.Should().Be(1);
            controller.Snapshot.LeaseLossElapsed.Should().Be(0);
            original.Gate.CommitAcknowledged.Should().BeFalse();
            business.IsCompleted.Should().BeFalse();
            boundary = "prepare_ack";
            prepareProxy.ReleaseHeldRequest();
            while (!original.Gate.CommitAcknowledged) await Task.Delay(10, deadline.Token);
            prepareProxy.CompletedCommits.Should().Be(completedCommitsBeforeRelease + 1);
            prepareProxy.DroppedCommitRequests.Should().Be(0);
            prepareProxy.DroppedCommitResponses.Should().Be(0);
            renewalProxy.DroppedCommitResponses.Should().Be(0);
            releaseCallback.Set();
            await callback.Value.DisposeAsync();
            callback = null;
            (await external.WaitAsync(deadline.Token)).Should().BeTrue();
            var completed = await business.WaitAsync(deadline.Token);
            completed.CommitAcknowledged.Should().BeTrue();
            completed.CleanupFailed.Should().BeFalse();
            original.LeaseWorkSettled.Should().BeFalse("Prepare ACK does not settle the remaining external work");
            original.RenewalPermitted.Should().BeFalse();
            boundary = "prepare_facts";
            observedAttempts = await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job);
            observedAttempts.Value.Should().Be(1);
            var facts = await ReadNativeSettlementFactsAsync(database, job, finalized, original.Identity!.WakeGeneration);
            facts.State.Should().Be("available");
            facts.Phase.Should().Be("prepared");
            facts.Normal.Should().Be(1);
            facts.Finalize.Should().Be(0);
            facts.NormalCounter.Should().Be(1);
            facts.FinalizeCounter.Should().Be(0);
            facts.GenerationDelta.Should().Be(0);
            facts.Finalized.Should().Be(0);
            var operation = await ReadUnarmedPrepareOperationAsync(database, job, deadline.Token);
            (await provider.CountsAsync(operation)).Should().Be((0L, 0L, 0L));
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, prepared.Value)).Should().Be(1);
        }
        catch (Exception error)
        {
            failure = ExceptionDispatchInfo.Capture(error);
            await WriteNativeSettlementFailureAsync(database, job, finalized, boundary, observedAttempts,
                original, null, null, controller.Snapshot);
        }
        finally
        {
            generation.RequestHostStop();
            controller.RequestStop();
            prepareProxy.ReleaseHeldRequest();
            renewalProxy.ReleaseRenewalCommitResponse();
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
                        await WriteNativeSettlementFailureAsync(database, job, finalized, "prepare_cleanup", observedAttempts,
                            original, null, null, controller.Snapshot);
                    }
                    return false;
                }
            }
            var callbackJoined = callback is not { } registration
                || await CleanupStepAsync(() => registration.DisposeAsync().AsTask());
            var admissionJoined = await CleanupStepAsync(() => admissionFlight?.Task.Unwrap() ?? Task.CompletedTask);
            var externalJoined = await CleanupStepAsync(() => externalFlight?.Task.Unwrap() ?? Task.CompletedTask);
            // An early assertion can precede the normal capture. The actual
            // external flight owns and awaits this task; retain its issued
            // business flight separately before judging cleanup evidence.
            if (business is null && original is not null)
                lock (NativeSdkField(original, "_sync"))
                    business = (Task<JobBusinessExecutionResult>?)typeof(BackgroundJobExecutionControl)
                        .GetField("_business", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                        .GetValue(original);
            var businessJoined = await CleanupStepAsync(() => business ?? Task.CompletedTask);
            var renewalJoined = await CleanupStepAsync(() => renewal?.Completion ?? Task.CompletedTask);
            var admissionClean = admissionJoined && NativeSettlementAdmissionClean(admissionFlight);
            var externalClean = externalJoined && (externalFlight is null || externalFlight.Task.Result.Result);
            var businessClean = businessJoined && (business is null || business.Result is
            { CleanupFailed: false, SourceOwner.CleanupConfirmed: true });
            var renewalClean = renewalJoined && (renewal is null || renewal.Completion.Result.CleanupConfirmed);
            if (original is not null && callbackJoined && admissionClean && externalClean && businessClean && renewalClean)
                await CleanupStepAsync(async () =>
                {
                    originalJoin ??= original.JoinAsync(operationCleanupConfirmed: true);
                    originalJoined = await originalJoin;
                    if (originalJoined)
                    {
                        privateCompletion = work!.CompleteAsync(cleanupConfirmed: true);
                        await privateCompletion;
                    }
                });
            var controlJoined = await CleanupStepAsync(() => controller.Completion);
            var generationJoined = await CleanupStepAsync(() => generation.Completion);
            if (failure is not null)
            {
                try
                {
                    output.WriteLine("Native settlement cleanup;originalAdmission={0};finalizeAdmission=not_issued;external={1};business={2};disposition=not_issued;callback={3};original={4};finalize=not_issued;control={5};generation={6};executions={7};running={8};retained={9};privateWork={10}.",
                        NativeSettlementCleanupState(admissionFlight, admissionClean), NativeSettlementCleanupState(externalFlight, externalClean),
                        NativeSettlementCleanupState(business, businessClean), !callbackIssued ? "not_issued" : callbackJoined ? "joined" : "incomplete",
                        NativeSettlementCleanupState(original, originalJoined && privateCompletion?.IsCompletedSuccessfully == true),
                        NativeSettlementCleanupState(controller, controlJoined && renewalClean), NativeSettlementCleanupState(generation, generationJoined),
                        NativeSettlementNumber(controller.Snapshot.Executions, 64), NativeSettlementNumber(generation.Snapshot.Running, 64),
                        NativeSettlementNumber(generation.Snapshot.Retained, 64), generationJoined
                            && (work is null || privateCompletion?.IsCompletedSuccessfully == true) ? "joined" : "incomplete");
                }
                catch (Exception) { /* Cleanup diagnostics cannot replace the original failure. */ }
            }
        }
        failure?.Throw();
        try
        {
            originalJoined.Should().BeTrue();
            controller.Snapshot.Executions.Should().Be(0);
            NativeSettlementCause(original).Should().Be("renewal_refused");
            controller.Snapshot.LeaseLossRenewalRefused.Should().Be(1);
            controller.Snapshot.LeaseLossElapsed.Should().Be(0);
        }
        catch
        {
            await WriteNativeSettlementFailureAsync(database, job, finalized, "prepare_post_cleanup", observedAttempts,
                original, null, null, controller.Snapshot);
            throw;
        }
    }

    private static async Task<Guid> ReadUnarmedPrepareOperationAsync(DisposableSchemaDatabase database,
        BackgroundJobId job, CancellationToken token)
    {
        await using var app = new NpgsqlConnection(database.AppConnectionString);
        await app.OpenAsync(token);
        await using var transaction = await app.BeginTransactionAsync(token);
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        await using var command = new NpgsqlCommand("""
            SELECT j.operation_id,a.execute_intent_at IS NULL,a.outcome IS NULL,a.outcome_kind IS NULL
            FROM background_jobs j JOIN background_job_attempts a ON a.id=j.current_attempt_id
            WHERE j.id=@job AND j.phase='prepared' AND a.stage='normal'
            """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("job", job.Value);
        await using var row = await command.ExecuteReaderAsync(token);
        (await row.ReadAsync(token)).Should().BeTrue();
        row.GetBoolean(1).Should().BeTrue();
        row.GetBoolean(2).Should().BeTrue();
        row.GetBoolean(3).Should().BeTrue();
        return row.GetGuid(0);
    }
}
