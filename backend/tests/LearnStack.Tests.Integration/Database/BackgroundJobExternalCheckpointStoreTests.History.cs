using FluentAssertions;
using System.Diagnostics;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobExternalCheckpointStoreTests
{
    [Theory]
    [InlineData("prepare", false, true)]
    [InlineData("prepare", true, true)]
    [InlineData("arm", false, true)]
    [InlineData("arm", true, true)]
    [InlineData("outcome", false, true)]
    [InlineData("outcome", true, true)]
    [InlineData("no_start", false, true)]
    [InlineData("no_start", true, true)]
    [InlineData("prepare", false, false)]
    [InlineData("prepare", true, false)]
    [InlineData("arm", false, false)]
    [InlineData("arm", true, false)]
    [InlineData("outcome", false, false)]
    [InlineData("outcome", true, false)]
    [InlineData("no_start", false, false)]
    [InlineData("no_start", true, false)]
    public async Task Locked_history_resolves_the_exact_lost_ack_without_reopening_stopped_work_or_rewriting_physical_history(
        string kind, bool stopped, bool committed)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external");
        await using var host = RecoveryHost(database, preparing: kind != "no_start");
        var original = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        var executor = host.Services.GetRequiredService<JobExecutor>();
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        BackgroundJobExternalRecoveryBinding? recovery = null;
        JobExternalReadHandle? read = null;
        if (kind == "no_start")
        {
            await ParkExpiredExternalAsync(database, original);
            var acquired = await store.AcquireExternalRecoveryAsync(job, await RecoveryGenerationAsync(database, job), original.Envelope.Queue, CancellationToken.None);
            acquired.ThrowIfFailed();
            recovery = BindRecovery(acquired, runtime);
            read = (await executor.ReadExternalStateAsync(recovery, CancellationToken.None)).Handle;
        }
        else if (kind != "prepare")
        {
            (await executor.ExecuteBusinessAsync(original, original.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare)))
                .CommitAcknowledged.Should().BeTrue();
            read = (await executor.ReadExternalStateAsync(original, CancellationToken.None)).Handle;
            if (kind == "outcome")
                (await executor.RecordExternalArmAsync(original, read!, original.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm), CancellationToken.None))
                    .CommitAcknowledged.Should().BeTrue();
        }
        if (kind != "prepare") read.Should().NotBeNull();
        await using var proxy = new CommitResponseDroppingProxy(database.AppConnectionString, dropCommitRequest: !committed);
        await using var brokenHost = RecoveryHost(database, preparing: true, appConnectionString: proxy.ConnectionString);
        var broken = brokenHost.Services.GetRequiredService<JobExecutor>();
        var run = recovery?.Run ?? original.Run;
        var gate = run.OpenBoundary(kind switch
        {
            "prepare" => BackgroundJobCheckpointKind.Prepare,
            "arm" => BackgroundJobCheckpointKind.Arm,
            "outcome" => BackgroundJobCheckpointKind.Outcome,
            "no_start" => BackgroundJobCheckpointKind.NoStart,
            _ => throw new InvalidOperationException(),
        });
        BackgroundJobCheckpointHistoryOrigin origin;
        JobBusinessExecutionResult? business = null;
        JobExternalCheckpointResult? checkpoint = null;
        if (kind == "prepare")
        {
            business = await broken.ExecuteBusinessAsync(original, gate).WaitAsync(TimeSpan.FromSeconds(15));
            business.ExecutionIssued.Should().BeTrue();
            business.CommitStarted.Should().BeTrue();
            business.CommitAcknowledged.Should().BeFalse();
            business.KnownRollback.Should().BeFalse();
            business.DurableOutcomeUncertain.Should().BeTrue();
            origin = BackgroundJobCheckpointHistoryOrigin.ForPrepare(business);
        }
        else
        {
            checkpoint = await (kind switch
            {
                "arm" => broken.RecordExternalArmAsync(original, read!, gate, CancellationToken.None),
                "outcome" => broken.RecordExternalOutcomeAsync(original, read!, gate, DriverObservation(original, read!.Snapshot), CancellationToken.None),
                "no_start" => broken.RecordExternalNoStartAsync(recovery!, read!, gate, CancellationToken.None),
                _ => throw new InvalidOperationException(),
            }).WaitAsync(TimeSpan.FromSeconds(15));
            checkpoint.Issued.Should().BeTrue();
            checkpoint.CommitUncertain.Should().BeTrue();
            origin = kind switch
            {
                "arm" => BackgroundJobCheckpointHistoryOrigin.ForArm(checkpoint),
                "outcome" => BackgroundJobCheckpointHistoryOrigin.ForOutcome(checkpoint),
                "no_start" => BackgroundJobCheckpointHistoryOrigin.ForNoStart(checkpoint),
                _ => throw new InvalidOperationException(),
            };
        }
        proxy.DroppedCommitResponses.Should().Be(committed ? 1 : 0);
        proxy.DroppedCommitRequests.Should().Be(committed ? 0 : 1);
        gate.CommitAcknowledged.Should().BeFalse();
        gate.CommitConfirmed.Should().BeFalse();
        gate.KnownRollback.Should().BeFalse();
        run.TryOpenBoundary(BackgroundJobCheckpointKind.Outcome).Should().BeNull();
        if (stopped) run.Close();
        await using (var blocker = await PostgresFixture.OpenAsync(database.AppConnectionString))
        await using (var transaction = await blocker.BeginTransactionAsync())
        {
            await SchemaQueries.AssertApplicationRoleAsync(blocker, transaction);
            await EducationSchemaSeed.AnnounceAsync(blocker, transaction, SchemaFixture.TenantA, null);
            await SchemaQueries.ExecuteAsync(blocker, transaction, "SELECT id FROM background_jobs WHERE id=@id FOR UPDATE", ("id", job.Value));
            var skipped = await store.ProbeExternalCheckpointAsync(origin, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            skipped.ThrowIfFailed();
            skipped.Value.Should().BeNull("a SKIP LOCKED row is unavailable, never proof of absent history");
            gate.CommitConfirmed.Should().BeFalse();
            await transaction.RollbackAsync();
        }
        var proof = await store.ProbeExternalCheckpointAsync(origin, CancellationToken.None);
        proof.ThrowIfFailed();
        proof.CleanupConfirmed.Should().BeTrue();
        proof.CommitAcknowledged.Should().BeFalse("history is read then explicitly rolled back");
        proof.KnownRollback.Should().BeFalse("the probe's ROLLBACK says nothing about the original transaction");
        proof.Value.Should().NotBeNull();
        proof.Value!.Disposition.Should().Be(committed ? BackgroundJobCheckpointHistoryDisposition.Committed
            : BackgroundJobCheckpointHistoryDisposition.Absent);
        proof.Value.Facts.AttemptId.Should().Be(original.AttemptId.Value);
        proof.Value.Facts.AdmissionToken.Should().Be(original.Claim.Token);
        proof.Value.Facts.AdmittedAt.Should().Be(original.AdmittedAt);
        proof.Value.Facts.Deadline.Should().Be(original.Deadline);
        proof.Value.Facts.JobId.Should().Be(job.Value);
        if (committed) proof.Value.Confirmation.Should().NotBeNull();
        else proof.Value.Confirmation.Should().BeNull("known absence cannot impersonate a committed checkpoint");
        if (business is not null)
        {
            if (committed) proof.Value.Confirmation!.Matches(original, gate).Should().BeTrue();
            business.CommitAcknowledged.Should().BeFalse();
            business.KnownRollback.Should().BeFalse();
        }
        else if (committed && checkpoint!.Arm is { } arm) proof.Value.Confirmation!.Matches(arm).Should().BeTrue();
        else if (committed) proof.Value.Confirmation!.Matches(checkpoint!.Outcome!).Should().BeTrue();
        gate.CommitConfirmed.Should().Be(committed);
        gate.CommitAcknowledged.Should().BeFalse();
        gate.KnownRollback.Should().BeFalse();
        run.IsClosed.Should().Be(stopped);
        if (committed && checkpoint?.Arm is { } confirmedArm)
            proof.Value.Confirmation!.AllowsOriginalExecute(confirmedArm).Should().Be(!stopped);
        var next = run.TryOpenBoundary(BackgroundJobCheckpointKind.Outcome);
        if (stopped || !committed) next.Should().BeNull("an absence proof does not reactivate or reuse a consumed root");
        else next.Should().NotBeNull("only resolved history on an open run permits another framework boundary");
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
    }

    [Theory]
    [InlineData("live")]
    [InlineData("takeover")]
    [InlineData("elapsed")]
    [InlineData("borrowed")]
    public async Task Confirmed_arm_entry_requires_exact_command_and_still_current_conservative_lease(string state)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external");
        var construction = new ProviderConstructionProbe();
        await using var host = BridgeCanaryHost(database, construction);
        var original = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        var executor = host.Services.GetRequiredService<JobExecutor>();
        (await executor.ExecuteBusinessAsync(original, original.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare)))
            .CommitAcknowledged.Should().BeTrue();
        var read = await executor.ReadExternalStateAsync(original, CancellationToken.None);
        read.Handle.Should().NotBeNull();
        await using var proxy = new CommitResponseDroppingProxy(database.AppConnectionString);
        await using var brokenHost = RecoveryHost(database, preparing: true, appConnectionString: proxy.ConnectionString);
        var gate = original.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm);
        var arm = await brokenHost.Services.GetRequiredService<JobExecutor>()
            .RecordExternalArmAsync(original, read.Handle!, gate, CancellationToken.None);
        proxy.DroppedCommitResponses.Should().Be(1);
        arm.CommitUncertain.Should().BeTrue();
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        if (state == "takeover")
        {
            await RevokeForDriverRecoveryAsync(database, job);
            var acquired = await store.AcquireExternalRecoveryAsync(job, await RecoveryGenerationAsync(database, job), original.Envelope.Queue, CancellationToken.None);
            acquired.ThrowIfFailed();
            acquired.Value.Should().NotBeNull();
            acquired.Value!.RecoveryToken.Should().NotBe(original.Claim.Token);
        }
        else if (state == "elapsed")
        {
            await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
            await SchemaQueries.ExecuteAsync(dispatcher, null,
                "UPDATE background_jobs SET ownership_until=clock_timestamp()+interval '5 seconds' WHERE id=@id", ("id", job.Value));
        }
        var proof = await store.ProbeExternalCheckpointAsync(BackgroundJobCheckpointHistoryOrigin.ForArm(arm), CancellationToken.None);
        proof.ThrowIfFailed();
        proof.Value.Should().NotBeNull();
        proof.Value!.Disposition.Should().Be(BackgroundJobCheckpointHistoryDisposition.Committed);
        var confirmation = proof.Value.Confirmation!;
        confirmation.Matches(arm.Arm!).Should().BeTrue();
        gate.CommitConfirmed.Should().BeTrue();
        gate.CommitAcknowledged.Should().BeFalse();
        original.Run.IsClosed.Should().BeFalse("takeover is independent of this old process's memory stop bit");
        if (state == "elapsed")
        {
            var until = proof.Value.Facts.CurrentOwnershipUntil!.Value;
            confirmation.AllowsOriginalExecute(arm.Arm!).Should().BeTrue();
            await using var clock = await PostgresFixture.OpenAsync(database.AppConnectionString);
            var wait = Stopwatch.StartNew();
            while (true)
            {
                await using var command = new NpgsqlCommand("SELECT clock_timestamp()>=@until", (NpgsqlConnection)clock) { CommandTimeout = 5 };
                command.Parameters.AddWithValue("until", until);
                if (await command.ExecuteScalarAsync() is true) break;
                if (wait.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("The exact observed lease did not expire.");
                await Task.Delay(10);
            }
        }
        if (state == "borrowed")
        {
            // An explicit negative fixture, never a production result factory.
            var otherCommand = new RecordBackgroundJobArmCommand(original, gate,
                read.Handle!.Snapshot.PlanFingerprint!, read.Handle.Snapshot.OperationId!.Value);
            confirmation.Matches(otherCommand).Should().BeFalse();
            arm = new(JobExternalCheckpointStatus.Indeterminate, otherCommand, null, true, false, []);
        }
        confirmation.AllowsOriginalExecute(arm.Arm!).Should().Be(state == "live");
        if (state is "takeover" or "elapsed")
            original.Run.TryIssueExternalExecute(gate).Should().BeFalse("the final CAS independently rejects stale current authority");
        var (bridge, scopes) = ObservedProviderBridge(host.Services);
        var result = await bridge.ExecuteExternalAsync(read.Handle!, arm, CancellationToken.None);
        result.Observation.Should().BeNull();
        result.CleanupFailed.Should().BeFalse();
        scopes.Calls.Should().Be(state == "live" ? 1 : 0);
        construction.Calls.Should().Be(state == "live" ? 1 : 0);
        if (state == "live") construction.ConstructorCanaryHit.Should().BeTrue();
        original.Run.TrySealUnusedExternalExecute().Should().BeTrue("the unchanged throwing adapter never reached Execute");
        gate.CommitAcknowledged.Should().BeFalse();
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
    }

    [Fact]
    public async Task Closed_duplicate_prepare_has_no_own_commit_history_and_cannot_create_a_history_origin()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external");
        await using var host = RecoveryHost(database, preparing: true);
        var original = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        var executor = host.Services.GetRequiredService<JobExecutor>();
        var gate = original.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare);
        var actual = await executor.ExecuteBusinessAsync(original, gate);
        actual.CommitAcknowledged.Should().BeTrue();
        actual.ExecutionIssued.Should().BeTrue();
        BackgroundJobCheckpointHistoryOrigin.ForPrepare(actual).Business.Should().BeSameAs(actual);
        gate.Close();
        var duplicate = await executor.ExecuteBusinessAsync(original, gate);
        duplicate.Receipt.Should().BeSameAs(original);
        duplicate.Gate.Should().BeSameAs(gate);
        duplicate.ExecutionIssued.Should().BeFalse();
        duplicate.CommitStarted.Should().BeFalse();
        duplicate.CommitAcknowledged.Should().BeFalse();
        duplicate.KnownRollback.Should().BeFalse();
        duplicate.DurableOutcomeUncertain.Should().BeFalse();
        gate.CommitAcknowledged.Should().BeTrue("the old gate still describes the first real invocation");
        Action borrow = () => BackgroundJobCheckpointHistoryOrigin.ForPrepare(duplicate);
        borrow.Should().Throw<InvalidOperationException>();
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
    }
}
