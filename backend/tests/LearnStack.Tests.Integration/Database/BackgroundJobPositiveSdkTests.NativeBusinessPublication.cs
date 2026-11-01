using System.Data;
using System.Runtime.ExceptionServices;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Time;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobPositiveSdkTests
{
    [Fact]
    public Task Business_observation_waits_for_the_actual_Prepare_task_publication() =>
        AssertBusinessPublicationAsync(injectPrimaryFailure: false);

    [Fact]
    public Task Business_observation_preserves_the_primary_failure_while_joining_actual_Prepare_work() =>
        AssertBusinessPublicationAsync(injectPrimaryFailure: true);

    private async Task AssertBusinessPublicationAsync(bool injectPrimaryFailure)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        var prepared = OrganizationId.From(Guid.CreateVersion7());
        var finalized = OrganizationId.From(Guid.CreateVersion7());
        var probe = new NativeNoPlanPrepareProbe();
        var construction = new BackgroundJobPositiveProviderConstructionProbe();
        await using var host = Fixture.Host(database, provider.Settings, construction, services =>
        {
            services.AddSingleton(probe);
            var request = Fixture.RequestType("Prepare");
            var plan = Fixture.Registry.Resolve(BackgroundJobPositiveSdkFixture.Key).Definition!.ProfileRegistration.PlanType!;
            var contract = typeof(IRequestHandler<,>).MakeGenericType(request, typeof(Result<>).MakeGenericType(plan));
            services.RemoveAll(contract);
            services.AddTransient(contract, typeof(NativeNoPlanPermanentPrepare<,>).MakeGenericType(request, plan));
        });
        var executor = host.Services.GetRequiredService<JobExecutor>();
        var submitted = await Fixture.SubmitAsync(host.Services, Fixture.Parameters(Guid.CreateVersion7(), prepared, finalized));
        submitted.IsSuccess.Should().BeTrue();
        var job = submitted.Value;
        var routing = host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>();
        var claim = await routing.TryClaimAsync(job, await GenerationAsync(database, job));
        claim.Should().NotBeNull();
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var bounds = new BackgroundJobRuntimeOptions { RenewalInterval = TimeSpan.FromSeconds(1) };
        var controller = new BackgroundJobControl(source, Fixture.Registry, executor, runtime, bounds,
            new SystemGuidFactory(), TimeProvider.System);
        var generation = new HangfireGenerationWork(Fixture.Registry.Infrastructure.Pools["external"], runtime);
        var work = generation.TryBegin()!;
        var execution = controller.TryBegin(claim!, work)!;
        work.HoldExecution(execution);
        Task<JobStartExecutionResult>? admission = null;
        Task<JobBusinessExecutionResult>? business = null;
        Task<Task<JobBusinessExecutionResult>>? observation = null;
        JobBusinessExecutionResult? result = null;
        var executionHeld = false;
        var generationHeld = false;
        var joined = false;
        Task<bool>? executionJoin = null;
        Task? privateCompletion = null;
        ExceptionDispatchInfo? failure = null;
        ExceptionDispatchInfo? cleanupFailure = null;
        var injected = injectPrimaryFailure ? new InvalidOperationException("The publication fixture primary failure.") : null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            // This controls the real owners directly; it does not claim to run
            // the complete native Hangfire handoff or the NoPlan status path.
            admission = executor.ExecutePublishedAsync(claim!, execution.Publication, execution.AdmissionGate, null, work.Token);
            work.HoldAdmission(admission);
            execution.HoldAdmission(admission);
            var admitted = await admission.WaitAsync(deadline.Token);
            admitted.Outcome.Should().Be(JobStartExecutionOutcome.Started);
            admitted.CleanupFailed.Should().BeFalse();
            await execution.WaitForPublicationAsync().WaitAsync(deadline.Token);
            execution.Identity!.Stage.Should().Be(BackgroundJobExecutionStage.Prepare);

            // Start real SQL work but deliberately defer both owner assignments.
            business = executor.ExecuteBusinessAsync(admitted.Receipt!, execution.Gate, work.Token);
            await probe.Written.Task.WaitAsync(deadline.Token);
            business.IsCompleted.Should().BeFalse();
            using (var refusedObservation = new CancellationTokenSource())
            {
                refusedObservation.Cancel();
                var unconfirmed = await ObserveBusinessCleanupAsync(business, refusedObservation.Token);
                unconfirmed.Should().NotBeNull();
                unconfirmed!.SourceException.Should().BeAssignableTo<OperationCanceledException>();
                business.IsCompleted.Should().BeFalse("canceling cleanup observation cannot complete the held SQL root");
                probe.Attempt!.Source.CleanupConfirmed.Should().BeFalse();
                probe.Attempt.Unit.HasActiveTransaction.Should().BeTrue();
                await work.CompleteAsync(cleanupConfirmed: false).WaitAsync(deadline.Token);
                controller.Snapshot.Executions.Should().Be(1);
                generation.Snapshot.Running.Should().Be(0);
                generation.Snapshot.Retained.Should().Be(1, "an unconfirmed cleanup report cannot release the actual private owner");
                probe.Attempt.Source.CleanupConfirmed.Should().BeFalse();
                output.WriteLine("Native business publication dirty;observation=cancelled;business=pending;sourceCleanup=unconfirmed;execution=retained;privateWork=retained.");
            }
            Action oldObservation = () => _ = NativeSdkField(execution, "_business");
            oldObservation.Should().Throw<InvalidOperationException>();
            if (injected is not null) throw injected;
            observation = NativeSdkBusinessAsync(execution, deadline.Token);
            observation.IsCompleted.Should().BeFalse("the control has not published the actual task yet");

            execution.HoldBusiness(business);
            executionHeld = true;
            work.HoldBusiness(business);
            generationHeld = true;
            (await observation.WaitAsync(deadline.Token)).Should().BeSameAs(business);
            business.IsCompleted.Should().BeFalse("the real handler still holds its nested SQL write");
            output.WriteLine("Native business publication;origin=direct_owner;oldLookup=refused;newObserver=pending;published=same_actual_task;stage=prepare.");
            probe.AllowReturn.TrySetResult();
            result = await business.WaitAsync(deadline.Token);
            result.ExecutionIssued.Should().BeTrue();
            result.KnownRollback.Should().BeTrue();
            result.CommitAcknowledged.Should().BeFalse();
            result.DurableOutcomeUncertain.Should().BeFalse();
            result.CleanupFailed.Should().BeFalse();
            result.SourceOwner.CleanupConfirmed.Should().BeTrue();
            result.Decision.Disposition.Should().Be(BackgroundJobFailureDisposition.Terminal);
            result.Decision.TerminalReason.Should().Be(BackgroundJobTerminalReason.PermanentFailure);
            var attempt = probe.Attempt!;
            attempt.Source.Should().BeSameAs(result.SourceOwner);
            attempt.Unit.HasActiveTransaction.Should().BeFalse();
            attempt.Connection.State.Should().Be(ConnectionState.Closed);
            Action disposedUnit = () => _ = attempt.Unit.Connection;
            disposedUnit.Should().Throw<ObjectDisposedException>();
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, prepared.Value)).Should().Be(0);
        }
        catch (Exception error) when (NativeWaitCatchable(error))
        {
            failure = ExceptionDispatchInfo.Capture(error);
        }
        finally
        {
            // One shared observation budget does not cancel or complete actual work.
            // The first failure survives every later publication/join observation.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            string? cleanupBoundary = null;
            void RecordCleanupFailure(string boundary, ExceptionDispatchInfo error)
            {
                cleanupBoundary ??= boundary;
                cleanupFailure ??= error;
                failure ??= error;
            }
            async Task<bool> CleanupStepAsync(string boundary, Func<Task> step)
            {
                ExceptionDispatchInfo? error;
                try { error = await ObserveBusinessCleanupAsync(step(), cleanup.Token); }
                catch (Exception caught) when (NativeWaitCatchable(caught)) { error = ExceptionDispatchInfo.Capture(caught); }
                if (error is null) return true;
                RecordCleanupFailure(boundary, error);
                return false;
            }
            if (business is not null)
            {
                try
                {
                    if (!executionHeld) { execution.HoldBusiness(business); executionHeld = true; }
                    if (!generationHeld) { work.HoldBusiness(business); generationHeld = true; }
                }
                catch (Exception error) when (NativeWaitCatchable(error))
                {
                    RecordCleanupFailure("publication", ExceptionDispatchInfo.Capture(error));
                }
            }
            probe.AllowReturn.TrySetResult();
            var observationJoined = observation is null || await CleanupStepAsync("observation", () => observation);
            if (!observationJoined)
            {
                try { deadline.Cancel(); }
                catch (Exception error) when (NativeWaitCatchable(error))
                {
                    RecordCleanupFailure("observation_cancel", ExceptionDispatchInfo.Capture(error));
                }
            }
            var admissionJoined = admission is not null && await CleanupStepAsync("admission", () => admission);
            var businessJoined = business is null || await CleanupStepAsync("business", () => business);
            if (businessJoined && business is { IsCompletedSuccessfully: true }) result = business.Result;
            var clean = admissionJoined && admission is { IsCompletedSuccessfully: true, Result.CleanupFailed: false }
                && businessJoined && (business is null || executionHeld && generationHeld
                    && result is { CleanupFailed: false, SourceOwner.CleanupConfirmed: true });
            if (!clean)
            {
                RecordCleanupFailure("actual_work", ExceptionDispatchInfo.Capture(
                    new InvalidOperationException("Actual publication work cleanup remains unconfirmed.")));
                try { generation.RequestHostStop(); }
                catch (Exception error) when (NativeWaitCatchable(error))
                {
                    RecordCleanupFailure("host_stop", ExceptionDispatchInfo.Capture(error));
                }
            }
            var executionJoined = await CleanupStepAsync("execution", () => executionJoin ??= execution.JoinAsync(clean));
            joined = executionJoined && executionJoin is { IsCompletedSuccessfully: true, Result: true };
            var privateObserved = await CleanupStepAsync("private", () => privateCompletion ??= work.CompleteAsync(joined));
            try
            {
                generation.CloseStarts();
                controller.RequestStop();
            }
            catch (Exception error) when (NativeWaitCatchable(error))
            {
                RecordCleanupFailure("stop", ExceptionDispatchInfo.Capture(error));
            }
            var controlJoined = await CleanupStepAsync("control", () => controller.Completion);
            var generationJoined = await CleanupStepAsync("generation", () => generation.Completion);
            if (failure is not null)
            {
                static string State(bool issued, bool complete) => !issued ? "not_issued" : complete ? "joined" : "incomplete";
                try
                {
                    output.WriteLine("Native business publication cleanup;primary=preserved;cleanupBoundary=" + (cleanupBoundary ?? "none")
                        + ";admission=" + State(admission is not null, admissionJoined)
                        + ";business=" + State(business is not null, businessJoined && clean)
                        + ";execution=" + State(executionJoin is not null, joined)
                        + ";privateWork=" + State(privateCompletion is not null, privateObserved && joined && generationJoined)
                        + ";control=" + State(true, controlJoined) + ";generation=" + State(true, generationJoined) + ".");
                }
                catch (Exception error) when (NativeWaitCatchable(error)) { }
            }
        }
        if (injected is not null && ReferenceEquals(failure?.SourceException, injected))
        {
            Action rethrow = failure!.Throw;
            rethrow.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(injected);
            cleanupFailure?.Throw();
            result.Should().NotBeNull();
            result!.KnownRollback.Should().BeTrue();
            result.SourceOwner.CleanupConfirmed.Should().BeTrue();
            probe.Attempt!.Unit.HasActiveTransaction.Should().BeFalse();
            probe.Attempt.Connection.State.Should().Be(ConnectionState.Closed);
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, prepared.Value)).Should().Be(0);
        }
        else
        {
            failure?.Throw();
            injected.Should().BeNull("the requested primary failure must be observed before publication");
        }
        joined.Should().BeTrue();
        controller.Snapshot.Executions.Should().Be(0);
        generation.Snapshot.Running.Should().Be(0);
        generation.Snapshot.Retained.Should().Be(0);
        probe.Prepares.Should().Be(1);
        construction.Calls.Should().Be(0);
        (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, finalized.Value)).Should().Be(0);
        await using var external = new NpgsqlConnection(provider.Settings.ConnectionString);
        await external.OpenAsync();
        await using var effects = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM provider_operations)+(SELECT count(*) FROM provider_attempts)
                +(SELECT count(*) FROM provider_calls)+(SELECT count(*) FROM provider_effects)
            """, external) { CommandTimeout = 5 };
        ((long)(await effects.ExecuteScalarAsync())!).Should().Be(0);
    }

    private static async Task<ExceptionDispatchInfo?> ObserveBusinessCleanupAsync(Task actual, CancellationToken cancellationToken)
    {
        try
        {
            await actual.WaitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception error) when (NativeWaitCatchable(error))
        {
            return ExceptionDispatchInfo.Capture(error);
        }
    }
}
