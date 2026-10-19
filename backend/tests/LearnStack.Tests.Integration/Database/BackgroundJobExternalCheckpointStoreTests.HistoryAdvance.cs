using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobExternalCheckpointStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task History_reads_the_original_stamped_attempt_after_a_real_later_admission_replaces_current_attempt_and_generation(bool armed)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external");
        await using var host = RecoveryHost(database, preparing: true);
        var original = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        var executor = host.Services.GetRequiredService<JobExecutor>();
        await using var proxy = new CommitResponseDroppingProxy(database.AppConnectionString);
        await using var brokenHost = RecoveryHost(database, preparing: true, appConnectionString: proxy.ConnectionString);
        var broken = brokenHost.Services.GetRequiredService<JobExecutor>();
        BackgroundJobCheckpointHistoryOrigin origin;
        if (armed)
        {
            (await executor.ExecuteBusinessAsync(original, original.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare)))
                .CommitAcknowledged.Should().BeTrue();
            var read = await executor.ReadExternalStateAsync(original, CancellationToken.None);
            read.Handle.Should().NotBeNull();
            var arm = await broken.RecordExternalArmAsync(original, read.Handle!,
                original.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm), CancellationToken.None);
            arm.CommitUncertain.Should().BeTrue();
            origin = BackgroundJobCheckpointHistoryOrigin.ForArm(arm);
        }
        else
        {
            var prepare = await broken.ExecuteBusinessAsync(original, original.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare));
            prepare.DurableOutcomeUncertain.Should().BeTrue();
            origin = BackgroundJobCheckpointHistoryOrigin.ForPrepare(prepare);
        }
        proxy.DroppedCommitResponses.Should().Be(1);
        await RevokeForDriverRecoveryAsync(database, job);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var acquired = await store.AcquireExternalRecoveryAsync(job, await RecoveryGenerationAsync(database, job), original.Envelope.Queue, CancellationToken.None);
        acquired.ThrowIfFailed();
        var recovery = BindRecovery(acquired, runtime);
        var recoveryRead = await executor.ReadExternalStateAsync(recovery, CancellationToken.None);
        recoveryRead.Handle.Should().NotBeNull();
        if (armed)
        {
            // Explicit app/storage fixture data, not a real provider-method proof.
            BackgroundJobRetainedExternalObservation observation;
            using (BackgroundJobExecutionAdmission.EnterExternal())
                observation = BackgroundJobRetainedExternalObservation.Capture(new(job, original.AttemptId,
                    recoveryRead.Handle!.Snapshot.OperationId!.Value, original.Deadline), recoveryRead.Handle.Snapshot.PlanFingerprint!,
                    BackgroundJobExternalResult.Failed(new WriterOutcome(Guid.NewGuid()),
                        new BackgroundJobQuiescenceEvidence(Guid.NewGuid(), BackgroundJobQuiescenceKind.ProviderFenced),
                        BackgroundJobExternalFailurePolicy.TransientRepeatable));
            (await executor.RecordExternalOutcomeAsync(recovery, recoveryRead.Handle!,
                recovery.Run.OpenBoundary(BackgroundJobCheckpointKind.Outcome), observation, CancellationToken.None))
                .CommitAcknowledged.Should().BeTrue();
        }
        else
            (await executor.RecordExternalNoStartAsync(recovery, recoveryRead.Handle!,
                recovery.Run.OpenBoundary(BackgroundJobCheckpointKind.NoStart), CancellationToken.None))
                .CommitAcknowledged.Should().BeTrue();
        await using (var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString))
            await SchemaQueries.ExecuteAsync(dispatcher, null, """
                UPDATE background_jobs SET phase='pending',available_after=clock_timestamp(),
                    ownership_token=NULL,ownership_kind=NULL,ownership_until=NULL
                WHERE id=@id AND phase='reconciling'
                """, ("id", job.Value));
        var later = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        later.AttemptId.Should().NotBe(original.AttemptId);
        later.NormalAttemptOrdinal.Should().Be(2);
        later.Stage.Should().BeNull("the later admission reuses the immutable acknowledged plan");
        later.Claim.WakeGeneration.Should().BeGreaterThan(original.Claim.WakeGeneration);
        original.Run.IsClosed.Should().BeFalse("an old memory stop flag cannot mask changed durable current identity");
        var proof = await store.ProbeExternalCheckpointAsync(origin, CancellationToken.None);
        proof.ThrowIfFailed();
        proof.Value.Should().NotBeNull();
        proof.Value!.Disposition.Should().Be(BackgroundJobCheckpointHistoryDisposition.Committed);
        proof.Value.Facts.AttemptId.Should().Be(original.AttemptId.Value);
        proof.Value.Facts.AdmissionToken.Should().Be(original.Claim.Token);
        proof.Value.Facts.Ordinal.Should().Be(1);
        proof.Value.Facts.CurrentAttemptId.Should().Be(later.AttemptId.Value);
        proof.Value.Facts.CurrentWakeGeneration.Should().Be(later.Claim.WakeGeneration);
        proof.Value.Facts.NormalAttempts.Should().Be(2);
        proof.Value.Facts.AttemptPlanFingerprint.Should().Be(proof.Value.Facts.PlanFingerprint);
        if (armed)
        {
            proof.Value.Facts.OutcomeKind.Should().Be("failed");
            proof.Value.Facts.FailurePolicy.Should().Be("transient_repeatable");
            proof.Value.Confirmation!.AllowsOriginalExecute(origin.Checkpoint!.Arm!).Should().BeFalse();
            original.Run.TryIssueExternalExecute(origin.Gate).Should().BeFalse();
        }
        origin.Gate.CommitAcknowledged.Should().BeFalse();
        origin.Gate.CommitConfirmed.Should().BeTrue();
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(2,
            "the only extra attempt was the independently counted later admission, never the history probe");
    }
}
