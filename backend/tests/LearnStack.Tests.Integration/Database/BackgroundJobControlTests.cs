using System.Data.Common;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Real guarded control transitions, immutable receipts, finite backoff and locked reconciliation.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed partial class BackgroundJobControlTests(SchemaFixture schema)
{
    private static BackgroundJobRuntimeFixture Fixture => BackgroundJobRuntimeFixture.Shared;

    [Fact]
    public async Task Known_rollback_traverses_recovery_observes_all_five_waits_and_never_admits_a_seventh_start()
    {
        await using var database = await DatabaseAsync();
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        await using var host = Fixture.Host(database, new BackgroundJobFixtureProbe());
        await using var source = Source(database.OutboxConnectionString);
        var store = Control(source);
        var routing = host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>();
        var executor = host.Services.GetRequiredService<JobExecutor>();
        var retry = new BackgroundJobFailureDecision(BackgroundJobFailureDisposition.Retry, "concurrency_conflict", null);
        var attempts = new List<Guid>();
        for (var ordinal = 1; ordinal <= 6; ordinal++)
        {
            var claim = (await routing.TryClaimAsync(job, ordinal))!;
            claim.Should().NotBeNull();
            var started = await executor.ExecuteAsync(claim);
            started.Outcome.Should().Be(JobStartExecutionOutcome.Started);
            started.Receipt!.NormalAttemptOrdinal.Should().Be(ordinal);
            attempts.Add(started.Receipt.AttemptId.Value);
            var identity = BackgroundJobExecutionIdentity.FromReceipt(started.Receipt);
            var before = DateTimeOffset.UtcNow;
            var controlled = await store.FinishKnownRollbackAsync(identity, retry);
            controlled.ThrowIfFailed();
            controlled.CleanupConfirmed.Should().BeTrue();
            controlled.CommitAcknowledged.Should().BeTrue();
            var row = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
            row.NormalAttempts.Should().Be(ordinal);
            row.CurrentAttempt.Should().Be(identity.AttemptId.Value);
            row.OriginalDue.Should().Be(BackgroundJobSchemaSeed.Slot);
            row.OwnershipToken.Should().BeNull();
            if (ordinal < 6)
            {
                row.Phase.Should().Be("pending");
                var delay = BackgroundJobRuntimeOptions.RetryDelays[ordinal - 1];
                row.AvailableAfter.Should().BeOnOrAfter(before + delay - TimeSpan.FromMilliseconds(50));
                row.AvailableAfter.Should().BeOnOrBefore(DateTimeOffset.UtcNow + delay);
                controlled.Value!.WakeGeneration.Should().Be(ordinal + 1);
                (await routing.TryClaimAsync(job, ordinal + 1)).Should().BeNull("the persisted accepted wait is an admission fence");
                // Advance only test eligibility after observing the exact accepted
                // wait; do not spend an hour sleeping or edit the attempt ledger.
                await DispatchAsync(database, "UPDATE background_jobs SET available_after=clock_timestamp() WHERE id=@id", ("id", job.Value));
            }
            else
            {
                row.Phase.Should().Be("terminal_pending");
                controlled.Value!.TerminalReason.Should().Be("retry_exhausted");
                (await routing.TryClaimAsync(job, 7)).Should().BeNull();
            }
        }
        attempts.Should().OnlyHaveUniqueItems();
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(6);
    }

    [Fact]
    public async Task Expired_routing_with_zero_or_prior_attempts_keeps_its_generation_and_spends_no_start()
    {
        await using var database = await DatabaseAsync();
        await using var host = Fixture.Host(database, new BackgroundJobFixtureProbe());
        await using var source = Source(database.OutboxConnectionString);
        var control = Control(source);
        var routing = host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>();
        foreach (var priorStart in new[] { false, true })
        {
            var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
            var generation = 1L;
            Guid? priorAttempt = null;
            if (priorStart)
            {
                var first = (await routing.TryClaimAsync(job, generation))!;
                var admission = await host.Services.GetRequiredService<JobExecutor>().ExecuteAsync(first);
                priorAttempt = admission.Receipt!.AttemptId.Value;
                var changed = await control.FinishKnownRollbackAsync(BackgroundJobExecutionIdentity.FromReceipt(admission.Receipt!),
                    new(BackgroundJobFailureDisposition.Retry, "unexpected_fault", null));
                changed.ThrowIfFailed();
                generation = 2;
                await DispatchAsync(database, "UPDATE background_jobs SET available_after=clock_timestamp() WHERE id=@id", ("id", job.Value));
            }
            var route = (await routing.TryClaimAsync(job, generation))!;
            await DispatchAsync(database, "UPDATE background_jobs SET ownership_until=clock_timestamp()-interval '1 microsecond' WHERE id=@id", ("id", job.Value));
            var recovery = await control.RunRecoveryQuantumAsync(BackgroundJobControlLane.Recovery, []);
            recovery.ThrowIfFailed();
            recovery.Value.Should().Contain(change => change.JobId == job.Value && change.WakeGeneration == generation && change.Phase == "pending");
            var row = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
            row.OwnershipToken.Should().BeNull();
            row.CurrentAttempt.Should().Be(priorAttempt);
            row.NormalAttempts.Should().Be(priorStart ? 1 : 0);
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(priorStart ? 1 : 0);
            var next = await routing.TryClaimAsync(job, generation);
            next.Should().NotBeNull();
            next!.Token.Should().NotBe(route.Token);
        }
    }

    [Fact]
    public async Task Explicit_tombstone_terminalizes_but_unknown_and_Pending_remain_deferred()
    {
        await using var database = await DatabaseAsync();
        const string retired = "fixture.retired.v1";
        var retiredJob = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString, retired);
        var unknown = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString, "fixture.unknown.v1");
        var pending = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString, BackgroundJobRuntimeFixture.PendingKey);
        await using var source = Source(database.OutboxConnectionString);
        var registry = BackgroundJobDefinitionRegistry.Create([], BackgroundJobFrameworkRequests.All, [], [retired]);
        var result = await Control(source, registry).RunRecoveryQuantumAsync(BackgroundJobControlLane.Retirement, []);
        result.ThrowIfFailed();
        result.Value.Should().ContainSingle(change => change.JobId == retiredJob.Value && change.TerminalReason == "retired_definition");
        foreach (var job in new[] { unknown, pending })
        {
            var row = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
            row.Phase.Should().Be("pending");
            row.NormalAttempts.Should().Be(0);
        }
        var pendingControl = await Control(source).RunRecoveryQuantumAsync(BackgroundJobControlLane.Retirement, []);
        pendingControl.ThrowIfFailed();
        pendingControl.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Batch_renewal_ignores_small_control_quantum_returns_actual_DB_time_and_never_resurrects_expiry()
    {
        await using var database = await DatabaseAsync();
        await using var host = Fixture.Host(database, new BackgroundJobFixtureProbe());
        await using var source = Source(database.OutboxConnectionString);
        var control = Control(source, options: new() { ControlBatchSize = 1 });
        var identities = new List<BackgroundJobExecutionIdentity>();
        for (var i = 0; i < 3; i++)
        {
            var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
            var claim = (await host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>().TryClaimAsync(job, 1))!;
            var start = await host.Services.GetRequiredService<JobExecutor>().ExecuteAsync(claim);
            identities.Add(BackgroundJobExecutionIdentity.FromReceipt(start.Receipt!));
        }
        await DispatchAsync(database, "UPDATE background_jobs SET ownership_until=clock_timestamp()-interval '1 microsecond' WHERE id=@id", ("id", identities[2].JobId.Value));
        var renewed = await control.RenewBatchAsync(identities);
        renewed.ThrowIfFailed();
        renewed.CommitAcknowledged.Should().BeTrue();
        renewed.Value.Should().HaveCount(2);
        foreach (var renewal in renewed.Value!)
        {
            renewal.OwnershipUntil.Should().BeAfter(renewal.DbNow);
            renewal.OwnershipUntil.Should().BeOnOrBefore(renewal.Identity.Deadline);
            var row = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, renewal.Identity.JobId))!;
            row.OwnershipUntil.Should().Be(renewal.OwnershipUntil);
            row.NormalAttempts.Should().Be(1);
        }
        var expired = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, identities[2].JobId))!;
        expired.OwnershipUntil.Should().BeBefore(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Renewal_remains_compatible_with_the_actual_parent_keyshare_held_by_an_uncommitted_child_INSERT()
    {
        await using var database = await DatabaseAsync();
        await using var host = Fixture.Host(database, new BackgroundJobFixtureProbe());
        await using var source = Source(database.OutboxConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        var claim = (await host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>().TryClaimAsync(job, 1))!;
        var admitted = await host.Services.GetRequiredService<JobExecutor>().ExecuteAsync(claim);
        var identity = BackgroundJobExecutionIdentity.FromReceipt(admitted.Receipt!);
        await using var business = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await business.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(business, transaction, SchemaFixture.TenantA, null);
        await SchemaQueries.ExecuteAsync(business, transaction, """
            INSERT INTO background_jobs (id, tenant_id, definition_key, profile, queue, parameters,
                original_due_at, traceparent, parent_job_id)
            VALUES (@child, @tenant, @definition, 'transactional', 'transactional', @parameters,
                clock_timestamp(), @trace, @parent)
            """, ("child", Guid.CreateVersion7()), ("tenant", SchemaFixture.TenantA),
            ("definition", BackgroundJobRuntimeFixture.TenantKey),
            ("parameters", "{\"ResourceId\":\"12345678-1234-7123-8123-123456789abc\"}"),
            ("trace", BackgroundJobRuntimeFixture.Traceparent), ("parent", job.Value));
        // The actual INSERT trigger holds FOR KEY SHARE on the parent. This
        // planted old locking shape must skip that otherwise healthy execution.
        await using (var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString))
        await using (var dirty = new NpgsqlCommand("SELECT id FROM background_jobs WHERE id=@id FOR UPDATE SKIP LOCKED", (NpgsqlConnection)dispatcher))
        {
            dirty.Parameters.AddWithValue("id", job.Value);
            (await dirty.ExecuteScalarAsync()).Should().BeNull("FOR UPDATE conflicts with the real child-parent KEY SHARE lock");
        }
        var renewed = await Control(source).RenewBatchAsync([identity]);
        renewed.ThrowIfFailed();
        renewed.Value.Should().ContainSingle(renewal => renewal.Identity.JobId == job,
            "the lease-only NO KEY UPDATE lock is compatible with a held child INSERT");
        renewed.Value!.Single().OwnershipUntil.Should().BeOnOrBefore(identity.Deadline);
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Admission_probe_waits_for_the_actual_original_row_lock_before_interpreting_commit_or_rollback(bool commit)
    {
        await using var database = await DatabaseAsync();
        await using var host = Fixture.Host(database, new BackgroundJobFixtureProbe());
        await using var source = Source(database.OutboxConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        var claim = (await host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>().TryClaimAsync(job, 1))!;
        await using var original = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await original.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(original, transaction, SchemaFixture.TenantA, null);
        var attemptId = Guid.CreateVersion7();
        await BackgroundJobSchemaSeed.AdmitAsync(original, transaction, job.Value, attemptId, claim.Token, SchemaFixture.TenantA, null);
        var probe = Control(source).ProbeCommitAsync(BackgroundJobCommitProbe.ForAdmission(claim));
        await Task.Delay(100);
        probe.IsCompleted.Should().BeFalse("the actual uncommitted start still holds the job row");
        if (commit) await transaction.CommitAsync(); else await transaction.RollbackAsync();
        var observed = await probe.WaitAsync(TimeSpan.FromSeconds(5));
        observed.ThrowIfFailed();
        observed.Value!.Disposition.Should().Be(commit ? BackgroundJobProbeDisposition.Admitted : BackgroundJobProbeDisposition.RolledBack);
        observed.Value.NormalAttempts.Should().Be(commit ? 1 : 0);
        observed.Value.CurrentAttemptId.Should().Be(commit ? attemptId : null,
            "the ledger must be read after the waiting job lock has observed the original transaction's outcome");
        observed.CommitAcknowledged.Should().BeFalse("a read-only lock probe rolls back; it issues no second uncertain COMMIT");
    }

    [Fact]
    public async Task Locked_probe_timeout_retains_uncertainty_and_does_not_claim_rollback()
    {
        await using var database = await DatabaseAsync();
        await using var host = Fixture.Host(database, new BackgroundJobFixtureProbe());
        await using var source = Source(database.OutboxConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        var claim = (await host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>().TryClaimAsync(job, 1))!;
        await using var original = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await using var transaction = await original.BeginTransactionAsync();
        await SchemaQueries.ExecuteAsync(original, transaction, "SELECT id FROM background_jobs WHERE id=@id FOR UPDATE", ("id", job.Value));
        var observed = await Control(source, options: new() { ProbeTimeout = TimeSpan.FromMilliseconds(100) })
            .ProbeCommitAsync(BackgroundJobCommitProbe.ForAdmission(claim));
        observed.Failed.Should().BeTrue();
        observed.Value.Should().BeNull();
        observed.KnownRollback.Should().BeFalse("the failed probe cannot label the original transaction rolled back");
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(0);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Real_retry_COMMIT_ack_loss_reconciles_the_locked_new_generation_without_reissuing_control()
    {
        await using var database = await DatabaseAsync();
        await using var host = Fixture.Host(database, new BackgroundJobFixtureProbe());
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        var claim = (await host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>().TryClaimAsync(job, 1))!;
        var start = await host.Services.GetRequiredService<JobExecutor>().ExecuteAsync(claim);
        await using var proxy = new CommitResponseDroppingProxy(database.OutboxConnectionString, commitsToPass: 0);
        await using var faultySource = Source(proxy.ConnectionString);
        var result = await Control(faultySource).FinishKnownRollbackAsync(BackgroundJobExecutionIdentity.FromReceipt(start.Receipt!),
            new(BackgroundJobFailureDisposition.Retry, "unexpected_fault", null));
        proxy.DroppedCommitResponses.Should().Be(1);
        result.CommitUncertain.Should().BeTrue();
        result.Value.Should().BeNull();
        result.Probes.Should().ContainSingle();
        await using var healthySource = Source(database.OutboxConnectionString);
        var resolved = await Control(healthySource).ProbeCommitAsync(result.Probes.Single());
        resolved.ThrowIfFailed();
        resolved.Value!.Disposition.Should().Be(BackgroundJobProbeDisposition.ControlCommitted);
        resolved.Value.WakeGeneration.Should().Be(2);
        resolved.Value.NormalAttempts.Should().Be(1);
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
    }

    [Fact]
    public async Task Runtime_control_retains_the_original_unknown_COMMIT_cursor_after_a_fresh_probe_fails_with_positive_cleanup()
    {
        await using var database = await DatabaseAsync();
        await using var host = Fixture.Host(database, new BackgroundJobFixtureProbe());
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        var routing = (await host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>().TryClaimAsync(job, 1))!;
        await DispatchAsync(database, "UPDATE background_jobs SET ownership_until=clock_timestamp()-interval '1 microsecond' WHERE id=@id", ("id", job.Value));
        await using var proxy = new CommitResponseDroppingProxy(database.OutboxConnectionString, rejectReconciliationConnections: true);
        await using var source = Source(proxy.ConnectionString);
        var bounds = new BackgroundJobRuntimeOptions
        {
            SqlTimeout = TimeSpan.FromMilliseconds(500),
            ProbeTimeout = TimeSpan.FromMilliseconds(200),
            LeaseDuration = TimeSpan.FromSeconds(15),
            RenewalInterval = TimeSpan.FromSeconds(1),
            ProgressTimeout = TimeSpan.FromSeconds(30),
            ControlPoll = TimeSpan.FromMilliseconds(50),
        };
        var gate = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var control = new BackgroundJobControl(source, Fixture.Registry,
            host.Services.GetRequiredService<JobExecutor>(), gate, bounds, new SystemGuidFactory(), TimeProvider.System);
        control.Start();
        try
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (proxy.DroppedCommitResponses == 0 || control.Snapshot.PendingOutcomes == 0
                || PendingProbeDue(control) <= TimeProvider.System.GetTimestamp() + TimeProvider.System.TimestampFrequency)
                await Task.Delay(25, wait.Token);
            await Task.Delay(300, wait.Token);
            control.Snapshot.PendingOutcomes.Should().Be(1,
                "failed acquisition of the fresh lock probe must retain the original unknown-COMMIT descriptor after concrete probe cleanup");
            control.Snapshot.UnknownOutcomes.Should().BeGreaterThan(0);
            control.Snapshot.RecoveryHealthy.Should().BeFalse();
            var row = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
            row.Phase.Should().Be("pending");
            row.OwnershipToken.Should().BeNull("the real server committed routing cleanup before its acknowledgement was dropped");
            row.NormalAttempts.Should().Be(0);
            // A second actual route can observe the settled database state, but
            // it cannot erase this runtime's unresolved original probe receipt.
            var second = await host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>().TryClaimAsync(job, routing.WakeGeneration);
            second.Should().NotBeNull();
            control.Snapshot.PendingOutcomes.Should().Be(1);
        }
        finally
        {
            gate.StopStarts();
            control.RequestStop();
            await control.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private async Task<DisposableSchemaDatabase> DatabaseAsync()
    {
        var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        return database;
    }

    private static PrivilegedDataSource Source(string connectionString) => new(() => OutboxDispatcherDataSource.Build(connectionString));
    private static PostgresBackgroundJobControlStore Control(PrivilegedDataSource source,
        BackgroundJobDefinitionRegistry? registry = null, BackgroundJobRuntimeOptions? options = null) =>
        new(source, registry ?? Fixture.Registry, options ?? new(), new SystemGuidFactory());

    private static async Task DispatchAsync(DisposableSchemaDatabase database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await SchemaQueries.ExecuteAsync(connection, null, sql, parameters);
    }

    // Observe the actual private cursor's paced retry, without installing a
    // callback or replacing either the store or the PostgreSQL probe boundary.
    private static long PendingProbeDue(BackgroundJobControl control)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var cursor = typeof(BackgroundJobControl).GetField("_pendingControl", flags)!.GetValue(control);
        return cursor is null ? 0 : (long)cursor.GetType().GetProperty("Due", flags)!.GetValue(cursor)!;
    }
}
