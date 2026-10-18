using System.Diagnostics;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRuntimeFacadeTests
{
    [Fact]
    public async Task Native_grouped_permanent_refusal_survives_lost_checkpoint_ACK_and_a_real_locked_probe()
    {
        await using var database = await DatabaseAsync();
        var bounds = NativeCommitFaultBounds() with { SqlTimeout = TimeSpan.FromSeconds(1) };
        await using var proxy = new CommitResponseDroppingProxy(database.AppConnectionString,
            commitsToPass: 2, holdCommitResponse: true);
        var probe = new BackgroundJobFixtureProbe
        {
            Body = (_, _, _) => Task.FromResult(Result<None>.Fail(new Error(new LocalizedMessage("lockey_business_rule_violation")))),
        };
        await using var host = Fixture.Host(database, probe, configure: services =>
        {
            services.RemoveAll<BackgroundJobDefinitionRegistry>();
            services.AddSingleton(Fixture.CapacityRegistry());
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(bounds);
        }, appConnectionString: proxy.ConnectionString);
        var physical = new BackgroundJobPhysicalPoolSettings(true, 52);
        var controlName = "native_capacity_control_" + Guid.NewGuid().ToString("N");
        await using var source = Source(database, physical, controlName);
        using var globals = new GlobalFilterSnapshot();
        using var deadline = new CancellationTokenSource(TestWait);
        var runtime = Runtime(host.Services, source, bounds, physical);
        using var ambient = new Activity("foreign-terminal-caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        var observation = host.Services.GetRequiredService<BackgroundJobObservation>();
        using var metrics = new BackgroundJobStageProbe(observation);
        using var traces = new BackgroundJobTraceProbe(observation);
        await using var locker = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        NpgsqlTransaction? locked = null;
        try
        {
            await runtime.StartAsync(deadline.Token);
            await EventuallyQualificationAsync(() => Task.FromResult(runtime.Snapshot.IsHealthy), deadline.Token);
            var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
                BackgroundJobRuntimeFixture.CapacityKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup);
            await proxy.CommitResponseHeld.WaitAsync(deadline.Token);
            proxy.CompletedCommits.Should().Be(3, "the checkpoint is the third actual app COMMIT after framework read and admission");
            var execution = NativeCommitExecution(runtime, job);
            var identity = execution.Identity!;
            var admitted = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
            admitted.Phase.Should().Be("reconciling");
            admitted.NormalAttempts.Should().Be(1);
            (await ReadCapacityCleanupEvidenceAsync(database, job)).Should().Be((true, true, "transactional_rollback"));
            locked = await locker.BeginTransactionAsync(deadline.Token);
            await LockNativeJobAsync(locker, locked, job, deadline.Token);
            proxy.ReleaseHeldResponse();
            await WaitForNativeJobLockAsync(database, locker.ProcessID, "learnstack_outbox_admin", controlName, deadline.Token);
            NativeCommitExecution(runtime, job).Should().BeSameAs(execution);
            execution.Identity.Should().BeSameAs(identity);
            runtime.Snapshot.Control!.PendingOutcomes.Should().Be(1);
            probe.Constructions.Should().Equal("CapacityRoot");
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
            await locked.RollbackAsync(deadline.Token);
            await locked.DisposeAsync();
            locked = null;
            await EventuallyQualificationAsync(async () => runtime.Snapshot.Control is { Executions: 0, PendingOutcomes: 0 }
                && await NativePermanentControlSettledAsync(database, job, deadline.Token), deadline.Token);
            await AssertNativePermanentControlAuditAsync(database, job, admitted.CurrentAttempt!.Value, deadline.Token);
            metrics.Duration("capacity_cleanup").Tags["outcome"].Should().Be("UnknownCommit");
            metrics.Points.Should().ContainSingle(point => point.Name == "learnstack_job_unknown_outcome_total"
                && point.Tags["stage"] == "capacity_cleanup");
            var terminal = traces.Started.Single(activity => activity.OperationName == "job.terminal");
            terminal.ParentSpanId.Should().Be(default(System.Diagnostics.ActivitySpanId));
            terminal.TraceId.Should().NotBe(ambient.TraceId);
            traces.Stopped.Should().Contain(terminal);
            (await ReadCapacityCleanupEvidenceAsync(database, job)).Should().Be((true, true, "transactional_rollback"));
            probe.Constructions.Should().Equal("CapacityRoot");
            proxy.CompletedCommits.Should().Be(3, "locked reconciliation must not issue another app checkpoint");
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
            var checkpoint = (BackgroundJobCapacityCheckpointOperation)typeof(BackgroundJobExecutionControl)
                .GetField("_capacityCheckpoint", PrivateInstance)!.GetValue(execution)!;
            var historical = await new PostgresBackgroundJobControlStore(source,
                host.Services.GetRequiredService<BackgroundJobDefinitionRegistry>(), bounds, new SystemGuidFactory())
                .ProbeCommitAsync(BackgroundJobCommitProbe.ForCapacityCleanup(checkpoint.Recovery,
                    BackgroundJobCapacityCleanupEvidence.TransactionalRollback), deadline.Token);
            historical.ThrowIfFailed();
            historical.Value!.Disposition.Should().Be(BackgroundJobProbeDisposition.CapacityCleanupCommitted);
            historical.Value.CurrentRecoveryAuthority.Should().BeFalse("terminal routing cleared ownership without erasing immutable cleanup history");
            await AssertNoNativeProviderRetryAsync(source, job, deadline.Token);
        }
        finally
        {
            proxy.ReleaseHeldResponse();
            try
            {
                if (locked is not null)
                {
                    try { await locked.RollbackAsync(); }
                    finally { await locked.DisposeAsync(); }
                }
            }
            finally
            {
                using var cleanup = new CancellationTokenSource(TestWait);
                await runtime.StopAsync(cleanup.Token);
                await runtime.Completion.WaitAsync(cleanup.Token);
                await DisposePinnedSourceAsync(source);
            }
        }
    }
}
