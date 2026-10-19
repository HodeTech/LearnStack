using System.Text.Json;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobExternalCheckpointStoreTests
{
    [Theory]
    [InlineData(BackgroundJobExternalState.Succeeded, null, null)]
    [InlineData(BackgroundJobExternalState.Failed, BackgroundJobExternalFailurePolicy.Permanent, "permanent_failure")]
    [InlineData(BackgroundJobExternalState.Failed, BackgroundJobExternalFailurePolicy.TransientNotRepeatable, "external_retry_forbidden")]
    [InlineData(BackgroundJobExternalState.Cancelled, BackgroundJobExternalFailurePolicy.TransientNotRepeatable, "external_retry_forbidden")]
    public async Task Actual_app_finalize_admission_and_owner_preserve_selected_facts_without_reserving_or_reexecuting_external_work(
        BackgroundJobExternalState state, BackgroundJobExternalFailurePolicy? policy, string? terminalCode)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external");
        ConfirmedStartReceipt normal;
        BackgroundJobExternalSnapshot prepared;
        var outcomeJson = "{\"ArtifactId\":\"" + Guid.NewGuid().ToString("D") + "\"}";
        // Explicit lower-layer normal admission and supplied observation. This
        // test proves the real Finalize path, not a successful adapter/native run.
        await using (var prepareHost = RecoveryHost(database, preparing: true))
        {
            normal = await AdmitFixtureAsync(prepareHost.Services, database, await FixtureClaim.ClaimAsync(database, job));
            var executor = prepareHost.Services.GetRequiredService<JobExecutor>();
            (await executor.ExecuteBusinessAsync(normal, normal.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare)))
                .CommitAcknowledged.Should().BeTrue();
            prepared = (await executor.ReadExternalStateAsync(normal, CancellationToken.None)).Handle!.Require(normal);
            var arm = normal.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm);
            (await SendCheckpointAsync(prepareHost.Services, normal,
                new RecordBackgroundJobArmCommand(normal, arm, prepared.PlanFingerprint!, prepared.OperationId!.Value), arm))
                .IsSuccess.Should().BeTrue();
            var completed = normal.Run.OpenBoundary(BackgroundJobCheckpointKind.Outcome);
            (await SendCheckpointAsync(prepareHost.Services, normal, new RecordBackgroundJobOutcomeCommand(normal, completed,
                prepared.PlanFingerprint!, prepared.OperationId!.Value, outcomeJson, state, policy, Guid.NewGuid(),
                BackgroundJobQuiescenceKind.ProviderFenced), completed)).IsSuccess.Should().BeTrue();
        }

        var probe = new FinalizeProbe();
        var diagnostics = new CheckpointDiagnostics();
        var definition = Fixture.Registry.Resolve(BackgroundJobRuntimeFixture.ExternalKey).Definition!;
        await using var host = Fixture.Host(database, new BackgroundJobFixtureProbe(), configure: services =>
        {
            services.AddSingleton(probe);
            services.AddSingleton(diagnostics);
            services.Insert(0, ServiceDescriptor.Transient(typeof(IPipelineBehavior<,>), typeof(FixtureDiagnosticsBehavior<,>)));
            services.AddTransient(typeof(IRequestHandler<,>).MakeGenericType(definition.ProfileRegistration.FinalizeRequestType!,
                typeof(Result<None>)), typeof(FixtureFinalizeHandler<>).MakeGenericType(definition.ProfileRegistration.FinalizeRequestType!));
        });
        var claim = await FixtureClaim.ClaimAsync(database, job);
        claim.LedgerStage.Should().Be(BackgroundJobLedgerStage.Finalize);
        // The confined routing claim is explicit fixture setup; envelope reading,
        // counted admission, immutable receipt and business owner are production.
        var started = await host.Services.GetRequiredService<JobExecutor>().ExecuteAsync(claim, BackgroundJobTestRun.Create());
        started.Outcome.Should().Be(JobStartExecutionOutcome.Started, "actual Finalize admission: {0}/{1}",
            diagnostics.FailureType, diagnostics.SqlState);
        started.CleanupFailed.Should().BeFalse();
        var receipt = started.Receipt!;
        receipt.Stage.Should().Be(BackgroundJobExecutionStage.Finalize);
        receipt.NormalAttemptOrdinal.Should().Be(1);
        receipt.FinalizeAttemptOrdinal.Should().Be(1);
        receipt.SelectedNormalAttemptId.Should().Be(normal.AttemptId);
        receipt.AttemptId.Should().NotBe(normal.AttemptId);

        var result = await host.Services.GetRequiredService<JobExecutor>().ExecuteBusinessAsync(receipt,
            receipt.Run.OpenBoundary(BackgroundJobCheckpointKind.Finalize));
        result.CommitAcknowledged.Should().BeTrue("actual Finalize fence: {0}/{1}; {2}/{3}/{4}",
            result.Decision.Disposition, result.Decision.ErrorCode, diagnostics.FailureType, diagnostics.SqlState, diagnostics.ConstraintName);
        result.CleanupFailed.Should().BeFalse();
        result.FinalizationRead.Should().NotBeNull();
        result.FinalizationRead!.CommitAcknowledged.Should().BeTrue();
        receipt.SourceOwner.CleanupConfirmed.Should().BeTrue();
        probe.Calls.Should().Be(1);
        probe.AnnouncedOwner.Should().BeTrue();
        var expected = BackgroundJobExternalPolicy.Select(state, policy, 1);
        probe.Status.Should().Be(expected.FinalizationStatus);
        probe.Reason.Should().Be(expected.FinalizationReason);
        probe.PlanJson.Should().Be(prepared.PlanJson);
        probe.OutcomeJson.Should().Be(outcomeJson);

        BackgroundJobCheckpointHistoryOrigin.ForFinalize(result).Business.Should().BeSameAs(result);
        result.Gate.Close();
        var duplicate = await host.Services.GetRequiredService<JobExecutor>().ExecuteBusinessAsync(receipt, result.Gate);
        duplicate.ExecutionIssued.Should().BeFalse();
        duplicate.CommitStarted.Should().BeFalse();
        duplicate.CommitAcknowledged.Should().BeFalse();
        duplicate.KnownRollback.Should().BeFalse();
        duplicate.DurableOutcomeUncertain.Should().BeFalse();
        duplicate.FinalizationRead.Should().BeNull("a consumed root cannot issue another app read");
        probe.Calls.Should().Be(1);
        Action borrow = () => BackgroundJobCheckpointHistoryOrigin.ForFinalize(duplicate);
        borrow.Should().Throw<InvalidOperationException>();
        result.CommitAcknowledged.Should().BeTrue("duplicate refusal cannot rewrite the original physical ACK");

        await using var outside = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await outside.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(outside, transaction);
        await EducationSchemaSeed.AnnounceAsync(outside, transaction, SchemaFixture.TenantA, null);
        long storedGeneration;
        DateTimeOffset? terminalTimestamp;
        await using (var command = new NpgsqlCommand("""
            SELECT j.phase,j.processed_at IS NOT NULL,j.dead_lettered_at IS NOT NULL,j.terminal_reason,
                j.normal_attempts,j.finalize_attempts,j.final_outcome_attempt_id,j.current_attempt_id,
                j.business_finalization_incomplete,r.released_at IS NOT NULL,
                (SELECT count(*) FROM background_job_reservations x WHERE x.job_id=j.id),
                (SELECT count(*) FROM background_job_attempts a WHERE a.job_id=j.id AND a.stage='finalize'),
                j.wake_generation,j.dead_lettered_at,j.ownership_token IS NULL,j.ownership_kind IS NULL
            FROM background_jobs j JOIN background_job_reservations r ON r.attempt_id=j.final_outcome_attempt_id
            WHERE j.id=@job
            """, (NpgsqlConnection)outside, (NpgsqlTransaction)transaction))
        {
            command.Parameters.AddWithValue("job", job.Value);
            await using var row = await command.ExecuteReaderAsync();
            (await row.ReadAsync()).Should().BeTrue();
            row.GetString(0).Should().Be(terminalCode is null ? "completed" : "terminal_pending");
            row.GetBoolean(1).Should().Be(terminalCode is null);
            row.GetBoolean(2).Should().Be(terminalCode is not null);
            (row.IsDBNull(3) ? null : row.GetString(3)).Should().Be(terminalCode);
            row.GetInt32(4).Should().Be(1);
            row.GetInt32(5).Should().Be(1);
            row.GetGuid(6).Should().Be(normal.AttemptId.Value);
            row.GetGuid(7).Should().Be(receipt.AttemptId.Value);
            row.GetBoolean(8).Should().BeFalse();
            row.GetBoolean(9).Should().BeTrue();
            row.GetInt64(10).Should().Be(1, "Finalize must never reserve external capacity again");
            row.GetInt64(11).Should().Be(1);
            storedGeneration = row.GetInt64(12);
            terminalTimestamp = row.IsDBNull(13) ? null : row.GetFieldValue<DateTimeOffset>(13);
            row.GetBoolean(14).Should().BeTrue();
            row.GetBoolean(15).Should().BeTrue();
        }
        await using var audit = new NpgsqlCommand("""
            SELECT metadata::text,timestamp FROM audit_log WHERE operation='platform.job.dead_letter'
                AND metadata->>'job_id'=@job
            """, (NpgsqlConnection)outside, (NpgsqlTransaction)transaction);
        audit.Parameters.AddWithValue("job", job.Value.ToString("D"));
        await using var audits = await audit.ExecuteReaderAsync();
        (await audits.ReadAsync()).Should().Be(terminalCode is not null);
        if (terminalCode is not null)
        {
            using var metadata = JsonDocument.Parse(audits.GetString(0));
            var facts = metadata.RootElement;
            facts.GetProperty("reason").GetString().Should().Be(terminalCode);
            facts.GetProperty("selected_normal_attempt_id").GetGuid().Should().Be(normal.AttemptId.Value);
            facts.GetProperty("attempt_id").GetGuid().Should().Be(receipt.AttemptId.Value);
            facts.GetProperty("acquisition_token").GetGuid().Should().Be(receipt.Claim.Token,
                "the real terminal trigger clears NEW ownership, so the witness must return OLD");
            facts.GetProperty("wake_generation").GetInt64().Should().Be(storedGeneration);
            facts.GetProperty("normal_attempts").GetInt32().Should().Be(1);
            facts.GetProperty("finalize_attempts").GetInt32().Should().Be(1);
            facts.GetProperty("external_state").GetString().Should().Be(state.ToString().ToLowerInvariant());
            facts.GetProperty("finalization_status").GetString().Should().Be("failed");
            facts.GetProperty("finalization_reason").GetString().Should().Be(state == BackgroundJobExternalState.Cancelled
                ? "external_cancelled" : "external_failed");
            facts.GetProperty("external_failure_policy").GetString().Should().Be(policy == BackgroundJobExternalFailurePolicy.Permanent
                ? "permanent" : "transient_not_repeatable");
            facts.GetProperty("external_failure_class").GetString().Should().Be(policy == BackgroundJobExternalFailurePolicy.Permanent
                ? "permanent" : "transient");
            facts.GetProperty("external_retry_permitted").GetBoolean().Should().BeFalse();
            facts.GetProperty("business_finalization_incomplete").GetBoolean().Should().BeFalse();
            audits.GetFieldValue<DateTimeOffset>(1).Should().Be(terminalTimestamp!.Value);
            (await audits.ReadAsync()).Should().BeFalse();
        }
    }

    private sealed class FinalizeProbe
    {
        internal int Calls { get; set; }
        internal bool AnnouncedOwner { get; set; }
        internal string? PlanJson { get; set; }
        internal string? OutcomeJson { get; set; }
        internal BackgroundJobFinalizationStatus Status { get; set; }
        internal BackgroundJobFinalizationReason Reason { get; set; }
    }

    private sealed class FixtureFinalizeHandler<TRequest>(FinalizeProbe probe, IUnitOfWork unit, ITenantContextAccessor accessor)
        : IRequestHandler<TRequest, Result<None>> where TRequest : notnull, IRequest<Result<None>>
    {
        public async Task<Result<None>> Handle(TRequest request, CancellationToken cancellationToken)
        {
            await SchemaQueries.AssertApplicationRoleAsync(unit.Connection, unit.Transaction!);
            var announcement = unit.GetTenantContextAnnouncement(unit.Transaction);
            probe.Calls++;
            probe.AnnouncedOwner = unit.Mode == TransactionMode.ReadWrite && announcement is { Generation: > 0 }
                && announcement.TenantId == accessor.Current!.TenantId && announcement.OrganizationId == accessor.Current.OrganizationId;
            var finalization = typeof(TRequest).GetProperty("Finalization")!.GetValue(request)!;
            var type = finalization.GetType();
            var plan = type.GetProperty("Plan")!.GetValue(finalization)!;
            var outcome = type.GetProperty("Outcome")!.GetValue(finalization);
            probe.PlanJson = BackgroundJobPayloadContract.Serialize(plan, plan.GetType(), BackgroundJobPayloadKind.Plan);
            probe.OutcomeJson = outcome is null ? null : BackgroundJobPayloadContract.Serialize(outcome, outcome.GetType(), BackgroundJobPayloadKind.Outcome);
            probe.Status = (BackgroundJobFinalizationStatus)type.GetProperty("Status")!.GetValue(finalization)!;
            probe.Reason = (BackgroundJobFinalizationReason)type.GetProperty("Reason")!.GetValue(finalization)!;
            return Result<None>.Ok(None.Value);
        }
    }
}
