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
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    public async Task Explicit_retired_NoPlan_uses_actual_cleanup_and_one_MUST_without_an_app_definition(int ordinal)
    {
        // Explicit store scheduling/admission fixture; native dispatch has its
        // own qualification. Every ledger/checkpoint/audit here commits real SQL.
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var originalHost = NoPlanRefusalHost(database);
        await using var cleanupHost = NoPlanRefusalHost(database, emptyDefinitions: true);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var active = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var control = new PostgresBackgroundJobControlStore(source, UninvokableRetiredRegistry(), new(), new SystemGuidFactory(), runtime);
        ConfirmedStartReceipt? normal = null;
        try
        {
            var job = ordinal == 0 ? await ExternalRetryJobAsync(database)
                : (normal = await NoPlanOrdinalAsync(database, originalHost.Services, ordinal, active, runtime)).Envelope.JobId;
            if (normal is not null)
            {
                CloseUninvokableNormal(normal);
                await ExpireUninvokableOwnerAsync(database, job);
            }
            var before = await ReadUninvokableStateAsync(database, job);
            before.Normal.Should().Be(ordinal);
            before.NoStarts.Should().Be(Math.Max(0, ordinal - 1));
            before.Released.Should().Be(Math.Max(0, ordinal - 1));
            var requested = await control.RunRecoveryQuantumAsync(BackgroundJobControlLane.Retirement, []);
            requested.ThrowIfFailed();
            requested.CommitAcknowledged.Should().BeTrue();
            requested.CleanupConfirmed.Should().BeTrue();
            requested.Value.Should().ContainSingle(change => change.JobId == job.Value && change.TerminalReason == "retired_definition");
            var origin = requested.Probes.Should().ContainSingle().Which;
            (await ReadUninvokableStateAsync(database, job)).Immutable.Should().Be(before.Immutable);
            await CompleteUninvokableTerminalAsync(database, cleanupHost.Services, source, runtime, control, job,
                origin, needsCleanup: ordinal > 0);
        }
        finally { if (normal is not null) CloseUninvokableNormal(normal); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retired_prepared_unarmed_cleanup_preserves_original_or_reused_plan_and_positive_prior_outcome(bool reused)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var host = RecoveryHost(database, preparing: true);
        await using var cleanupHost = NoPlanRefusalHost(database, emptyDefinitions: true);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var active = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var control = new PostgresBackgroundJobControlStore(source, UninvokableRetiredRegistry(), new(), new SystemGuidFactory(), runtime);
        var first = await PrepareUninvokableAsync(database, host.Services);
        var current = first;
        string? prior = null;
        try
        {
            if (reused)
            {
                await WriteUninvokableOutcomeAsync(host.Services, first, BackgroundJobExternalState.Failed,
                    BackgroundJobExternalFailurePolicy.TransientRepeatable);
                prior = await RetainedSelectionFactsAsync(database, first);
                var retry = await active.RetryKnownExternalAsync(BackgroundJobExecutionIdentity.FromReceipt(first),
                    BackgroundJobExternalRetryMode.OriginalCompleted, CancellationToken.None);
                retry.ThrowIfFailed();
                retry.CommitAcknowledged.Should().BeTrue();
                retry.CleanupConfirmed.Should().BeTrue();
                CloseUninvokableNormal(first);
                var due = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, first.Envelope.JobId))!.AvailableAfter;
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (await RetryDatabaseClockAsync(database) < due) await Task.Delay(10, wait.Token);
                current = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, first.Envelope.JobId));
                current.Stage.Should().BeNull("the second real admission reuses its immutable plan without Prepare");
                current.NormalAttemptOrdinal.Should().Be(2);
            }
            CloseUninvokableNormal(current);
            await ExpireUninvokableOwnerAsync(database, current.Envelope.JobId);
            var before = await ReadUninvokableStateAsync(database, current.Envelope.JobId);
            before.Prepared.Should().BeTrue();
            before.CurrentStamped.Should().Be(!reused);
            before.Outcomes.Should().Be(reused ? 1 : 0);
            before.NoStarts.Should().Be(0);
            var requested = await control.RunRecoveryQuantumAsync(BackgroundJobControlLane.Retirement, []);
            requested.ThrowIfFailed();
            requested.Value.Should().ContainSingle(change => change.JobId == current.Envelope.JobId.Value);
            await CompleteUninvokableTerminalAsync(database, cleanupHost.Services, source, runtime, control,
                current.Envelope.JobId, requested.Probes.Single(), needsCleanup: true);
            var after = await ReadUninvokableStateAsync(database, current.Envelope.JobId);
            after.Immutable.Should().Be(before.Immutable);
            after.CurrentStamped.Should().Be(before.CurrentStamped, "cleanup cannot invent or replace an attempt plan stamp");
            if (prior is not null) (await RetainedSelectionFactsAsync(database, first)).Should().Be(prior);
        }
        finally { CloseUninvokableNormal(first); if (!ReferenceEquals(first, current)) CloseUninvokableNormal(current); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retired_prepared_selected_outcome_survives_actual_finalize_admission_and_one_bypass_MUST(bool failed)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var host = RecoveryHost(database, preparing: true);
        await using var finalizeHost = Fixture.Host(database, new BackgroundJobFixtureProbe());
        await using var cleanupHost = NoPlanRefusalHost(database, emptyDefinitions: true);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var control = new PostgresBackgroundJobControlStore(source, UninvokableRetiredRegistry(), new(), new SystemGuidFactory(), runtime);
        var normal = await PrepareUninvokableAsync(database, host.Services);
        ConfirmedStartReceipt? finalizer = null;
        try
        {
            await WriteUninvokableOutcomeAsync(host.Services, normal, failed ? BackgroundJobExternalState.Failed : BackgroundJobExternalState.Succeeded,
                failed ? BackgroundJobExternalFailurePolicy.Permanent : null);
            var selected = await RetainedSelectionFactsAsync(database, normal);
            CloseUninvokableNormal(normal);
            // Actual production Finalize admission, with no Finalize root call.
            var start = await finalizeHost.Services.GetRequiredService<JobExecutor>().ExecuteAsync(
                await FixtureClaim.ClaimAsync(database, normal.Envelope.JobId), BackgroundJobTestRun.Create());
            start.Outcome.Should().Be(JobStartExecutionOutcome.Started);
            start.CleanupFailed.Should().BeFalse();
            finalizer = start.Receipt!;
            finalizer.FinalizeAttemptOrdinal.Should().Be(1);
            finalizer.SelectedNormalAttemptId.Should().Be(normal.AttemptId);
            CloseUninvokableNormal(finalizer);
            await ExpireUninvokableOwnerAsync(database, normal.Envelope.JobId);
            var before = await ReadUninvokableStateAsync(database, normal.Envelope.JobId);
            var requested = await control.RunRecoveryQuantumAsync(BackgroundJobControlLane.Retirement, []);
            requested.ThrowIfFailed();
            requested.Value.Should().ContainSingle(change => change.JobId == normal.Envelope.JobId.Value);
            var claim = await CompleteUninvokableTerminalAsync(database, cleanupHost.Services, source, runtime, control,
                normal.Envelope.JobId, requested.Probes.Single(), needsCleanup: false);
            claim.Prepared!.FinalizeAttempts.Should().Be(1);
            claim.Prepared.SelectedNormalAttemptId.Should().Be(normal.AttemptId.Value);
            claim.Prepared.FinalizationStatus.Should().Be(failed ? "failed" : "succeeded");
            claim.Prepared.FinalizationReason.Should().Be(failed ? "external_failed" : "none");
            claim.Prepared.State.Should().Be(failed ? "failed" : "succeeded");
            claim.Prepared.Policy.Should().Be(failed ? "permanent" : null);
            (await ReadUninvokableStateAsync(database, normal.Envelope.JobId)).Immutable.Should().Be(before.Immutable);
            (await RetainedSelectionFactsAsync(database, normal)).Should().Be(selected);
        }
        finally { CloseUninvokableNormal(normal); if (finalizer is not null) CloseUninvokableNormal(finalizer); }
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("pending")]
    [InlineData("unresolved")]
    [InlineData("live_owner")]
    public async Task Retirement_defers_unknown_Pending_unresolved_and_live_owners_without_releasing_occupancy(string mode)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var host = RecoveryHost(database, preparing: true);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var registry = mode == "unknown" ? BackgroundJobDefinitionRegistry.CreateEmpty()
            : mode == "pending" ? UninvokablePendingRegistry() : UninvokableRetiredRegistry();
        var control = new PostgresBackgroundJobControlStore(source, registry, new(), new SystemGuidFactory(), runtime);
        var normal = await PrepareUninvokableAsync(database, host.Services);
        try
        {
            if (mode == "unresolved") await ArmUninvokableAsync(host.Services, normal);
            CloseUninvokableNormal(normal);
            if (mode != "live_owner") await ExpireUninvokableOwnerAsync(database, normal.Envelope.JobId);
            var before = await ReadUninvokableStateAsync(database, normal.Envelope.JobId);
            var owner = await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, normal.Envelope.JobId);
            var request = await control.RunRecoveryQuantumAsync(BackgroundJobControlLane.Retirement, []);
            request.ThrowIfFailed();
            request.CleanupConfirmed.Should().BeTrue();
            request.Value.Should().BeEmpty();
            request.Probes.Should().BeEmpty();
            (await ReadUninvokableStateAsync(database, normal.Envelope.JobId)).Should().Be(before);
            (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, normal.Envelope.JobId)).Should().Be(owner);
            before.Released.Should().Be(0);
            before.NoStarts.Should().Be(0);
            before.Intents.Should().Be(mode == "unresolved" ? 1 : 0);
            (await ReadUninvokableAuditAsync(database, normal.Envelope.JobId)).Count.Should().Be(0);
        }
        finally { CloseUninvokableNormal(normal); }
    }

    [Fact]
    public async Task Retirement_positive_selection_passes_more_than_a_full_batch_of_earlier_unresolved_heads()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var host = RecoveryHost(database, preparing: true);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var bounds = new BackgroundJobRuntimeOptions { ControlBatchSize = 1 };
        var control = new PostgresBackgroundJobControlStore(source, UninvokableRetiredRegistry(), bounds, new SystemGuidFactory(), runtime);
        var blocked = new List<ConfirmedStartReceipt>();
        try
        {
            for (var index = 0; index < bounds.ControlBatchSize + 1; index++)
            {
                var normal = await PrepareUninvokableAsync(database, host.Services);
                blocked.Add(normal);
                await ArmUninvokableAsync(host.Services, normal);
                CloseUninvokableNormal(normal);
                await ExpireUninvokableOwnerAsync(database, normal.Envelope.JobId);
            }
            var healthy = await ExternalRetryJobAsync(database);
            var before = new List<UninvokableState>();
            foreach (var normal in blocked) before.Add(await ReadUninvokableStateAsync(database, normal.Envelope.JobId));
            var request = await control.RunRecoveryQuantumAsync(BackgroundJobControlLane.Retirement, []);
            request.ThrowIfFailed();
            request.Value.Should().ContainSingle(change => change.JobId == healthy.Value && change.TerminalReason == "retired_definition",
                "ineligible earlier heads must be filtered before the actual batch LIMIT");
            (await ReadUninvokableStateAsync(database, healthy)).Phase.Should().Be("terminal_pending");
            for (var index = 0; index < blocked.Count; index++)
            {
                (await ReadUninvokableStateAsync(database, blocked[index].Envelope.JobId)).Should().Be(before[index]);
                before[index].Intents.Should().Be(1);
                before[index].Released.Should().Be(0);
                (await ReadUninvokableAuditAsync(database, blocked[index].Envelope.JobId)).Count.Should().Be(0);
            }
        }
        finally { foreach (var normal in blocked) CloseUninvokableNormal(normal); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retired_selected_request_commit_loss_uses_exact_original_metadata_across_physical_TimeZones(bool committed)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var host = RecoveryHost(database, preparing: true);
        await using var cleanupHost = NoPlanRefusalHost(database, emptyDefinitions: true);
        var requestConnection = new NpgsqlConnectionStringBuilder(database.OutboxConnectionString) { Timezone = "Europe/Istanbul" }.ConnectionString;
        var probeConnection = new NpgsqlConnectionStringBuilder(database.OutboxConnectionString) { Timezone = "UTC" }.ConnectionString;
        await using var proxy = new CommitResponseDroppingProxy(requestConnection, dropCommitRequest: !committed);
        await using var brokenSource = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(proxy.ConnectionString));
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(probeConnection));
        await AssertUninvokableTimezoneAsync(brokenSource, "Europe/Istanbul");
        await AssertUninvokableTimezoneAsync(source, "UTC");
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var registry = UninvokableRetiredRegistry();
        var broken = new PostgresBackgroundJobControlStore(brokenSource, registry, new(), new SystemGuidFactory(), runtime);
        var control = new PostgresBackgroundJobControlStore(source, registry, new(), new SystemGuidFactory(), runtime);
        var normal = await PrepareUninvokableAsync(database, host.Services);
        try
        {
            await WriteUninvokableOutcomeAsync(host.Services, normal, BackgroundJobExternalState.Succeeded, null);
            CloseUninvokableNormal(normal);
            var before = await ReadUninvokableStateAsync(database, normal.Envelope.JobId);
            var unknown = await broken.RunRecoveryQuantumAsync(BackgroundJobControlLane.Retirement, []);
            unknown.CommitAcknowledged.Should().BeFalse();
            unknown.CommitUncertain.Should().BeTrue();
            unknown.CleanupConfirmed.Should().BeTrue();
            unknown.Value.Should().BeNull();
            proxy.DroppedCommitRequests.Should().Be(committed ? 0 : 1);
            proxy.DroppedCommitResponses.Should().Be(committed ? 1 : 0);
            var origin = unknown.Probes.Should().ContainSingle().Which;
            origin.UninvokableHistory.Should().NotBeNull();
            origin.UninvokableHistory!.SelectedFacts.Should().NotBeNull();
            var proof = await control.ProbeCommitAsync(origin);
            proof.ThrowIfFailed();
            proof.CleanupConfirmed.Should().BeTrue();
            proof.Value!.Disposition.Should().Be(committed
                ? BackgroundJobProbeDisposition.ControlCommitted : BackgroundJobProbeDisposition.RolledBack);
            proof.CommitAcknowledged.Should().BeFalse();
            unknown.CommitAcknowledged.Should().BeFalse();
            foreach (var substituted in new[]
            {
                origin with { ExpectedTerminalReason = "invalid_envelope" },
                origin with { Token = Guid.NewGuid() },
                origin with { Profile = "transactional" },
                origin with { Boundary = BackgroundJobCommitBoundary.Business },
                origin with { UninvokableHistory = null },
                origin with { SelectedNormalAttemptId = Guid.CreateVersion7() },
            })
            {
                var refused = await control.ProbeCommitAsync(substituted);
                refused.ThrowIfFailed();
                refused.Value!.Disposition.Should().Be(BackgroundJobProbeDisposition.Unresolved);
            }
            (await ReadUninvokableAuditAsync(database, normal.Envelope.JobId)).Count.Should().Be(0);
            (await ReadUninvokableStateAsync(database, normal.Envelope.JobId)).Immutable.Should().Be(before.Immutable);
            if (!committed)
            {
                (await ReadUninvokableStateAsync(database, normal.Envelope.JobId)).Should().Be(before);
                var retry = await control.RunRecoveryQuantumAsync(BackgroundJobControlLane.Retirement, []);
                retry.ThrowIfFailed();
                retry.Value.Should().ContainSingle(change => change.JobId == normal.Envelope.JobId.Value);
            }
            await CompleteUninvokableTerminalAsync(database, cleanupHost.Services, source, runtime, control,
                normal.Envelope.JobId, origin, needsCleanup: false);
            unknown.CommitAcknowledged.Should().BeFalse("later proof cannot rewrite the original physical ACK");
        }
        finally { CloseUninvokableNormal(normal); }
    }

    [Fact]
    public async Task Uninvokable_history_cannot_be_attached_to_foreign_actual_terminal_or_finalizer_descriptors()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var prepareHost = RecoveryHost(database, preparing: true);
        await using var finalizerHost = PreparedTerminalHost(database);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var retired = new PostgresBackgroundJobControlStore(source, UninvokableRetiredRegistry(), new(), new SystemGuidFactory(), runtime);
        var active = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var originalJob = await ExternalRetryJobAsync(database);
        var requested = await retired.RunRecoveryQuantumAsync(BackgroundJobControlLane.Retirement, []);
        requested.ThrowIfFailed();
        var original = requested.Probes.Should().ContainSingle().Which;
        var normal = await PrepareUninvokableAsync(database, prepareHost.Services);
        ConfirmedStartReceipt? finalizer = null;
        try
        {
            await WriteUninvokableOutcomeAsync(prepareHost.Services, normal, BackgroundJobExternalState.Succeeded, null);
            CloseUninvokableNormal(normal);
            var executor = finalizerHost.Services.GetRequiredService<JobExecutor>();
            var start = await executor.ExecuteAsync(await FixtureClaim.ClaimAsync(database, normal.Envelope.JobId), BackgroundJobTestRun.Create());
            start.Outcome.Should().Be(JobStartExecutionOutcome.Started);
            finalizer = start.Receipt!;
            var failed = await executor.ExecuteBusinessAsync(finalizer, finalizer.Run.OpenBoundary(BackgroundJobCheckpointKind.Finalize));
            failed.KnownRollback.Should().BeTrue();
            failed.CleanupFailed.Should().BeFalse();
            failed.Decision.TerminalReason.Should().Be(BackgroundJobTerminalReason.PermanentFailure);
            CloseUninvokableNormal(finalizer);
            var finalized = await active.FinishExternalFinalizeAsync(BackgroundJobExecutionIdentity.FromReceipt(finalizer),
                failed, null, CancellationToken.None);
            finalized.ThrowIfFailed();
            finalized.CommitAcknowledged.Should().BeTrue();
            var finalizerProbe = finalized.Probes.Should().ContainSingle().Which;
            finalizerProbe.FinalizationHistory.Should().NotBeNull();
            var terminal = new PostgresBackgroundJobTerminalStore(source, finalizerHost.Services.GetRequiredService<IServiceScopeFactory>(),
                new(), new SystemGuidFactory(), runtime);
            var claim = await terminal.TryClaimTerminalAsync([originalJob.Value]);
            claim.ThrowIfFailed();
            claim.Value!.Prepared.Should().NotBeNull();
            claim.Value.JobId.Should().Be(normal.Envelope.JobId.Value);
            var terminalProbe = claim.Probes.Should().ContainSingle().Which;
            foreach (var foreign in new[] { finalizerProbe, terminalProbe })
            {
                var actual = await active.ProbeCommitAsync(foreign);
                actual.ThrowIfFailed();
                actual.Value!.Disposition.Should().Be(BackgroundJobProbeDisposition.ControlCommitted,
                    "the negative must start from an independently valid foreign descriptor");
                var mixed = await active.ProbeCommitAsync(foreign with { UninvokableHistory = original.UninvokableHistory });
                mixed.ThrowIfFailed();
                mixed.Value!.Disposition.Should().Be(BackgroundJobProbeDisposition.Unresolved,
                    "foreign history cannot select another specialized classifier before checking the original request");
            }
            (await ReadUninvokableAuditAsync(database, originalJob)).Count.Should().Be(0);
            (await ReadUninvokableAuditAsync(database, normal.Envelope.JobId)).Count.Should().Be(0);
        }
        finally { CloseUninvokableNormal(normal); if (finalizer is not null) CloseUninvokableNormal(finalizer); }
    }

    private static BackgroundJobDefinitionRegistry UninvokableRetiredRegistry() => BackgroundJobDefinitionRegistry.Create(
        [], BackgroundJobFrameworkRequests.All, [], [BackgroundJobRuntimeFixture.ExternalKey], Fixture.Registry.Infrastructure);

    private static BackgroundJobDefinitionRegistry UninvokablePendingRegistry()
    {
        // Same actual fixture types/adapter/topology; only this read-only
        // deployment catalogue changes the existing external key to Pending.
        var definition = Fixture.Registry.Resolve(BackgroundJobRuntimeFixture.ExternalKey).Definition!;
        Fixture.Registry.Infrastructure.TryGetAdapter(definition.ProfileRegistration.ExternalPortType!, out var adapter).Should().BeTrue();
        var module = new BackgroundJobModuleRegistration(definition.ParameterType.Assembly,
            Fixture.RequestType("Declarations").Assembly, adapter!.ImplementationType.Assembly);
        return BackgroundJobDefinitionRegistry.Create(
            [new(definition.Key, Fixture.RequestType("Declarations"), definition.ParameterType, definition.Scope,
                BackgroundJobDefinitionReadiness.Pending, definition.Queue, definition.ProfileRegistration, capacityGroup: definition.CapacityGroup)],
            BackgroundJobFrameworkRequests.All.Concat(definition.RootRequestTypes.Select(type =>
                new BackgroundJobRequestDescriptor(type, BackgroundJobRequestClassification.Ordinary, definition.Scope))),
            [module], infrastructure: Fixture.Registry.Infrastructure);
    }

    private static async Task<ConfirmedStartReceipt> PrepareUninvokableAsync(DisposableSchemaDatabase database, IServiceProvider services)
    {
        var normal = await AdmitFixtureAsync(services, database, await FixtureClaim.ClaimAsync(database, await ExternalRetryJobAsync(database)));
        var prepared = await services.GetRequiredService<JobExecutor>().ExecuteBusinessAsync(normal,
            normal.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare));
        prepared.CommitAcknowledged.Should().BeTrue();
        prepared.CleanupFailed.Should().BeFalse();
        normal.SourceOwner.CleanupConfirmed.Should().BeTrue();
        return normal;
    }

    private static async Task<BackgroundJobExternalSnapshot> ArmUninvokableAsync(IServiceProvider services, ConfirmedStartReceipt normal)
    {
        var executor = services.GetRequiredService<JobExecutor>();
        var read = await executor.ReadExternalStateAsync(normal, CancellationToken.None);
        read.CommitAcknowledged.Should().BeTrue();
        read.CleanupFailed.Should().BeFalse();
        var arm = await executor.RecordExternalArmAsync(normal, read.Handle!, normal.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm),
            CancellationToken.None);
        arm.CommitAcknowledged.Should().BeTrue();
        arm.CleanupFailed.Should().BeFalse();
        return read.Handle!.Snapshot;
    }

    private static async Task WriteUninvokableOutcomeAsync(IServiceProvider services, ConfirmedStartReceipt normal,
        BackgroundJobExternalState state, BackgroundJobExternalFailurePolicy? policy)
    {
        var prepared = await ArmUninvokableAsync(services, normal);
        // Explicit writer fixture evidence, not a claim that a provider ran.
        var gate = normal.Run.OpenBoundary(BackgroundJobCheckpointKind.Outcome);
        var command = new RecordBackgroundJobOutcomeCommand(normal, gate, prepared.PlanFingerprint!, prepared.OperationId!.Value,
            "{\"ArtifactId\":\"" + Guid.NewGuid().ToString("D") + "\"}", state, policy, Guid.NewGuid(), BackgroundJobQuiescenceKind.ProviderFenced);
        (await SendCheckpointAsync(services, normal, command, gate)).IsSuccess.Should().BeTrue();
        command.CommitAcknowledged.Should().BeTrue();
        normal.SourceOwner.CleanupConfirmed.Should().BeTrue();
    }

    private static void CloseUninvokableNormal(ConfirmedStartReceipt normal)
    {
        normal.Run.Close();
        normal.SourceOwner.CloseIssuance();
        normal.SourceOwner.CleanupConfirmed.Should().BeTrue();
    }

    private static async Task ExpireUninvokableOwnerAsync(DisposableSchemaDatabase database, BackgroundJobId job)
    {
        // Explicit store eligibility fixture, only after actual source cleanup;
        // this changes scheduling ownership and never supplies effect evidence.
        await using var control = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await SchemaQueries.ExecuteAsync(control, null, "UPDATE background_jobs SET ownership_until=clock_timestamp() WHERE id=@id",
            ("id", job.Value));
    }

    private static async Task AssertUninvokableTimezoneAsync(PrivilegedDataSource source, string expected)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SHOW TimeZone", connection) { CommandTimeout = 5 };
        (await command.ExecuteScalarAsync()).Should().Be(expected);
    }

    private static async Task<BackgroundJobTerminalClaim> CompleteUninvokableTerminalAsync(DisposableSchemaDatabase database,
        IServiceProvider services, PrivilegedDataSource source, BackgroundJobRuntimeGate runtime, PostgresBackgroundJobControlStore control,
        BackgroundJobId job, BackgroundJobCommitProbe requestOrigin, bool needsCleanup)
    {
        services.GetRequiredService<BackgroundJobDefinitionRegistry>().DefinitionKeys.Should().BeEmpty();
        var before = await ReadUninvokableStateAsync(database, job);
        before.Phase.Should().Be("terminal_pending");
        before.Reason.Should().Be("retired_definition");
        before.TerminalAt.Should().BeNull();
        (await ReadUninvokableAuditAsync(database, job)).Count.Should().Be(0);
        var terminal = new PostgresBackgroundJobTerminalStore(source, services.GetRequiredService<IServiceScopeFactory>(),
            new(), new SystemGuidFactory(), runtime);
        JobExternalCheckpointResult? checkpoint = null;
        if (needsCleanup)
        {
            var early = await terminal.TryClaimTerminalAsync([]);
            early.ThrowIfFailed();
            early.Value.Should().BeNull("current occupancy still needs actual app NoStart and release");
            var acquired = await control.AcquireExternalTerminalNoStartAsync([], CancellationToken.None);
            acquired.ThrowIfFailed();
            acquired.CommitAcknowledged.Should().BeTrue();
            acquired.CleanupConfirmed.Should().BeTrue();
            acquired.Value!.JobId.Should().Be(job);
            acquired.Value.Purpose.Should().Be(BackgroundJobExternalRecoveryPurpose.TerminalNoStart);
            acquired.Value.TerminalReason.Should().Be("retired_definition");
            var recovery = BindRecovery(acquired, runtime);
            var constructions = Fixture.RootConstructions;
            try
            {
                recovery.Run.TryIssueExternalReconcile(recovery).Should().BeFalse();
                recovery.Run.TryOpenBoundary(BackgroundJobCheckpointKind.Outcome).Should().BeNull();
                Action ordinary = () => BackgroundJobExecutionIdentity.FromRecovery(acquired.Value);
                ordinary.Should().Throw<InvalidOperationException>();
                var executor = services.GetRequiredService<JobExecutor>();
                var read = await executor.ReadExternalStateAsync(recovery, CancellationToken.None);
                read.CommitAcknowledged.Should().BeTrue();
                read.CleanupFailed.Should().BeFalse();
                (read.Handle!.Snapshot.PlanJson is not null).Should().Be(before.Prepared);
                read.Handle.Snapshot.OutcomeJson.Should().BeNull();
                read.Handle.Snapshot.SelectedNormalAttemptId.Should().BeNull();
                var forbidden = await executor.ReconcileExternalAsync(recovery, read.Handle, CancellationToken.None);
                forbidden.Status.Should().Be(JobExternalExecutionStatus.EntryRefused);
                forbidden.Observation.Should().BeNull();
                forbidden.CleanupFailed.Should().BeFalse();
                checkpoint = await executor.RecordExternalTerminalNoStartAsync(recovery, read.Handle,
                    recovery.Run.OpenBoundary(BackgroundJobCheckpointKind.NoStart), CancellationToken.None);
                checkpoint.CommitAcknowledged.Should().BeTrue();
                checkpoint.CleanupFailed.Should().BeFalse();
                checkpoint.Outcome!.IsTerminalNoStart.Should().BeTrue();
                recovery.SourceOwner.CleanupConfirmed.Should().BeTrue();
                Fixture.RootConstructions.Should().Be(constructions);
            }
            finally { recovery.Run.Close(); recovery.SourceOwner.CloseIssuance(); }
            var live = await terminal.TryClaimTerminalAsync([]);
            live.ThrowIfFailed();
            live.Value.Should().BeNull("actual cleanup does not revoke its still-live acquisition lease");
            await ExpireUninvokableOwnerAsync(database, job);
        }
        else
        {
            var unnecessary = await control.AcquireExternalTerminalNoStartAsync([], CancellationToken.None);
            unnecessary.ThrowIfFailed();
            unnecessary.Value.Should().BeNull("zero admissions or already complete selected history needs no app cleanup");
        }
        var requested = await control.ProbeCommitAsync(requestOrigin);
        requested.ThrowIfFailed();
        requested.Value!.Disposition.Should().Be(BackgroundJobProbeDisposition.ControlCommitted);
        var acquiredClaim = await terminal.TryClaimTerminalAsync([]);
        acquiredClaim.ThrowIfFailed();
        acquiredClaim.CommitAcknowledged.Should().BeTrue();
        acquiredClaim.CleanupConfirmed.Should().BeTrue();
        var claim = acquiredClaim.Value!;
        claim.JobId.Should().Be(job.Value);
        claim.Reason.Should().Be("retired_definition");
        claim.NormalAttempts.Should().Be(before.Normal);
        (claim.Prepared is not null).Should().Be(before.Prepared);
        var completed = await terminal.TryTerminalizeAsync(claim, BackgroundJobRuntimeFixture.Traceparent);
        completed.ThrowIfFailed();
        completed.CommitAcknowledged.Should().BeTrue();
        completed.CleanupConfirmed.Should().BeTrue();
        var after = await ReadUninvokableStateAsync(database, job);
        after.TerminalAt.Should().NotBeNull();
        after.Incomplete.Should().Be(before.Prepared);
        after.Immutable.Should().Be(before.Immutable);
        after.Attempts.Should().Be(before.Normal + before.FinalizeAttempts);
        after.Reservations.Should().Be(before.Normal);
        after.Released.Should().Be(before.Normal);
        after.NoStarts.Should().Be(before.NoStarts + (needsCleanup ? 1 : 0));
        after.Intents.Should().Be(before.Intents);
        after.Outcomes.Should().Be(before.Outcomes);
        var audit = await ReadUninvokableAuditAsync(database, job);
        audit.Count.Should().Be(1);
        audit.At.Should().Be(after.TerminalAt);
        using (var parsed = JsonDocument.Parse(audit.Metadata!))
        {
            var metadata = parsed.RootElement;
            metadata.GetProperty("job_id").GetGuid().Should().Be(job.Value);
            metadata.GetProperty("reason").GetString().Should().Be("retired_definition");
            metadata.GetProperty("normal_attempts").GetInt32().Should().Be(before.Normal);
            metadata.GetProperty("acquisition_token").GetGuid().Should().Be(claim.Token);
            if (before.Prepared)
            {
                metadata.GetProperty("business_finalization_incomplete").GetBoolean().Should().BeTrue();
                metadata.GetProperty("finalize_attempts").GetInt32().Should().Be(before.FinalizeAttempts);
                if (claim.Prepared!.SelectedNormalAttemptId is { } selected)
                {
                    metadata.GetProperty("selected_normal_attempt_id").GetGuid().Should().Be(selected);
                    metadata.GetProperty("finalization_status").GetString().Should().Be(claim.Prepared.FinalizationStatus);
                    metadata.GetProperty("finalization_reason").GetString().Should().Be(claim.Prepared.FinalizationReason);
                    metadata.GetProperty("external_state").GetString().Should().Be(claim.Prepared.State);
                }
            }
        }
        if (checkpoint is not null)
        {
            var history = await control.ProbeExternalCheckpointAsync(BackgroundJobCheckpointHistoryOrigin.ForNoStart(checkpoint), CancellationToken.None);
            history.ThrowIfFailed();
            history.Value!.Disposition.Should().Be(BackgroundJobCheckpointHistoryDisposition.Committed);
            history.Value.Confirmation!.Matches(checkpoint.Outcome!).Should().BeTrue();
        }
        var historyAfter = await control.ProbeCommitAsync(requestOrigin);
        historyAfter.ThrowIfFailed();
        historyAfter.Value!.Disposition.Should().Be(BackgroundJobProbeDisposition.ControlCommitted);
        var duplicate = await terminal.TryTerminalizeAsync(claim, BackgroundJobRuntimeFixture.Traceparent);
        duplicate.ThrowIfFailed();
        duplicate.Value.Should().BeNull();
        (await ReadUninvokableAuditAsync(database, job)).Count.Should().Be(1);
        return claim;
    }

    private sealed record UninvokableState(string Phase, string? Reason, DateTimeOffset? TerminalAt, bool Incomplete,
        int Normal, int FinalizeAttempts, bool Prepared, bool CurrentStamped, string Immutable, long Attempts, long Reservations,
        long Released, long NoStarts, long Intents, long Outcomes);

    private static async Task<UninvokableState> ReadUninvokableStateAsync(DisposableSchemaDatabase database, BackgroundJobId job)
    {
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        await using var command = new NpgsqlCommand("""
            SELECT j.phase,j.terminal_reason,j.dead_lettered_at,j.business_finalization_incomplete,
                j.normal_attempts,j.finalize_attempts,j.plan IS NOT NULL,COALESCE(current_attempt.plan_fingerprint IS NOT NULL,false),
                jsonb_build_array(j.plan,j.plan_fingerprint,j.operation_id,j.prepared_at,j.final_outcome_attempt_id,
                    j.finalization_status,j.finalization_reason,j.current_attempt_id,j.normal_attempts,j.finalize_attempts,
                    j.wake_generation,j.attempt_deadline,
                    (SELECT jsonb_agg(jsonb_build_array(a.id,a.tenant_id,a.organization_id,a.job_id,a.stage,a.ordinal,
                        a.admission_token,a.admitted_at,a.deadline,a.final_outcome_attempt_id,a.plan_fingerprint,a.operation_id,
                        a.execute_intent_at,a.outcome,a.outcome_kind,a.failure_policy,a.outcome_at,a.quiescence_id,a.quiescence_kind,a.quiescent_at,
                        CASE WHEN a.id<>j.current_attempt_id THEN a.no_start_at END) ORDER BY a.stage,a.ordinal)
                     FROM background_job_attempts a WHERE a.job_id=j.id),
                    (SELECT jsonb_agg(jsonb_build_array(r.tenant_id,r.capacity_group,r.job_id,r.attempt_id,
                        CASE WHEN r.attempt_id<>j.current_attempt_id THEN r.released_at END,
                        CASE WHEN r.attempt_id<>j.current_attempt_id THEN r.release_reason END) ORDER BY r.attempt_id)
                     FROM background_job_reservations r WHERE r.job_id=j.id))::text,
                (SELECT count(*) FROM background_job_attempts a WHERE a.job_id=j.id),
                (SELECT count(*) FROM background_job_reservations r WHERE r.job_id=j.id),
                (SELECT count(*) FROM background_job_reservations r WHERE r.job_id=j.id AND r.released_at IS NOT NULL),
                (SELECT count(*) FROM background_job_attempts a WHERE a.job_id=j.id AND a.no_start_at IS NOT NULL),
                (SELECT count(*) FROM background_job_attempts a WHERE a.job_id=j.id AND a.execute_intent_at IS NOT NULL),
                (SELECT count(*) FROM background_job_attempts a WHERE a.job_id=j.id AND a.outcome IS NOT NULL)
            FROM background_jobs j LEFT JOIN background_job_attempts current_attempt ON current_attempt.id=j.current_attempt_id WHERE j.id=@id
            """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("id", job.Value);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        return new(row.GetString(0), row.IsDBNull(1) ? null : row.GetString(1), row.IsDBNull(2) ? null : row.GetFieldValue<DateTimeOffset>(2),
            row.GetBoolean(3), row.GetInt32(4), row.GetInt32(5), row.GetBoolean(6), row.GetBoolean(7), row.GetString(8),
            row.GetInt64(9), row.GetInt64(10), row.GetInt64(11), row.GetInt64(12), row.GetInt64(13), row.GetInt64(14));
    }

    private static async Task<(long Count, string? Metadata, DateTimeOffset? At)> ReadUninvokableAuditAsync(
        DisposableSchemaDatabase database, BackgroundJobId job)
    {
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        // Independent of phase/dead_lettered_at; a state predicate cannot hide a stray MUST.
        await using var command = new NpgsqlCommand("""
            SELECT count(*),min(metadata::text),min(timestamp) FROM audit_log
            WHERE operation='platform.job.dead_letter' AND metadata->>'job_id'=@id
            """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("id", job.Value.ToString("D"));
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        return (row.GetInt64(0), row.IsDBNull(1) ? null : row.GetString(1), row.IsDBNull(2) ? null : row.GetFieldValue<DateTimeOffset>(2));
    }
}
