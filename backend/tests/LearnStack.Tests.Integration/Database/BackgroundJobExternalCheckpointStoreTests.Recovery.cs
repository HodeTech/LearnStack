using System.Diagnostics;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Persistence;
using MediatR;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobExternalCheckpointStoreTests
{
    [Fact]
    public async Task Actual_recovery_acquisition_preserves_expired_original_ledger_without_new_counted_attempt_or_execute_permission()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external");
        await using var host = RecoveryHost(database);
        var original = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        await ParkExpiredExternalAsync(database, original);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var acquired = await store.AcquireExternalRecoveryAsync(job, await RecoveryGenerationAsync(database, job), original.Envelope.Queue, CancellationToken.None);
        acquired.ThrowIfFailed();
        acquired.CommitAcknowledged.Should().BeTrue();
        acquired.CleanupConfirmed.Should().BeTrue();
        acquired.Value.Should().NotBeNull();
        var recovery = acquired.Value!;
        recovery.Deadline.Should().Be(original.Deadline).And.BeBefore(recovery.DbNow);
        recovery.AdmittedAt.Should().Be(original.AdmittedAt);
        recovery.AdmissionToken.Should().Be(original.Claim.Token);
        recovery.AttemptId.Should().Be(original.AttemptId);
        recovery.RecoveryToken.Should().NotBe(original.Claim.Token);
        recovery.OwnershipUntil.Should().BeAfter(recovery.DbNow).And.BeAfter(recovery.Deadline);
        recovery.NormalAttemptOrdinal.Should().Be(1);
        var run = new BackgroundJobRunGate(runtime.RunStop);
        BackgroundJobExternalRecoveryBinding binding;
        using (JobApplicationSourceOwner.Create().Enter()) binding = recovery.Bind(run);
        run.TryOpenBoundary(BackgroundJobCheckpointKind.Prepare).Should().BeNull();
        run.TryOpenBoundary(BackgroundJobCheckpointKind.Arm).Should().BeNull();
        run.TryIssueExternalReconcile(binding).Should().BeTrue("only the separately bound recovery entry may issue");
        run.TryIssueExternalReconcile(binding).Should().BeFalse();
        var probe = await store.ProbeExternalRecoveryAsync(acquired, acquired.Probes.Single(), CancellationToken.None);
        probe.ThrowIfFailed();
        probe.CommitAcknowledged.Should().BeFalse("the confirmation is a read-only rollback, not another acquisition COMMIT");
        probe.CleanupConfirmed.Should().BeTrue();
        probe.Value.Should().BeSameAs(recovery, "a second proof of the same token cannot remint a Reconcile permission");
        probe.Value!.RecoveryToken.Should().Be(recovery.RecoveryToken);
        using (JobApplicationSourceOwner.Create().Enter())
        {
            Action duplicate = () => probe.Value.Bind(new BackgroundJobRunGate(runtime.RunStop));
            duplicate.Should().Throw<InvalidOperationException>("the original receipt already owns its one invocation binding");
        }
        await AssertSingleUnresolvedAttemptAsync(database, original);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("generation")]
    [InlineData("admitted")]
    [InlineData("deadline")]
    [InlineData("organization")]
    public async Task External_recovery_probe_cannot_reconstruct_authority_from_a_changed_or_substituted_descriptor(string substitution)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external");
        await using var host = RecoveryHost(database);
        var original = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        await ParkExpiredExternalAsync(database, original);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var acquired = await store.AcquireExternalRecoveryAsync(job, await RecoveryGenerationAsync(database, job), original.Envelope.Queue, CancellationToken.None);
        acquired.ThrowIfFailed();
        acquired.Value.Should().NotBeNull();
        var exact = acquired.Probes.Single();
        var wrong = substitution switch
        {
            "token" => exact with { RecoveryToken = Guid.NewGuid() },
            "generation" => exact with { WakeGeneration = exact.WakeGeneration + 1 },
            "admitted" => exact with { AdmittedAt = exact.AdmittedAt!.Value.AddTicks(10) },
            "deadline" => exact with { Deadline = exact.Deadline!.Value.AddTicks(10) },
            "organization" => exact with { OrganizationId = SchemaFixture.OrgA1 },
            _ => throw new InvalidOperationException(),
        };
        var refused = () => store.ProbeExternalRecoveryAsync(acquired, wrong, CancellationToken.None);
        await refused.Should().ThrowAsync<ArgumentException>("a scalar copy cannot replace the original descriptor object");
        var positive = await store.ProbeExternalRecoveryAsync(acquired, exact, CancellationToken.None);
        positive.ThrowIfFailed();
        positive.Value.Should().NotBeNull("only the exact original ledger and still-current proposed recovery token may publish");
        await AssertSingleUnresolvedAttemptAsync(database, original);
    }

    [Fact]
    public async Task Recovery_skips_a_live_job_lock_instead_of_claiming_or_interpreting_the_skipped_row_as_absence()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external");
        await using var host = RecoveryHost(database);
        var original = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        await ParkExpiredExternalAsync(database, original);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        await using (var blocker = await PostgresFixture.OpenAsync(database.AppConnectionString))
        await using (var transaction = await blocker.BeginTransactionAsync())
        {
            await SchemaQueries.AssertApplicationRoleAsync(blocker, transaction);
            await EducationSchemaSeed.AnnounceAsync(blocker, transaction, SchemaFixture.TenantA, null);
            await SchemaQueries.ExecuteAsync(blocker, transaction, "SELECT id FROM background_jobs WHERE id=@id FOR UPDATE", ("id", job.Value));
            var skipped = await store.AcquireExternalRecoveryAsync(job, await RecoveryGenerationAsync(database, job),
                original.Envelope.Queue, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            skipped.ThrowIfFailed();
            skipped.Value.Should().BeNull();
            skipped.CommitAcknowledged.Should().BeFalse();
            skipped.Probes.Should().BeEmpty();
            await transaction.RollbackAsync();
        }
        var later = await store.AcquireExternalRecoveryAsync(job, await RecoveryGenerationAsync(database, job), original.Envelope.Queue, CancellationToken.None);
        later.ThrowIfFailed();
        later.Value.Should().NotBeNull("a fresh acquisition after the real lock owner finishes has independent authority");
    }

    [Fact]
    public async Task Real_external_acquisition_ack_loss_requires_a_clean_exact_probe_and_repeated_probes_share_one_receipt()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external");
        await using var host = RecoveryHost(database);
        var original = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        await ParkExpiredExternalAsync(database, original);
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        await using var proxy = new CommitResponseDroppingProxy(database.OutboxConnectionString);
        await using var brokenSource = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(proxy.ConnectionString));
        var brokenStore = new PostgresBackgroundJobControlStore(brokenSource, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var unknown = await brokenStore.AcquireExternalRecoveryAsync(job, await RecoveryGenerationAsync(database, job),
            original.Envelope.Queue, CancellationToken.None);
        proxy.DroppedCommitResponses.Should().Be(1);
        unknown.CommitUncertain.Should().BeTrue();
        unknown.CommitAcknowledged.Should().BeFalse();
        unknown.Value.Should().BeNull("a persisted token and an unknown ACK alone cannot publish a receipt");
        unknown.CleanupConfirmed.Should().BeTrue();
        unknown.Probes.Should().ContainSingle();
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var confirmed = await store.ProbeExternalRecoveryAsync(unknown, unknown.Probes.Single(), CancellationToken.None);
        confirmed.ThrowIfFailed();
        confirmed.CleanupConfirmed.Should().BeTrue();
        confirmed.Value.Should().NotBeNull();
        var receipt = confirmed.Value!;
        unknown.Value.Should().BeSameAs(receipt);
        unknown.CommitAcknowledged.Should().BeFalse("historical confirmation preserves the physical ACK loss");
        receipt.RecoveryToken.Should().Be(unknown.Probes.Single().RecoveryToken!.Value);
        var repeated = await store.ProbeExternalRecoveryAsync(unknown, unknown.Probes.Single(), CancellationToken.None);
        repeated.ThrowIfFailed();
        repeated.Value.Should().BeSameAs(receipt);
        await AssertSingleUnresolvedAttemptAsync(database, original);
    }

    [Fact]
    public async Task A_real_replacement_token_denies_the_old_exact_probe_without_erasing_its_acquisition_history()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external");
        await using var host = RecoveryHost(database);
        var original = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        await ParkExpiredExternalAsync(database, original);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var acquired = await store.AcquireExternalRecoveryAsync(job, await RecoveryGenerationAsync(database, job), original.Envelope.Queue, CancellationToken.None);
        acquired.ThrowIfFailed();
        acquired.Value.Should().NotBeNull();
        var historical = acquired.Value;
        await using (var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString))
            await SchemaQueries.ExecuteAsync(dispatcher, null, """
                UPDATE background_jobs SET ownership_until=clock_timestamp() WHERE id=@id;
                UPDATE background_jobs SET ownership_token=@replacement,ownership_until=clock_timestamp()+interval '1 minute'
                    WHERE id=@id AND ownership_kind='recovery' AND ownership_until<=clock_timestamp()
                """, ("id", job.Value), ("replacement", Guid.NewGuid()));
        var stale = await store.ProbeExternalRecoveryAsync(acquired, acquired.Probes.Single(), CancellationToken.None);
        stale.ThrowIfFailed();
        stale.CleanupConfirmed.Should().BeTrue();
        stale.Value.Should().BeNull("the original proposed token is no longer current");
        acquired.Value.Should().BeSameAs(historical);
        acquired.CommitAcknowledged.Should().BeTrue("takeover does not rewrite historical ACK evidence");
        await AssertSingleUnresolvedAttemptAsync(database, original);
    }

    [Fact]
    public async Task Fresh_recovery_reads_absent_plan_and_records_no_start_without_inventing_a_provider_report()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external");
        await using var host = RecoveryHost(database);
        var original = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        await ParkExpiredExternalAsync(database, original);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var acquired = await store.AcquireExternalRecoveryAsync(job, await RecoveryGenerationAsync(database, job), original.Envelope.Queue, CancellationToken.None);
        acquired.ThrowIfFailed();
        var recovery = BindRecovery(acquired, runtime);
        var snapshot = await ReadRecoveryAsync(host.Services, recovery);
        snapshot.PlanJson.Should().BeNull();
        snapshot.PlanFingerprint.Should().BeNull();
        snapshot.OperationId.Should().BeNull();
        snapshot.OutcomeJson.Should().BeNull();
        snapshot.ExecuteIntentRecorded.Should().BeFalse();
        snapshot.NoStartRecorded.Should().BeFalse();
        snapshot.Envelope.Should().Be(original.Envelope, "causal metadata comes from the actual app read, never the bypass receipt");
        var gate = recovery.Run.OpenBoundary(BackgroundJobCheckpointKind.NoStart);
        var command = RecordBackgroundJobOutcomeCommand.ForNoStart(recovery, gate);
        command.IsNoStart.Should().BeTrue();
        Action report = () => _ = command.OutcomeJson;
        report.Should().Throw<InvalidOperationException>();
        (await SendRecoveryAsync(host.Services, recovery, command, gate)).IsSuccess.Should().BeTrue();
        gate.CommitAcknowledged.Should().BeTrue();
        recovery.SourceOwner.CleanupConfirmed.Should().BeTrue();
        var after = await ReadRecoveryAsync(host.Services, recovery);
        after.NoStartRecorded.Should().BeTrue();
        after.ExecuteIntentRecorded.Should().BeFalse();
        after.OutcomeState.Should().BeNull();
        after.FailurePolicy.Should().BeNull();
        after.PlanJson.Should().BeNull();
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
    }

    [Theory]
    [InlineData(false, BackgroundJobExternalState.Failed, BackgroundJobExternalFailurePolicy.TransientRepeatable, null)]
    [InlineData(true, BackgroundJobExternalState.Failed, BackgroundJobExternalFailurePolicy.TransientRepeatable, null)]
    [InlineData(true, BackgroundJobExternalState.Succeeded, null, "none")]
    [InlineData(true, BackgroundJobExternalState.Failed, BackgroundJobExternalFailurePolicy.Permanent, "external_failed")]
    [InlineData(true, BackgroundJobExternalState.Failed, BackgroundJobExternalFailurePolicy.TransientNotRepeatable, "external_failed")]
    [InlineData(true, BackgroundJobExternalState.Cancelled, BackgroundJobExternalFailurePolicy.TransientNotRepeatable, "external_cancelled")]
    public async Task Armed_recovery_refuses_no_start_and_preserves_committed_facts_over_a_conflicting_later_report(
        bool recordOutcome, BackgroundJobExternalState state, BackgroundJobExternalFailurePolicy? policy, string? selectedReason)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external");
        await using var host = RecoveryHost(database, preparing: true);
        var original = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        var prepareGate = original.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare);
        var prepared = await host.Services.GetRequiredService<JobExecutor>().ExecuteBusinessAsync(original, prepareGate);
        prepared.CommitAcknowledged.Should().BeTrue();
        var fingerprint = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("{\"ArtifactId\":\"" + RecoveryArtifact.ToString("D") + "\"}")));
        var operation = BackgroundJobExternalIdentity.OperationId(job, fingerprint);
        var armGate = original.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm);
        (await SendCheckpointAsync(host.Services, original,
            new RecordBackgroundJobArmCommand(original, armGate, fingerprint, operation), armGate)).IsSuccess.Should().BeTrue();
        original.Run.Close();
        await using (var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString))
            await SchemaQueries.ExecuteAsync(dispatcher, null, """
                UPDATE background_jobs SET ownership_until=clock_timestamp() WHERE id=@id;
                UPDATE background_jobs SET phase='reconciling',ownership_token=NULL,ownership_kind=NULL,ownership_until=NULL,probe_after=clock_timestamp()
                    WHERE id=@id AND ownership_until<=clock_timestamp()
                """, ("id", job.Value));
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var acquired = await store.AcquireExternalRecoveryAsync(job, await RecoveryGenerationAsync(database, job), original.Envelope.Queue, CancellationToken.None);
        acquired.ThrowIfFailed();
        var recovery = BindRecovery(acquired, runtime);
        var snapshot = await ReadRecoveryAsync(host.Services, recovery);
        snapshot.ExecuteIntentRecorded.Should().BeTrue();
        snapshot.PlanFingerprint.Should().Be(fingerprint);
        if (!recordOutcome)
        {
            var gate = recovery.Run.OpenBoundary(BackgroundJobCheckpointKind.NoStart);
            var noStart = RecordBackgroundJobOutcomeCommand.ForNoStart(recovery, gate);
            var refuse = () => SendRecoveryAsync(host.Services, recovery, noStart, gate);
            await refuse.Should().ThrowAsync<InvalidOperationException>();
            gate.CommitAcknowledged.Should().BeFalse();
            gate.KnownRollback.Should().BeTrue();
            var after = await ReadRecoveryAsync(host.Services, recovery);
            after.ExecuteIntentRecorded.Should().BeTrue();
            after.NoStartRecorded.Should().BeFalse();
            after.OutcomeState.Should().BeNull();
        }
        else
        {
            var outcome = "{\"ArtifactId\":\"" + Guid.NewGuid().ToString("D") + "\"}";
            var gate = recovery.Run.OpenBoundary(BackgroundJobCheckpointKind.Outcome);
            var command = new RecordBackgroundJobOutcomeCommand(recovery, gate, fingerprint, operation, outcome,
                state, policy,
                Guid.NewGuid(), BackgroundJobQuiescenceKind.ProviderFenced);
            (await SendRecoveryAsync(host.Services, recovery, command, gate)).IsSuccess.Should().BeTrue();
            gate.CommitAcknowledged.Should().BeTrue();
            if (selectedReason is null)
            {
                var laterGate = recovery.Run.OpenBoundary(BackgroundJobCheckpointKind.Outcome);
                var conflicting = new RecordBackgroundJobOutcomeCommand(recovery, laterGate, "invalid", Guid.Empty,
                    "not-json", BackgroundJobExternalState.Unknown, null, Guid.Empty, (BackgroundJobQuiescenceKind)99);
                (await SendRecoveryAsync(host.Services, recovery, conflicting, laterGate)).IsSuccess.Should().BeTrue(
                    "the complete immutable outcome has precedence before evaluating a later report");
                var after = await ReadRecoveryAsync(host.Services, recovery);
                after.OutcomeJson.Should().Be(outcome);
                after.OutcomeState.Should().Be(state);
                after.FailurePolicy.Should().Be(policy);
                after.NoStartRecorded.Should().BeFalse();
            }
            else
            {
                await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
                await using var transaction = await app.BeginTransactionAsync();
                await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
                await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
                await using var selected = new NpgsqlCommand("""
                    SELECT j.phase,j.final_outcome_attempt_id,j.finalization_status,j.finalization_reason,
                        j.ownership_token IS NULL AND j.ownership_kind IS NULL AND j.ownership_until IS NULL,
                        j.wake_generation,a.outcome,a.outcome_kind,r.release_reason,r.released_at IS NOT NULL
                    FROM background_jobs j JOIN background_job_attempts a ON a.id=j.current_attempt_id
                        JOIN background_job_reservations r ON r.attempt_id=a.id WHERE j.id=@id
                    """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction);
                selected.Parameters.AddWithValue("id", job.Value);
                await using var row = await selected.ExecuteReaderAsync();
                (await row.ReadAsync()).Should().BeTrue();
                row.GetString(0).Should().Be("finalize_pending");
                row.GetGuid(1).Should().Be(original.AttemptId.Value);
                row.GetString(2).Should().Be(state == BackgroundJobExternalState.Succeeded ? "succeeded" : "failed");
                row.GetString(3).Should().Be(selectedReason);
                row.GetBoolean(4).Should().BeTrue();
                row.GetInt64(5).Should().Be(acquired.Value!.WakeGeneration + 1);
                row.GetString(6).Should().Be(outcome);
                row.GetString(7).Should().Be(state.ToString().ToLowerInvariant());
                row.GetString(8).Should().Be("quiescent");
                row.GetBoolean(9).Should().BeTrue();
            }
        }
        recovery.SourceOwner.CleanupConfirmed.Should().BeTrue();
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
    }

    private static readonly Guid RecoveryArtifact = new("53b87b8e-95d2-4a8e-b6ed-e27cdbb68c30");

    private static BackgroundJobExternalRecoveryBinding BindRecovery(OwnedBackgroundJobExternalRecovery acquired, BackgroundJobRuntimeGate runtime)
    {
        acquired.Value.Should().NotBeNull();
        using var source = JobApplicationSourceOwner.Create().Enter();
        return acquired.Value!.Bind(new BackgroundJobRunGate(runtime.RunStop));
    }

    private static async Task<BackgroundJobExternalSnapshot> ReadRecoveryAsync(IServiceProvider services, BackgroundJobExternalRecoveryBinding recovery)
    {
        var read = await SendRecoveryAsync(services, recovery, new ReadBackgroundJobExternalStateQuery(recovery), null);
        read.IsSuccess.Should().BeTrue();
        return read.Value ?? throw new InvalidOperationException("The real recovery read returned no snapshot.");
    }

    private static async Task<Result<T>> SendRecoveryAsync<T>(IServiceProvider services, BackgroundJobExternalRecoveryBinding recovery,
        IRequest<Result<T>> request, BackgroundJobCommitGate? gate)
    {
        using var binding = recovery.SourceOwner.Enter();
        recovery.SourceOwner.RequireFreshScopeReady();
        var accessor = services.GetRequiredService<ITenantContextAccessor>();
        var previous = accessor.Current;
        var context = JobTenantContext.ForRecoveryRead(recovery.Receipt.TenantId, recovery.Receipt.OrganizationId,
            recovery.Receipt.JobId, recovery.Receipt.DefinitionKey[..recovery.Receipt.DefinitionKey.IndexOf('.', StringComparison.Ordinal)]);
        context.CorrelationId.Should().BeNull();
        context.ParentJobId.Should().BeNull();
        context.IncomingEventId.Should().BeNull();
        context.CausalActorUserId.Should().BeNull();
        accessor.Current = context;
        try
        {
            await using var scope = services.CreateAsyncScope();
            var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var admission = services.GetRequiredService<BackgroundJobExecutionAdmission>();
            using var authority = request switch
            {
                ReadBackgroundJobExternalStateQuery read => admission.EnterExternalCheckpoint(read, unit),
                RecordBackgroundJobOutcomeCommand outcome => admission.EnterExternalCheckpoint(outcome, unit),
                _ => throw new InvalidOperationException("Unexpected recovery checkpoint fixture request."),
            };
            if (gate is not null) gate.TryIssue().Should().BeTrue();
            return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
        }
        finally { accessor.Current = previous; }
    }

    private static WebApplicationFactory<Program> RecoveryHost(DisposableSchemaDatabase database, bool preparing = false,
        CheckpointCommitPause? pause = null, Action<IServiceCollection>? configure = null, string? appConnectionString = null) =>
        Fixture.Host(database, new BackgroundJobFixtureProbe(), configure: services =>
        {
            services.AddSingleton(new FixtureAdmissionWindow(preparing ? TimeSpan.FromMinutes(4) : TimeSpan.FromMilliseconds(500)));
            services.RemoveAll<IRequestHandler<AdmitBackgroundJobAttemptCommand, Result<None>>>();
            services.AddTransient<IRequestHandler<AdmitBackgroundJobAttemptCommand, Result<None>>, FixtureAdmissionHandler>();
            if (pause is not null)
            {
                services.AddSingleton(pause);
                services.AddTransient(typeof(IPipelineBehavior<,>), typeof(AfterCheckpointHandler<,>));
            }
            if (preparing)
            {
                var definition = Fixture.Registry.Resolve(BackgroundJobRuntimeFixture.ExternalKey).Definition!;
                services.AddSingleton(new PreparedArtifact(RecoveryArtifact));
                services.AddTransient(typeof(IRequestHandler<,>).MakeGenericType(definition.ProfileRegistration.RootRequestType,
                    typeof(Result<>).MakeGenericType(definition.ProfileRegistration.PlanType!)),
                    typeof(FixturePrepareHandler<,>).MakeGenericType(definition.ProfileRegistration.RootRequestType,
                        definition.ProfileRegistration.PlanType!));
            }
            configure?.Invoke(services);
        }, appConnectionString: appConnectionString);

    private static async Task<long> RecoveryGenerationAsync(DisposableSchemaDatabase database, BackgroundJobId job)
    {
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        await using var command = new NpgsqlCommand("SELECT wake_generation FROM background_jobs WHERE id=@id",
            (NpgsqlConnection)app, (NpgsqlTransaction)transaction)
        { CommandTimeout = 5 };
        command.Parameters.AddWithValue("id", job.Value);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ParkExpiredExternalAsync(DisposableSchemaDatabase database, ConfirmedStartReceipt original)
    {
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        var started = Stopwatch.StartNew();
        while (true)
        {
            await using var clock = new NpgsqlCommand("SELECT clock_timestamp()>=@deadline", (NpgsqlConnection)dispatcher)
            { CommandTimeout = 5 };
            clock.Parameters.AddWithValue("deadline", original.Deadline);
            if ((bool)(await clock.ExecuteScalarAsync())!) break;
            if (started.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Original fixture deadline did not expire.");
            await Task.Delay(10);
        }
        await SchemaQueries.ExecuteAsync(dispatcher, null, """
            UPDATE background_jobs SET phase='reconciling',ownership_token=NULL,ownership_kind=NULL,ownership_until=NULL,probe_after=clock_timestamp()
            WHERE id=@id AND ownership_until<=clock_timestamp()
            """, ("id", original.Envelope.JobId.Value));
    }

    private static async Task AssertSingleUnresolvedAttemptAsync(DisposableSchemaDatabase database, ConfirmedStartReceipt original)
    {
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        await using var command = new NpgsqlCommand("""
            SELECT j.normal_attempts,j.finalize_attempts,count(a.id),bool_and(a.execute_intent_at IS NULL),
                bool_and(r.released_at IS NULL)
            FROM background_jobs j JOIN background_job_attempts a ON a.job_id=j.id
            JOIN background_job_reservations r ON r.attempt_id=a.id
            WHERE j.id=@id GROUP BY j.normal_attempts,j.finalize_attempts
            """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction);
        command.Parameters.AddWithValue("id", original.Envelope.JobId.Value);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        row.GetInt32(0).Should().Be(1);
        row.GetInt32(1).Should().Be(0);
        row.GetInt64(2).Should().Be(1);
        row.GetBoolean(3).Should().BeTrue();
        row.GetBoolean(4).Should().BeTrue("recovery acquisition cannot infer no-start or release occupancy");
    }
}
