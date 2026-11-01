using System.Data;
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
    public async Task Business_observation_waits_for_the_actual_Prepare_task_publication()
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
        Exception? publicationFailure = null;
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
            Action oldObservation = () => _ = NativeSdkField(execution, "_business");
            oldObservation.Should().Throw<InvalidOperationException>();
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
        finally
        {
            // Even on assertion failure, retain and await the actual issued task;
            // only a successful real cleanup result is passed to the owner join.
            if (business is not null)
            {
                try
                {
                    if (!executionHeld) { execution.HoldBusiness(business); executionHeld = true; }
                    if (!generationHeld) { work.HoldBusiness(business); generationHeld = true; }
                }
                catch (Exception failure) { publicationFailure = failure; }
            }
            probe.AllowReturn.TrySetResult();
            if (observation is not null && !observation.IsCompleted) deadline.Cancel();
            if (observation is not null)
            {
                try { _ = await observation; }
                catch (Exception) { /* The assertion path reports the observation failure. */ }
            }
            JobStartExecutionResult? actualAdmission = null;
            if (admission is not null)
            {
                try { actualAdmission = await admission; }
                catch (Exception) { /* A fault cannot supply cleanup confirmation. */ }
            }
            if (business is not null)
            {
                try { result = await business; }
                catch (Exception) { /* A fault cannot supply cleanup confirmation. */ }
            }
            var clean = actualAdmission is { CleanupFailed: false }
                && (business is null || executionHeld && generationHeld && result is { CleanupFailed: false }
                    && result.SourceOwner.CleanupConfirmed)
                && publicationFailure is null;
            joined = await execution.JoinAsync(clean);
            await work.CompleteAsync(joined);
            generation.CloseStarts();
            controller.RequestStop();
            await controller.Completion.WaitAsync(TimeSpan.FromSeconds(15));
            if (joined) await generation.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        }
        publicationFailure.Should().BeNull();
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
}
