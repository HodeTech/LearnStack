using System.Text.Json;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobExternalCheckpointStoreTests
{
    [Theory]
    [InlineData(false, false, null, 1, false)]
    [InlineData(true, false, null, 1, false)]
    [InlineData(true, true, null, 1, false)]
    [InlineData(false, false, false, 1, false)]
    [InlineData(false, false, true, 1, false)]
    [InlineData(true, false, false, 1, false)]
    [InlineData(true, false, true, 1, false)]
    [InlineData(false, false, null, 6, false)]
    [InlineData(true, false, null, 6, false)]
    [InlineData(false, false, null, 6, true)]
    public async Task Actual_clean_finalizer_disposition_preserves_decision_and_exact_commit_identity(
        bool permanent, bool expiredOwner, bool? commitLanded, int finalizeOrdinal, bool selectedFailure)
    {
        // Explicit supplied provider observation isolates the real app Finalize
        // and control transaction. Native/provider execution has separate proofs.
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var definition = Fixture.Registry.Resolve(BackgroundJobRuntimeFixture.ExternalKey).Definition!;
        await using var prepareHost = RecoveryHost(database, preparing: true);
        var prepareExecutor = prepareHost.Services.GetRequiredService<JobExecutor>();
        var job = await ExternalRetryJobAsync(database);
        var normal = await AdmitFixtureAsync(prepareHost.Services, database, await FixtureClaim.ClaimAsync(database, job));
        (await prepareExecutor.ExecuteBusinessAsync(normal, normal.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare)))
            .CommitAcknowledged.Should().BeTrue();
        var read = await prepareExecutor.ReadExternalStateAsync(normal, CancellationToken.None);
        var prepared = read.Handle!.Require(normal);
        var arm = normal.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm);
        (await SendCheckpointAsync(prepareHost.Services, normal,
            new RecordBackgroundJobArmCommand(normal, arm, prepared.PlanFingerprint!, prepared.OperationId!.Value), arm)).IsSuccess.Should().BeTrue();
        var outcome = normal.Run.OpenBoundary(BackgroundJobCheckpointKind.Outcome);
        (await SendCheckpointAsync(prepareHost.Services, normal, new RecordBackgroundJobOutcomeCommand(normal, outcome,
            prepared.PlanFingerprint!, prepared.OperationId!.Value, "{\"ArtifactId\":\"" + Guid.NewGuid().ToString("D") + "\"}",
            selectedFailure ? BackgroundJobExternalState.Failed : BackgroundJobExternalState.Succeeded,
            selectedFailure ? BackgroundJobExternalFailurePolicy.Permanent : null, Guid.NewGuid(), BackgroundJobQuiescenceKind.ProviderFenced), outcome))
            .IsSuccess.Should().BeTrue();
        normal.Run.Close();
        normal.SourceOwner.CloseIssuance();
        // The prefix fixture admits normal attempts only. Finalize must use
        // the actual production admission handler and its separate counter.
        await using var host = Fixture.Host(database, new BackgroundJobFixtureProbe(), configure: services =>
        {
            services.AddSingleton(new FinalizerControlRefusal(permanent, finalizeOrdinal));
            services.AddTransient(typeof(IRequestHandler<,>).MakeGenericType(definition.ProfileRegistration.FinalizeRequestType!,
                typeof(Result<None>)), typeof(FinalizerControlRefusalHandler<>).MakeGenericType(definition.ProfileRegistration.FinalizeRequestType!));
        });
        var executor = host.Services.GetRequiredService<JobExecutor>();
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var control = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        ConfirmedStartReceipt receipt = null!;
        JobBusinessExecutionResult failed = null!;
        BackgroundJobExecutionIdentity identity = null!;
        var admittedIds = new HashSet<Guid>();
        var delays = new[] { 1, 5, 30, 300, 3600 };
        for (var ordinal = 1; ordinal <= finalizeOrdinal; ordinal++)
        {
            var started = await executor.ExecuteAsync(await FixtureClaim.ClaimAsync(database, job), BackgroundJobTestRun.Create());
            started.Outcome.Should().Be(JobStartExecutionOutcome.Started);
            receipt = started.Receipt!;
            receipt.FinalizeAttemptOrdinal.Should().Be(ordinal);
            receipt.NormalAttemptOrdinal.Should().Be(1);
            admittedIds.Add(receipt.AttemptId.Value).Should().BeTrue();
            failed = await executor.ExecuteBusinessAsync(receipt, receipt.Run.OpenBoundary(BackgroundJobCheckpointKind.Finalize));
            failed.KnownRollback.Should().BeTrue();
            failed.CleanupFailed.Should().BeFalse();
            failed.SourceOwner.CleanupConfirmed.Should().BeTrue();
            failed.Decision.ErrorCode.Should().Be(permanent && ordinal == finalizeOrdinal
                ? "permanent_failure" : "concurrency_conflict");
            identity = BackgroundJobExecutionIdentity.FromReceipt(receipt);
            receipt.Run.Close();
            receipt.SourceOwner.CloseIssuance();
            if (ordinal == finalizeOrdinal) break;
            var priorClock = await RetryDatabaseClockAsync(database);
            var retry = await control.FinishExternalFinalizeAsync(identity, failed, null, CancellationToken.None);
            retry.ThrowIfFailed();
            retry.CommitAcknowledged.Should().BeTrue();
            retry.CleanupConfirmed.Should().BeTrue();
            var scheduled = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
            scheduled.Phase.Should().Be("finalize_pending");
            scheduled.AvailableAfter.Should().BeOnOrAfter(priorClock.AddSeconds(delays[ordinal - 1]));
            // Keep every real committed admission and its actual backoff proof;
            // explicit fixture eligibility avoids waiting over an hour here.
            await using var eligibility = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
            await SchemaQueries.ExecuteAsync(eligibility, null,
                "UPDATE background_jobs SET available_after=clock_timestamp() WHERE id=@id AND phase='finalize_pending'",
                ("id", job.Value));
        }
        if (expiredOwner)
        {
            // Model queue/lock delay after the actual permanent result and its
            // cleanup. Do not replace its real token, receipt or classifier.
            await using var sql = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
            await SchemaQueries.ExecuteAsync(sql, null,
                "UPDATE background_jobs SET ownership_until=clock_timestamp() WHERE id=@id AND ownership_token=@token",
                ("id", job.Value), ("token", identity.Token));
        }
        var before = await RetryDatabaseClockAsync(database);
        await using var proxy = new CommitResponseDroppingProxy(database.OutboxConnectionString,
            dropCommitRequest: commitLanded == false);
        await using var brokenSource = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(proxy.ConnectionString));
        var actualStore = commitLanded is null ? control
            : new PostgresBackgroundJobControlStore(brokenSource, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var changed = await actualStore.FinishExternalFinalizeAsync(identity, failed, null, CancellationToken.None);
        changed.CleanupConfirmed.Should().BeTrue();
        changed.CommitAcknowledged.Should().Be(commitLanded is null);
        if (commitLanded is not null)
        {
            changed.CommitUncertain.Should().BeTrue();
            changed.Value.Should().BeNull();
            proxy.DroppedCommitRequests.Should().Be(commitLanded.Value ? 0 : 1);
            proxy.DroppedCommitResponses.Should().Be(commitLanded.Value ? 1 : 0);
        }
        else changed.ThrowIfFailed();
        var descriptor = changed.Probes.Should().ContainSingle().Subject;
        var observed = await control.ProbeCommitAsync(descriptor);
        observed.ThrowIfFailed();
        observed.Value!.Disposition.Should().Be(commitLanded == false
            ? BackgroundJobProbeDisposition.RolledBack : BackgroundJobProbeDisposition.ControlCommitted);
        foreach (var substituted in new[]
        {
            descriptor with { Token = Guid.NewGuid() },
            descriptor with { IsRecovery = !descriptor.IsRecovery },
            descriptor with { Boundary = BackgroundJobCommitBoundary.Business, Token = Guid.NewGuid() },
            descriptor with { Boundary = BackgroundJobCommitBoundary.Control, Token = Guid.NewGuid() },
            descriptor with { FinalizeOrdinal = descriptor.FinalizeOrdinal == 6 ? 5 : 6 },
            descriptor with { Ordinal = 2 },
            descriptor with { ExpectedTerminalReason = descriptor.ExpectedTerminalReason == "finalize_exhausted" ? "finalize_refused" : "finalize_exhausted" },
        })
        {
            var wrong = await control.ProbeCommitAsync(substituted);
            wrong.ThrowIfFailed();
            wrong.Value!.Disposition.Should().BeOneOf(BackgroundJobProbeDisposition.Unresolved,
                BackgroundJobProbeDisposition.ChangedOwner);
        }
        if (commitLanded == false)
        {
            var retry = await control.FinishExternalFinalizeAsync(identity, failed, null, CancellationToken.None);
            retry.ThrowIfFailed();
            retry.CommitAcknowledged.Should().BeTrue();
        }
        var stored = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
        stored.Phase.Should().Be(permanent || finalizeOrdinal == 6 ? "terminal_pending" : "finalize_pending");
        stored.NormalAttempts.Should().Be(1);
        stored.CurrentAttempt.Should().Be(receipt.AttemptId.Value);
        stored.OwnershipToken.Should().BeNull();
        stored.OwnershipKind.Should().BeNull();
        stored.OwnershipUntil.Should().BeNull();
        if (!permanent && finalizeOrdinal < 6) stored.AvailableAfter.Should().BeOnOrAfter(before.AddSeconds(1));
        await AssertFinalizerControlSelectedFactsAsync(database, receipt, normal, prepared, permanent ? "finalize_refused" : finalizeOrdinal == 6 ? "finalize_exhausted" : null, finalizeOrdinal, selectedFailure);
        if (selectedFailure)
            await AssertSelectedFailureFinalizerExhaustionAuditAsync(database, host.Services, source, runtime, normal, receipt);
    }

    private static async Task AssertFinalizerControlSelectedFactsAsync(DisposableSchemaDatabase database, ConfirmedStartReceipt receipt,
        ConfirmedStartReceipt normal, BackgroundJobExternalSnapshot prepared, string? terminalReason, int finalizeOrdinal, bool selectedFailure)
    {
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        await using var inspect = new NpgsqlCommand("""
            SELECT j.finalize_attempts,j.final_outcome_attempt_id,j.finalization_status,j.finalization_reason,
                j.terminal_reason,j.plan_fingerprint,j.operation_id,j.business_finalization_incomplete,
                n.outcome_kind,n.failure_policy,r.release_reason,
                (SELECT count(*) FROM background_job_reservations x WHERE x.job_id=j.id),
                (SELECT count(*) FROM background_job_attempts x WHERE x.job_id=j.id),
                (SELECT count(*) FROM audit_log a WHERE a.operation='platform.job.dead_letter' AND a.metadata->>'job_id'=@text)
            FROM background_jobs j JOIN background_job_attempts n ON n.id=j.final_outcome_attempt_id
                JOIN background_job_reservations r ON r.attempt_id=n.id WHERE j.id=@id
            """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction) { CommandTimeout = 5 };
        inspect.Parameters.AddWithValue("id", receipt.Envelope.JobId.Value);
        inspect.Parameters.AddWithValue("text", receipt.Envelope.JobId.Value.ToString("D"));
        await using var row = await inspect.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        row.GetInt32(0).Should().Be(finalizeOrdinal);
        row.GetGuid(1).Should().Be(normal.AttemptId.Value);
        row.GetString(2).Should().Be(selectedFailure ? "failed" : "succeeded");
        row.GetString(3).Should().Be(selectedFailure ? "external_failed" : "none");
        (row.IsDBNull(4) ? null : row.GetString(4)).Should().Be(terminalReason);
        row.GetString(5).Should().Be(prepared.PlanFingerprint);
        row.GetGuid(6).Should().Be(prepared.OperationId!.Value);
        row.GetBoolean(7).Should().BeFalse("the request has not yet committed its bypass terminal audit");
        row.GetString(8).Should().Be(selectedFailure ? "failed" : "succeeded");
        row.IsDBNull(9).Should().Be(!selectedFailure);
        if (selectedFailure) row.GetString(9).Should().Be("permanent");
        row.GetString(10).Should().Be("quiescent");
        row.GetInt64(11).Should().Be(1);
        row.GetInt64(12).Should().Be(finalizeOrdinal + 1, "one normal and separately counted Finalize admissions; control inserts no ledger");
        row.GetInt64(13).Should().Be(0);
    }

    private static async Task AssertSelectedFailureFinalizerExhaustionAuditAsync(DisposableSchemaDatabase database,
        IServiceProvider services, PrivilegedDataSource source, BackgroundJobRuntimeGate runtime,
        ConfirmedStartReceipt normal, ConfirmedStartReceipt finalizer)
    {
        var terminal = new PostgresBackgroundJobTerminalStore(source, services.GetRequiredService<IServiceScopeFactory>(),
            new(), new SystemGuidFactory(), runtime);
        var acquired = await terminal.TryClaimTerminalAsync([]);
        acquired.ThrowIfFailed();
        acquired.CommitAcknowledged.Should().BeTrue();
        acquired.CleanupConfirmed.Should().BeTrue();
        acquired.Value.Should().NotBeNull();
        var claim = acquired.Value!;
        claim.Prepared!.FinalizeAttempts.Should().Be(6);
        claim.Prepared.State.Should().Be("failed");
        claim.Prepared.Policy.Should().Be("permanent");
        var completed = await terminal.TryTerminalizeAsync(claim, finalizer.Envelope.Traceparent);
        completed.ThrowIfFailed();
        completed.CommitAcknowledged.Should().BeTrue();
        completed.CleanupConfirmed.Should().BeTrue();
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        await using (var state = new NpgsqlCommand("""
            SELECT dead_lettered_at IS NOT NULL,business_finalization_incomplete,finalization_status,finalization_reason,
                terminal_reason,normal_attempts,finalize_attempts FROM background_jobs WHERE id=@id
            """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction) { CommandTimeout = 5 })
        {
            state.Parameters.AddWithValue("id", finalizer.Envelope.JobId.Value);
            await using var row = await state.ExecuteReaderAsync();
            (await row.ReadAsync()).Should().BeTrue();
            row.GetBoolean(0).Should().BeTrue();
            row.GetBoolean(1).Should().BeTrue();
            row.GetString(2).Should().Be("failed");
            row.GetString(3).Should().Be("external_failed");
            row.GetString(4).Should().Be("finalize_exhausted");
            row.GetInt32(5).Should().Be(1);
            row.GetInt32(6).Should().Be(6);
        }
        await using var audit = new NpgsqlCommand("""
            SELECT metadata::text FROM audit_log WHERE operation='platform.job.dead_letter' AND metadata->>'job_id'=@id
            """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction) { CommandTimeout = 5 };
        audit.Parameters.AddWithValue("id", finalizer.Envelope.JobId.Value.ToString("D"));
        await using var audits = await audit.ExecuteReaderAsync();
        (await audits.ReadAsync()).Should().BeTrue();
        using var metadata = JsonDocument.Parse(audits.GetString(0));
        var facts = metadata.RootElement;
        facts.GetProperty("reason").GetString().Should().Be("finalize_exhausted");
        facts.GetProperty("selected_normal_attempt_id").GetGuid().Should().Be(normal.AttemptId.Value);
        facts.GetProperty("attempt_id").GetGuid().Should().Be(finalizer.AttemptId.Value);
        facts.GetProperty("acquisition_token").GetGuid().Should().Be(claim.Token);
        facts.GetProperty("external_state").GetString().Should().Be("failed");
        facts.GetProperty("external_failure_policy").GetString().Should().Be("permanent");
        facts.GetProperty("external_retry_permitted").GetBoolean().Should().BeFalse();
        facts.GetProperty("finalization_reason").GetString().Should().Be("external_failed");
        facts.GetProperty("business_finalization_incomplete").GetBoolean().Should().BeTrue();
        (await audits.ReadAsync()).Should().BeFalse();
    }

    private sealed record FinalizerControlRefusal(bool Permanent, int PermanentOrdinal = 1)
    {
        private int _calls;
        internal bool NextIsPermanent() => Interlocked.Increment(ref _calls) >= PermanentOrdinal && Permanent;
    }
    private sealed class FinalizerControlRefusalHandler<TRequest>(FinalizerControlRefusal refusal)
        : IRequestHandler<TRequest, Result<None>> where TRequest : notnull, IRequest<Result<None>>
    {
        public Task<Result<None>> Handle(TRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(Result<None>.Fail(new Error(new LocalizedMessage(refusal.NextIsPermanent()
                ? "lockey_validation_failed" : "lockey_concurrency_conflict"))));
    }
}
