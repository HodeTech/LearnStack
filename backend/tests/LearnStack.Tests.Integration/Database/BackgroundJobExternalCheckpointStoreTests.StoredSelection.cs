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
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Retained_marker_cannot_confirm_a_rolled_back_selection_and_lost_ack_confirms_the_exact_selection(
        bool noStart, bool committed)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external");
        await using var host = RecoveryHost(database, preparing: true);
        var executor = host.Services.GetRequiredService<JobExecutor>();
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var original = await RetainedSelectionAttemptAsync(database, host.Services, job, noStart ? 6 : 1, store, runtime);
        var before = (await executor.ReadExternalStateAsync(original, CancellationToken.None)).Handle!.Require(original);
        if (!noStart)
            (await executor.RecordExternalArmAsync(original,
                (await executor.ReadExternalStateAsync(original, CancellationToken.None)).Handle!,
                original.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm), CancellationToken.None)).CommitAcknowledged.Should().BeTrue();
        await ExpireSelectionLeaseAsync(database, original);
        var acquired = await store.AcquireExternalRecoveryAsync(job, await RecoveryGenerationAsync(database, job),
            original.Envelope.Queue, CancellationToken.None);
        acquired.ThrowIfFailed();
        var recovery = BindRecovery(acquired, runtime);
        // Legal retained data without its selection, written as learnstack_app.
        // No actual provider execution/causality is claimed by this fixture.
        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            if (noStart)
                await SchemaQueries.ExecuteAsync(app, transaction, """
                    UPDATE background_job_attempts SET no_start_at=clock_timestamp() WHERE id=@attempt;
                    UPDATE background_job_reservations SET released_at=clock_timestamp(),release_reason='never_started'
                        WHERE attempt_id=@attempt AND released_at IS NULL
                    """, ("attempt", original.AttemptId.Value));
            else
                await SchemaQueries.ExecuteAsync(app, transaction, """
                    UPDATE background_job_attempts SET outcome=@outcome,outcome_kind='succeeded',outcome_at=clock_timestamp(),
                        quiescence_id=@evidence,quiescence_kind='provider_fenced',quiescent_at=clock_timestamp()
                        WHERE id=@attempt;
                    UPDATE background_job_reservations SET released_at=clock_timestamp(),release_reason='quiescent'
                        WHERE attempt_id=@attempt AND released_at IS NULL
                    """, ("attempt", original.AttemptId.Value), ("outcome", "{\"ArtifactId\":\"" + Guid.NewGuid().ToString("D") + "\"}"),
                    ("evidence", Guid.NewGuid()));
            await transaction.CommitAsync();
        }
        var retained = await ReadStoredSelectionAsync(database, original);
        retained.SelectedNormalAttemptId.Should().BeNull();
        retained.NoStartRecorded.Should().Be(noStart);
        retained.OutcomeState.Should().Be(noStart ? null : BackgroundJobExternalState.Succeeded);
        var originalFacts = await RetainedSelectionFactsAsync(database, original);
        var read = await executor.ReadExternalStateAsync(recovery, CancellationToken.None);
        read.Handle.Should().NotBeNull();
        await using var proxy = new CommitResponseDroppingProxy(database.AppConnectionString, dropCommitRequest: !committed);
        await using var brokenHost = RecoveryHost(database, preparing: true, appConnectionString: proxy.ConnectionString);
        var broken = brokenHost.Services.GetRequiredService<JobExecutor>();
        var gate = recovery.Run.OpenBoundary(noStart ? BackgroundJobCheckpointKind.NoStart : BackgroundJobCheckpointKind.Outcome);
        var result = await (noStart
            ? broken.RecordExternalNoStartAsync(recovery, read.Handle!, gate, CancellationToken.None)
            : broken.SelectStoredExternalOutcomeAsync(recovery, read.Handle!, gate, CancellationToken.None));
        result.CleanupFailed.Should().BeFalse();
        result.CommitUncertain.Should().BeTrue();
        result.Outcome!.IsNoStart.Should().Be(noStart);
        result.Outcome.IsStoredCompleted.Should().Be(!noStart);
        if (!noStart)
        {
            result.Outcome.PlanFingerprint.Should().Be(before.PlanFingerprint);
            result.Outcome.OperationId.Should().Be(before.OperationId!.Value);
            Action suppliedReport = () => _ = result.Outcome.OutcomeJson;
            suppliedReport.Should().Throw<InvalidOperationException>("stored selection must not invent provider report bytes");
        }
        proxy.DroppedCommitRequests.Should().Be(committed ? 0 : 1);
        proxy.DroppedCommitResponses.Should().Be(committed ? 1 : 0);
        var origin = noStart ? BackgroundJobCheckpointHistoryOrigin.ForNoStart(result)
            : BackgroundJobCheckpointHistoryOrigin.ForOutcome(result);
        var proof = await store.ProbeExternalCheckpointAsync(origin, CancellationToken.None);
        proof.ThrowIfFailed();
        proof.CleanupConfirmed.Should().BeTrue();
        proof.Value.Should().NotBeNull();
        proof.Value!.Disposition.Should().Be(committed ? BackgroundJobCheckpointHistoryDisposition.Committed
            : BackgroundJobCheckpointHistoryDisposition.Unresolved,
            "a retained marker without selection cannot prove that the new selection-only transaction committed");
        gate.CommitConfirmed.Should().Be(committed);
        gate.CommitAcknowledged.Should().BeFalse();
        gate.KnownRollback.Should().BeFalse();
        var selected = await ReadStoredSelectionAsync(database, original);
        selected.OutcomeJson.Should().Be(retained.OutcomeJson);
        selected.OutcomeState.Should().Be(retained.OutcomeState);
        selected.FailurePolicy.Should().Be(retained.FailurePolicy);
        selected.NoStartRecorded.Should().Be(retained.NoStartRecorded);
        (await RetainedSelectionFactsAsync(database, original)).Should().Be(originalFacts,
            "selection does not rewrite outcome, quiescence, no-start or the original reservation release");
        (await RecoveryGenerationAsync(database, job)).Should().Be(acquired.Value!.WakeGeneration + (committed ? 1 : 0));
        selected.SelectedNormalAttemptId.Should().Be(committed ? original.AttemptId : null);
        selected.FinalizationStatus.Should().Be(committed
            ? noStart ? BackgroundJobFinalizationStatus.Failed : BackgroundJobFinalizationStatus.Succeeded : null);
        selected.FinalizationReason.Should().Be(committed
            ? noStart ? BackgroundJobFinalizationReason.NormalAttemptsExhausted : BackgroundJobFinalizationReason.None : null);
        if (committed)
        {
            proof.Value.Confirmation!.Matches(result.Outcome).Should().BeTrue();
            BackgroundJobObservationQueries.AssertQueue(await BackgroundJobObservationQueries.ReadAsync(source, Fixture.Registry),
                "external", 1, 1);
            await using var finalHost = Fixture.Host(database, new BackgroundJobFixtureProbe());
            var claim = await FixtureClaim.ClaimAsync(database, job);
            var final = await finalHost.Services.GetRequiredService<JobExecutor>().ExecuteAsync(claim, BackgroundJobTestRun.Create());
            final.Receipt.Should().NotBeNull();
            final.Receipt!.AttemptId.Should().NotBe(original.AttemptId);
            final.Receipt.SelectedNormalAttemptId.Should().Be(original.AttemptId);
            BackgroundJobObservationQueries.AssertQueue(await BackgroundJobObservationQueries.ReadAsync(source, Fixture.Registry),
                "external", 1, 0);
            var advanced = await store.ProbeExternalCheckpointAsync(origin, CancellationToken.None);
            advanced.ThrowIfFailed();
            advanced.Value!.Disposition.Should().Be(BackgroundJobCheckpointHistoryDisposition.Committed,
                "the immutable selected normal attempt survives a distinct counted finalize admission");
            advanced.Value.Facts.CurrentAttemptId.Should().Be(final.Receipt.AttemptId.Value);
            advanced.Value.Confirmation!.Matches(result.Outcome).Should().BeTrue();
            final.Receipt.Run.Close();
        }
        recovery.Run.Close();
        recovery.SourceOwner.CleanupConfirmed.Should().BeTrue();
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("no_start")]
    [InlineData("repeatable")]
    [InlineData("cancelled_repeatable")]
    [InlineData("permanent")]
    [InlineData("not_repeatable")]
    [InlineData("cancelled_not_repeatable")]
    public async Task Stored_selection_uses_fresh_policy_and_refuses_missing_or_early_repeatable_facts(string mode)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external");
        await using var host = RecoveryHost(database, preparing: true);
        var executor = host.Services.GetRequiredService<JobExecutor>();
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var original = await RetainedSelectionAttemptAsync(database, host.Services, job, 1, store, runtime);
        var prepared = await executor.ReadExternalStateAsync(original, CancellationToken.None);
        var plan = prepared.Handle!.Require(original);
        if (mode is not ("absent" or "no_start"))
            (await executor.RecordExternalArmAsync(original, prepared.Handle,
                original.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm), CancellationToken.None)).CommitAcknowledged.Should().BeTrue();
        await ExpireSelectionLeaseAsync(database, original);
        var acquired = await store.AcquireExternalRecoveryAsync(job, await RecoveryGenerationAsync(database, job),
            original.Envelope.Queue, CancellationToken.None);
        acquired.ThrowIfFailed();
        var recovery = BindRecovery(acquired, runtime);
        if (mode == "no_start")
        {
            var noStartRead = await executor.ReadExternalStateAsync(recovery, CancellationToken.None);
            (await executor.RecordExternalNoStartAsync(recovery, noStartRead.Handle!,
                recovery.Run.OpenBoundary(BackgroundJobCheckpointKind.NoStart), CancellationToken.None)).CommitAcknowledged.Should().BeTrue();
        }
        else if (mode != "absent")
        {
            await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
            await using var transaction = await app.BeginTransactionAsync();
            await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            await SchemaQueries.ExecuteAsync(app, transaction, """
                UPDATE background_job_attempts SET outcome=@outcome,outcome_kind=@state,failure_policy=@policy,
                    outcome_at=clock_timestamp(),quiescence_id=@evidence,quiescence_kind='provider_fenced',quiescent_at=clock_timestamp()
                    WHERE id=@attempt;
                UPDATE background_job_reservations SET released_at=clock_timestamp(),release_reason='quiescent'
                    WHERE attempt_id=@attempt AND released_at IS NULL
                """, ("attempt", original.AttemptId.Value), ("outcome", "{\"ArtifactId\":\"" + Guid.NewGuid().ToString("D") + "\"}"),
                ("state", mode.StartsWith("cancelled_", StringComparison.Ordinal) ? "cancelled" : "failed"),
                ("policy", mode == "permanent" ? "permanent" : mode.EndsWith("not_repeatable", StringComparison.Ordinal)
                    ? "transient_not_repeatable" : "transient_repeatable"), ("evidence", Guid.NewGuid()));
            await transaction.CommitAsync();
        }
        var read = await executor.ReadExternalStateAsync(recovery, CancellationToken.None);
        read.Handle.Should().NotBeNull();
        var gate = recovery.Run.OpenBoundary(BackgroundJobCheckpointKind.Outcome);
        var immutable = await RetainedSelectionFactsAsync(database, original);
        var accepted = mode is "permanent" or "not_repeatable" or "cancelled_not_repeatable";
        if (accepted)
        {
            var selected = await executor.SelectStoredExternalOutcomeAsync(recovery, read.Handle!, gate, CancellationToken.None);
            selected.CommitAcknowledged.Should().BeTrue();
            selected.CleanupFailed.Should().BeFalse();
        }
        else
        {
            var rejectDriver = () => executor.SelectStoredExternalOutcomeAsync(recovery, read.Handle!, gate, CancellationToken.None);
            await rejectDriver.Should().ThrowAsync<InvalidOperationException>();
            gate.CommitStarted.Should().BeFalse();
            // The real handler must also refuse a direct framework command;
            // the driver predicate is not its only policy protection.
            var command = RecordBackgroundJobOutcomeCommand.ForStoredCompleted(recovery, gate,
                plan.PlanFingerprint!, plan.OperationId!.Value);
            var rejectHandler = () => SendRecoveryAsync(host.Services, recovery, command, gate);
            await rejectHandler.Should().ThrowAsync<InvalidOperationException>();
            command.CommitAcknowledged.Should().BeFalse();
        }
        var after = await ReadStoredSelectionAsync(database, original);
        after.SelectedNormalAttemptId.Should().Be(accepted ? original.AttemptId : null);
        after.FinalizationReason.Should().Be(accepted ? mode.StartsWith("cancelled_", StringComparison.Ordinal)
            ? BackgroundJobFinalizationReason.ExternalCancelled : BackgroundJobFinalizationReason.ExternalFailed : null);
        (await RetainedSelectionFactsAsync(database, original)).Should().Be(immutable);
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
        recovery.SourceOwner.CleanupConfirmed.Should().BeTrue();
        recovery.Run.Close();
    }

    private static async Task<string> RetainedSelectionFactsAsync(DisposableSchemaDatabase database, ConfirmedStartReceipt normal)
    {
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        await using var command = new NpgsqlCommand("""
            SELECT jsonb_build_array(a.outcome,a.outcome_kind,a.failure_policy,a.outcome_at,a.quiescence_id,
                a.quiescence_kind,a.quiescent_at,a.no_start_at,r.released_at,r.release_reason)::text
            FROM background_job_attempts a JOIN background_job_reservations r ON r.attempt_id=a.id WHERE a.id=@attempt
            """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("attempt", normal.AttemptId.Value);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<ConfirmedStartReceipt> RetainedSelectionAttemptAsync(DisposableSchemaDatabase database,
        IServiceProvider services, BackgroundJobId job, int target, PostgresBackgroundJobControlStore store,
        BackgroundJobRuntimeGate runtime)
    {
        for (var ordinal = 1; ordinal <= target; ordinal++)
        {
            var normal = await AdmitFixtureAsync(services, database, await FixtureClaim.ClaimAsync(database, job));
            normal.NormalAttemptOrdinal.Should().Be(ordinal);
            var executor = services.GetRequiredService<JobExecutor>();
            if (ordinal == 1)
                (await executor.ExecuteBusinessAsync(normal, normal.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare)))
                    .CommitAcknowledged.Should().BeTrue();
            if (ordinal == target) return normal;
            await ExpireSelectionLeaseAsync(database, normal);
            var acquired = await store.AcquireExternalRecoveryAsync(job, await RecoveryGenerationAsync(database, job),
                normal.Envelope.Queue, CancellationToken.None);
            acquired.ThrowIfFailed();
            var recovery = BindRecovery(acquired, runtime);
            var read = await executor.ReadExternalStateAsync(recovery, CancellationToken.None);
            (await executor.RecordExternalNoStartAsync(recovery, read.Handle!,
                recovery.Run.OpenBoundary(BackgroundJobCheckpointKind.NoStart), CancellationToken.None)).CommitAcknowledged.Should().BeTrue();
            recovery.Run.Close();
            await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
            await SchemaQueries.ExecuteAsync(dispatcher, null, """
                UPDATE background_jobs SET phase='pending',ownership_kind=NULL,ownership_token=NULL,ownership_until=NULL
                WHERE id=@id AND phase='reconciling'
                """, ("id", job.Value));
        }
        throw new InvalidOperationException("No original normal attempt was admitted.");
    }
}
