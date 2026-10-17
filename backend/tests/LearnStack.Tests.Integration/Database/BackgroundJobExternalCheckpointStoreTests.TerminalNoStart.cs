using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobExternalCheckpointStoreTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(true, 6)]
    public async Task Terminal_no_start_uses_actual_fresh_purpose_and_app_history_without_a_contract_or_provider(bool emptyDefinitions, int ordinal)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var originalHost = NoPlanRefusalHost(database, admissionWindow: TimeSpan.FromSeconds(2));
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var normal = await NoPlanOrdinalAsync(database, originalHost.Services, ordinal, store, runtime);
        var failed = await originalHost.Services.GetRequiredService<JobExecutor>().ExecuteBusinessAsync(normal,
            normal.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare));
        failed.KnownRollback.Should().BeTrue();
        failed.Decision.TerminalReason.Should().Be(BackgroundJobTerminalReason.PermanentFailure);
        var requested = await store.RequestNoPlanExternalTerminalAsync(BackgroundJobExecutionIdentity.FromReceipt(normal),
            BackgroundJobTerminalReason.PermanentFailure, CancellationToken.None);
        requested.ThrowIfFailed();
        requested.CommitAcknowledged.Should().BeTrue();
        normal.Run.Close();
        using (var expiry = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while (await RetryDatabaseClockAsync(database) < normal.Deadline) await Task.Delay(10, expiry.Token);
        await using var host = NoPlanRefusalHost(database, emptyDefinitions);
        var registry = host.Services.GetRequiredService<BackgroundJobDefinitionRegistry>();
        var freshStore = new PostgresBackgroundJobControlStore(source, registry, new(), new SystemGuidFactory(), runtime);
        var acquired = await freshStore.AcquireExternalTerminalNoStartAsync([], CancellationToken.None);
        acquired.ThrowIfFailed();
        acquired.CommitAcknowledged.Should().BeTrue();
        acquired.CleanupConfirmed.Should().BeTrue();
        acquired.Value!.Purpose.Should().Be(BackgroundJobExternalRecoveryPurpose.TerminalNoStart);
        acquired.Value.TerminalReason.Should().Be("permanent_failure");
        acquired.Value.Deadline.Should().Be(normal.Deadline).And.BeBefore(acquired.Value.DbNow);
        acquired.Value.AdmissionToken.Should().Be(normal.Claim.Token);
        acquired.Value.RecoveryToken.Should().NotBe(normal.Claim.Token);
        var confirmation = await freshStore.ProbeExternalRecoveryAsync(acquired, acquired.Probes.Single(), CancellationToken.None);
        confirmation.ThrowIfFailed();
        confirmation.Value.Should().BeSameAs(acquired.Value);
        var recovery = BindRecovery(acquired, runtime);
        recovery.Run.TryIssueExternalReconcile(recovery).Should().BeFalse();
        recovery.Run.TryOpenBoundary(BackgroundJobCheckpointKind.Outcome).Should().BeNull();
        Action ordinaryIdentity = () => BackgroundJobExecutionIdentity.FromRecovery(acquired.Value);
        ordinaryIdentity.Should().Throw<InvalidOperationException>();
        var parameterHook = Fixture.ParameterType.GetField("OnConstruction")!;
        var previous = parameterHook.GetValue(null);
        var constructions = 0;
        parameterHook.SetValue(null, (Action)(() =>
        {
            Interlocked.Increment(ref constructions);
            throw new InvalidOperationException("Terminal cleanup must not construct a module payload.");
        }));
        try
        {
            var executor = host.Services.GetRequiredService<JobExecutor>();
            var read = await executor.ReadExternalStateAsync(recovery, CancellationToken.None);
            read.CommitAcknowledged.Should().BeTrue();
            read.CleanupFailed.Should().BeFalse();
            read.Handle!.Snapshot.Envelope.Should().Be(normal.Envelope,
                "causal fields come from the actual app-role envelope even without a current definition");
            read.Handle.Snapshot.PlanJson.Should().BeNull();
            var provider = await executor.ReconcileExternalAsync(recovery, read.Handle, CancellationToken.None);
            provider.Status.Should().Be(JobExternalExecutionStatus.EntryRefused);
            provider.Observation.Should().BeNull();
            provider.CleanupFailed.Should().BeFalse();
            var gate = recovery.Run.OpenBoundary(BackgroundJobCheckpointKind.NoStart);
            Action ordinaryFactory = () => RecordBackgroundJobOutcomeCommand.ForNoStart(recovery, gate);
            ordinaryFactory.Should().Throw<ArgumentException>();
            var written = await executor.RecordExternalTerminalNoStartAsync(recovery, read.Handle, gate, CancellationToken.None);
            written.CommitAcknowledged.Should().BeTrue();
            written.CleanupFailed.Should().BeFalse();
            written.Outcome!.IsTerminalNoStart.Should().BeTrue();
            var history = await freshStore.ProbeExternalCheckpointAsync(BackgroundJobCheckpointHistoryOrigin.ForNoStart(written),
                CancellationToken.None);
            history.ThrowIfFailed();
            history.Value!.Disposition.Should().Be(BackgroundJobCheckpointHistoryDisposition.Committed);
            history.Value.Confirmation!.Matches(written.Outcome).Should().BeTrue();
            var requestHistory = await freshStore.ProbeCommitAsync(requested.Probes.Single());
            requestHistory.ThrowIfFailed();
            requestHistory.Value!.Disposition.Should().Be(BackgroundJobProbeDisposition.ControlCommitted);
            constructions.Should().Be(0);
            await AssertTerminalNoStartPairAsync(database, normal, acquired.Value.RecoveryToken);
            recovery.SourceOwner.CleanupConfirmed.Should().BeTrue();
        }
        finally
        {
            parameterHook.SetValue(null, previous);
            recovery.Run.Close();
            recovery.SourceOwner.CloseIssuance();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Terminal_no_start_lost_commit_confirms_only_its_actual_issued_pair_without_rewriting_ack(bool committed)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var host = NoPlanRefusalHost(database);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var normal = await NoPlanOrdinalAsync(database, host.Services, 1, store, runtime);
        var failed = await host.Services.GetRequiredService<JobExecutor>().ExecuteBusinessAsync(normal,
            normal.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare));
        failed.KnownRollback.Should().BeTrue();
        var request = await store.RequestNoPlanExternalTerminalAsync(BackgroundJobExecutionIdentity.FromReceipt(normal),
            BackgroundJobTerminalReason.PermanentFailure, CancellationToken.None);
        request.ThrowIfFailed();
        request.CommitAcknowledged.Should().BeTrue();
        normal.Run.Close();
        var acquired = await store.AcquireExternalTerminalNoStartAsync([], CancellationToken.None);
        acquired.ThrowIfFailed();
        var recovery = BindRecovery(acquired, runtime);
        var read = await host.Services.GetRequiredService<JobExecutor>().ReadExternalStateAsync(recovery, CancellationToken.None);
        read.CleanupFailed.Should().BeFalse();
        read.CommitAcknowledged.Should().BeTrue();
        var before = await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, normal.Envelope.JobId);
        var immutable = await RetainedSelectionFactsAsync(database, normal);
        await using var proxy = new CommitResponseDroppingProxy(database.AppConnectionString, dropCommitRequest: !committed);
        await using var brokenHost = NoPlanRefusalHost(database, appConnectionString: proxy.ConnectionString);
        var gate = recovery.Run.OpenBoundary(BackgroundJobCheckpointKind.NoStart);
        var actual = await brokenHost.Services.GetRequiredService<JobExecutor>().RecordExternalTerminalNoStartAsync(recovery,
            read.Handle!, gate, CancellationToken.None);
        actual.CommitAcknowledged.Should().BeFalse();
        actual.CommitStarted.Should().BeTrue();
        actual.KnownRollback.Should().BeFalse();
        actual.CleanupFailed.Should().BeFalse();
        proxy.DroppedCommitRequests.Should().Be(committed ? 0 : 1);
        proxy.DroppedCommitResponses.Should().Be(committed ? 1 : 0);
        var history = await store.ProbeExternalCheckpointAsync(BackgroundJobCheckpointHistoryOrigin.ForNoStart(actual), CancellationToken.None);
        history.ThrowIfFailed();
        history.Value!.Disposition.Should().Be(committed
            ? BackgroundJobCheckpointHistoryDisposition.Committed : BackgroundJobCheckpointHistoryDisposition.Absent);
        actual.CommitAcknowledged.Should().BeFalse();
        if (committed) await AssertTerminalNoStartPairAsync(database, normal, acquired.Value!.RecoveryToken);
        else
        {
            (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, normal.Envelope.JobId)).Should().Be(before,
                "request loss preserves the already acknowledged cleanup recovery owner");
            (await RetainedSelectionFactsAsync(database, normal)).Should().Be(immutable,
                "neither NoStart nor reservation release can survive a refused COMMIT");
        }
        recovery.Run.Close();
        recovery.SourceOwner.CloseIssuance();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Terminal_no_start_acquisition_lost_commit_never_publishes_a_rollback_or_changes_its_purpose(bool committed)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var host = NoPlanRefusalHost(database);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var normal = await NoPlanOrdinalAsync(database, host.Services, 1, store, runtime);
        var prepared = await host.Services.GetRequiredService<JobExecutor>().ExecuteBusinessAsync(normal,
            normal.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare));
        prepared.KnownRollback.Should().BeTrue();
        var requested = await store.RequestNoPlanExternalTerminalAsync(BackgroundJobExecutionIdentity.FromReceipt(normal),
            BackgroundJobTerminalReason.PermanentFailure, CancellationToken.None);
        requested.ThrowIfFailed();
        requested.CommitAcknowledged.Should().BeTrue();
        normal.Run.Close();
        await using var proxy = new CommitResponseDroppingProxy(database.OutboxConnectionString, dropCommitRequest: !committed);
        await using var brokenSource = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(proxy.ConnectionString));
        var broken = new PostgresBackgroundJobControlStore(brokenSource, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var acquisition = await broken.AcquireExternalTerminalNoStartAsync([], CancellationToken.None);
        acquisition.CommitAcknowledged.Should().BeFalse();
        acquisition.CommitUncertain.Should().BeTrue();
        acquisition.CleanupConfirmed.Should().BeTrue();
        acquisition.Value.Should().BeNull();
        proxy.DroppedCommitRequests.Should().Be(committed ? 0 : 1);
        proxy.DroppedCommitResponses.Should().Be(committed ? 1 : 0);
        var descriptor = acquisition.Probes.Should().ContainSingle().Subject;
        var confirmed = await store.ProbeExternalRecoveryAsync(acquisition, descriptor, CancellationToken.None);
        confirmed.ThrowIfFailed();
        confirmed.CleanupConfirmed.Should().BeTrue();
        if (committed)
        {
            confirmed.Value.Should().NotBeNull();
            confirmed.Value!.Purpose.Should().Be(BackgroundJobExternalRecoveryPurpose.TerminalNoStart);
            confirmed.Value.TerminalReason.Should().Be("permanent_failure");
            confirmed.Value.RecoveryToken.Should().Be(descriptor.RecoveryToken!.Value);
            var again = await store.ProbeExternalRecoveryAsync(acquisition, descriptor, CancellationToken.None);
            again.ThrowIfFailed();
            again.Value.Should().BeSameAs(confirmed.Value);
        }
        else
        {
            confirmed.Value.Should().BeNull();
            acquisition.Value.Should().BeNull();
            await AssertNoPlanRequestAsync(database, normal, "permanent_failure");
        }
        acquisition.CommitAcknowledged.Should().BeFalse();
    }

    [Fact]
    public async Task Terminal_no_start_release_refusal_rolls_back_the_preceding_marker()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var host = NoPlanRefusalHost(database);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var normal = await NoPlanOrdinalAsync(database, host.Services, 1, store, runtime);
        var prepared = await host.Services.GetRequiredService<JobExecutor>().ExecuteBusinessAsync(normal,
            normal.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare));
        prepared.KnownRollback.Should().BeTrue();
        var requested = await store.RequestNoPlanExternalTerminalAsync(BackgroundJobExecutionIdentity.FromReceipt(normal),
            BackgroundJobTerminalReason.PermanentFailure, CancellationToken.None);
        requested.ThrowIfFailed();
        requested.CommitAcknowledged.Should().BeTrue();
        normal.Run.Close();
        var acquired = await store.AcquireExternalTerminalNoStartAsync([], CancellationToken.None);
        acquired.ThrowIfFailed();
        var recovery = BindRecovery(acquired, runtime);
        var executor = host.Services.GetRequiredService<JobExecutor>();
        var read = await executor.ReadExternalStateAsync(recovery, CancellationToken.None);
        read.CommitAcknowledged.Should().BeTrue();
        var before = await RetainedSelectionFactsAsync(database, normal);
        await using (var admin = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
            await SchemaQueries.ExecuteAsync(admin, null, """
                ALTER TABLE background_job_reservations ADD CONSTRAINT ck_test_refuse_terminal_release
                    CHECK (released_at IS NULL)
                """);
        var written = await executor.RecordExternalTerminalNoStartAsync(recovery, read.Handle!,
            recovery.Run.OpenBoundary(BackgroundJobCheckpointKind.NoStart), CancellationToken.None);
        written.CommitAcknowledged.Should().BeFalse();
        written.KnownRollback.Should().BeTrue();
        written.CleanupFailed.Should().BeFalse();
        (await RetainedSelectionFactsAsync(database, normal)).Should().Be(before,
            "the real reservation CHECK refuses after the marker UPDATE in the same transaction");
        recovery.Run.Close();
        recovery.SourceOwner.CloseIssuance();
    }

    private static async Task AssertTerminalNoStartPairAsync(DisposableSchemaDatabase database,
        ConfirmedStartReceipt normal, Guid token, string reason = "permanent_failure")
    {
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, normal.Envelope.TenantId.Value, normal.Envelope.OrganizationId?.Value);
        await using var query = new NpgsqlCommand("""
            SELECT j.phase,j.terminal_reason,j.normal_attempts,j.finalize_attempts,j.ownership_token,j.ownership_kind,
                a.no_start_at IS NOT NULL,r.released_at>=a.no_start_at,r.release_reason,
                j.plan IS NULL AND a.plan_fingerprint IS NULL AND a.operation_id IS NULL AND a.execute_intent_at IS NULL
                    AND a.outcome IS NULL AND a.quiescence_id IS NULL AND j.finalization_status IS NULL AND j.dead_lettered_at IS NULL,
                (SELECT count(*) FROM background_job_attempts all_attempts WHERE all_attempts.job_id=j.id),
                (SELECT count(*) FROM audit_log audit WHERE audit.operation='platform.job.dead_letter' AND audit.metadata->>'job_id'=j.id::text)
            FROM background_jobs j JOIN background_job_attempts a ON a.id=j.current_attempt_id
                JOIN background_job_reservations r ON r.attempt_id=a.id WHERE j.id=@id
            """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction) { CommandTimeout = 5 };
        query.Parameters.AddWithValue("id", normal.Envelope.JobId.Value);
        await using var row = await query.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        row.GetString(0).Should().Be("terminal_pending");
        row.GetString(1).Should().Be(reason);
        row.GetInt32(2).Should().Be(normal.NormalAttemptOrdinal);
        row.GetInt32(3).Should().Be(0);
        row.GetGuid(4).Should().Be(token);
        row.GetString(5).Should().Be("recovery");
        row.GetBoolean(6).Should().BeTrue();
        row.GetBoolean(7).Should().BeTrue();
        row.GetString(8).Should().Be("never_started");
        row.GetBoolean(9).Should().BeTrue();
        row.GetInt64(10).Should().Be(normal.NormalAttemptOrdinal);
        row.GetInt64(11).Should().Be(0);
    }
}
