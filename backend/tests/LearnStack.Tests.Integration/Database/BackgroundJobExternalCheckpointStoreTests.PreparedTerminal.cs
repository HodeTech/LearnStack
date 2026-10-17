using System.Text.Json;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Results;
using MediatR;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobExternalCheckpointStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prepared_terminal_refusal_preserves_selected_success_and_commits_incomplete_with_one_real_MUST(bool auditFault)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var host = PreparedTerminalHost(database);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var control = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var terminal = new PostgresBackgroundJobTerminalStore(source, host.Services.GetRequiredService<IServiceScopeFactory>(),
            new(), new SystemGuidFactory(), runtime);
        var fixture = await PreparedTerminalRequestAsync(database, host.Services, control);
        var before = await ReadPreparedTerminalAsync(database, fixture);
        before.Completed.Should().BeFalse();
        before.Incomplete.Should().BeFalse();
        before.AuditCount.Should().Be(0);
        var acquired = await terminal.TryClaimTerminalAsync([]);
        acquired.ThrowIfFailed();
        acquired.CommitAcknowledged.Should().BeTrue();
        acquired.CleanupConfirmed.Should().BeTrue();
        acquired.Value.Should().NotBeNull();
        var claim = acquired.Value!;
        AssertPreparedTerminalClaim(claim, fixture);
        var claimed = await control.ProbeCommitAsync(acquired.Probes.Single());
        claimed.ThrowIfFailed();
        claimed.CleanupConfirmed.Should().BeTrue();
        claimed.Value!.Disposition.Should().Be(BackgroundJobProbeDisposition.ControlCommitted);
        var duplicateClaim = await terminal.TryClaimTerminalAsync([]);
        duplicateClaim.ThrowIfFailed();
        duplicateClaim.Value.Should().BeNull("the original terminal acquisition still owns its live lease");

        if (auditFault)
        {
            await using var migration = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
            await SchemaQueries.ExecuteAsync(migration, null, "REVOKE INSERT ON audit_log FROM learnstack_outbox_admin");
            try
            {
                var failed = await terminal.TryTerminalizeAsync(claim, fixture.Finalizer.Envelope.Traceparent);
                failed.Failed.Should().BeTrue();
                failed.KnownRollback.Should().BeTrue();
                failed.CommitAcknowledged.Should().BeFalse();
                failed.CleanupConfirmed.Should().BeTrue();
                var rolledBack = await ReadPreparedTerminalAsync(database, fixture);
                rolledBack.Should().Be(before,
                    "audit INSERT refusal must roll back both the dead-letter timestamp and trigger's incomplete flag");
                var observed = await control.ProbeCommitAsync(failed.Probes.Single());
                observed.ThrowIfFailed();
                observed.CleanupConfirmed.Should().BeTrue();
                observed.Value!.Disposition.Should().Be(BackgroundJobProbeDisposition.RolledBack);
            }
            finally { await SchemaQueries.ExecuteAsync(migration, null, "GRANT INSERT ON audit_log TO learnstack_outbox_admin"); }
        }
        var completed = await terminal.TryTerminalizeAsync(claim, fixture.Finalizer.Envelope.Traceparent);
        completed.ThrowIfFailed();
        completed.CommitAcknowledged.Should().BeTrue();
        completed.CleanupConfirmed.Should().BeTrue();
        await AssertPreparedTerminalAuditAsync(database, fixture, claim, before);
        var duplicate = await terminal.TryTerminalizeAsync(claim, fixture.Finalizer.Envelope.Traceparent);
        duplicate.ThrowIfFailed();
        duplicate.Value.Should().BeNull();
        duplicate.CleanupConfirmed.Should().BeTrue();
        await AssertPreparedTerminalAuditAsync(database, fixture, claim, before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prepared_terminal_actual_commit_loss_requires_its_exact_claim_history_and_never_a_second_audit(bool committed)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var host = PreparedTerminalHost(database);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var control = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var scopes = host.Services.GetRequiredService<IServiceScopeFactory>();
        var terminal = new PostgresBackgroundJobTerminalStore(source, scopes, new(), new SystemGuidFactory(), runtime);
        var fixture = await PreparedTerminalRequestAsync(database, host.Services, control);
        var before = await ReadPreparedTerminalAsync(database, fixture);
        var acquired = await terminal.TryClaimTerminalAsync([]);
        acquired.ThrowIfFailed();
        acquired.CleanupConfirmed.Should().BeTrue();
        acquired.Value.Should().NotBeNull();
        var claim = acquired.Value!;
        AssertPreparedTerminalClaim(claim, fixture);

        // An independently acquired real claim is used only as a negative
        // descriptor substitution. No claim, receipt or witness is manufactured.
        var foreign = await PreparedTerminalRequestAsync(database, host.Services, control);
        var foreignAcquisition = await terminal.TryClaimTerminalAsync([fixture.Finalizer.Envelope.JobId.Value]);
        foreignAcquisition.ThrowIfFailed();
        foreignAcquisition.CommitAcknowledged.Should().BeTrue();
        foreignAcquisition.CleanupConfirmed.Should().BeTrue();
        foreignAcquisition.Value!.JobId.Should().Be(foreign.Finalizer.Envelope.JobId.Value);

        await using var proxy = new CommitResponseDroppingProxy(database.OutboxConnectionString, dropCommitRequest: !committed);
        await using var broken = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(proxy.ConnectionString));
        var unknown = await new PostgresBackgroundJobTerminalStore(broken, scopes, new(), new SystemGuidFactory(), runtime)
            .TryTerminalizeAsync(claim, fixture.Finalizer.Envelope.Traceparent);
        unknown.CommitUncertain.Should().BeTrue();
        unknown.CommitAcknowledged.Should().BeFalse();
        unknown.CleanupConfirmed.Should().BeTrue();
        unknown.Value.Should().BeNull();
        proxy.DroppedCommitRequests.Should().Be(committed ? 0 : 1);
        proxy.DroppedCommitResponses.Should().Be(committed ? 1 : 0);
        var descriptor = unknown.Probes.Single();
        descriptor.TerminalClaim.Should().BeSameAs(claim);
        var observed = await control.ProbeCommitAsync(descriptor);
        observed.ThrowIfFailed();
        observed.CleanupConfirmed.Should().BeTrue();
        observed.Value!.Disposition.Should().Be(committed
            ? BackgroundJobProbeDisposition.TerminalCompleted : BackgroundJobProbeDisposition.RolledBack);
        foreach (var copied in new[]
        {
            descriptor with { Boundary = BackgroundJobCommitBoundary.Business },
            descriptor with { Boundary = BackgroundJobCommitBoundary.Control, ExpectedGeneration = claim.WakeGeneration,
                ExpectedPhase = "completed" },
            descriptor with { Token = Guid.NewGuid() },
            descriptor with { ExpectedTerminalReason = "finalize_exhausted" },
            descriptor with { Ordinal = 2 },
            descriptor with { FinalizeOrdinal = 2 },
            descriptor with { SelectedNormalAttemptId = Guid.CreateVersion7() },
            descriptor with { ExpectedUntil = claim.OwnershipUntil.AddTicks(10) },
            descriptor with { IsRecovery = false },
            descriptor with { Profile = "transactional" },
            descriptor with { Profile = null },
            descriptor with { TerminalClaim = foreignAcquisition.Value },
            descriptor with { TerminalClaim = null },
        })
        {
            var refused = await control.ProbeCommitAsync(copied);
            refused.ThrowIfFailed();
            refused.CleanupConfirmed.Should().BeTrue();
            refused.Value!.Disposition.Should().Be(BackgroundJobProbeDisposition.Unresolved,
                "copied metadata cannot replace the exact original prepared terminal claim: {0}", copied);
        }
        unknown.CommitAcknowledged.Should().BeFalse("a historical observation cannot rewrite the physical ACK");
        if (!committed)
        {
            (await ReadPreparedTerminalAsync(database, fixture)).Should().Be(before);
            var retried = await terminal.TryTerminalizeAsync(claim, fixture.Finalizer.Envelope.Traceparent);
            retried.ThrowIfFailed();
            retried.CommitAcknowledged.Should().BeTrue();
            retried.CleanupConfirmed.Should().BeTrue();
        }
        else
        {
            var duplicate = await terminal.TryTerminalizeAsync(claim, fixture.Finalizer.Envelope.Traceparent);
            duplicate.ThrowIfFailed();
            duplicate.CleanupConfirmed.Should().BeTrue();
            duplicate.Value.Should().BeNull();
        }
        await AssertPreparedTerminalAuditAsync(database, fixture, claim, before);
        (await ReadPreparedTerminalAsync(database, foreign)).AuditCount.Should().Be(0);
    }

    [Theory]
    [InlineData("unreleased")]
    [InlineData("mismatched_history")]
    [InlineData("expired_claim")]
    public async Task Prepared_terminal_rechecks_actual_occupancy_history_and_live_owner_after_acquisition(string attack)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var host = PreparedTerminalHost(database);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var control = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var terminal = new PostgresBackgroundJobTerminalStore(source, host.Services.GetRequiredService<IServiceScopeFactory>(),
            new(), new SystemGuidFactory(), runtime);
        var fixture = await PreparedTerminalRequestAsync(database, host.Services, control);
        var acquired = await terminal.TryClaimTerminalAsync([]);
        acquired.ThrowIfFailed();
        acquired.CommitAcknowledged.Should().BeTrue();
        var claim = acquired.Value!;
        if (attack == "expired_claim")
        {
            // Actual work already cleaned. Expiry is only a stale-authority
            // fixture, never evidence that an external effect has quiesced.
            await using var sql = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
            await SchemaQueries.ExecuteAsync(sql, null, """
                UPDATE background_jobs SET ownership_until=clock_timestamp()
                WHERE id=@id AND ownership_token=@token AND ownership_kind='recovery'
                """, ("id", claim.JobId), ("token", claim.Token));
        }
        else await CorruptPreparedTerminalHistoryAsync(database, fixture, attack);
        var refused = await terminal.TryTerminalizeAsync(claim, fixture.Finalizer.Envelope.Traceparent);
        refused.ThrowIfFailed();
        refused.Value.Should().BeNull();
        refused.CommitAcknowledged.Should().BeFalse();
        refused.CleanupConfirmed.Should().BeTrue();
        var actual = await ReadPreparedTerminalAsync(database, fixture);
        actual.Completed.Should().BeFalse();
        actual.Incomplete.Should().BeFalse();
        actual.AuditCount.Should().Be(0);
        if (attack != "expired_claim")
        {
            // Remove only the live-lease selection obstacle, so a negative
            // acquisition really tests current occupancy/history predicates.
            // The planted corruption intentionally violates the transition
            // guard too. Expire fixture scheduling without asking that guard
            // to accept corrupt history; restore it before real acquisition.
            await using var migration = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
            await SchemaQueries.ExecuteAsync(migration, null,
                "ALTER TABLE background_jobs NO FORCE ROW LEVEL SECURITY; ALTER TABLE background_jobs DISABLE TRIGGER USER");
            try
            {
                await SchemaQueries.ExecuteAsync(migration, null,
                    "UPDATE background_jobs SET ownership_until=clock_timestamp() WHERE id=@id AND ownership_token=@token",
                    ("id", claim.JobId), ("token", claim.Token));
            }
            finally
            {
                await SchemaQueries.ExecuteAsync(migration, null,
                    "ALTER TABLE background_jobs ENABLE TRIGGER USER; ALTER TABLE background_jobs FORCE ROW LEVEL SECURITY");
            }
            var selected = await terminal.TryClaimTerminalAsync([]);
            selected.ThrowIfFailed();
            selected.CleanupConfirmed.Should().BeTrue();
            selected.Value.Should().BeNull();
            var probe = await control.ProbeCommitAsync(refused.Probes.Single());
            probe.ThrowIfFailed();
            probe.Value!.Disposition.Should().Be(BackgroundJobProbeDisposition.Unresolved);
        }
    }

    private sealed record PreparedTerminalFixture(ConfirmedStartReceipt Normal, ConfirmedStartReceipt Finalizer,
        BackgroundJobExternalSnapshot Prepared, string Outcome);

    private static WebApplicationFactory<Program> PreparedTerminalHost(DisposableSchemaDatabase database)
    {
        var request = Fixture.Registry.Resolve(BackgroundJobRuntimeFixture.ExternalKey).Definition!.ProfileRegistration.FinalizeRequestType!;
        return Fixture.Host(database, new BackgroundJobFixtureProbe(), configure: services =>
        {
            services.AddSingleton(new FinalizerControlRefusal(Permanent: true));
            services.AddTransient(typeof(IRequestHandler<,>).MakeGenericType(request, typeof(Result<None>)),
                typeof(FinalizerControlRefusalHandler<>).MakeGenericType(request));
        });
    }

    private static async Task<PreparedTerminalFixture> PreparedTerminalRequestAsync(DisposableSchemaDatabase database,
        IServiceProvider services, PostgresBackgroundJobControlStore control)
    {
        // Lower-layer fixture: real normal Prepare and writes, supplied successful
        // provider observation, then separate production Finalize admission/root.
        await using var prepareHost = RecoveryHost(database, preparing: true);
        var executor = prepareHost.Services.GetRequiredService<JobExecutor>();
        var job = await ExternalRetryJobAsync(database);
        var normal = await AdmitFixtureAsync(prepareHost.Services, database, await FixtureClaim.ClaimAsync(database, job));
        var preparedResult = await executor.ExecuteBusinessAsync(normal, normal.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare));
        preparedResult.CommitAcknowledged.Should().BeTrue();
        preparedResult.CleanupFailed.Should().BeFalse();
        var read = await executor.ReadExternalStateAsync(normal, CancellationToken.None);
        read.CommitAcknowledged.Should().BeTrue();
        read.CleanupFailed.Should().BeFalse();
        var prepared = read.Handle!.Require(normal);
        var arm = normal.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm);
        (await SendCheckpointAsync(prepareHost.Services, normal,
            new RecordBackgroundJobArmCommand(normal, arm, prepared.PlanFingerprint!, prepared.OperationId!.Value), arm))
            .IsSuccess.Should().BeTrue();
        var outcomeJson = "{\"ArtifactId\":\"" + Guid.NewGuid().ToString("D") + "\"}";
        var outcome = normal.Run.OpenBoundary(BackgroundJobCheckpointKind.Outcome);
        (await SendCheckpointAsync(prepareHost.Services, normal, new RecordBackgroundJobOutcomeCommand(normal, outcome,
            prepared.PlanFingerprint!, prepared.OperationId!.Value, outcomeJson, BackgroundJobExternalState.Succeeded,
            null, Guid.NewGuid(), BackgroundJobQuiescenceKind.ProviderFenced), outcome)).IsSuccess.Should().BeTrue();
        normal.SourceOwner.CleanupConfirmed.Should().BeTrue();
        normal.Run.Close();
        normal.SourceOwner.CloseIssuance();
        var finalExecutor = services.GetRequiredService<JobExecutor>();
        var started = await finalExecutor.ExecuteAsync(await FixtureClaim.ClaimAsync(database, job), BackgroundJobTestRun.Create());
        started.Outcome.Should().Be(JobStartExecutionOutcome.Started);
        started.CleanupFailed.Should().BeFalse();
        var receipt = started.Receipt!;
        receipt.FinalizeAttemptOrdinal.Should().Be(1);
        receipt.SelectedNormalAttemptId.Should().Be(normal.AttemptId);
        var failed = await finalExecutor.ExecuteBusinessAsync(receipt, receipt.Run.OpenBoundary(BackgroundJobCheckpointKind.Finalize));
        failed.KnownRollback.Should().BeTrue();
        failed.CleanupFailed.Should().BeFalse();
        failed.SourceOwner.CleanupConfirmed.Should().BeTrue();
        failed.Decision.ErrorCode.Should().Be("permanent_failure");
        receipt.Run.Close();
        var requested = await control.FinishExternalFinalizeAsync(BackgroundJobExecutionIdentity.FromReceipt(receipt),
            failed, null, CancellationToken.None);
        requested.ThrowIfFailed();
        requested.CommitAcknowledged.Should().BeTrue();
        requested.CleanupConfirmed.Should().BeTrue();
        requested.Value!.TerminalReason.Should().Be("finalize_refused");
        return new(normal, receipt, prepared, outcomeJson);
    }

    private static void AssertPreparedTerminalClaim(BackgroundJobTerminalClaim claim, PreparedTerminalFixture fixture)
    {
        claim.Profile.Should().Be("external");
        claim.Reason.Should().Be("finalize_refused");
        claim.AttemptId.Should().Be(fixture.Finalizer.AttemptId.Value);
        claim.NormalAttempts.Should().Be(1);
        claim.Prepared.Should().NotBeNull();
        claim.Prepared!.FinalizeAttempts.Should().Be(1);
        claim.Prepared.SelectedNormalAttemptId.Should().Be(fixture.Normal.AttemptId.Value);
        claim.Prepared.FinalizationStatus.Should().Be("succeeded");
        claim.Prepared.FinalizationReason.Should().Be("none");
        claim.Prepared.PlanFingerprint.Should().Be(fixture.Prepared.PlanFingerprint);
        claim.Prepared.OperationId.Should().Be(fixture.Prepared.OperationId!.Value);
        claim.Prepared.State.Should().Be("succeeded");
        claim.Prepared.Policy.Should().BeNull();
        claim.Token.Should().NotBe(fixture.Normal.Claim.Token).And.NotBe(fixture.Finalizer.Claim.Token);
    }

    private sealed record PreparedTerminalState(bool Completed, bool Incomplete, string Phase, string Reason,
        string Status, string ModuleReason, Guid Selected, Guid Current, int NormalCount, int FinalizeCount,
        string Plan, string Fingerprint, Guid Operation, string Outcome, string State, string? Policy,
        long Reservations, long ReleasedReservations, long Attempts, long AuditCount,
        DateTimeOffset? TerminalAt, string? AuditMetadata, DateTimeOffset? AuditAt);

    private static async Task<PreparedTerminalState> ReadPreparedTerminalAsync(DisposableSchemaDatabase database,
        PreparedTerminalFixture fixture)
    {
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, fixture.Normal.Envelope.TenantId.Value,
            fixture.Normal.Envelope.OrganizationId?.Value);
        await using var command = new NpgsqlCommand("""
            SELECT j.dead_lettered_at IS NOT NULL,j.business_finalization_incomplete,j.phase,j.terminal_reason,
                j.finalization_status,j.finalization_reason,j.final_outcome_attempt_id,j.current_attempt_id,
                j.normal_attempts,j.finalize_attempts,j.plan,j.plan_fingerprint,j.operation_id,n.outcome,n.outcome_kind,n.failure_policy,
                (SELECT count(*) FROM background_job_reservations r WHERE r.job_id=j.id),
                (SELECT count(*) FROM background_job_reservations r WHERE r.job_id=j.id AND r.released_at IS NOT NULL),
                (SELECT count(*) FROM background_job_attempts a WHERE a.job_id=j.id),
                (SELECT count(*) FROM audit_log a WHERE a.operation='platform.job.dead_letter' AND a.metadata->>'job_id'=@text),
                j.dead_lettered_at,
                (SELECT a.metadata::text FROM audit_log a WHERE a.operation='platform.job.dead_letter' AND a.metadata->>'job_id'=@text LIMIT 1),
                (SELECT a.timestamp FROM audit_log a WHERE a.operation='platform.job.dead_letter' AND a.metadata->>'job_id'=@text LIMIT 1)
            FROM background_jobs j JOIN background_job_attempts n ON n.id=j.final_outcome_attempt_id AND n.job_id=j.id
            WHERE j.id=@id
            """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("id", fixture.Finalizer.Envelope.JobId.Value);
        command.Parameters.AddWithValue("text", fixture.Finalizer.Envelope.JobId.Value.ToString("D"));
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        return new(row.GetBoolean(0), row.GetBoolean(1), row.GetString(2), row.GetString(3), row.GetString(4), row.GetString(5),
            row.GetGuid(6), row.GetGuid(7), row.GetInt32(8), row.GetInt32(9), row.GetString(10), row.GetString(11), row.GetGuid(12),
            row.GetString(13), row.GetString(14), row.IsDBNull(15) ? null : row.GetString(15), row.GetInt64(16), row.GetInt64(17),
            row.GetInt64(18), row.GetInt64(19), row.IsDBNull(20) ? null : row.GetFieldValue<DateTimeOffset>(20),
            row.IsDBNull(21) ? null : row.GetString(21), row.IsDBNull(22) ? null : row.GetFieldValue<DateTimeOffset>(22));
    }

    private static async Task AssertPreparedTerminalAuditAsync(DisposableSchemaDatabase database, PreparedTerminalFixture fixture,
        BackgroundJobTerminalClaim claim, PreparedTerminalState before)
    {
        var after = await ReadPreparedTerminalAsync(database, fixture);
        after.Completed.Should().BeTrue();
        after.Incomplete.Should().BeTrue();
        after.AuditCount.Should().Be(1);
        after.AuditAt.Should().Be(after.TerminalAt);
        (after with { Completed = false, Incomplete = false, AuditCount = 0, TerminalAt = null, AuditMetadata = null, AuditAt = null })
            .Should().Be(before, "only terminal completion and its MUST change; the selected success remains immutable");
        after.Plan.Should().Be(fixture.Prepared.PlanJson);
        after.Outcome.Should().Be(fixture.Outcome);
        after.NormalCount.Should().Be(1);
        after.FinalizeCount.Should().Be(1);
        after.Attempts.Should().Be(2);
        after.Reservations.Should().Be(1);
        after.ReleasedReservations.Should().Be(1);
        using var parsed = JsonDocument.Parse(after.AuditMetadata!);
        var metadata = parsed.RootElement;
        metadata.GetProperty("job_id").GetGuid().Should().Be(claim.JobId);
        metadata.GetProperty("attempt_id").GetGuid().Should().Be(fixture.Finalizer.AttemptId.Value);
        metadata.GetProperty("acquisition_token").GetGuid().Should().Be(claim.Token);
        metadata.GetProperty("wake_generation").GetInt64().Should().Be(claim.WakeGeneration);
        metadata.GetProperty("reason").GetString().Should().Be("finalize_refused");
        metadata.GetProperty("normal_attempts").GetInt32().Should().Be(1);
        metadata.GetProperty("finalize_attempts").GetInt32().Should().Be(1);
        metadata.GetProperty("selected_normal_attempt_id").GetGuid().Should().Be(fixture.Normal.AttemptId.Value);
        metadata.GetProperty("finalization_status").GetString().Should().Be("succeeded");
        metadata.GetProperty("finalization_reason").GetString().Should().Be("none");
        metadata.GetProperty("external_state").GetString().Should().Be("succeeded");
        metadata.GetProperty("business_finalization_incomplete").GetBoolean().Should().BeTrue();
        foreach (var absent in new[] { "external_failure_policy", "external_failure_class", "external_retry_permitted" })
            metadata.TryGetProperty(absent, out _).Should().BeFalse();
    }

    private static async Task CorruptPreparedTerminalHistoryAsync(DisposableSchemaDatabase database,
        PreparedTerminalFixture fixture, string attack)
    {
        await using var migration = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        var table = attack == "unreleased" ? "background_job_reservations" : "background_job_attempts";
        await SchemaQueries.ExecuteAsync(migration, null,
            "ALTER TABLE " + table + " NO FORCE ROW LEVEL SECURITY; ALTER TABLE " + table + " DISABLE TRIGGER USER");
        try
        {
            await SchemaQueries.ExecuteAsync(migration, null, attack == "unreleased" ? """
                UPDATE background_job_reservations SET released_at=NULL,release_reason=NULL WHERE attempt_id=@attempt
                """ : """
                UPDATE background_job_attempts SET plan_fingerprint=CASE WHEN plan_fingerprint=repeat('a',64)
                    THEN repeat('b',64) ELSE repeat('a',64) END WHERE id=@attempt
                """, ("attempt", fixture.Normal.AttemptId.Value));
        }
        finally
        {
            await SchemaQueries.ExecuteAsync(migration, null,
                "ALTER TABLE " + table + " ENABLE TRIGGER USER; ALTER TABLE " + table + " FORCE ROW LEVEL SECURITY");
        }
    }
}
