using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobPositiveSdkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_native_recovery_owner_renews_past_old_deadlines_without_admission_or_another_Execute(bool loseAcquisitionAck)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        await using var originalHost = Fixture.Host(database, provider.Settings, configure: services =>
        {
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(new BackgroundJobRuntimeOptions { ExternalWaitTimeout = TimeSpan.FromSeconds(4) });
        });
        var flow = await AdmitPrepareAndArmAsync(database, originalHost, Guid.CreateVersion7(),
            OrganizationId.From(Guid.CreateVersion7()), OrganizationId.From(Guid.CreateVersion7()));
        var executed = await flow.Executor.ExecuteExternalAsync(flow.Read, flow.Arm, CancellationToken.None);
        executed.Status.Should().Be(JobExternalExecutionStatus.Reported);
        flow.Receipt.Run.Close();
        await WaitForDatabaseLeaseAsync(database, flow.Receipt.Deadline);
        // This slice arranges the parked row explicitly. Automatic park/bridge
        // generation and full facade invocation are separate controls.
        await using (var connection = await PostgresFixture.OpenAsync(database.OutboxConnectionString))
            await SchemaQueries.ExecuteAsync(connection, null, """
                UPDATE background_jobs SET phase='reconciling',ownership_token=NULL,ownership_kind=NULL,ownership_until=NULL,probe_after=clock_timestamp()
                WHERE id=@id AND ownership_until<=clock_timestamp()
                """, ("id", flow.Receipt.Envelope.JobId.Value));
        var construction = new BackgroundJobPositiveProviderConstructionProbe(hold: true);
        await using var recoveryHost = Fixture.Host(database, provider.Settings, construction);
        var bounds = new BackgroundJobRuntimeOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(15),
            RenewalInterval = TimeSpan.FromSeconds(1),
            SqlTimeout = TimeSpan.FromSeconds(1),
            ProbeTimeout = TimeSpan.FromSeconds(30),
            ProgressTimeout = TimeSpan.FromSeconds(30),
        };
        bounds.Validate();
        await using var proxy = new CommitResponseDroppingProxy(database.OutboxConnectionString,
            commitsToPass: loseAcquisitionAck ? 0 : int.MaxValue, lifetime: TimeSpan.FromSeconds(90));
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(proxy.ConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var time = new NativeLeaseTimerObservation();
        var control = new BackgroundJobControl(source, Fixture.Registry, recoveryHost.Services.GetRequiredService<JobExecutor>(),
            runtime, bounds, new SystemGuidFactory(), time);
        var generation = new HangfireGenerationWork(Fixture.Registry.Infrastructure.Pools["external"], runtime, bounds, time);
        var work = generation.TryBegin()!;
        var execution = control.TryBeginRecovery(flow.Receipt.Envelope.JobId, await GenerationAsync(database, flow.Receipt.Envelope.JobId),
            flow.Receipt.Envelope.Queue, work)!;
        work.HoldExecution(execution);
        Task<bool>? actual = null;
        var clean = false;
        try
        {
            control.Snapshot.Executions.Should().Be(1, "the native owner is rooted before any recovery SQL");
            execution.WaitForPublicationAsync().IsCompleted.Should().BeTrue("there is no unfinished admission publication");
            actual = Task.Run(() => control.ExecuteExternalRecoveryAsync(execution));
            await construction.Entered.WaitAsync(TimeSpan.FromSeconds(15));
            execution.Identity.Should().NotBeNull();
            var identity = execution.Identity!;
            identity.IsRecovery.Should().BeTrue();
            identity.Token.Should().NotBe(flow.Receipt.Claim.Token);
            identity.AdmissionToken.Should().Be(flow.Receipt.Claim.Token);
            identity.AttemptId.Should().Be(flow.Receipt.AttemptId);
            identity.Deadline.Should().BeBefore(DateTimeOffset.UtcNow);
            execution.RunGate.IsClosed.Should().BeFalse();
            proxy.DroppedCommitResponses.Should().Be(loseAcquisitionAck ? 1 : 0);
            var oldLease = identity.OwnershipUntil;
            control.Start();
            using var until = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!until.IsCancellationRequested && (time.Timer(execution).Changes == 0 || control.Snapshot.Renewals == 0))
                await Task.Delay(10);
            time.Timer(execution).Changes.Should().BeGreaterThan(0);
            (await NativeLeaseAsync(database, identity.JobId)).Should().BeAfter(oldLease);
            await WaitForDatabaseLeaseAsync(database, oldLease);
            execution.RunGate.IsClosed.Should().BeFalse("actual recovery renewal survives both expired original and acquisition windows");
            execution.Token.IsCancellationRequested.Should().BeFalse();
            (await provider.CountsAsync(flow.Operation)).Should().Be((1L, 0L, 1L));
            construction.Release();
            clean = await actual.WaitAsync(TimeSpan.FromSeconds(15));
            clean.Should().BeTrue();
            (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, identity.JobId))!.Phase.Should().Be("finalize_pending");
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, identity.JobId)).Should().Be(1);
            (await provider.CountsAsync(flow.Operation)).Should().Be((1L, 1L, 1L));
        }
        finally
        {
            construction.Release();
            if (actual is not null) clean = await actual;
            await work.CompleteAsync(await execution.JoinAsync(clean));
            generation.CloseStarts();
            control.RequestStop();
            await control.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        }
        control.Snapshot.Executions.Should().Be(0);
        await generation.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
