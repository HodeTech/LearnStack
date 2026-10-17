using System.Diagnostics;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobExternalCheckpointStoreTests
{
    [Fact]
    public async Task Named_writer_cannot_borrow_an_old_gates_known_rollback_and_a_new_gate_can_commit()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external");
        var refusal = new RefuseFirstWriterResult();
        await using var host = RecoveryHost(database, preparing: true, configure: services =>
        {
            services.AddSingleton(refusal);
            services.AddTransient<IPipelineBehavior<RecordBackgroundJobArmCommand, Result<None>>, RefuseFirstWriterAfterHandler>();
        });
        var original = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        var executor = host.Services.GetRequiredService<JobExecutor>();
        (await executor.ExecuteBusinessAsync(original, original.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare)))
            .CommitAcknowledged.Should().BeTrue();
        var read = await executor.ReadExternalStateAsync(original, CancellationToken.None);
        read.Handle.Should().NotBeNull();
        var gate = original.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm);
        var rolledBack = await executor.RecordExternalArmAsync(original, read.Handle!, gate, CancellationToken.None);
        rolledBack.Status.Should().Be(JobExternalCheckpointStatus.Refused);
        rolledBack.Issued.Should().BeTrue();
        rolledBack.KnownRollback.Should().BeTrue();
        rolledBack.CommitStarted.Should().BeFalse();
        rolledBack.CommitAcknowledged.Should().BeFalse();
        rolledBack.CleanupFailed.Should().BeFalse();
        original.Run.IsClosed.Should().BeFalse();
        (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!.Phase.Should().Be("prepared");
        var duplicate = await executor.RecordExternalArmAsync(original, read.Handle!, gate, CancellationToken.None);
        duplicate.Status.Should().Be(JobExternalCheckpointStatus.Refused);
        duplicate.Issued.Should().BeFalse();
        duplicate.KnownRollback.Should().BeFalse("the old command's rollback does not describe this unissued command");
        duplicate.CommitStarted.Should().BeFalse();
        duplicate.CommitAcknowledged.Should().BeFalse();
        duplicate.CommitUncertain.Should().BeFalse();
        refusal.Calls.Should().Be(1);
        var retry = await executor.RecordExternalArmAsync(original, read.Handle!,
            original.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm), CancellationToken.None);
        retry.Status.Should().Be(JobExternalCheckpointStatus.Completed);
        retry.CommitAcknowledged.Should().BeTrue();
        retry.KnownRollback.Should().BeFalse();
        retry.CleanupFailed.Should().BeFalse();
        refusal.Calls.Should().Be(2);
        (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!.Phase.Should().Be("executing");
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
    }

    [Theory]
    [InlineData("arm")]
    [InlineData("outcome")]
    public async Task Named_writer_retains_its_exact_unacknowledged_commit_when_postgres_committed_before_socket_loss(string kind)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external");
        await using var host = RecoveryHost(database, preparing: true);
        var original = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        var executor = host.Services.GetRequiredService<JobExecutor>();
        (await executor.ExecuteBusinessAsync(original, original.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare)))
            .CommitAcknowledged.Should().BeTrue();
        var read = await executor.ReadExternalStateAsync(original, CancellationToken.None);
        read.Handle.Should().NotBeNull();
        if (kind == "outcome")
            (await executor.RecordExternalArmAsync(original, read.Handle!,
                original.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm), CancellationToken.None))
                .CommitAcknowledged.Should().BeTrue();
        // All previous reads/writes use the healthy host. Only this exact writer
        // uses the proxy, so its first COMMIT response is the one withheld.
        await using var proxy = new CommitResponseDroppingProxy(database.AppConnectionString);
        await using var brokenHost = RecoveryHost(database, preparing: true, appConnectionString: proxy.ConnectionString);
        var broken = brokenHost.Services.GetRequiredService<JobExecutor>();
        var gate = original.Run.OpenBoundary(kind == "arm" ? BackgroundJobCheckpointKind.Arm : BackgroundJobCheckpointKind.Outcome);
        var actual = kind == "arm"
            ? broken.RecordExternalArmAsync(original, read.Handle!, gate, CancellationToken.None)
            : broken.RecordExternalOutcomeAsync(original, read.Handle!, gate,
                DriverObservation(original, read.Handle!.Snapshot), CancellationToken.None);
        var result = await actual.WaitAsync(TimeSpan.FromSeconds(15));
        proxy.DroppedCommitResponses.Should().Be(1);
        result.Status.Should().Be(JobExternalCheckpointStatus.Indeterminate);
        result.Gate.Should().BeSameAs(gate);
        result.Issued.Should().BeTrue();
        result.CommitStarted.Should().BeTrue();
        result.CommitAcknowledged.Should().BeFalse();
        result.KnownRollback.Should().BeFalse();
        result.CommitUncertain.Should().BeTrue();
        if (kind == "arm") result.Arm!.Gate.Should().BeSameAs(gate);
        else result.Outcome!.Gate.Should().BeSameAs(gate);
        var row = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
        row.Phase.Should().Be(kind == "arm" ? "executing" : "finalize_pending",
            "an outside app connection observes the actual landed transaction despite its lost acknowledgement");
        row.NormalAttempts.Should().Be(1);
        original.Run.TryOpenBoundary(BackgroundJobCheckpointKind.Outcome).Should().BeNull(
            "cleaned resources and a completed task cannot resolve this exact unknown predecessor");
        gate.CommitAcknowledged.Should().BeFalse("an outside observation is not the original physical COMMIT callback");
    }

    [Theory]
    [InlineData("arm")]
    [InlineData("outcome")]
    public async Task Named_writer_stop_after_handler_before_commit_is_an_acknowledged_rollback_not_unknown_history(string kind)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external");
        var pause = new CheckpointCommitPause(kind == "arm" ? typeof(RecordBackgroundJobArmCommand)
            : typeof(RecordBackgroundJobOutcomeCommand));
        await using var host = RecoveryHost(database, preparing: true, pause);
        var original = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        var executor = host.Services.GetRequiredService<JobExecutor>();
        (await executor.ExecuteBusinessAsync(original, original.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare)))
            .CommitAcknowledged.Should().BeTrue();
        var read = await executor.ReadExternalStateAsync(original, CancellationToken.None);
        read.Handle.Should().NotBeNull();
        if (kind == "outcome")
            (await executor.RecordExternalArmAsync(original, read.Handle!,
                original.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm), CancellationToken.None))
                .CommitAcknowledged.Should().BeTrue();
        using var cancellation = new CancellationTokenSource();
        var gate = original.Run.OpenBoundary(kind == "arm" ? BackgroundJobCheckpointKind.Arm : BackgroundJobCheckpointKind.Outcome);
        pause.Enabled = true;
        var actual = kind == "arm"
            ? executor.RecordExternalArmAsync(original, read.Handle!, gate, cancellation.Token)
            : executor.RecordExternalOutcomeAsync(original, read.Handle!, gate,
                DriverObservation(original, read.Handle!.Snapshot), cancellation.Token);
        Exception? premiseFailure = null;
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            actual.IsCompleted.Should().BeFalse();
            gate.CommitStarted.Should().BeFalse();
            await cancellation.CancelAsync();
            original.Run.IsClosed.Should().BeTrue();
        }
        catch (Exception failure) { premiseFailure = failure; }
        finally { pause.Release.TrySetResult(); }
        var result = await actual.WaitAsync(TimeSpan.FromSeconds(15));
        premiseFailure.Should().BeNull();
        result.Status.Should().Be(JobExternalCheckpointStatus.Refused);
        result.Issued.Should().BeTrue();
        result.CommitStarted.Should().BeFalse();
        result.CommitAcknowledged.Should().BeFalse();
        result.KnownRollback.Should().BeTrue();
        result.CommitUncertain.Should().BeFalse();
        result.CleanupFailed.Should().BeFalse();
        result.RetainedCleanup.Should().BeEmpty();
        result.SourceOwner.CleanupConfirmed.Should().BeTrue();
        var row = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
        row.Phase.Should().Be(kind == "arm" ? "prepared" : "executing");
        row.NormalAttempts.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Named_writer_cancellation_closes_the_run_before_issuance_or_during_actual_activity_cleanup(bool alreadyCancelled)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var trace = ActivityTraceId.CreateRandom();
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup,
            profile: "external", traceparent: $"00-{trace}-{ActivitySpanId.CreateRandom()}-01");
        await using var host = RecoveryHost(database, preparing: true);
        var original = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        var executor = host.Services.GetRequiredService<JobExecutor>();
        (await executor.ExecuteBusinessAsync(original, original.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare)))
            .CommitAcknowledged.Should().BeTrue();
        var read = await executor.ReadExternalStateAsync(original, CancellationToken.None);
        read.Handle.Should().NotBeNull();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name.Length == 0,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (alreadyCancelled || activity.OperationName != "job.external_checkpoint" || activity.TraceId != trace) return;
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            },
        };
        ActivitySource.AddActivityListener(listener);
        using var cancellation = new CancellationTokenSource();
        if (alreadyCancelled) await cancellation.CancelAsync();
        var gate = original.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm);
        var actual = Task.Run(() => executor.RecordExternalArmAsync(original, read.Handle!, gate, cancellation.Token));
        Exception? premiseFailure = null;
        try
        {
            if (alreadyCancelled) await actual.WaitAsync(TimeSpan.FromSeconds(15));
            else
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                actual.IsCompleted.Should().BeFalse();
                gate.CommitAcknowledged.Should().BeTrue();
                original.SourceOwner.CleanupConfirmed.Should().BeTrue();
                await cancellation.CancelAsync();
            }
            original.Run.IsClosed.Should().BeTrue();
        }
        catch (Exception failure) { premiseFailure = failure; }
        finally { release.TrySetResult(); }
        var result = await actual.WaitAsync(TimeSpan.FromSeconds(15));
        premiseFailure.Should().BeNull();
        result.Status.Should().Be(alreadyCancelled ? JobExternalCheckpointStatus.Refused : JobExternalCheckpointStatus.Completed);
        result.Issued.Should().Be(!alreadyCancelled);
        result.CommitAcknowledged.Should().Be(!alreadyCancelled);
        result.KnownRollback.Should().BeFalse();
        result.CommitUncertain.Should().BeFalse();
        result.CleanupFailed.Should().BeFalse();
        result.RetainedCleanup.Should().BeEmpty();
        Action continueStopped = () => _ = read.Handle!.Require(original);
        continueStopped.Should().Throw<InvalidOperationException>();
        if (alreadyCancelled) entered.Task.IsCompleted.Should().BeFalse();
    }

    private sealed class RefuseFirstWriterResult
    {
        internal int Calls;
    }

    private sealed class RefuseFirstWriterAfterHandler(RefuseFirstWriterResult refusal)
        : IPipelineBehavior<RecordBackgroundJobArmCommand, Result<None>>
    {
        public async Task<Result<None>> Handle(RecordBackgroundJobArmCommand request,
            RequestHandlerDelegate<Result<None>> next, CancellationToken cancellationToken)
        {
            var result = await next();
            result.IsSuccess.Should().BeTrue();
            return Interlocked.Increment(ref refusal.Calls) == 1
                ? Result<None>.Fail(new Error(new LocalizedMessage("lockey_dependency_unavailable"))) : result;
        }
    }
}
