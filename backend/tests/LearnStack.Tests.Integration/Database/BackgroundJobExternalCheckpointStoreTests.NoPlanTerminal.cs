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
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobExternalCheckpointStoreTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public async Task No_plan_terminal_request_preserves_actual_prepare_refusal_before_no_start_release_or_audit(int ordinal)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var host = NoPlanRefusalHost(database);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var normal = await NoPlanOrdinalAsync(database, host.Services, ordinal, store, runtime);
        var prepared = await host.Services.GetRequiredService<JobExecutor>().ExecuteBusinessAsync(normal,
            normal.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare));
        prepared.KnownRollback.Should().BeTrue();
        prepared.CleanupFailed.Should().BeFalse();
        prepared.ExecutionIssued.Should().BeTrue();
        prepared.Decision.TerminalReason.Should().Be(BackgroundJobTerminalReason.PermanentFailure);
        var immutable = await RetainedSelectionFactsAsync(database, normal);
        var identity = BackgroundJobExecutionIdentity.FromReceipt(normal);
        var request = await store.RequestNoPlanExternalTerminalAsync(identity,
            prepared.Decision.TerminalReason!.Value, CancellationToken.None);
        request.ThrowIfFailed();
        request.CommitAcknowledged.Should().BeTrue();
        request.CleanupConfirmed.Should().BeTrue();
        request.Value.Should().Be(new BackgroundJobControlChange(normal.Envelope.JobId.Value, identity.WakeGeneration,
            "terminal_pending", "permanent_failure"));
        (await RetainedSelectionFactsAsync(database, normal)).Should().Be(immutable,
            "the durable request precedes app NoStart, quiescence or release");
        await AssertNoPlanRequestAsync(database, normal, "permanent_failure");
        var probe = await store.ProbeCommitAsync(request.Probes.Should().ContainSingle().Subject);
        probe.ThrowIfFailed();
        probe.Value!.Disposition.Should().Be(BackgroundJobProbeDisposition.ControlCommitted);
        probe.Value.RecoveryReceipt.Should().BeNull();
        probe.Value.CurrentRecoveryAuthority.Should().BeFalse();
        probe.CommitAcknowledged.Should().BeFalse();
        var routing = new PostgresBackgroundJobRoutingStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        (await routing.TryClaimAsync(normal.Envelope.JobId, identity.WakeGeneration)).Should().BeNull();
        var bridge = new PostgresBackgroundJobBridgeStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        (await bridge.TryClaimAsync([])).Should().BeNull();
        var duplicate = await store.RequestNoPlanExternalTerminalAsync(identity,
            BackgroundJobTerminalReason.PermanentFailure, CancellationToken.None);
        duplicate.ThrowIfFailed();
        duplicate.CommitAcknowledged.Should().BeFalse();
        duplicate.Value.Should().BeNull();
        normal.Run.Close();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task No_plan_terminal_request_lost_commit_distinguishes_the_exact_reason_from_rollback(bool committed)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var host = NoPlanRefusalHost(database);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var normal = await NoPlanOrdinalAsync(database, host.Services, 6, store, runtime);
        var prepared = await host.Services.GetRequiredService<JobExecutor>().ExecuteBusinessAsync(normal,
            normal.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare));
        prepared.KnownRollback.Should().BeTrue();
        prepared.Decision.TerminalReason.Should().Be(BackgroundJobTerminalReason.PermanentFailure);
        var before = await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, normal.Envelope.JobId);
        var immutable = await RetainedSelectionFactsAsync(database, normal);
        await using var proxy = new CommitResponseDroppingProxy(database.OutboxConnectionString, dropCommitRequest: !committed);
        await using var brokenSource = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(proxy.ConnectionString));
        var broken = new PostgresBackgroundJobControlStore(brokenSource, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var request = await broken.RequestNoPlanExternalTerminalAsync(BackgroundJobExecutionIdentity.FromReceipt(normal),
            BackgroundJobTerminalReason.PermanentFailure, CancellationToken.None);
        request.CommitAcknowledged.Should().BeFalse();
        request.CommitUncertain.Should().BeTrue();
        request.CleanupConfirmed.Should().BeTrue();
        request.Value.Should().BeNull();
        proxy.DroppedCommitRequests.Should().Be(committed ? 0 : 1);
        proxy.DroppedCommitResponses.Should().Be(committed ? 1 : 0);
        var descriptor = request.Probes.Should().ContainSingle().Subject;
        descriptor.ExpectedTerminalReason.Should().Be("permanent_failure");
        var proof = await store.ProbeCommitAsync(descriptor);
        proof.ThrowIfFailed();
        proof.Value!.Disposition.Should().Be(committed
            ? BackgroundJobProbeDisposition.ControlCommitted : BackgroundJobProbeDisposition.RolledBack);
        proof.CleanupConfirmed.Should().BeTrue();
        request.CommitAcknowledged.Should().BeFalse("history never rewrites the writer's physical ACK");
        (await RetainedSelectionFactsAsync(database, normal)).Should().Be(immutable);
        if (!committed)
            (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, normal.Envelope.JobId)).Should().Be(before);
        else
        {
            await AssertNoPlanRequestAsync(database, normal, "permanent_failure");
            var otherReason = await store.ProbeCommitAsync(descriptor with { ExpectedTerminalReason = "retry_exhausted" });
            otherReason.ThrowIfFailed();
            otherReason.Value!.Disposition.Should().Be(BackgroundJobProbeDisposition.Unresolved,
                "the same sixth admission cannot certify a different terminal decision");
        }
        normal.Run.Close();
    }

    [Fact]
    public async Task No_plan_sixth_recovery_requests_exhaustion_without_spending_a_seventh_admission_or_releasing_capacity()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var host = NoPlanRefusalHost(database);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var normal = await NoPlanOrdinalAsync(database, host.Services, 6, store, runtime);
        await ExpireSelectionLeaseAsync(database, normal);
        var acquired = await store.AcquireExternalRecoveryAsync(normal.Envelope.JobId,
            await RecoveryGenerationAsync(database, normal.Envelope.JobId), normal.Envelope.Queue, CancellationToken.None);
        acquired.ThrowIfFailed();
        acquired.CommitAcknowledged.Should().BeTrue();
        var identity = BackgroundJobExecutionIdentity.FromRecovery(acquired.Value!);
        var request = await store.RequestNoPlanExternalTerminalAsync(identity,
            BackgroundJobTerminalReason.RetryExhausted, CancellationToken.None);
        request.ThrowIfFailed();
        request.CommitAcknowledged.Should().BeTrue();
        request.Value!.TerminalReason.Should().Be("retry_exhausted");
        await AssertNoPlanRequestAsync(database, normal, "retry_exhausted");
        var probe = await store.ProbeCommitAsync(request.Probes.Should().ContainSingle().Subject);
        probe.ThrowIfFailed();
        probe.Value!.Disposition.Should().Be(BackgroundJobProbeDisposition.ControlCommitted);
        probe.Value.NormalAttempts.Should().Be(6);
        var cleanup = await store.AcquireExternalTerminalNoStartAsync([], CancellationToken.None);
        cleanup.ThrowIfFailed();
        cleanup.Value!.TerminalReason.Should().Be("retry_exhausted");
        var binding = BindRecovery(cleanup, runtime);
        var executor = host.Services.GetRequiredService<JobExecutor>();
        var read = await executor.ReadExternalStateAsync(binding, CancellationToken.None);
        read.CommitAcknowledged.Should().BeTrue();
        var written = await executor.RecordExternalTerminalNoStartAsync(binding, read.Handle!,
            binding.Run.OpenBoundary(BackgroundJobCheckpointKind.NoStart), CancellationToken.None);
        written.CommitAcknowledged.Should().BeTrue();
        written.CleanupFailed.Should().BeFalse();
        await AssertTerminalNoStartPairAsync(database, normal, cleanup.Value.RecoveryToken, "retry_exhausted");
        binding.Run.Close();
        binding.SourceOwner.CloseIssuance();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task No_plan_request_refuses_a_committed_plan_or_an_expired_original_without_changing_history(bool preparedPlan)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var host = RecoveryHost(database, preparing: true);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var job = await ExternalRetryJobAsync(database);
        var normal = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        if (preparedPlan)
            (await host.Services.GetRequiredService<JobExecutor>().ExecuteBusinessAsync(normal,
                normal.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare))).CommitAcknowledged.Should().BeTrue();
        else
        {
            await using var sql = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
            await SchemaQueries.ExecuteAsync(sql, null,
                "UPDATE background_jobs SET ownership_until=clock_timestamp() WHERE id=@id", ("id", job.Value));
        }
        var before = await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job);
        var immutable = await RetainedSelectionFactsAsync(database, normal);
        var refused = await store.RequestNoPlanExternalTerminalAsync(BackgroundJobExecutionIdentity.FromReceipt(normal),
            BackgroundJobTerminalReason.PermanentFailure, CancellationToken.None);
        refused.ThrowIfFailed();
        refused.Value.Should().BeNull();
        refused.CommitAcknowledged.Should().BeFalse();
        refused.CleanupConfirmed.Should().BeTrue();
        refused.Probes.Should().BeEmpty();
        (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job)).Should().Be(before);
        (await RetainedSelectionFactsAsync(database, normal)).Should().Be(immutable);
        normal.Run.Close();
    }

    private static async Task<ConfirmedStartReceipt> NoPlanOrdinalAsync(DisposableSchemaDatabase database,
        IServiceProvider services, int target, PostgresBackgroundJobControlStore store, BackgroundJobRuntimeGate runtime)
    {
        var job = await ExternalRetryJobAsync(database);
        for (var ordinal = 1; ordinal <= target; ordinal++)
        {
            var normal = await AdmitFixtureAsync(services, database, await FixtureClaim.ClaimAsync(database, job));
            normal.NormalAttemptOrdinal.Should().Be(ordinal);
            normal.Stage.Should().Be(BackgroundJobExecutionStage.Prepare);
            if (ordinal == target) return normal;
            await ExpireSelectionLeaseAsync(database, normal);
            var acquired = await store.AcquireExternalRecoveryAsync(job, await RecoveryGenerationAsync(database, job),
                normal.Envelope.Queue, CancellationToken.None);
            acquired.ThrowIfFailed();
            var recovery = BindRecovery(acquired, runtime);
            var executor = services.GetRequiredService<JobExecutor>();
            var read = await executor.ReadExternalStateAsync(recovery, CancellationToken.None);
            read.Handle!.Snapshot.PlanJson.Should().BeNull();
            (await executor.RecordExternalNoStartAsync(recovery, read.Handle!,
                recovery.Run.OpenBoundary(BackgroundJobCheckpointKind.NoStart), CancellationToken.None))
                .CommitAcknowledged.Should().BeTrue();
            recovery.Run.Close();
            // Explicit store-fixture eligibility setup, not native retry pacing.
            await using var sql = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
            await SchemaQueries.ExecuteAsync(sql, null, """
                UPDATE background_jobs SET phase='pending',ownership_kind=NULL,ownership_token=NULL,ownership_until=NULL
                WHERE id=@id AND phase='reconciling'
                """, ("id", job.Value));
        }
        throw new InvalidOperationException("The requested normal fixture was not admitted.");
    }

    private static async Task AssertNoPlanRequestAsync(DisposableSchemaDatabase database, ConfirmedStartReceipt normal, string reason)
    {
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, normal.Envelope.TenantId.Value, normal.Envelope.OrganizationId?.Value);
        await using var command = new NpgsqlCommand("""
            SELECT j.phase,j.terminal_reason,j.normal_attempts,j.finalize_attempts,
                j.ownership_token IS NULL AND j.ownership_kind IS NULL AND j.ownership_until IS NULL,
                j.plan IS NULL AND j.finalization_status IS NULL AND j.dead_lettered_at IS NULL,
                a.no_start_at IS NULL AND a.execute_intent_at IS NULL AND a.outcome IS NULL,
                r.released_at IS NULL,(SELECT count(*) FROM background_job_attempts n WHERE n.job_id=j.id),
                (SELECT count(*) FROM audit_log audit WHERE audit.metadata->>'job_id'=j.id::text AND audit.operation='platform.job.dead_letter')
            FROM background_jobs j JOIN background_job_attempts a ON a.id=j.current_attempt_id
                JOIN background_job_reservations r ON r.attempt_id=a.id WHERE j.id=@id
            """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("id", normal.Envelope.JobId.Value);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        row.GetString(0).Should().Be("terminal_pending");
        row.GetString(1).Should().Be(reason);
        row.GetInt32(2).Should().Be(normal.NormalAttemptOrdinal);
        row.GetInt32(3).Should().Be(0);
        for (var column = 4; column <= 7; column++) row.GetBoolean(column).Should().BeTrue();
        row.GetInt64(8).Should().Be(normal.NormalAttemptOrdinal);
        row.GetInt64(9).Should().Be(0, "the request is not terminal audit completion");
    }

    private static WebApplicationFactory<Program> NoPlanRefusalHost(DisposableSchemaDatabase database,
        bool emptyDefinitions = false, string? appConnectionString = null, TimeSpan? admissionWindow = null) =>
        RecoveryHost(database, preparing: true, configure: services =>
        {
            var profile = Fixture.Registry.Resolve(BackgroundJobRuntimeFixture.ExternalKey).Definition!.ProfileRegistration;
            var service = typeof(IRequestHandler<,>).MakeGenericType(profile.RootRequestType,
                typeof(Result<>).MakeGenericType(profile.PlanType!));
            services.RemoveAll(service);
            services.AddTransient(service, typeof(NoPlanRefusalHandler<,>).MakeGenericType(profile.RootRequestType, profile.PlanType!));
            if (admissionWindow is { } window)
            {
                services.RemoveAll<FixtureAdmissionWindow>();
                services.AddSingleton(new FixtureAdmissionWindow(window));
            }
            if (emptyDefinitions)
            {
                services.RemoveAll<BackgroundJobDefinitionRegistry>();
                services.AddSingleton(BackgroundJobDefinitionRegistry.CreateEmpty());
            }
        }, appConnectionString: appConnectionString);

    private sealed class NoPlanRefusalHandler<TRequest, TPlan> : IRequestHandler<TRequest, Result<TPlan>>
        where TRequest : notnull, IRequest<Result<TPlan>> where TPlan : IBackgroundJobPlan
    {
        public Task<Result<TPlan>> Handle(TRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(Result<TPlan>.Fail(new Error(new LocalizedMessage("lockey_validation_failed"))));
    }
}
