using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.Server;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class HangfireGenerationTests
{
    [Fact]
    public async Task Stop_before_start_owns_positive_no_Core_cleanup_and_forbids_late_start()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var host = Host(database);
        await using var source = Source(database);
        using var globals = new GlobalFilterSnapshot();
        var topology = host.Services.GetRequiredService<BackgroundJobDefinitionRegistry>().Infrastructure;
        var gate = new BackgroundJobRuntimeGate(topology);
        var provider = HangfireStorageBootstrap.Create(source, topology, new HangfireBoundedLogProvider(), gate);
        var generation = Generation(host.Services, provider, new(provider), HangfireRuntimeFilters.Create(),
            topology.Pools["transactional"], gate);
        using var callerBudget = new CancellationTokenSource(TestWait);

        await generation.StopAsync(callerBudget.Token);
        (await generation.Ready).Should().BeFalse();
        AssertNoCoreCleanup(generation, HangfireGenerationReason.HostStop);
        gate.IsOpen.Should().BeTrue();
        await AssertServersAsync(source);
        var lateStart = () => generation.Start();
        lateStart.Should().Throw<InvalidOperationException>().WithMessage("A generation starts once.");
        await generation.StopAsync(callerBudget.Token);
        AssertNoCoreCleanup(generation, HangfireGenerationReason.HostStop);
        await AssertServersAsync(source);
    }

    [Fact]
    public async Task Real_announcement_SQL_permission_refusal_is_nonfatal_and_joins_its_outer_owner()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var host = Host(database);
        await using var source = Source(database);
        using var globals = new GlobalFilterSnapshot();
        var topology = host.Services.GetRequiredService<BackgroundJobDefinitionRegistry>().Infrastructure;
        var gate = new BackgroundJobRuntimeGate(topology);
        var provider = HangfireStorageBootstrap.Create(source, topology, new HangfireBoundedLogProvider(), gate);
        var generation = Generation(host.Services, provider, new(provider), HangfireRuntimeFilters.Create(),
            topology.Pools["transactional"], gate);
        await OwnerSqlAsync(database, "REVOKE INSERT (id,data,lastheartbeat) ON public.server FROM learnstack_outbox_admin");
        try
        {
            await using (var sql = await source.OpenConnectionAsync())
            await using (var privilege = new NpgsqlCommand("SELECT has_column_privilege(current_user,'public.server','id','INSERT')", sql))
                (await privilege.ExecuteScalarAsync()).Should().Be(false);
            generation.Start();
            (await generation.Ready.WaitAsync(TestWait)).Should().BeFalse();
            await generation.Completion.WaitAsync(TestWait);

            var snapshot = generation.Snapshot;
            snapshot.HandlePublished.Should().BeTrue("Core exists even though its first announcement was refused");
            snapshot.Captured.Should().BeFalse();
            snapshot.WaitEntered.Should().BeFalse();
            snapshot.NormalChildrenJoined.Should().BeFalse();
            snapshot.OuterJoined.Should().BeTrue();
            snapshot.CleanupComplete.Should().BeTrue();
            snapshot.Draining.Should().BeTrue();
            snapshot.Reason.Should().Be(HangfireGenerationReason.Announcement);
            gate.State.Should().Be(BackgroundJobProcessState.Running);
            await AssertServersAsync(source);
        }
        finally
        {
            await OwnerSqlAsync(database, "GRANT INSERT (id,data,lastheartbeat) ON public.server TO learnstack_outbox_admin");
            using var callerBudget = new CancellationTokenSource(TestWait);
            await generation.StopAsync(callerBudget.Token);
        }
        // Restoring the grant cannot revive the already joined outer loop.
        await AssertServersAsync(source);
        generation.Completion.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task Synthetic_startup_timer_failure_drains_before_any_Core_construction()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var host = Host(database);
        await using var source = Source(database);
        using var globals = new GlobalFilterSnapshot();
        var topology = host.Services.GetRequiredService<BackgroundJobDefinitionRegistry>().Infrastructure;
        var gate = new BackgroundJobRuntimeGate(topology);
        var provider = HangfireStorageBootstrap.Create(source, topology, new HangfireBoundedLogProvider(), gate);
        var generation = Generation(host.Services, provider, new(provider), HangfireRuntimeFilters.Create(),
            topology.Pools["transactional"], gate, new RefusingStartupTimer());
        try
        {
            var start = () => generation.Start();
            start.Should().NotThrow();
            (await generation.Ready.WaitAsync(TestWait)).Should().BeFalse();
            await generation.Completion.WaitAsync(TestWait);
            AssertNoCoreCleanup(generation, HangfireGenerationReason.StartupStalled);
            generation.TryAdmitInvocation().Should().BeFalse();
            gate.IsOpen.Should().BeTrue();
            await AssertServersAsync(source);
        }
        finally
        {
            using var callerBudget = new CancellationTokenSource(TestWait);
            await generation.StopAsync(callerBudget.Token);
        }
    }

    [Fact]
    public async Task Manual_owner_heartbeat_contains_a_synthetic_clock_fault_until_actual_child_join()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var host = Host(database);
        await using var source = Source(database);
        using var globals = new GlobalFilterSnapshot();
        var topology = host.Services.GetRequiredService<BackgroundJobDefinitionRegistry>().Infrastructure;
        var gate = new BackgroundJobRuntimeGate(topology);
        var provider = HangfireStorageBootstrap.Create(source, topology, new HangfireBoundedLogProvider(), gate);
        var inventory = new HangfireComponentInventory(provider);
        var clock = new ArmedClockFailure();
        var generation = Generation(host.Services, provider, inventory, HangfireRuntimeFilters.Create(),
            topology.Pools["transactional"], gate, clock);
        using var callbackConnection = new PoolHangfireStorage(provider, generation, inventory.Selected).GetConnection();
        Task? observedCallback = null;
        generation.Start();
        try
        {
            (await generation.Ready.WaitAsync(TestWait)).Should().BeTrue();
            var server = await ServerIdAsync(source, "transactional");
            clock.Arm();
            // Invoke the exact concrete callback boundary now. This is not a
            // qualification of Core's independent thirty-second scheduling.
            var callback = Task.Run(() => callbackConnection.Heartbeat(server));
            observedCallback = ObserveHeartbeatRefusalAsync(callback, generation);
            await observedCallback.WaitAsync(TestWait);
            await generation.Completion.WaitAsync(TestWait);
            AssertFaultJoined(generation, gate, HangfireGenerationReason.HeartbeatConfiguration);
            await AssertServersAsync(source);
        }
        finally
        {
            using var callerBudget = new CancellationTokenSource(TestWait);
            await Task.WhenAll(generation.StopAsync(callerBudget.Token),
                (observedCallback ?? Task.CompletedTask).WaitAsync(callerBudget.Token));
        }
    }

    [Fact]
    public async Task A_stopped_closed_invocation_refuses_before_routing_an_actually_eligible_job()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var fixture = BackgroundJobRuntimeFixture.Shared;
        var positive = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        var late = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        await using var host = fixture.Host(database, new BackgroundJobFixtureProbe());
        await using var source = Source(database);
        using var globals = new GlobalFilterSnapshot();
        var topology = fixture.Registry.Infrastructure;
        var gate = new BackgroundJobRuntimeGate(topology);
        var provider = HangfireStorageBootstrap.Create(source, topology, new HangfireBoundedLogProvider(), gate);
        var pool = topology.Pools["transactional"];
        var generation = Generation(host.Services, provider, new(provider), HangfireRuntimeFilters.Create(), pool, gate);
        var activator = HangfireInvocationContract.BindGeneration(HangfireInvocationContract.CreateActivator(pool, topology,
            host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>(), host.Services.GetRequiredService<JobExecutor>(),
            BackgroundJobRecurrenceRegistry.CreateEmpty(), gate), generation);
        using var contextConnection = provider.GetConnection();
        generation.Start();
        try
        {
            (await generation.Ready.WaitAsync(TestWait)).Should().BeTrue();
            var lateContext = ActivationContext(contextConnection, pool, late);
            // These are test-owned scopes created through the actual closed
            // activator, not a fabricated or held Core dispatcher.
            using var lateScope = activator.BeginScope(lateContext);
            var retained = (HangfireJobWakeup)lateScope.Resolve(typeof(HangfireJobWakeup));
            using (var positiveScope = activator.BeginScope(ActivationContext(contextConnection, pool, positive)))
                await ((HangfireJobWakeup)positiveScope.Resolve(typeof(HangfireJobWakeup))).RunAsync(positive, 1);
            var admitted = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, positive))!;
            admitted.Phase.Should().Be("executing");
            admitted.NormalAttempts.Should().Be(1);
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, positive)).Should().Be(1);

            generation.RequestDrain(HangfireGenerationReason.HeartbeatTransient);
            await generation.Completion.WaitAsync(TestWait);
            await retained.RunAsync(late, 1);
            await AssertPendingJobAsync(database, late, expectsRouting: false);
            var newScope = () => activator.BeginScope(lateContext);
            newScope.Should().Throw<InvalidOperationException>().WithMessage("The Hangfire invocation contract refused activation.");
            gate.IsOpen.Should().BeTrue();
            AssertJoined(generation);
        }
        finally
        {
            using var callerBudget = new CancellationTokenSource(TestWait);
            await generation.StopAsync(callerBudget.Token);
        }
    }

    [Fact]
    public async Task Actual_worker_routing_that_resumes_after_local_drain_cannot_admit_a_business_attempt()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        var fixture = BackgroundJobRuntimeFixture.Shared;
        await using var host = fixture.Host(database, new BackgroundJobFixtureProbe());
        await using var source = Source(database);
        using var globals = new GlobalFilterSnapshot();
        var topology = fixture.Registry.Infrastructure;
        var filters = HangfireRuntimeFilters.Create();
        var gate = new BackgroundJobRuntimeGate(topology);
        var provider = HangfireStorageBootstrap.Create(source, topology, new HangfireBoundedLogProvider(), gate);
        var pool = topology.Pools["transactional"];
        var generation = Generation(host.Services, provider, new(provider), filters, pool, gate);
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var held = await owner.BeginTransactionAsync();
        var released = false;
        generation.Start();
        try
        {
            (await generation.Ready.WaitAsync(TestWait)).Should().BeTrue();
            await using (var acquire = new NpgsqlCommand("LOCK TABLE public.background_jobs IN ACCESS EXCLUSIVE MODE", owner, held))
                await acquire.ExecuteNonQueryAsync();
            var vendor = CreateWakeup(provider, filters, pool, job);
            await EventuallyAsync(() => HasBlockedRoutingAsync(source));
            (await StateAsync(source, vendor)).Should().Be("Processing");
            generation.RequestDrain(HangfireGenerationReason.HeartbeatTransient);
            generation.Snapshot.Draining.Should().BeTrue();
            generation.Completion.IsCompleted.Should().BeFalse("actual worker SQL is still held");
            await held.RollbackAsync();
            released = true;
            await generation.Completion.WaitAsync(TestWait);

            // A committed routing token and native operational success prove
            // this reached the second gate, rather than merely timing SQL out.
            (await StateAsync(source, vendor)).Should().Be("Succeeded");
            await AssertPendingJobAsync(database, job, expectsRouting: true);
            AssertJoined(generation);
            gate.IsOpen.Should().BeTrue();
        }
        finally
        {
            if (!released) await held.RollbackAsync();
            using var callerBudget = new CancellationTokenSource(TestWait);
            await generation.StopAsync(callerBudget.Token);
        }
    }

    [Fact]
    public async Task Real_missing_metadata_SQL_via_manual_owner_heartbeat_preserves_a_healthy_pool()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var host = Host(database);
        await using var source = Source(database);
        using var globals = new GlobalFilterSnapshot();
        var topology = host.Services.GetRequiredService<BackgroundJobDefinitionRegistry>().Infrastructure;
        var filters = HangfireRuntimeFilters.Create();
        var gate = new BackgroundJobRuntimeGate(topology);
        var provider = HangfireStorageBootstrap.Create(source, topology, new HangfireBoundedLogProvider(), gate);
        var inventory = new HangfireComponentInventory(provider);
        var affected = Generation(host.Services, provider, inventory, filters, topology.Pools["transactional"], gate);
        var healthy = Generation(host.Services, provider, inventory, filters, topology.Pools["external"], gate);
        using var callbackConnection = new PoolHangfireStorage(provider, affected, inventory.Selected).GetConnection();
        Task? observedCallback = null;
        affected.Start();
        healthy.Start();
        try
        {
            (await Task.WhenAll(affected.Ready, healthy.Ready).WaitAsync(TestWait)).Should().OnlyContain(ready => ready);
            var server = await ServerIdAsync(source, "transactional");
            // Delete through the actual guarded shared provider. Calling the
            // live generation's removal boundary here would mean unsafe unwind.
            using (var remove = provider.GetConnection()) remove.RemoveServer(server);
            await AssertServersAsync(source, topology.Pools["external"]);
            var callback = Task.Run(() => callbackConnection.Heartbeat(server));
            observedCallback = ObserveHeartbeatRefusalAsync(callback, affected);
            await observedCallback.WaitAsync(TestWait);
            await affected.Completion.WaitAsync(TestWait);
            AssertFaultJoined(affected, gate, HangfireGenerationReason.HeartbeatMissing);
            AssertReady(healthy);
            var vendor = CreateOrphanWakeup(provider, filters, topology.Pools["external"]);
            await EventuallyAsync(async () => await StateAsync(source, vendor) == "Succeeded");
            await AssertNoBusinessAdmissionAsync(source);
            await AssertServersAsync(source, topology.Pools["external"]);
        }
        finally
        {
            using var callerBudget = new CancellationTokenSource(TestWait);
            gate.StopStarts();
            affected.RequestDrain(HangfireGenerationReason.HostStop);
            healthy.RequestDrain(HangfireGenerationReason.HostStop);
            await Task.WhenAll(affected.StopAsync(callerBudget.Token), healthy.StopAsync(callerBudget.Token),
                (observedCallback ?? Task.CompletedTask).WaitAsync(callerBudget.Token));
        }
        AssertJoined(healthy);
        await AssertServersAsync(source);
    }

    private static void AssertNoCoreCleanup(HangfireGeneration generation, HangfireGenerationReason reason)
    {
        var snapshot = generation.Snapshot;
        snapshot.HandlePublished.Should().BeFalse();
        snapshot.Captured.Should().BeFalse();
        snapshot.WaitEntered.Should().BeFalse();
        snapshot.NormalChildrenJoined.Should().BeFalse();
        snapshot.OuterJoined.Should().BeFalse();
        snapshot.CleanupComplete.Should().BeTrue();
        snapshot.Draining.Should().BeTrue();
        snapshot.Reason.Should().Be(reason);
        generation.Completion.IsCompletedSuccessfully.Should().BeTrue();
    }

    private static void AssertFaultJoined(HangfireGeneration generation, BackgroundJobRuntimeGate gate,
        HangfireGenerationReason reason)
    {
        var snapshot = generation.Snapshot;
        snapshot.NormalChildrenJoined.Should().BeTrue();
        snapshot.OuterJoined.Should().BeTrue();
        snapshot.CleanupComplete.Should().BeTrue();
        snapshot.Draining.Should().BeTrue();
        snapshot.HeartbeatFault.Should().BeTrue();
        snapshot.Reason.Should().Be(reason);
        generation.Completion.IsCompletedSuccessfully.Should().BeTrue();
        gate.State.Should().Be(BackgroundJobProcessState.Running);
    }

    private static async Task ObserveHeartbeatRefusalAsync(Task callback, HangfireGeneration generation)
    {
        var invoke = () => callback;
        (await invoke.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should()
            .Be("The owned Hangfire lifecycle refused continuation.");
        generation.Snapshot.NormalChildrenJoined.Should().BeTrue("the failure cannot return before actual child join");
    }

    private static PerformContext ActivationContext(Hangfire.Storage.IStorageConnection connection,
        BackgroundJobWorkerPoolRegistration pool, BackgroundJobId id) =>
        new(null, connection, new BackgroundJob("1", new Job(typeof(HangfireJobWakeup),
            HangfireInvocationContract.JobMethod, [id, 1L], pool.Queues[0]), DateTime.UtcNow), new JobCancellationToken(canceled: false));

    private static async Task AssertPendingJobAsync(DisposableSchemaDatabase database, BackgroundJobId id, bool expectsRouting)
    {
        var row = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, id))!;
        row.Phase.Should().Be("pending");
        row.NormalAttempts.Should().Be(0);
        row.CurrentAttempt.Should().BeNull();
        if (expectsRouting)
        {
            row.OwnershipToken.Should().NotBeNull();
            row.OwnershipKind.Should().Be("routing");
        }
        else
        {
            row.OwnershipToken.Should().BeNull();
            row.OwnershipKind.Should().BeNull();
        }
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, id)).Should().Be(0);
    }

    private static async Task<bool> HasBlockedRoutingAsync(PrivilegedDataSource source)
    {
        await using var sql = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (SELECT FROM pg_locks blocked JOIN pg_stat_activity activity ON activity.pid=blocked.pid
                WHERE blocked.database=(SELECT oid FROM pg_database WHERE datname=current_database())
                  AND blocked.relation='public.background_jobs'::regclass AND NOT blocked.granted
                  AND activity.usename='learnstack_outbox_admin' AND activity.pid<>pg_backend_pid())
            """, sql);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> ServerIdAsync(PrivilegedDataSource source, string pool)
    {
        await using var sql = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT id FROM public.server WHERE id LIKE @prefix", sql);
        command.Parameters.AddWithValue("prefix", "learnstack-" + pool + ":%");
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task OwnerSqlAsync(DisposableSchemaDatabase database, string sql)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var command = new NpgsqlCommand(sql, owner);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class RefusingStartupTimer : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            throw new InvalidOperationException("Synthetic startup observation timer failure");
    }

    private sealed class ArmedClockFailure : TimeProvider
    {
        private int _armed;
        internal void Arm() => Volatile.Write(ref _armed, 1);
        public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;
        public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow();
        public override long GetTimestamp() => Volatile.Read(ref _armed) != 0
            ? throw new InvalidOperationException("Synthetic owner heartbeat clock failure") : TimeProvider.System.GetTimestamp();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            TimeProvider.System.CreateTimer(callback, state, dueTime, period);
    }
}
