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
    [InlineData(false, nameof(BackgroundJobExternalUnresolvedDisposition.Running), 1)]
    [InlineData(false, nameof(BackgroundJobExternalUnresolvedDisposition.ReconcileRequired), 1)]
    [InlineData(false, nameof(BackgroundJobExternalUnresolvedDisposition.Unknown), 1)]
    [InlineData(true, nameof(BackgroundJobExternalUnresolvedDisposition.Running), 1)]
    [InlineData(true, nameof(BackgroundJobExternalUnresolvedDisposition.Unknown), 1)]
    [InlineData(true, nameof(BackgroundJobExternalUnresolvedDisposition.ProbeFault), 1)]
    [InlineData(true, nameof(BackgroundJobExternalUnresolvedDisposition.ProbeTimeout), 1)]
    [InlineData(true, nameof(BackgroundJobExternalUnresolvedDisposition.Running), 6)]
    public async Task Unresolved_status_paces_or_quarantines_without_outcome_release_or_another_admission(
        bool recovered, string dispositionName, int ordinal)
    {
        var disposition = Enum.Parse<BackgroundJobExternalUnresolvedDisposition>(dispositionName);
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await ExternalRetryJobAsync(database);
        await using var host = RecoveryHost(database, preparing: true);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var options = UnresolvedBounds;
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, options, new SystemGuidFactory(), runtime);
        var normal = await RetainedSelectionAttemptAsync(database, host.Services, job, ordinal, store, runtime);
        var identity = await UnresolvedFixtureAsync(database, host.Services, normal, store, recovered);
        var immutable = await RetainedSelectionFactsAsync(database, normal);
        var lower = await RetryDatabaseClockAsync(database);
        // This is a status/writer test. The finite disposition is explicit
        // fixture input, not evidence that a real provider timed out or ran.
        var changed = await store.FinishExternalUnresolvedAsync(identity, disposition, CancellationToken.None);
        changed.ThrowIfFailed();
        changed.CommitAcknowledged.Should().BeTrue();
        changed.CleanupConfirmed.Should().BeTrue();
        var upper = await RetryDatabaseClockAsync(database);
        var after = await ReadUnresolvedFixtureAsync(database, job);
        var quarantined = disposition == BackgroundJobExternalUnresolvedDisposition.Unknown;
        var failed = disposition is BackgroundJobExternalUnresolvedDisposition.ProbeFault or BackgroundJobExternalUnresolvedDisposition.ProbeTimeout;
        after.Phase.Should().Be(quarantined ? "quarantined" : "reconciling");
        after.Generation.Should().Be(identity.WakeGeneration + (quarantined ? 0 : 1));
        after.Failures.Should().Be(failed ? 1 : 0);
        after.Quarantine.Should().Be(quarantined ? "unknown_outcome" : null);
        after.Token.Should().BeNull();
        after.Normal.Should().Be(ordinal);
        after.Finalize.Should().Be(0);
        after.Unreleased.Should().Be(1);
        after.Terminal.Should().BeNull();
        if (quarantined) after.ProbeAfter.Should().BeNull();
        else after.ProbeAfter!.Value.Should().BeOnOrAfter(lower + options.RecoveryProbeInterval)
            .And.BeOnOrBefore(upper + options.RecoveryProbeInterval);
        (await RetainedSelectionFactsAsync(database, normal)).Should().Be(immutable);
        var descriptor = changed.Probes.Should().ContainSingle().Subject;
        descriptor.ExpectedUntil.Should().BeNull();
        descriptor.ExpectedAvailableAfter.Should().BeNull();
        var proof = await store.ProbeCommitAsync(descriptor);
        proof.ThrowIfFailed();
        proof.CleanupConfirmed.Should().BeTrue();
        proof.Value!.Disposition.Should().Be(BackgroundJobProbeDisposition.ControlCommitted);
        proof.Value.RecoveryReceipt.Should().BeNull();
        proof.Value.CurrentRecoveryAuthority.Should().BeFalse();
        normal.Run.Close();
    }

    [Fact]
    public async Task Unsuccessful_probes_count_once_running_preserves_count_and_sixth_failure_quarantines()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await ExternalRetryJobAsync(database);
        await using var host = RecoveryHost(database, preparing: true);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, UnresolvedBounds, new SystemGuidFactory(), runtime);
        var normal = await RetainedSelectionAttemptAsync(database, host.Services, job, 1, store, runtime);
        var identity = await UnresolvedFixtureAsync(database, host.Services, normal, store, true);
        var immutable = await RetainedSelectionFactsAsync(database, normal);
        for (var count = 1; count <= 6; count++)
        {
            var changed = await store.FinishExternalUnresolvedAsync(identity, BackgroundJobExternalUnresolvedDisposition.ProbeFault, CancellationToken.None);
            changed.ThrowIfFailed();
            changed.CommitAcknowledged.Should().BeTrue();
            var after = await ReadUnresolvedFixtureAsync(database, job);
            after.Failures.Should().Be(count);
            after.Phase.Should().Be(count == 6 ? "quarantined" : "reconciling");
            after.Quarantine.Should().Be(count == 6 ? "probe_exhausted" : null);
            after.Unreleased.Should().Be(1);
            after.Normal.Should().Be(1);
            after.Finalize.Should().Be(0);
            after.Terminal.Should().BeNull();
            var duplicate = await store.FinishExternalUnresolvedAsync(identity, BackgroundJobExternalUnresolvedDisposition.ProbeFault, CancellationToken.None);
            duplicate.ThrowIfFailed();
            duplicate.CommitAcknowledged.Should().BeFalse();
            duplicate.Probes.Should().BeEmpty();
            (await ReadUnresolvedFixtureAsync(database, job)).Should().Be(after);
            if (count == 6) break;
            identity = await ReacquireUnresolvedFixtureAsync(database, normal, store);
            if (count == 2)
            {
                var running = await store.FinishExternalUnresolvedAsync(identity, BackgroundJobExternalUnresolvedDisposition.Running, CancellationToken.None);
                running.ThrowIfFailed();
                running.CommitAcknowledged.Should().BeTrue();
                (await ReadUnresolvedFixtureAsync(database, job)).Failures.Should().Be(2,
                    "Running is neither a failed probe nor permission to reset the cumulative count");
                identity = await ReacquireUnresolvedFixtureAsync(database, normal, store);
            }
        }
        (await RetainedSelectionFactsAsync(database, normal)).Should().Be(immutable);
        var unavailable = await store.AcquireExternalRecoveryAsync(job, await RecoveryGenerationAsync(database, job),
            normal.Envelope.Queue, CancellationToken.None);
        unavailable.ThrowIfFailed();
        unavailable.Value.Should().BeNull("quarantine never grants another probe or external admission");
        normal.Run.Close();
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Unresolved_lost_commit_has_exact_history_without_replaying_a_count_or_borrowing_a_successor(
        bool recovered, bool quarantine, bool committed)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await ExternalRetryJobAsync(database);
        await using var host = RecoveryHost(database, preparing: true);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, UnresolvedBounds, new SystemGuidFactory(), runtime);
        var normal = await RetainedSelectionAttemptAsync(database, host.Services, job, 1, store, runtime);
        var identity = await UnresolvedFixtureAsync(database, host.Services, normal, store, recovered);
        var before = await ReadUnresolvedFixtureAsync(database, job);
        var immutable = await RetainedSelectionFactsAsync(database, normal);
        await using var proxy = new CommitResponseDroppingProxy(database.OutboxConnectionString, dropCommitRequest: !committed);
        await using var brokenSource = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(proxy.ConnectionString));
        var broken = new PostgresBackgroundJobControlStore(brokenSource, Fixture.Registry, UnresolvedBounds, new SystemGuidFactory(), runtime);
        var changed = await broken.FinishExternalUnresolvedAsync(identity, quarantine ? BackgroundJobExternalUnresolvedDisposition.Unknown
            : recovered ? BackgroundJobExternalUnresolvedDisposition.ProbeFault : BackgroundJobExternalUnresolvedDisposition.Running,
            CancellationToken.None);
        changed.CommitAcknowledged.Should().BeFalse();
        changed.CommitUncertain.Should().BeTrue();
        changed.CleanupConfirmed.Should().BeTrue();
        changed.Value.Should().BeNull();
        proxy.DroppedCommitRequests.Should().Be(committed ? 0 : 1);
        proxy.DroppedCommitResponses.Should().Be(committed ? 1 : 0);
        var descriptor = changed.Probes.Should().ContainSingle().Subject;
        var proof = await store.ProbeCommitAsync(descriptor);
        proof.ThrowIfFailed();
        proof.CleanupConfirmed.Should().BeTrue();
        proof.Value!.Disposition.Should().Be(committed ? BackgroundJobProbeDisposition.ControlCommitted : BackgroundJobProbeDisposition.RolledBack);
        proof.CommitAcknowledged.Should().BeFalse();
        changed.CommitAcknowledged.Should().BeFalse();
        (await RetainedSelectionFactsAsync(database, normal)).Should().Be(immutable);
        if (!committed) (await ReadUnresolvedFixtureAsync(database, job)).Should().Be(before);
        else
        {
            var duplicate = await store.FinishExternalUnresolvedAsync(identity, BackgroundJobExternalUnresolvedDisposition.Unknown, CancellationToken.None);
            duplicate.ThrowIfFailed();
            duplicate.CommitAcknowledged.Should().BeFalse();
            (await ReadUnresolvedFixtureAsync(database, job)).Failures.Should().Be(recovered && !quarantine ? 1 : 0);
            if (!quarantine)
            {
                var successor = await ReacquireUnresolvedFixtureAsync(database, normal, store);
                successor.Token.Should().NotBe(identity.Token);
                var superseded = await store.ProbeCommitAsync(descriptor);
                superseded.ThrowIfFailed();
                superseded.Value!.Disposition.Should().Be(BackgroundJobProbeDisposition.ChangedOwner);
                superseded.Value.RecoveryReceipt.Should().BeNull();
            }
        }
        normal.Run.Close();
    }

    [Theory]
    [InlineData("no_intent")]
    [InlineData("completed")]
    [InlineData("expired")]
    [InlineData("closed_process")]
    public async Task Unresolved_stale_report_refuses_durable_known_facts_or_closed_authority(string mode)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await ExternalRetryJobAsync(database);
        await using var host = RecoveryHost(database, preparing: true);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, UnresolvedBounds, new SystemGuidFactory(), runtime);
        var normal = await RetainedSelectionAttemptAsync(database, host.Services, job, 1, store, runtime);
        var identity = mode == "completed"
            ? await KnownRetryFixtureAsync(database, host.Services, normal, store, runtime, true, false)
            : mode == "no_intent" ? BackgroundJobExecutionIdentity.FromReceipt(normal)
            : await UnresolvedFixtureAsync(database, host.Services, normal, store, true);
        if (mode == "expired")
        {
            await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
            await SchemaQueries.ExecuteAsync(dispatcher, null, "UPDATE background_jobs SET ownership_until=clock_timestamp() WHERE id=@id", ("id", job.Value));
        }
        if (mode == "closed_process") runtime.StopStarts();
        var before = await ReadUnresolvedFixtureAsync(database, job);
        var facts = await RetainedSelectionFactsAsync(database, normal);
        var result = await store.FinishExternalUnresolvedAsync(identity, BackgroundJobExternalUnresolvedDisposition.Unknown, CancellationToken.None);
        result.ThrowIfFailed();
        result.CommitAcknowledged.Should().BeFalse();
        result.CleanupConfirmed.Should().BeTrue();
        (await ReadUnresolvedFixtureAsync(database, job)).Should().Be(before);
        (await RetainedSelectionFactsAsync(database, normal)).Should().Be(facts);
        normal.Run.Close();
    }

    [Fact]
    public async Task Unresolved_final_park_refusal_rolls_back_shortening_and_counter_together()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await ExternalRetryJobAsync(database);
        await using var host = RecoveryHost(database, preparing: true);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, UnresolvedBounds, new SystemGuidFactory(), runtime);
        var normal = await RetainedSelectionAttemptAsync(database, host.Services, job, 1, store, runtime);
        var identity = await UnresolvedFixtureAsync(database, host.Services, normal, store, true);
        var before = await ReadUnresolvedFixtureAsync(database, job);
        await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
            await SchemaQueries.ExecuteAsync(owner, null, """
                ALTER TABLE background_jobs ADD CONSTRAINT reject_unresolved_park
                CHECK (NOT (profile='external' AND phase='reconciling' AND ownership_token IS NULL))
                """);
        var changed = await store.FinishExternalUnresolvedAsync(identity, BackgroundJobExternalUnresolvedDisposition.ProbeFault, CancellationToken.None);
        changed.Failed.Should().BeTrue();
        Action failure = changed.ThrowIfFailed;
        failure.Should().Throw<PostgresException>().Which.ConstraintName.Should().Be("reject_unresolved_park");
        changed.KnownRollback.Should().BeTrue();
        changed.CommitUncertain.Should().BeFalse();
        changed.CleanupConfirmed.Should().BeTrue();
        changed.Probes.Should().BeEmpty();
        (await ReadUnresolvedFixtureAsync(database, job)).Should().Be(before);
        normal.Run.Close();
    }

    [Fact]
    public async Task Positive_repeatable_outcome_preserves_probe_count_until_the_next_counted_admission()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await ExternalRetryJobAsync(database);
        await using var host = RecoveryHost(database, preparing: true);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, UnresolvedBounds, new SystemGuidFactory(), runtime);
        var normal = await RetainedSelectionAttemptAsync(database, host.Services, job, 1, store, runtime);
        var identity = await UnresolvedFixtureAsync(database, host.Services, normal, store, true);
        var fault = await store.FinishExternalUnresolvedAsync(identity, BackgroundJobExternalUnresolvedDisposition.ProbeFault, CancellationToken.None);
        fault.ThrowIfFailed();
        fault.CommitAcknowledged.Should().BeTrue();
        (await ReadUnresolvedFixtureAsync(database, job)).Failures.Should().Be(1);
        await WaitUnresolvedProbeDueAsync(database, job);
        var acquired = await store.AcquireExternalRecoveryAsync(job, await RecoveryGenerationAsync(database, job), normal.Envelope.Queue, CancellationToken.None);
        acquired.ThrowIfFailed();
        var recovery = BindRecovery(acquired, runtime);
        var executor = host.Services.GetRequiredService<JobExecutor>();
        var read = await executor.ReadExternalStateAsync(recovery, CancellationToken.None);
        BackgroundJobRetainedExternalObservation observation;
        // Explicit checkpoint fixture facts; this does not claim provider causality.
        using (BackgroundJobExecutionAdmission.EnterExternal())
            observation = BackgroundJobRetainedExternalObservation.Capture(new(job, normal.AttemptId,
                read.Handle!.Snapshot.OperationId!.Value, normal.Deadline), read.Handle.Snapshot.PlanFingerprint!,
                BackgroundJobExternalResult.Failed(new WriterOutcome(Guid.NewGuid()),
                    new(Guid.NewGuid(), BackgroundJobQuiescenceKind.ProviderFenced), BackgroundJobExternalFailurePolicy.TransientRepeatable));
        (await executor.RecordExternalOutcomeAsync(recovery, read.Handle!, recovery.Run.OpenBoundary(BackgroundJobCheckpointKind.Outcome),
            observation, CancellationToken.None)).CommitAcknowledged.Should().BeTrue();
        var scheduled = await store.RetryKnownExternalAsync(BackgroundJobExecutionIdentity.FromRecovery(acquired.Value!),
            BackgroundJobExternalRetryMode.RecoveryKnown, CancellationToken.None);
        scheduled.ThrowIfFailed();
        scheduled.CommitAcknowledged.Should().BeTrue();
        (await ReadUnresolvedFixtureAsync(database, job)).Failures.Should().Be(1);
        var until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (await RetryDatabaseClockAsync(database) < (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!.AvailableAfter)
        {
            if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("The actual fixed retry did not become due.");
            await Task.Delay(10);
        }
        var next = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        next.NormalAttemptOrdinal.Should().Be(2);
        (await ReadUnresolvedFixtureAsync(database, job)).Failures.Should().Be(0);
        normal.Run.Close();
        recovery.Run.Close();
        next.Run.Close();
    }

    [Fact]
    public async Task Running_beyond_the_original_deadline_and_grace_quarantines_without_proving_quiescence()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await ExternalRetryJobAsync(database);
        await using var host = RecoveryHost(database, preparing: true, configure: services =>
            services.AddSingleton(new FixtureAdmissionWindow(TimeSpan.FromSeconds(2))));
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var options = UnresolvedBounds with { QuiescenceGrace = TimeSpan.FromMilliseconds(50) };
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, options, new SystemGuidFactory(), runtime);
        var normal = await RetainedSelectionAttemptAsync(database, host.Services, job, 1, store, runtime);
        var identity = await UnresolvedFixtureAsync(database, host.Services, normal, store, true);
        var limit = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (await RetryDatabaseClockAsync(database) <= normal.Deadline + options.QuiescenceGrace)
        {
            if (DateTimeOffset.UtcNow >= limit) throw new TimeoutException("The actual immutable deadline/grace did not elapse.");
            await Task.Delay(10);
        }
        var immutable = await RetainedSelectionFactsAsync(database, normal);
        var changed = await store.FinishExternalUnresolvedAsync(identity, BackgroundJobExternalUnresolvedDisposition.Running, CancellationToken.None);
        changed.ThrowIfFailed();
        changed.CommitAcknowledged.Should().BeTrue();
        var after = await ReadUnresolvedFixtureAsync(database, job);
        after.Phase.Should().Be("quarantined");
        after.Quarantine.Should().Be("unknown_outcome");
        after.Failures.Should().Be(0);
        after.Unreleased.Should().Be(1);
        after.Terminal.Should().BeNull();
        after.Normal.Should().Be(1);
        (await RetainedSelectionFactsAsync(database, normal)).Should().Be(immutable,
            "the elapsed clock is containment only, not a quiescence, cancellation or outcome receipt");
        normal.Run.Close();
    }

    private static async Task<BackgroundJobExecutionIdentity> UnresolvedFixtureAsync(DisposableSchemaDatabase database,
        IServiceProvider services, ConfirmedStartReceipt normal, PostgresBackgroundJobControlStore store, bool recovered)
    {
        var executor = services.GetRequiredService<JobExecutor>();
        var read = await executor.ReadExternalStateAsync(normal, CancellationToken.None);
        (await executor.RecordExternalArmAsync(normal, read.Handle!, normal.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm),
            CancellationToken.None)).CommitAcknowledged.Should().BeTrue();
        if (!recovered) return BackgroundJobExecutionIdentity.FromReceipt(normal);
        await ExpireSelectionLeaseAsync(database, normal);
        return await ReacquireUnresolvedFixtureAsync(database, normal, store);
    }

    private static async Task<BackgroundJobExecutionIdentity> ReacquireUnresolvedFixtureAsync(DisposableSchemaDatabase database,
        ConfirmedStartReceipt normal, PostgresBackgroundJobControlStore store)
    {
        await WaitUnresolvedProbeDueAsync(database, normal.Envelope.JobId);
        var acquired = await store.AcquireExternalRecoveryAsync(normal.Envelope.JobId,
            await RecoveryGenerationAsync(database, normal.Envelope.JobId), normal.Envelope.Queue, CancellationToken.None);
        acquired.ThrowIfFailed();
        acquired.CleanupConfirmed.Should().BeTrue();
        acquired.CommitAcknowledged.Should().BeTrue();
        acquired.Value.Should().NotBeNull();
        return BackgroundJobExecutionIdentity.FromRecovery(acquired.Value!);
    }

    private static BackgroundJobRuntimeOptions UnresolvedBounds => new() { RecoveryProbeInterval = TimeSpan.FromSeconds(5) };

    private static async Task WaitUnresolvedProbeDueAsync(DisposableSchemaDatabase database, BackgroundJobId job)
    {
        var limit = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            var current = await ReadUnresolvedFixtureAsync(database, job);
            current.Phase.Should().Be("reconciling");
            current.Token.Should().BeNull();
            current.ProbeAfter.Should().NotBeNull();
            if (await RetryDatabaseClockAsync(database) >= current.ProbeAfter!.Value) return;
            if (DateTimeOffset.UtcNow >= limit) throw new TimeoutException("The actual bounded probe schedule did not become due.");
            await Task.Delay(25);
        }
    }

    private sealed record UnresolvedFixtureStatus(string Phase, long Generation, Guid? Token, DateTimeOffset? Until,
        int Failures, DateTimeOffset? ProbeAfter, string? Quarantine, string? Terminal, int Normal, int Finalize, long Unreleased);

    private static async Task<UnresolvedFixtureStatus> ReadUnresolvedFixtureAsync(DisposableSchemaDatabase database, BackgroundJobId job)
    {
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        await using var command = new NpgsqlCommand("""
            SELECT j.phase,j.wake_generation,j.ownership_token,j.ownership_until,j.unsuccessful_probes,j.probe_after,
                j.quarantine_reason,j.terminal_reason,j.normal_attempts,j.finalize_attempts,
                (SELECT count(*) FROM background_job_reservations r WHERE r.job_id=j.id AND r.released_at IS NULL)
            FROM background_jobs j WHERE j.id=@id
            """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("id", job.Value);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        return new(row.GetString(0), row.GetInt64(1), row.IsDBNull(2) ? null : row.GetGuid(2),
            row.IsDBNull(3) ? null : row.GetFieldValue<DateTimeOffset>(3), row.GetInt32(4),
            row.IsDBNull(5) ? null : row.GetFieldValue<DateTimeOffset>(5), row.IsDBNull(6) ? null : row.GetString(6),
            row.IsDBNull(7) ? null : row.GetString(7), row.GetInt32(8), row.GetInt32(9), row.GetInt64(10));
    }
}
