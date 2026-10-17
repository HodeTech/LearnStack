using System.Diagnostics;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobPositiveSdkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_external_recovery_wakes_advance_once_preserve_history_and_fence_old_callbacks(bool retainedUnarmed)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var migration = RecoveryWakeContext(database);
        const string RecoveryWakeMigration = "20261017111954_add_external_job_recovery_wakes";
        // Qualify this guard at its own schema boundary. Later migrations can
        // commit their own successful Down before this one correctly refuses.
        await migration.GetService<IMigrator>().MigrateAsync(RecoveryWakeMigration);
        if (retainedUnarmed)
            await migration.GetService<IMigrator>().MigrateAsync("20261017032122_add_external_job_failure_policy");
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        await using var host = ShortRecoveryOriginalHost(database, provider.Settings);
        var original = await AdmitPrepareAndArmAsync(database, host, Guid.CreateVersion7(),
            OrganizationId.From(Guid.CreateVersion7()), OrganizationId.From(Guid.CreateVersion7()));
        var job = original.Receipt.Envelope.JobId;
        original.Receipt.Run.Close();
        await WaitForDatabaseLeaseAsync(database, original.Receipt.Deadline);
        if (retainedUnarmed)
        {
            // Genuine retained state from the previous schema, not a disabled
            // current guard or an invented effect/recovery receipt.
            await using var legacy = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
            await SchemaQueries.ExecuteAsync(legacy, null, """
                UPDATE background_jobs SET phase='reconciling',ownership_token=NULL,ownership_kind=NULL,ownership_until=NULL
                WHERE id=@id AND ownership_until<=clock_timestamp()
                """, ("id", job.Value));
            await migration.GetService<IMigrator>().MigrateAsync(RecoveryWakeMigration);
        }
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var bounds = new BackgroundJobRuntimeOptions();
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, bounds, new SystemGuidFactory(), runtime);
        var generation = await GenerationAsync(database, job);
        (await store.AcquireExternalRecoveryAsync(job, generation, original.Receipt.Envelope.Queue, CancellationToken.None))
            .Value.Should().BeNull("neither an expired owner nor an unarmed legacy row is a current recovery wake");
        var lane = retainedUnarmed ? BackgroundJobControlLane.ExternalUnarmed : BackgroundJobControlLane.ExternalRecovery;
        (await store.RunRecoveryQuantumAsync(lane, [job.Value])).Value.Should().BeEmpty("retained native ownership excludes parking");
        var parked = await store.RunRecoveryQuantumAsync(lane, []);
        parked.ThrowIfFailed();
        parked.CommitAcknowledged.Should().BeTrue();
        parked.CleanupConfirmed.Should().BeTrue();
        parked.Value.Should().ContainSingle().Which.WakeGeneration.Should().Be(generation + 1);
        var current = await GenerationAsync(database, job);
        current.Should().Be(generation + 1);
        var inventory = await BackgroundJobSchemaInventory.ReadAsync(database.MigrationConnectionString);
        var downgrade = () => migration.GetService<IMigrator>().MigrateAsync("20261017032122_add_external_job_failure_policy");
        (await downgrade.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        (await BackgroundJobSchemaInventory.ReadAsync(database.MigrationConnectionString)).Should().Equal(inventory,
            "a refused downgrade rolls back its all-tenant inspection and FORCE RLS change");
        (await store.RunRecoveryQuantumAsync(BackgroundJobControlLane.ExternalRecovery, [])).Value.Should().BeEmpty();
        (await store.RunRecoveryQuantumAsync(BackgroundJobControlLane.ExternalUnarmed, [])).Value.Should().BeEmpty();
        (await GenerationAsync(database, job)).Should().Be(current);
        (await store.AcquireExternalRecoveryAsync(job, generation, original.Receipt.Envelope.Queue, CancellationToken.None))
            .Value.Should().BeNull("the original callback cannot acquire the new eligibility cycle");
        await using var sql = await source.OpenConnectionAsync();
        foreach (var change in new[] { "probe_after=NULL", "probe_after=clock_timestamp()+interval '1 hour'" })
        {
            var reset = () => SchemaQueries.ExecuteAsync(sql, null,
                "UPDATE background_jobs SET " + change + " WHERE id=@id", ("id", job.Value));
            (await reset.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        }
        var bridge = new PostgresBackgroundJobBridgeStore(source, Fixture.Registry, bounds, new SystemGuidFactory(), runtime);
        var hint = await bridge.TryClaimAsync([]);
        hint.Should().NotBeNull();
        hint!.WakeGeneration.Should().Be(current);
        hint.AvailableAfter.Should().BeOnOrBefore(hint.DatabaseNow);
        var acquired = await store.AcquireExternalRecoveryAsync(job, current, original.Receipt.Envelope.Queue, CancellationToken.None);
        acquired.ThrowIfFailed();
        acquired.Value.Should().NotBeNull();
        acquired.Value!.AdmissionToken.Should().Be(original.Receipt.Claim.Token);
        acquired.Value.AttemptId.Should().Be(original.Receipt.AttemptId);
        acquired.Value.Deadline.Should().Be(original.Receipt.Deadline);
        acquired.Value.WakeGeneration.Should().Be(current);
        (await bridge.AcknowledgeAsync(hint, "123")).Should().BeTrue("the actual callback acquired before bridge ACK");
        var clearLive = () => SchemaQueries.ExecuteAsync(sql, null, """
            UPDATE background_jobs SET ownership_token=NULL,ownership_kind=NULL,ownership_until=NULL,probe_after=clock_timestamp()
            WHERE id=@id
            """, ("id", job.Value));
        (await clearLive.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
        (await provider.CountsAsync(original.Operation)).Should().Be((0L, 0L, 0L));
        // Force only the fixture lease's expiry; no effect or cleanup fact is
        // added. Reacquisition must first publish a distinct parked generation.
        await SchemaQueries.ExecuteAsync(sql, null,
            "UPDATE background_jobs SET ownership_until=clock_timestamp() WHERE id=@id", ("id", job.Value));
        (await store.AcquireExternalRecoveryAsync(job, current, original.Receipt.Envelope.Queue, CancellationToken.None)).Value.Should().BeNull();
        var recovered = await store.RunRecoveryQuantumAsync(BackgroundJobControlLane.ExternalRecovery, []);
        recovered.ThrowIfFailed();
        recovered.Value.Should().ContainSingle().Which.WakeGeneration.Should().Be(current + 1);
        (await bridge.AcknowledgeAsync(hint, "124")).Should().BeFalse();
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_facade_parks_expired_external_work_reconciles_or_uses_retained_facts_and_finalizes(bool retainedOutcome)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        var prepared = OrganizationId.From(Guid.CreateVersion7());
        var finalized = OrganizationId.From(Guid.CreateVersion7());
        await using var originalHost = ShortRecoveryOriginalHost(database, provider.Settings);
        var original = await AdmitPrepareAndArmAsync(database, originalHost, Guid.CreateVersion7(), prepared, finalized);
        var executed = await original.Executor.ExecuteExternalAsync(original.Read, original.Arm, CancellationToken.None);
        executed.Status.Should().Be(JobExternalExecutionStatus.Reported);
        executed.CleanupFailed.Should().BeFalse();
        original.Receipt.SourceOwner.CleanupConfirmed.Should().BeTrue();
        if (retainedOutcome)
        {
            // Legal retained checkpoint fixture uses the actual provider result,
            // without forging a new report or clearing immutable selection.
            await PersistRetainedSdkOutcomeAsync(database, original.Receipt, executed.Observation!);
        }
        original.Receipt.Run.Close();
        await WaitForDatabaseLeaseAsync(database, original.Receipt.Deadline);
        var construction = new BackgroundJobPositiveProviderConstructionProbe();
        await using var host = Fixture.Host(database, provider.Settings, construction, configure: services =>
        {
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(new BackgroundJobRuntimeOptions { HandoffRepairGrace = TimeSpan.FromSeconds(15) });
        }, applicationPoolSize: 24);
        var bounds = host.Services.GetRequiredService<BackgroundJobRuntimeOptions>();
        var dispatcherConnection = new NpgsqlConnectionStringBuilder(database.OutboxConnectionString)
        { Pooling = true, MaxPoolSize = 50 }.ConnectionString;
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(dispatcherConnection));
        using var globals = new NativeSdkGlobalFilterSnapshot();
        var gate = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var ids = new SystemGuidFactory();
        var runtime = new BackgroundJobRuntime(source, Fixture.Registry, BackgroundJobRecurrenceRegistry.CreateEmpty(),
            host.Services.GetRequiredService<JobExecutor>(), new(source, Fixture.Registry, bounds, ids, gate),
            gate, bounds, ids, TimeProvider.System, new(true, 50), new HangfireBoundedLogProvider(),
            host.Services.GetRequiredService<ApplicationDataSourceLifetime>());
        runtime.BindMessagingLifetime(Task.CompletedTask, Task.CompletedTask);
        try
        {
            using var start = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await runtime.StartAsync(start.Token);
            runtime.Snapshot.ApplicationCapacity.Should().Be(new BackgroundJobApplicationCapacity(11, 24));
            host.Services.GetRequiredService<ApplicationDataSourceLifetime>().PoolSettings
                .Should().Be(new BackgroundJobApplicationPoolSettings(true, 24));
            await using (var physical = await source.OpenConnectionAsync(start.Token))
            {
                var settings = new NpgsqlConnectionStringBuilder(physical.ConnectionString);
                settings.Pooling.Should().BeTrue();
                settings.MaxPoolSize.Should().Be(50);
            }
            var waiting = Stopwatch.StartNew();
            var completionBudget = bounds.HandoffRepairGrace + bounds.TransactionTimeout + bounds.SqlTimeout * 2 + bounds.ControlPoll * 2;
            while (waiting.Elapsed < completionBudget)
            {
                if ((await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, original.Receipt.Envelope.JobId))!.Phase == "completed") break;
                await Task.Delay(20);
            }
            var row = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, original.Receipt.Envelope.JobId))!;
            if (row.Phase != "completed")
            {
                string hint;
                try
                {
                    using var diagnostic = new CancellationTokenSource(bounds.SqlTimeout);
                    hint = (await ReadEarlyFinalizeHintAsync(source, original.Receipt.Envelope.JobId, diagnostic.Token))?.ToString() ?? "none";
                }
                catch (Exception failure) when (failure.GetType() != typeof(OutOfMemoryException)
                    && failure.GetType() != typeof(StackOverflowException))
                {
                    hint = $"unavailable ({failure.GetType().Name})";
                }
                row.Phase.Should().Be("completed", $"actual repair hint={hint}; runtime={runtime.Snapshot}");
            }
            row.NormalAttempts.Should().Be(1);
            construction.Calls.Should().Be(retainedOutcome ? 0 : 1,
                "stored completed facts select finalization without another provider scope");
            (await provider.CountsAsync(original.Operation)).Should().Be((1L, retainedOutcome ? 0L : 1L, 1L));
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, original.Receipt.Envelope.JobId)).Should().Be(2);
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, prepared.Value)).Should().Be(1);
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, finalized.Value)).Should().Be(1);
        }
        finally
        {
            runtime.RequestStop();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await runtime.StopAsync(cleanup.Token);
            await runtime.Completion.WaitAsync(cleanup.Token);
        }
        runtime.Snapshot.CleanupConfirmed.Should().BeTrue();
        runtime.Snapshot.Control!.Executions.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_external_routing_is_repaired_in_both_pending_stages_without_spending_admission(bool finalize)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        await using var host = Fixture.Host(database, provider.Settings);
        BackgroundJobId job;
        if (finalize)
        {
            var original = await AdmitPrepareAndArmAsync(database, host, Guid.CreateVersion7(),
                OrganizationId.From(Guid.CreateVersion7()), OrganizationId.From(Guid.CreateVersion7()));
            var executed = await original.Executor.ExecuteExternalAsync(original.Read, original.Arm, CancellationToken.None);
            var outcome = await original.Executor.RecordExternalOutcomeAsync(original.Receipt, original.Read,
                original.Receipt.Run.OpenBoundary(LearnStack.Application.Jobs.BackgroundJobCheckpointKind.Outcome),
                executed.Observation!, CancellationToken.None);
            outcome.CommitAcknowledged.Should().BeTrue();
            original.Receipt.Run.Close();
            job = original.Receipt.Envelope.JobId;
        }
        else
        {
            var submitted = await Fixture.SubmitAsync(host.Services, Fixture.Parameters(Guid.CreateVersion7(),
                OrganizationId.From(Guid.CreateVersion7()), OrganizationId.From(Guid.CreateVersion7())));
            submitted.IsSuccess.Should().BeTrue();
            job = submitted.Value;
        }
        var generation = await GenerationAsync(database, job);
        var route = await host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>().TryClaimAsync(job, generation);
        route.Should().NotBeNull();
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        await using var sql = await source.OpenConnectionAsync();
        await SchemaQueries.ExecuteAsync(sql, null, "UPDATE background_jobs SET ownership_until=clock_timestamp() WHERE id=@id", ("id", job.Value));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var repaired = await store.RunRecoveryQuantumAsync(BackgroundJobControlLane.Recovery, []);
        repaired.ThrowIfFailed();
        repaired.Value.Should().ContainSingle().Which.WakeGeneration.Should().Be(generation);
        var bridge = new PostgresBackgroundJobBridgeStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var hint = await bridge.TryClaimAsync([]);
        hint.Should().NotBeNull();
        hint!.WakeGeneration.Should().Be(generation);
        var row = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
        row.Phase.Should().Be(finalize ? "finalize_pending" : "pending");
        row.NormalAttempts.Should().Be(finalize ? 1 : 0);
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(finalize ? 1 : 0);
    }

    private static PlatformDbContext RecoveryWakeContext(DisposableSchemaDatabase database) => new(
        new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(database.MigrationConnectionString,
            options => options.MigrationsHistoryTable(PlatformDbContextFactory.HistoryTable)).Options);
}
