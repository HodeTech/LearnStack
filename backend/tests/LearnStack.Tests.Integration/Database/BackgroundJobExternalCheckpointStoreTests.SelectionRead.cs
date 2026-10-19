using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobExternalCheckpointStoreTests
{
    [Theory]
    [InlineData(BackgroundJobExternalState.Succeeded, null, BackgroundJobFinalizationStatus.Succeeded, BackgroundJobFinalizationReason.None)]
    [InlineData(BackgroundJobExternalState.Failed, BackgroundJobExternalFailurePolicy.Permanent, BackgroundJobFinalizationStatus.Failed, BackgroundJobFinalizationReason.ExternalFailed)]
    [InlineData(BackgroundJobExternalState.Failed, BackgroundJobExternalFailurePolicy.TransientNotRepeatable, BackgroundJobFinalizationStatus.Failed, BackgroundJobFinalizationReason.ExternalFailed)]
    [InlineData(BackgroundJobExternalState.Cancelled, BackgroundJobExternalFailurePolicy.TransientNotRepeatable, BackgroundJobFinalizationStatus.Failed, BackgroundJobFinalizationReason.ExternalCancelled)]
    [InlineData(BackgroundJobExternalState.Failed, BackgroundJobExternalFailurePolicy.TransientRepeatable, null, null)]
    [InlineData(BackgroundJobExternalState.Cancelled, BackgroundJobExternalFailurePolicy.TransientRepeatable, null, null)]
    public async Task Actual_app_reader_preserves_the_selected_policy_and_rejects_projected_partial_or_conflicting_facts(
        BackgroundJobExternalState state, BackgroundJobExternalFailurePolicy? policy,
        BackgroundJobFinalizationStatus? status, BackgroundJobFinalizationReason? reason)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external");
        await using var host = RecoveryHost(database, preparing: true);
        var receipt = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        var executor = host.Services.GetRequiredService<JobExecutor>();
        (await executor.ExecuteBusinessAsync(receipt, receipt.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare)))
            .CommitAcknowledged.Should().BeTrue();
        var read = await executor.ReadExternalStateAsync(receipt, CancellationToken.None);
        var before = read.Handle!.Require(receipt);
        before.SelectedNormalAttemptId.Should().BeNull();
        before.FinalizationStatus.Should().BeNull();
        before.FinalizationReason.Should().BeNull();
        var arm = receipt.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm);
        (await SendCheckpointAsync(host.Services, receipt,
            new RecordBackgroundJobArmCommand(receipt, arm, before.PlanFingerprint!, before.OperationId!.Value), arm))
            .IsSuccess.Should().BeTrue();
        var completed = receipt.Run.OpenBoundary(BackgroundJobCheckpointKind.Outcome);
        var outcome = "{\"ArtifactId\":\"" + Guid.NewGuid().ToString("D") + "\"}";
        (await SendCheckpointAsync(host.Services, receipt, new RecordBackgroundJobOutcomeCommand(receipt, completed,
            before.PlanFingerprint!, before.OperationId!.Value, outcome, state, policy, Guid.NewGuid(),
            BackgroundJobQuiescenceKind.ProviderFenced), completed)).IsSuccess.Should().BeTrue();

        // Pure parser over an actual announced app reader. This is not a
        // Finalize receipt/owner, and never reopens the old normal authority.
        var selected = await ReadStoredSelectionAsync(database, receipt);
        selected.OutcomeJson.Should().Be(outcome);
        selected.OutcomeState.Should().Be(state);
        selected.FailurePolicy.Should().Be(policy);
        selected.SelectedNormalAttemptId.Should().Be(status is null ? null : receipt.AttemptId);
        selected.FinalizationStatus.Should().Be(status);
        selected.FinalizationReason.Should().Be(reason);
        if (status is null)
        {
            var forged = () => ReadStoredSelectionAsync(database, receipt, (receipt.AttemptId.Value, "failed", "external_failed"));
            await forged.Should().ThrowAsync<InvalidOperationException>("repeatable failure before six is not finalization input");
            return;
        }
        var statusText = status == BackgroundJobFinalizationStatus.Succeeded ? "succeeded" : "failed";
        var reasonText = reason switch
        {
            BackgroundJobFinalizationReason.None => "none",
            BackgroundJobFinalizationReason.ExternalFailed => "external_failed",
            BackgroundJobFinalizationReason.ExternalCancelled => "external_cancelled",
            _ => throw new InvalidOperationException("Unexpected first-attempt selection."),
        };
        for (var mask = 1; mask < 7; mask++)
        {
            var partial = () => ReadStoredSelectionAsync(database, receipt,
                ((mask & 1) != 0 ? receipt.AttemptId.Value : null,
                 (mask & 2) != 0 ? statusText : null, (mask & 4) != 0 ? reasonText : null));
            await partial.Should().ThrowAsync<InvalidOperationException>("every partial selection is refused independently of DDL");
        }
        foreach (var invalid in new (Guid?, string?, string?)[]
                 {
                     (Guid.CreateVersion7(), statusText, reasonText),
                     (receipt.AttemptId.Value, "undefined", reasonText),
                     (receipt.AttemptId.Value, statusText, "undefined"),
                     (receipt.AttemptId.Value, statusText, "permanent_failure"),
                     (receipt.AttemptId.Value, status == BackgroundJobFinalizationStatus.Succeeded ? "failed" : "succeeded", reasonText),
                 })
        {
            var wrong = () => ReadStoredSelectionAsync(database, receipt, invalid);
            await wrong.Should().ThrowAsync<InvalidOperationException>();
        }
        // Projection-only controls leave the guarded durable history unchanged.
        var unchanged = await ReadStoredSelectionAsync(database, receipt);
        unchanged.Should().BeEquivalentTo(selected);
    }

    [Fact]
    public async Task Actual_no_start_reader_preserves_a_stored_permanent_selection_before_exhaustion_without_inventing_provider_facts()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external");
        await using var host = RecoveryHost(database, preparing: true);
        var receipt = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        (await host.Services.GetRequiredService<JobExecutor>().ExecuteBusinessAsync(receipt,
            receipt.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare))).CommitAcknowledged.Should().BeTrue();
        await ExpireSelectionLeaseAsync(database, receipt);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var acquired = await store.AcquireExternalRecoveryAsync(job, await RecoveryGenerationAsync(database, job), receipt.Envelope.Queue, CancellationToken.None);
        acquired.ThrowIfFailed();
        var recovery = BindRecovery(acquired, runtime);
        var noStart = recovery.Run.OpenBoundary(BackgroundJobCheckpointKind.NoStart);
        (await SendRecoveryAsync(host.Services, recovery, RecordBackgroundJobOutcomeCommand.ForNoStart(recovery, noStart), noStart))
            .IsSuccess.Should().BeTrue();
        (await ReadStoredSelectionAsync(database, receipt)).FinalizationStatus.Should().BeNull();
        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            // Explicit valid storage setup for this parser; no production
            // permanent-failure selection writer is claimed by this test.
            await SchemaQueries.ExecuteAsync(app, transaction, """
                UPDATE background_jobs SET final_outcome_attempt_id=@attempt,finalization_status='failed',
                    finalization_reason='permanent_failure',phase='finalize_pending' WHERE id=@id
                """, ("id", job.Value), ("attempt", receipt.AttemptId.Value));
            await transaction.CommitAsync();
        }
        var selected = await ReadStoredSelectionAsync(database, receipt);
        selected.SelectedNormalAttemptId.Should().Be(receipt.AttemptId);
        selected.FinalizationStatus.Should().Be(BackgroundJobFinalizationStatus.Failed);
        selected.FinalizationReason.Should().Be(BackgroundJobFinalizationReason.PermanentFailure);
        selected.NoStartRecorded.Should().BeTrue();
        selected.ExecuteIntentRecorded.Should().BeFalse();
        selected.OutcomeJson.Should().BeNull();
        selected.OutcomeState.Should().BeNull();
        selected.FailurePolicy.Should().BeNull();
        var earlyExhaustion = () => ReadStoredSelectionAsync(database, receipt,
            (receipt.AttemptId.Value, "failed", "normal_attempts_exhausted"));
        await earlyExhaustion.Should().ThrowAsync<InvalidOperationException>();
        foreach (var stamp in new[] { SelectionStamp.Missing, SelectionStamp.Mismatch })
        {
            var invalidStamp = () => ReadStoredSelectionAsync(database, receipt, stamp: stamp);
            await invalidStamp.Should().ThrowAsync<InvalidOperationException>("a complete no-start selection must bind its original plan stamp");
        }
    }

    [Theory]
    [InlineData(BackgroundJobExternalState.Failed)]
    [InlineData(BackgroundJobExternalState.Cancelled)]
    public async Task Actual_sixth_repeatable_outcome_reader_preserves_the_completed_state_and_exhaustion_reason(BackgroundJobExternalState state)
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
        for (var ordinal = 1; ordinal <= 6; ordinal++)
        {
            var receipt = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
            receipt.NormalAttemptOrdinal.Should().Be(ordinal);
            if (ordinal == 1)
                (await executor.ExecuteBusinessAsync(receipt, receipt.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare)))
                    .CommitAcknowledged.Should().BeTrue();
            if (ordinal < 6)
            {
                await ExpireSelectionLeaseAsync(database, receipt);
                var acquired = await store.AcquireExternalRecoveryAsync(job, await RecoveryGenerationAsync(database, job), receipt.Envelope.Queue, CancellationToken.None);
                acquired.ThrowIfFailed();
                var recovery = BindRecovery(acquired, runtime);
                var noStart = recovery.Run.OpenBoundary(BackgroundJobCheckpointKind.NoStart);
                (await SendRecoveryAsync(host.Services, recovery, RecordBackgroundJobOutcomeCommand.ForNoStart(recovery, noStart), noStart))
                    .IsSuccess.Should().BeTrue();
                await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
                await SchemaQueries.ExecuteAsync(dispatcher, null, """
                    UPDATE background_jobs SET phase='pending',ownership_kind=NULL,ownership_token=NULL,ownership_until=NULL
                    WHERE id=@id AND phase='reconciling'
                    """, ("id", job.Value));
                continue;
            }
            receipt.Stage.Should().BeNull("the sixth counted admission reuses the committed original plan");
            var read = await executor.ReadExternalStateAsync(receipt, CancellationToken.None);
            var snapshot = read.Handle!.Require(receipt);
            var arm = receipt.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm);
            (await SendCheckpointAsync(host.Services, receipt, new RecordBackgroundJobArmCommand(receipt, arm,
                snapshot.PlanFingerprint!, snapshot.OperationId!.Value), arm)).IsSuccess.Should().BeTrue();
            var completed = receipt.Run.OpenBoundary(BackgroundJobCheckpointKind.Outcome);
            var outcome = "{\"ArtifactId\":\"" + Guid.NewGuid().ToString("D") + "\"}";
            (await SendCheckpointAsync(host.Services, receipt, new RecordBackgroundJobOutcomeCommand(receipt, completed,
                snapshot.PlanFingerprint!, snapshot.OperationId!.Value, outcome, state, BackgroundJobExternalFailurePolicy.TransientRepeatable,
                Guid.NewGuid(), BackgroundJobQuiescenceKind.ProviderFenced), completed)).IsSuccess.Should().BeTrue();
            var selected = await ReadStoredSelectionAsync(database, receipt);
            selected.OutcomeJson.Should().Be(outcome);
            selected.OutcomeState.Should().Be(state);
            selected.FailurePolicy.Should().Be(BackgroundJobExternalFailurePolicy.TransientRepeatable);
            selected.SelectedNormalAttemptId.Should().Be(receipt.AttemptId);
            selected.FinalizationStatus.Should().Be(BackgroundJobFinalizationStatus.Failed);
            selected.FinalizationReason.Should().Be(BackgroundJobFinalizationReason.NormalAttemptsExhausted);
            selected.NoStartRecorded.Should().BeFalse();
            var falseReason = () => ReadStoredSelectionAsync(database, receipt,
                (receipt.AttemptId.Value, "failed", state == BackgroundJobExternalState.Failed ? "external_failed" : "external_cancelled"));
            await falseReason.Should().ThrowAsync<InvalidOperationException>();
        }
    }

    private static async Task ExpireSelectionLeaseAsync(DisposableSchemaDatabase database, ConfirmedStartReceipt receipt)
    {
        receipt.Run.Close();
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await SchemaQueries.ExecuteAsync(dispatcher, null, """
            UPDATE background_jobs SET ownership_until=clock_timestamp() WHERE id=@id;
            UPDATE background_jobs SET phase='reconciling',ownership_token=NULL,ownership_kind=NULL,ownership_until=NULL,probe_after=clock_timestamp()
                WHERE id=@id AND ownership_until<=clock_timestamp()
            """, ("id", receipt.Envelope.JobId.Value));
    }

    private enum SelectionStamp { Actual, Missing, Mismatch }

    private static async Task<BackgroundJobExternalSnapshot> ReadStoredSelectionAsync(DisposableSchemaDatabase database,
        ConfirmedStartReceipt receipt, (Guid? Attempt, string? Status, string? Reason)? suffix = null,
        SelectionStamp stamp = SelectionStamp.Actual)
    {
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, receipt.Envelope.TenantId.Value, receipt.Envelope.OrganizationId?.Value);
        var projection = BackgroundJobStoredExternalFacts.Projection;
        if (stamp != SelectionStamp.Actual)
            projection = projection.Replace("a.plan_fingerprint,a.operation_id", stamp == SelectionStamp.Missing
                ? "NULL::text,NULL::uuid" : "@wrong_fingerprint::text,a.operation_id", StringComparison.Ordinal);
        if (suffix is not null)
        {
            var at = projection.IndexOf("j.final_outcome_attempt_id", StringComparison.Ordinal);
            at.Should().BeGreaterThan(0);
            projection = projection[..at] + "@selected::uuid,@status::text,@reason::text";
        }
        await using var command = new NpgsqlCommand("SELECT " + projection + BackgroundJobStoredExternalFacts.From + " j.id=@id",
            (NpgsqlConnection)app, (NpgsqlTransaction)transaction);
        command.Parameters.AddWithValue("id", receipt.Envelope.JobId.Value);
        command.Parameters.AddWithValue("normal_attempt", receipt.AttemptId.Value);
        command.Parameters.AddWithValue("normal_ordinal", receipt.NormalAttemptOrdinal);
        if (stamp == SelectionStamp.Mismatch) command.Parameters.AddWithValue("wrong_fingerprint", new string('b', 64));
        if (suffix is { } supplied)
        {
            command.Parameters.AddWithValue("selected", NpgsqlDbType.Uuid, (object?)supplied.Attempt ?? DBNull.Value);
            command.Parameters.AddWithValue("status", NpgsqlDbType.Text, (object?)supplied.Status ?? DBNull.Value);
            command.Parameters.AddWithValue("reason", NpgsqlDbType.Text, (object?)supplied.Reason ?? DBNull.Value);
        }
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        row.FieldCount.Should().Be(21);
        return BackgroundJobStoredExternalFacts.Read(row, Fixture.Registry.Resolve(BackgroundJobRuntimeFixture.ExternalKey).Definition!,
            receipt.Envelope, receipt.NormalAttemptOrdinal, receipt.AttemptId);
    }
}
