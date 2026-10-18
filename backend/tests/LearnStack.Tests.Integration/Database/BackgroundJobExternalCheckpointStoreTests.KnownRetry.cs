using System.Diagnostics;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobExternalCheckpointStoreTests
{
    [Theory]
    [InlineData(1, false, false)]
    [InlineData(2, false, false)]
    [InlineData(3, false, false)]
    [InlineData(4, false, false)]
    [InlineData(5, false, false)]
    [InlineData(1, true, false)]
    [InlineData(5, true, false)]
    [InlineData(1, true, true)]
    [InlineData(5, true, true)]
    public async Task Known_retry_uses_actual_released_history_and_fixed_backoff_without_spending_an_attempt(
        int ordinal, bool recovered, bool noStart)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await ExternalRetryJobAsync(database);
        await using var host = RecoveryHost(database, preparing: true);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var normal = await RetainedSelectionAttemptAsync(database, host.Services, job, ordinal, store, runtime);
        var identity = await KnownRetryFixtureAsync(database, host.Services, normal, store, runtime, recovered, noStart);
        var immutable = await RetainedSelectionFactsAsync(database, normal);
        var before = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
        before.OwnershipToken.Should().Be(identity.Token);
        before.OwnershipUntil.Should().BeAfter(DateTimeOffset.UtcNow,
            "the production writer must act on a still-live owner, not depend on expiry");
        var lower = await RetryDatabaseClockAsync(database);
        var changed = await store.RetryKnownExternalAsync(identity, recovered
            ? BackgroundJobExternalRetryMode.RecoveryKnown : BackgroundJobExternalRetryMode.OriginalCompleted, CancellationToken.None);
        changed.ThrowIfFailed();
        changed.CommitAcknowledged.Should().BeTrue();
        changed.CleanupConfirmed.Should().BeTrue();
        changed.Value.Should().Be(new BackgroundJobControlChange(job.Value, identity.WakeGeneration + 1, "pending", null));
        var upper = await RetryDatabaseClockAsync(database);
        var descriptor = changed.Probes.Should().ContainSingle().Subject;
        descriptor.ExpectedUntil.Should().BeNull("a retry schedule is not lease-renewal evidence");
        var after = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
        after.AvailableAfter.Should().Be(descriptor.ExpectedAvailableAfter!.Value);
        after.AvailableAfter.Should().BeOnOrAfter(lower + BackgroundJobFailurePolicy.GetRetryDelay(ordinal))
            .And.BeOnOrBefore(upper + BackgroundJobFailurePolicy.GetRetryDelay(ordinal));
        after.CurrentAttempt.Should().Be(normal.AttemptId.Value);
        after.NormalAttempts.Should().Be(ordinal);
        after.OwnershipToken.Should().BeNull();
        after.OwnershipKind.Should().BeNull();
        after.OwnershipUntil.Should().BeNull();
        (await RetainedSelectionFactsAsync(database, normal)).Should().Be(immutable);
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(ordinal);
        var proof = await store.ProbeCommitAsync(descriptor);
        proof.ThrowIfFailed();
        proof.CleanupConfirmed.Should().BeTrue();
        proof.Value!.Disposition.Should().Be(BackgroundJobProbeDisposition.ControlCommitted);
        proof.Value.RecoveryReceipt.Should().BeNull();
        proof.Value.CurrentRecoveryAuthority.Should().BeFalse();
        if (ordinal == 5)
        {
            var waiting = await BackgroundJobObservationQueries.ReadAsync(source, Fixture.Registry);
            BackgroundJobObservationQueries.AssertQueue(waiting, "external", 1, 0);
            waiting.Points.Should().NotContain(point => point.Family == BackgroundJobObservationFamily.ReservedCapacity);
            // The one-hour fifth backoff is already proved above. Advance only
            // test eligibility, preserving every actual history and release fact.
            await using (var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString))
                await SchemaQueries.ExecuteAsync(dispatcher, null,
                    "UPDATE background_jobs SET available_after=clock_timestamp() WHERE id=@id", ("id", job.Value));
            BackgroundJobObservationQueries.AssertQueue(await BackgroundJobObservationQueries.ReadAsync(source, Fixture.Registry),
                "external", 1, 1);
            var routing = new PostgresBackgroundJobRoutingStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
            var claim = await routing.TryClaimAsync(job, changed.Value!.WakeGeneration);
            claim.Should().NotBeNull("the counted eligible row is genuinely normal-routable after repeatable outcome or positive no-start history");
            claim!.LedgerStage.Should().Be(BackgroundJobLedgerStage.Normal);
        }
        normal.Run.Close();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Known_retry_lost_commit_probe_distinguishes_exact_rollback_routing_and_a_new_admission(
        bool recovered, bool committed)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await ExternalRetryJobAsync(database);
        await using var host = RecoveryHost(database, preparing: true);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var normal = await RetainedSelectionAttemptAsync(database, host.Services, job, 1, store, runtime);
        var identity = await KnownRetryFixtureAsync(database, host.Services, normal, store, runtime, recovered, noStart: false);
        var before = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
        var immutable = await RetainedSelectionFactsAsync(database, normal);
        await using var proxy = new CommitResponseDroppingProxy(database.OutboxConnectionString, dropCommitRequest: !committed);
        await using var brokenSource = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(proxy.ConnectionString));
        var broken = new PostgresBackgroundJobControlStore(brokenSource, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var changed = await broken.RetryKnownExternalAsync(identity, recovered
            ? BackgroundJobExternalRetryMode.RecoveryKnown : BackgroundJobExternalRetryMode.OriginalCompleted, CancellationToken.None);
        changed.CommitAcknowledged.Should().BeFalse();
        changed.CommitUncertain.Should().BeTrue();
        changed.CleanupConfirmed.Should().BeTrue();
        changed.Value.Should().BeNull();
        proxy.DroppedCommitRequests.Should().Be(committed ? 0 : 1);
        proxy.DroppedCommitResponses.Should().Be(committed ? 1 : 0);
        var descriptor = changed.Probes.Should().ContainSingle().Subject;
        var proof = await store.ProbeCommitAsync(descriptor);
        proof.ThrowIfFailed();
        proof.CleanupConfirmed.Should().BeTrue();
        proof.Value!.Disposition.Should().Be(committed
            ? BackgroundJobProbeDisposition.ControlCommitted : BackgroundJobProbeDisposition.RolledBack);
        proof.CommitAcknowledged.Should().BeFalse("historical reads roll back and never rewrite the original physical ACK");
        changed.CommitAcknowledged.Should().BeFalse();
        var after = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
        (await RetainedSelectionFactsAsync(database, normal)).Should().Be(immutable);
        if (!committed)
        {
            after.Should().Be(before, "the private shortening and handoff must roll back with the refused COMMIT");
            // A changed real recovery token must not hit generic rollback fallback.
            await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
            await SchemaQueries.ExecuteAsync(dispatcher, null, "UPDATE background_jobs SET ownership_until=clock_timestamp() WHERE id=@id",
                ("id", job.Value));
            if (!recovered)
                await SchemaQueries.ExecuteAsync(dispatcher, null, "UPDATE background_jobs SET phase='reconciling',ownership_kind='recovery',ownership_token=@new,ownership_until=clock_timestamp()+interval '1 minute' WHERE id=@id",
                    ("id", job.Value), ("new", Guid.NewGuid()));
            else
                await SchemaQueries.ExecuteAsync(dispatcher, null, "UPDATE background_jobs SET ownership_token=@new,ownership_until=clock_timestamp()+interval '1 minute' WHERE id=@id",
                    ("id", job.Value), ("new", Guid.NewGuid()));
        }
        else
        {
            var wait = Stopwatch.StartNew();
            while (await RetryDatabaseClockAsync(database) < after.AvailableAfter)
            {
                if (wait.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Actual fixed retry schedule did not become due.");
                await Task.Delay(10);
            }
            var claim = await FixtureClaim.ClaimAsync(database, job);
            var routed = await store.ProbeCommitAsync(descriptor);
            routed.ThrowIfFailed();
            routed.Value!.Disposition.Should().Be(BackgroundJobProbeDisposition.ControlCommitted,
                "a same-cycle routing hint cannot erase the exact committed schedule");
            routed.Value.OwnershipToken.Should().Be(claim.Token);
            var later = await AdmitFixtureAsync(host.Services, database, claim);
            later.AttemptId.Should().NotBe(normal.AttemptId);
            later.NormalAttemptOrdinal.Should().Be(2);
            later.Stage.Should().BeNull("the canonical plan and logical operation survive a normal retry");
            later.Run.Close();
        }
        var successor = await store.ProbeCommitAsync(descriptor);
        successor.ThrowIfFailed();
        successor.Value!.Disposition.Should().Be(BackgroundJobProbeDisposition.ChangedOwner);
        successor.Value.RecoveryReceipt.Should().BeNull();
        successor.Value.CurrentRecoveryAuthority.Should().BeFalse();
        normal.Run.Close();
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("intent_only")]
    [InlineData("succeeded")]
    [InlineData("permanent")]
    [InlineData("not_repeatable")]
    [InlineData("expired")]
    public async Task Known_retry_refuses_unresolved_nonrepeatable_or_expired_original_facts_without_a_handoff(string mode)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await ExternalRetryJobAsync(database);
        await using var host = RecoveryHost(database, preparing: true);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var normal = await RetainedSelectionAttemptAsync(database, host.Services, job, 1, store, runtime);
        var executor = host.Services.GetRequiredService<JobExecutor>();
        var read = await executor.ReadExternalStateAsync(normal, CancellationToken.None);
        if (mode != "absent")
        {
            (await executor.RecordExternalArmAsync(normal, read.Handle!, normal.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm),
                CancellationToken.None)).CommitAcknowledged.Should().BeTrue();
            if (mode != "intent_only")
            {
                var snapshot = read.Handle!.Snapshot;
                var command = new RecordBackgroundJobOutcomeCommand(normal, normal.Run.OpenBoundary(BackgroundJobCheckpointKind.Outcome),
                    snapshot.PlanFingerprint!, snapshot.OperationId!.Value, "{\"ArtifactId\":\"" + Guid.NewGuid().ToString("D") + "\"}",
                    mode == "succeeded" ? BackgroundJobExternalState.Succeeded : BackgroundJobExternalState.Failed,
                    mode == "succeeded" ? null : mode == "permanent" ? BackgroundJobExternalFailurePolicy.Permanent
                        : mode == "not_repeatable" ? BackgroundJobExternalFailurePolicy.TransientNotRepeatable
                        : BackgroundJobExternalFailurePolicy.TransientRepeatable,
                    Guid.NewGuid(), BackgroundJobQuiescenceKind.ProviderFenced);
                (await SendCheckpointAsync(host.Services, normal, command, command.Gate)).IsSuccess.Should().BeTrue();
            }
        }
        if (mode == "expired")
        {
            await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
            await SchemaQueries.ExecuteAsync(dispatcher, null, "UPDATE background_jobs SET ownership_until=clock_timestamp() WHERE id=@id", ("id", job.Value));
        }
        var before = await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job);
        var facts = await RetainedSelectionFactsAsync(database, normal);
        var result = await store.RetryKnownExternalAsync(BackgroundJobExecutionIdentity.FromReceipt(normal),
            BackgroundJobExternalRetryMode.OriginalCompleted, CancellationToken.None);
        result.ThrowIfFailed();
        result.CommitAcknowledged.Should().BeFalse();
        result.CleanupConfirmed.Should().BeTrue();
        result.Value.Should().BeNull();
        result.Probes.Should().BeEmpty();
        (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job)).Should().Be(before);
        (await RetainedSelectionFactsAsync(database, normal)).Should().Be(facts);
        var sample = await BackgroundJobObservationQueries.ReadAsync(source, Fixture.Registry);
        BackgroundJobObservationQueries.AssertQueue(sample, "external", 1,
            mode is "succeeded" or "permanent" or "not_repeatable" ? 1 : 0);
        var reservations = sample.Points.Where(point => point.Family == BackgroundJobObservationFamily.ReservedCapacity);
        if (mode is "absent" or "intent_only")
            reservations.Should().ContainSingle().Which.Should().Be(new BackgroundJobObservationPoint(
                BackgroundJobObservationFamily.ReservedCapacity, BackgroundJobRuntimeFixture.CapacityGroup, 0, 1, 0, 0));
        else reservations.Should().BeEmpty();
        var routing = new PostgresBackgroundJobRoutingStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var claim = await routing.TryClaimAsync(job, await RecoveryGenerationAsync(database, job));
        if (mode is "succeeded" or "permanent" or "not_repeatable")
        {
            claim.Should().NotBeNull("these outcomes selected Finalize, independently of normal-retry refusal");
            claim!.LedgerStage.Should().Be(BackgroundJobLedgerStage.Finalize);
        }
        else claim.Should().BeNull();
        normal.Run.Close();
    }

    private static Task<BackgroundJobId> ExternalRetryJobAsync(DisposableSchemaDatabase database) =>
        BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString, BackgroundJobRuntimeFixture.ExternalKey,
            capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external");

    [Fact]
    public async Task Known_retry_refused_final_update_rolls_back_the_private_live_owner_handoff()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await ExternalRetryJobAsync(database);
        await using var host = RecoveryHost(database, preparing: true);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var normal = await RetainedSelectionAttemptAsync(database, host.Services, job, 1, store, runtime);
        var identity = await KnownRetryFixtureAsync(database, host.Services, normal, store, runtime, false, false);
        var before = await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job);
        await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
            await SchemaQueries.ExecuteAsync(owner, null, """
                ALTER TABLE background_jobs ADD CONSTRAINT reject_known_retry_pending
                CHECK (NOT (profile='external' AND phase='pending' AND normal_attempts>0))
                """);
        var result = await store.RetryKnownExternalAsync(identity, BackgroundJobExternalRetryMode.OriginalCompleted, CancellationToken.None);
        result.Failed.Should().BeTrue();
        Action failure = result.ThrowIfFailed;
        failure.Should().Throw<PostgresException>().Which.ConstraintName.Should().Be("reject_known_retry_pending");
        result.KnownRollback.Should().BeTrue();
        result.CommitUncertain.Should().BeFalse();
        result.CleanupConfirmed.Should().BeTrue();
        result.Probes.Should().BeEmpty();
        (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job)).Should().Be(before,
            "the final injected storage refusal cannot leave a shortened lease or a private recovery token");
        normal.Run.Close();
    }

    [Fact]
    public async Task Known_retry_fresh_recovery_can_schedule_positive_no_plan_no_start_after_the_original_deadline()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await ExternalRetryJobAsync(database);
        await using var host = RecoveryHost(database);
        var normal = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        await ParkExpiredExternalAsync(database, normal);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var acquired = await store.AcquireExternalRecoveryAsync(job, await RecoveryGenerationAsync(database, job), normal.Envelope.Queue, CancellationToken.None);
        acquired.ThrowIfFailed();
        acquired.Value!.Deadline.Should().BeBefore(acquired.Value.DbNow);
        var recovery = BindRecovery(acquired, runtime);
        var executor = host.Services.GetRequiredService<JobExecutor>();
        var read = await executor.ReadExternalStateAsync(recovery, CancellationToken.None);
        read.Handle!.Snapshot.PlanJson.Should().BeNull();
        (await executor.RecordExternalNoStartAsync(recovery, read.Handle,
            recovery.Run.OpenBoundary(BackgroundJobCheckpointKind.NoStart), CancellationToken.None)).CommitAcknowledged.Should().BeTrue();
        var result = await store.RetryKnownExternalAsync(BackgroundJobExecutionIdentity.FromRecovery(acquired.Value),
            BackgroundJobExternalRetryMode.RecoveryKnown, CancellationToken.None);
        result.ThrowIfFailed();
        result.CommitAcknowledged.Should().BeTrue();
        result.CleanupConfirmed.Should().BeTrue();
        result.Value!.Phase.Should().Be("pending");
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
        recovery.Run.Close();
    }

    private static async Task<BackgroundJobExecutionIdentity> KnownRetryFixtureAsync(DisposableSchemaDatabase database,
        IServiceProvider services, ConfirmedStartReceipt normal, PostgresBackgroundJobControlStore store,
        BackgroundJobRuntimeGate runtime, bool recovered, bool noStart)
    {
        var executor = services.GetRequiredService<JobExecutor>();
        var read = await executor.ReadExternalStateAsync(normal, CancellationToken.None);
        if (!noStart)
            (await executor.RecordExternalArmAsync(normal, read.Handle!, normal.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm),
                CancellationToken.None)).CommitAcknowledged.Should().BeTrue();
        BackgroundJobExternalRecoveryBinding? recovery = null;
        if (recovered)
        {
            await ExpireSelectionLeaseAsync(database, normal);
            var acquisition = await store.AcquireExternalRecoveryAsync(normal.Envelope.JobId,
                await RecoveryGenerationAsync(database, normal.Envelope.JobId), normal.Envelope.Queue, CancellationToken.None);
            acquisition.ThrowIfFailed();
            recovery = BindRecovery(acquisition, runtime);
            read = await executor.ReadExternalStateAsync(recovery, CancellationToken.None);
        }
        JobExternalCheckpointResult written;
        if (noStart)
            written = await executor.RecordExternalNoStartAsync(recovery!, read.Handle!,
                recovery!.Run.OpenBoundary(BackgroundJobCheckpointKind.NoStart), CancellationToken.None);
        else
        {
            // Explicit storage/writer fixture evidence; this test makes no provider-causality claim.
            BackgroundJobRetainedExternalObservation observation;
            using (BackgroundJobExecutionAdmission.EnterExternal())
                observation = BackgroundJobRetainedExternalObservation.Capture(new(normal.Envelope.JobId, normal.AttemptId,
                    read.Handle!.Snapshot.OperationId!.Value, normal.Deadline), read.Handle.Snapshot.PlanFingerprint!,
                    BackgroundJobExternalResult.Failed(new WriterOutcome(Guid.NewGuid()),
                        new(Guid.NewGuid(), BackgroundJobQuiescenceKind.ProviderFenced), BackgroundJobExternalFailurePolicy.TransientRepeatable));
            var gate = (recovery?.Run ?? normal.Run).OpenBoundary(BackgroundJobCheckpointKind.Outcome);
            written = recovery is null
                ? await executor.RecordExternalOutcomeAsync(normal, read.Handle!, gate, observation, CancellationToken.None)
                : await executor.RecordExternalOutcomeAsync(recovery, read.Handle!, gate, observation, CancellationToken.None);
        }
        written.CommitAcknowledged.Should().BeTrue();
        written.CleanupFailed.Should().BeFalse();
        var identity = recovery is null ? BackgroundJobExecutionIdentity.FromReceipt(normal)
            : BackgroundJobExecutionIdentity.FromRecovery(recovery.Receipt);
        recovery?.Run.Close();
        return identity;
    }

    private static async Task<DateTimeOffset> RetryDatabaseClockAsync(DisposableSchemaDatabase database)
    {
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await using var command = new NpgsqlCommand("SELECT clock_timestamp()", (NpgsqlConnection)dispatcher) { CommandTimeout = 5 };
        return new DateTimeOffset((DateTime)(await command.ExecuteScalarAsync())!);
    }
}
