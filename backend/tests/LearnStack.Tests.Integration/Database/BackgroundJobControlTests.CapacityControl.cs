using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobControlTests
{
    [Fact]
    public async Task Another_instance_parks_expired_grouped_work_without_cleanup_and_keeps_its_occupancy_counted()
    {
        await using var database = await DatabaseAsync();
        var definitions = Fixture.CapacityRegistry();
        var bounds = new BackgroundJobRuntimeOptions
        {
            LeaseDuration = TimeSpan.FromMinutes(2),
            RenewalInterval = TimeSpan.FromSeconds(30),
            ProgressTimeout = TimeSpan.FromMinutes(3),
            ControlPoll = TimeSpan.FromMilliseconds(50),
        };
        bounds.Validate();
        var probe = new BackgroundJobFixtureProbe();
        await using var host = Fixture.Host(database, probe, configure: services =>
        {
            services.RemoveAll<BackgroundJobDefinitionRegistry>();
            services.AddSingleton(definitions);
            services.AddSingleton(bounds);
        });
        await using var source = Source(database.OutboxConnectionString);
        var store = Control(source, definitions);
        var routing = host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>();
        var executor = host.Services.GetRequiredService<JobExecutor>();
        var gate = new BackgroundJobRuntimeGate(definitions.Infrastructure);
        var generation = new HangfireGenerationWork(definitions.Infrastructure.Pools["transactional"], gate);
        var controller = new BackgroundJobControl(source, definitions, executor, gate, bounds,
            new SystemGuidFactory(), TimeProvider.System);
        var first = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.CapacityKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup);
        (await BackgroundJobObservationQueries.ReadAsync(source, definitions)).HasUnresolvedCapacityRecovery.Should().BeFalse(
            "pending work with no occupied reservation is healthy");
        var claim = (await routing.TryClaimAsync(first, 1))!;
        var work = generation.TryBegin()!;
        var execution = controller.TryBegin(claim, work)!;
        work.HoldExecution(execution);
        var admission = executor.ExecutePublishedAsync(claim, execution.Publication, execution.AdmissionGate, null, work.Token);
        work.HoldAdmission(admission);
        execution.HoldAdmission(admission);
        var admitted = await admission.WaitAsync(TimeSpan.FromSeconds(10));
        admitted.Outcome.Should().Be(JobStartExecutionOutcome.Started);
        try
        {
            await execution.WaitForPublicationAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await AssertReservedObservationAsync("jobssubmissionfixture.resource", definitions);
            (await BackgroundJobObservationQueries.ReadAsync(source, definitions)).HasUnresolvedCapacityRecovery.Should().BeFalse(
                "ordinary executing work with its occupied reservation remains healthy");
            await DispatchAsync(database,
                "UPDATE background_jobs SET ownership_until=clock_timestamp()-interval '1 microsecond' WHERE id=@id", ("id", first.Value));
            var recovered = await store.RunRecoveryQuantumAsync(BackgroundJobControlLane.Recovery, []);
            recovered.ThrowIfFailed();
            recovered.Value.Should().Contain(change => change.JobId == first.Value && change.Phase == "reconciling");
            var parked = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, first))!;
            parked.Phase.Should().Be("reconciling");
            parked.NormalAttempts.Should().Be(1);
            parked.CurrentAttempt.Should().Be(admitted.Receipt!.AttemptId.Value);
            parked.OwnershipToken.Should().BeNull();
            var unresolved = await BackgroundJobObservationQueries.ReadAsync(source, definitions);
            unresolved.HasUnresolvedCapacityRecovery.Should().BeTrue();
            unresolved.HasQuarantinedJobs.Should().BeFalse("recoverable parking does not freeze late positive cleanup");
            await AssertReservedObservationAsync("jobssubmissionfixture.resource", definitions);
            await AssertReservedObservationAsync("unknown", BackgroundJobDefinitionRegistry.CreateEmpty());
            (await BackgroundJobObservationQueries.ReadAsync(source, BackgroundJobDefinitionRegistry.CreateEmpty()))
                .HasUnresolvedCapacityRecovery.Should().BeTrue("a different deployment must still see occupied parked work");
            await DispatchAsync(database,
                "UPDATE background_jobs SET probe_after=clock_timestamp()-interval '1 microsecond' WHERE id=@id", ("id", first.Value));
            var probed = await store.RunRecoveryQuantumAsync(BackgroundJobControlLane.CapacityProbe, []);
            probed.ThrowIfFailed();
            probed.Value.Should().Contain(change => change.JobId == first.Value && change.Phase == "reconciling");
            var second = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
                BackgroundJobRuntimeFixture.CapacityKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup);
            BackgroundJobObservationQueries.AssertQueue(await BackgroundJobObservationQueries.ReadAsync(source, definitions),
                "transactional", 2, 1);
            var secondClaim = (await routing.TryClaimAsync(second, 1))!;
            var refused = await executor.ExecuteAsync(secondClaim, BackgroundJobTestRun.Create());
            refused.Outcome.Should().Be(JobStartExecutionOutcome.CapacityDeferred);
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, second)).Should().Be(0);
            probe.Constructions.Should().BeEmpty("neither recovery nor capacity refusal constructs business work");
            await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
            await using (var transaction = await app.BeginTransactionAsync())
            {
                await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
                (await SchemaQueries.CountAsync(app,
                    "SELECT count(*) FROM background_job_reservations WHERE released_at IS NULL", transaction)).Should().Be(1L);
                (await SchemaQueries.CountAsync(app,
                    "SELECT count(*) FROM background_job_attempts WHERE transactional_cleanup_at IS NOT NULL OR no_start_at IS NOT NULL", transaction))
                    .Should().Be(0L, "a different instance cannot manufacture the origin's positive cleanup witness");
            }

            var cleanup = await work.PrepareCapacityCleanupAsync(execution).WaitAsync(TimeSpan.FromSeconds(10));
            cleanup.Should().NotBeNull("only the original invocation's actual no-start cleanup supplies this evidence");
            var completion = controller.FinishAdmissionAsync(execution, admitted, cleanup);
            controller.Start();
            (await completion.WaitAsync(TimeSpan.FromSeconds(15))).Should().BeTrue();
            var recoveredSample = await BackgroundJobObservationQueries.ReadAsync(source, definitions);
            recoveredSample.HasUnresolvedCapacityRecovery.Should().BeFalse("late positive cleanup restores routability");
            recoveredSample.HasQuarantinedJobs.Should().BeFalse();
            recoveredSample.Points.Where(point => point.Family == BackgroundJobObservationFamily.ReservedCapacity).Should().BeEmpty();
            var recoveredRow = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, first))!;
            recoveredRow.Phase.Should().Be("pending");
            recoveredRow.NormalAttempts.Should().Be(1);
            unresolved.HasUnresolvedCapacityRecovery.Should().BeTrue("published cold samples remain immutable");
        }
        finally
        {
            admitted.SourceOwner.CloseIssuance();
            gate.StopStarts();
            controller.RequestStop();
            await controller.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            await work.CompleteAsync(await execution.JoinAsync(operationCleanupConfirmed: true));
            generation.CloseStarts();
            await generation.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        async Task AssertReservedObservationAsync(string label, BackgroundJobDefinitionRegistry manifest)
        {
            var sample = await BackgroundJobObservationQueries.ReadAsync(source, manifest);
            BackgroundJobObservationQueries.AssertQueue(sample, "transactional", 1, 0);
            sample.Points.Where(point => point.Family == BackgroundJobObservationFamily.ReservedCapacity)
                .Should().ContainSingle().Which.Should().Be(new BackgroundJobObservationPoint(
                    BackgroundJobObservationFamily.ReservedCapacity, label, 0, 1, 0, 0));
        }
    }
}
