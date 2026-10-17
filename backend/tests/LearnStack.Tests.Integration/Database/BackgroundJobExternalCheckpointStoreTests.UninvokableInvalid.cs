using System.Text.Json;
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
    [InlineData("no_plan", null, false)]
    [InlineData("prepared_retry", null, false)]
    [InlineData("selected", null, false)]
    [InlineData("selected", false, false)]
    [InlineData("selected", true, false)]
    [InlineData("selected", null, true)]
    public async Task Actual_invalid_envelope_read_requests_exact_terminal_without_another_admission(
        string history, bool? commitLanded, bool replaceRoutingOwner)
    {
        // Store/application primitive proof. The prefix uses real checkpoint
        // writes; the deliberately invalid payload is a migration fixture plant.
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var prefixHost = RecoveryHost(database, preparing: true);
        var callerProbe = new BackgroundJobFixtureProbe();
        await using var readHost = Fixture.Host(database, callerProbe);
        await using var auditHost = NoPlanRefusalHost(database, emptyDefinitions: true);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var control = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var routing = new PostgresBackgroundJobRoutingStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var normal = await InvalidUninvokablePrefixAsync(database, prefixHost.Services, control, runtime, history);
        try
        {
            var job = normal.Envelope.JobId;
            var prefix = await ReadUninvokableStateAsync(database, job);
            prefix.Normal.Should().Be(1);
            prefix.FinalizeAttempts.Should().Be(0);
            prefix.Attempts.Should().Be(1);
            prefix.Reservations.Should().Be(1);
            prefix.Released.Should().Be(1);
            prefix.NoStarts.Should().Be(history == "no_plan" ? 1 : 0);
            prefix.Outcomes.Should().Be(history == "no_plan" ? 0 : 1);
            prefix.Phase.Should().Be(history == "selected" ? "finalize_pending" : "pending");
            var selectedFacts = await RetainedSelectionFactsAsync(database, normal);
            var validEnvelope = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
            await PlantInvalidUninvokablePayloadAsync(database, normal);
            var plantedEnvelope = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
            plantedEnvelope.Parameters.Should().Be("{}");
            (plantedEnvelope with { Parameters = validEnvelope.Parameters }).Should().Be(validEnvelope,
                "the fault plant cannot alter the actual causal envelope or lifecycle metadata");
            var generation = await RecoveryGenerationAsync(database, job);
            var claim = (await routing.TryClaimAsync(job, generation))!;
            claim.Should().NotBeNull();
            claim.LedgerStage.Should().Be(history == "selected" ? BackgroundJobLedgerStage.Finalize : BackgroundJobLedgerStage.Normal);
            var roots = Fixture.RootConstructions;
            await AssertActualInvalidUninvokableReadAsync(database, readHost.Services, callerProbe, claim);
            if (replaceRoutingOwner)
            {
                var oldClaim = claim;
                await ExpireUninvokableOwnerAsync(database, job);
                claim = (await routing.TryClaimAsync(job, generation))!;
                claim.Should().NotBeNull();
                claim.Token.Should().NotBe(oldClaim.Token);
                await AssertActualInvalidUninvokableReadAsync(database, readHost.Services, callerProbe, claim);
                var currentOwner = await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job);
                var refused = await control.RequestInvalidEnvelopeAsync(oldClaim);
                refused.ThrowIfFailed();
                refused.Value.Should().BeNull("a real earlier routing claim cannot request against its replacement owner");
                refused.CommitAcknowledged.Should().BeFalse();
                refused.CleanupConfirmed.Should().BeTrue();
                refused.Probes.Should().BeEmpty();
                (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job)).Should().Be(currentOwner);
                (await ReadUninvokableStateAsync(database, job)).Should().Be(prefix);
                (await ReadUninvokableAuditAsync(database, job)).Count.Should().Be(0);
            }
            var beforeRequest = await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job);
            BackgroundJobCommitProbe origin;
            if (commitLanded is { } committed)
            {
                await using var proxy = new CommitResponseDroppingProxy(database.OutboxConnectionString, dropCommitRequest: !committed);
                await using var brokenSource = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(proxy.ConnectionString));
                var broken = new PostgresBackgroundJobControlStore(brokenSource, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
                var unknown = await broken.RequestInvalidEnvelopeAsync(claim);
                unknown.CommitAcknowledged.Should().BeFalse();
                unknown.CommitUncertain.Should().BeTrue();
                unknown.CleanupConfirmed.Should().BeTrue();
                unknown.Value.Should().BeNull();
                proxy.DroppedCommitRequests.Should().Be(committed ? 0 : 1);
                proxy.DroppedCommitResponses.Should().Be(committed ? 1 : 0);
                origin = unknown.Probes.Should().ContainSingle().Which;
                var proof = await control.ProbeCommitAsync(origin);
                proof.ThrowIfFailed();
                proof.CleanupConfirmed.Should().BeTrue();
                proof.Value!.Disposition.Should().Be(committed
                    ? BackgroundJobProbeDisposition.ControlCommitted : BackgroundJobProbeDisposition.RolledBack);
                proof.CommitAcknowledged.Should().BeFalse();
                if (!committed)
                {
                    (await ReadUninvokableStateAsync(database, job)).Should().Be(prefix);
                    (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job)).Should().Be(beforeRequest);
                    var retry = await control.RequestInvalidEnvelopeAsync(claim);
                    retry.ThrowIfFailed();
                    retry.CommitAcknowledged.Should().BeTrue();
                    retry.CleanupConfirmed.Should().BeTrue();
                    retry.Value!.TerminalReason.Should().Be("invalid_envelope");
                }
                unknown.CommitAcknowledged.Should().BeFalse("a probe or repeat cannot rewrite the physical original ACK");
            }
            else
            {
                var request = await control.RequestInvalidEnvelopeAsync(claim);
                request.ThrowIfFailed();
                request.CommitAcknowledged.Should().BeTrue();
                request.CleanupConfirmed.Should().BeTrue();
                request.Value!.TerminalReason.Should().Be("invalid_envelope");
                origin = request.Probes.Should().ContainSingle().Which;
            }
            origin.Token.Should().Be(claim.Token);
            origin.ExpectedUntil.Should().Be(claim.OwnershipUntil);
            origin.ExpectedTerminalReason.Should().Be("invalid_envelope");
            origin.UninvokableHistory.Should().NotBeNull();
            origin.UninvokableHistory!.Phase.Should().Be(prefix.Phase);
            origin.FinalizeOrdinal.Should().Be(0);
            origin.SelectedNormalAttemptId.Should().Be(history == "selected" ? normal.AttemptId.Value : (Guid?)null);
            (origin.UninvokableHistory.PlanFingerprint is not null).Should().Be(history != "no_plan");
            foreach (var changed in new[]
            {
                origin with { Token = Guid.NewGuid() },
                origin with { ExpectedTerminalReason = "retired_definition" },
                origin with { ExpectedPhase = "finalize_pending" },
                origin with { FinalizeOrdinal = 1 },
                origin with { UninvokableHistory = null },
            })
            {
                var refused = await control.ProbeCommitAsync(changed);
                refused.ThrowIfFailed();
                refused.Value!.Disposition.Should().Be(BackgroundJobProbeDisposition.Unresolved);
            }
            (await ReadUninvokableAuditAsync(database, job)).Count.Should().Be(0);
            await CompleteInvalidUninvokableAsync(database, auditHost.Services, source, runtime, control, normal, prefix, origin, history);
            (await RetainedSelectionFactsAsync(database, normal)).Should().Be(selectedFacts);
            (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!.Parameters.Should().Be("{}");
            Fixture.RootConstructions.Should().Be(roots, "the invalid app read, status and bypass cannot construct a module root");
            callerProbe.Constructions.Should().BeEmpty();
        }
        finally { CloseUninvokableNormal(normal); }
    }

    private static async Task<ConfirmedStartReceipt> InvalidUninvokablePrefixAsync(DisposableSchemaDatabase database,
        IServiceProvider services, PostgresBackgroundJobControlStore control, BackgroundJobRuntimeGate runtime, string history)
    {
        var normal = history == "no_plan"
            ? await NoPlanOrdinalAsync(database, services, 1, control, runtime)
            : await PrepareUninvokableAsync(database, services);
        try
        {
            if (history == "no_plan")
            {
                CloseUninvokableNormal(normal);
                await ExpireSelectionLeaseAsync(database, normal);
                var acquired = await control.AcquireExternalRecoveryAsync(normal.Envelope.JobId,
                    await RecoveryGenerationAsync(database, normal.Envelope.JobId), normal.Envelope.Queue, CancellationToken.None);
                acquired.ThrowIfFailed();
                var recovery = BindRecovery(acquired, runtime);
                try
                {
                    var executor = services.GetRequiredService<JobExecutor>();
                    var read = await executor.ReadExternalStateAsync(recovery, CancellationToken.None);
                    read.CommitAcknowledged.Should().BeTrue();
                    read.CleanupFailed.Should().BeFalse();
                    read.Handle!.Snapshot.PlanJson.Should().BeNull();
                    var ended = await executor.RecordExternalNoStartAsync(recovery, read.Handle,
                        recovery.Run.OpenBoundary(BackgroundJobCheckpointKind.NoStart), CancellationToken.None);
                    ended.CommitAcknowledged.Should().BeTrue();
                    ended.CleanupFailed.Should().BeFalse();
                    recovery.SourceOwner.CleanupConfirmed.Should().BeTrue();
                    var retry = await control.RetryKnownExternalAsync(BackgroundJobExecutionIdentity.FromRecovery(acquired.Value!),
                        BackgroundJobExternalRetryMode.RecoveryKnown, CancellationToken.None);
                    retry.ThrowIfFailed();
                    retry.CommitAcknowledged.Should().BeTrue();
                    retry.CleanupConfirmed.Should().BeTrue();
                    retry.Value!.Phase.Should().Be("pending");
                }
                finally { recovery.Run.Close(); recovery.SourceOwner.CloseIssuance(); }
            }
            else
            {
                await WriteUninvokableOutcomeAsync(services, normal,
                    history == "selected" ? BackgroundJobExternalState.Succeeded : BackgroundJobExternalState.Failed,
                    history == "selected" ? null : BackgroundJobExternalFailurePolicy.TransientRepeatable);
                if (history == "prepared_retry")
                {
                    var retry = await control.RetryKnownExternalAsync(BackgroundJobExecutionIdentity.FromReceipt(normal),
                        BackgroundJobExternalRetryMode.OriginalCompleted, CancellationToken.None);
                    retry.ThrowIfFailed();
                    retry.CommitAcknowledged.Should().BeTrue();
                    retry.CleanupConfirmed.Should().BeTrue();
                    retry.Value!.Phase.Should().Be("pending");
                }
                CloseUninvokableNormal(normal);
            }
            var due = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, normal.Envelope.JobId))!.AvailableAfter;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (await RetryDatabaseClockAsync(database) < due) await Task.Delay(10, deadline.Token);
            return normal;
        }
        catch { CloseUninvokableNormal(normal); throw; }
    }

    private static async Task AssertActualInvalidUninvokableReadAsync(DisposableSchemaDatabase database,
        IServiceProvider services, BackgroundJobFixtureProbe callerProbe, BackgroundJobRoutingClaim claim)
    {
        var before = await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, claim.JobId);
        var ledger = await ReadUninvokableStateAsync(database, claim.JobId);
        var run = BackgroundJobTestRun.Create();
        JobStartExecutionResult? read = null;
        try
        {
            read = await services.GetRequiredService<JobExecutor>().ExecuteAsync(claim, run);
            read.Outcome.Should().Be(JobStartExecutionOutcome.InvalidEnvelope);
            read.Receipt.Should().BeNull();
            read.ValidatedEnvelope.Should().BeNull();
            read.CleanupFailed.Should().BeFalse();
            read.RetainedCleanup.Should().BeEmpty();
            read.SourceOwner.CleanupConfirmed.Should().BeTrue();
            callerProbe.Constructions.Should().BeEmpty();
            (await ReadUninvokableStateAsync(database, claim.JobId)).Should().Be(ledger);
            (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, claim.JobId)).Should().Be(before);
            (await ReadUninvokableAuditAsync(database, claim.JobId)).Count.Should().Be(0);
        }
        finally { run.Close(); read?.SourceOwner.CloseIssuance(); }
    }

    private static async Task PlantInvalidUninvokablePayloadAsync(DisposableSchemaDatabase database, ConfirmedStartReceipt normal)
    {
        // Deliberate migration-only fault injection, not a legal producer edit.
        // Restore all triggers and FORCE in the same transaction before any app
        // read; causal metadata, plan, selection and positive history stay actual.
        await using var migration = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var transaction = await migration.BeginTransactionAsync();
        await SchemaQueries.ExecuteAsync(migration, transaction,
            "ALTER TABLE background_jobs NO FORCE ROW LEVEL SECURITY; ALTER TABLE background_jobs DISABLE TRIGGER USER");
        await SchemaQueries.ExecuteAsync(migration, transaction,
            "UPDATE background_jobs SET parameters='{}' WHERE id=@id", ("id", normal.Envelope.JobId.Value));
        await SchemaQueries.ExecuteAsync(migration, transaction,
            "ALTER TABLE background_jobs ENABLE TRIGGER USER; ALTER TABLE background_jobs FORCE ROW LEVEL SECURITY");
        await transaction.CommitAsync();
        await using var restored = new NpgsqlCommand("""
            SELECT c.relrowsecurity AND c.relforcerowsecurity
                AND EXISTS (SELECT 1 FROM pg_trigger t WHERE t.tgrelid=c.oid AND NOT t.tgisinternal)
                AND NOT EXISTS (SELECT 1 FROM pg_trigger t WHERE t.tgrelid=c.oid AND NOT t.tgisinternal AND t.tgenabled<>'O')
            FROM pg_class c WHERE c.oid='public.background_jobs'::regclass
            """, (NpgsqlConnection)migration) { CommandTimeout = 5 };
        (await restored.ExecuteScalarAsync()).Should().Be(true);
    }

    private static async Task CompleteInvalidUninvokableAsync(DisposableSchemaDatabase database, IServiceProvider services,
        PrivilegedDataSource source, BackgroundJobRuntimeGate runtime, PostgresBackgroundJobControlStore control,
        ConfirmedStartReceipt normal, UninvokableState prefix, BackgroundJobCommitProbe origin, string history)
    {
        services.GetRequiredService<BackgroundJobDefinitionRegistry>().DefinitionKeys.Should().BeEmpty();
        var job = normal.Envelope.JobId;
        var requested = await ReadUninvokableStateAsync(database, job);
        requested.Phase.Should().Be("terminal_pending");
        requested.Reason.Should().Be("invalid_envelope");
        requested.Incomplete.Should().BeFalse();
        requested.TerminalAt.Should().BeNull();
        requested.Immutable.Should().Be(prefix.Immutable);
        var unnecessary = await control.AcquireExternalTerminalNoStartAsync([], CancellationToken.None);
        unnecessary.ThrowIfFailed();
        unnecessary.Value.Should().BeNull("the actual pre-request NoStart or completed outcome already released this normal attempt");
        var beforeProof = await control.ProbeCommitAsync(origin);
        beforeProof.ThrowIfFailed();
        beforeProof.Value!.Disposition.Should().Be(BackgroundJobProbeDisposition.ControlCommitted);
        var terminal = new PostgresBackgroundJobTerminalStore(source, services.GetRequiredService<IServiceScopeFactory>(),
            new(), new SystemGuidFactory(), runtime);
        var acquired = await terminal.TryClaimTerminalAsync([]);
        acquired.ThrowIfFailed();
        acquired.CommitAcknowledged.Should().BeTrue();
        acquired.CleanupConfirmed.Should().BeTrue();
        var claim = acquired.Value!;
        claim.JobId.Should().Be(job.Value);
        claim.Reason.Should().Be("invalid_envelope");
        claim.NormalAttempts.Should().Be(1);
        (claim.Prepared is not null).Should().Be(history != "no_plan");
        if (claim.Prepared is { } prepared)
        {
            prepared.FinalizeAttempts.Should().Be(0);
            prepared.SelectedNormalAttemptId.Should().Be(history == "selected" ? normal.AttemptId.Value : (Guid?)null);
            prepared.FinalizationStatus.Should().Be(history == "selected" ? "succeeded" : null);
            prepared.FinalizationReason.Should().Be(history == "selected" ? "none" : null);
        }
        var completed = await terminal.TryTerminalizeAsync(claim, normal.Envelope.Traceparent);
        completed.ThrowIfFailed();
        completed.CommitAcknowledged.Should().BeTrue();
        completed.CleanupConfirmed.Should().BeTrue();
        var after = await ReadUninvokableStateAsync(database, job);
        after.TerminalAt.Should().NotBeNull();
        after.Incomplete.Should().Be(history != "no_plan");
        (after with { Phase = prefix.Phase, Reason = prefix.Reason, TerminalAt = null, Incomplete = false })
            .Should().Be(prefix, "terminal disposition cannot spend N/F, alter plan/selection/outcome, or invent another NoStart or release");
        var audit = await ReadUninvokableAuditAsync(database, job);
        audit.Count.Should().Be(1);
        audit.At.Should().Be(after.TerminalAt);
        using var parsed = JsonDocument.Parse(audit.Metadata!);
        var metadata = parsed.RootElement;
        metadata.GetProperty("job_id").GetGuid().Should().Be(job.Value);
        metadata.GetProperty("reason").GetString().Should().Be("invalid_envelope");
        metadata.GetProperty("normal_attempts").GetInt32().Should().Be(1);
        metadata.GetProperty("acquisition_token").GetGuid().Should().Be(claim.Token);
        if (history != "no_plan")
        {
            metadata.GetProperty("finalize_attempts").GetInt32().Should().Be(0);
            metadata.GetProperty("business_finalization_incomplete").GetBoolean().Should().BeTrue();
        }
        if (history == "selected")
        {
            metadata.GetProperty("selected_normal_attempt_id").GetGuid().Should().Be(normal.AttemptId.Value);
            metadata.GetProperty("finalization_status").GetString().Should().Be("succeeded");
            metadata.GetProperty("finalization_reason").GetString().Should().Be("none");
            metadata.GetProperty("external_state").GetString().Should().Be("succeeded");
        }
        var afterProof = await control.ProbeCommitAsync(origin);
        afterProof.ThrowIfFailed();
        afterProof.Value!.Disposition.Should().Be(BackgroundJobProbeDisposition.ControlCommitted);
        var duplicate = await terminal.TryTerminalizeAsync(claim, normal.Envelope.Traceparent);
        duplicate.ThrowIfFailed();
        duplicate.Value.Should().BeNull();
        (await ReadUninvokableAuditAsync(database, job)).Count.Should().Be(1);
    }
}
