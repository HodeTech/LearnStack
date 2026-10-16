using System.Diagnostics;
using System.Globalization;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.PostgreSql;
using Hangfire.States;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Jobs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Ordinary real PostgreSQL generation lifecycle; fatal cases require a separately qualified Linux child.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed partial class HangfireGenerationTests(SchemaFixture schema)
{
    private static readonly TimeSpan TestWait = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_real_generation_becomes_ready_runs_only_closed_wakeups_and_joins_after_local_drain()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var host = Host(database);
        await using var source = Source(database);
        using var globals = new GlobalFilterSnapshot();
        var definitions = host.Services.GetRequiredService<BackgroundJobDefinitionRegistry>();
        var topology = definitions.Infrastructure;
        var gate = new BackgroundJobRuntimeGate(topology);
        var filters = HangfireRuntimeFilters.Create();
        var provider = HangfireStorageBootstrap.Create(source, topology, new HangfireBoundedLogProvider(), gate);
        var inventory = new HangfireComponentInventory(provider);
        var pool = topology.Pools["transactional"];
        var generation = Generation(host.Services, provider, inventory, filters, pool, gate);
        generation.Start();
        try
        {
            (await generation.Ready.WaitAsync(TestWait)).Should().BeTrue();
            AssertReady(generation);
            await AssertServersAsync(source, pool);

            // This opaque identity has no authoritative business row. Successful
            // native transport execution must not mint a business admission.
            var vendor = CreateOrphanWakeup(provider, filters, pool);
            await EventuallyAsync(async () => await StateAsync(source, vendor) == "Succeeded");
            await AssertNoBusinessAdmissionAsync(source);

            // A direct local-drain request exercises ownership, rather than
            // pretending to reproduce a metadata-heartbeat storage failure.
            generation.RequestDrain(HangfireGenerationReason.HeartbeatTransient);
            generation.Snapshot.Draining.Should().BeTrue();
            gate.IsOpen.Should().BeTrue();
            await generation.Completion.WaitAsync(TestWait);

            AssertJoined(generation);
            generation.Snapshot.Reason.Should().Be(HangfireGenerationReason.HeartbeatTransient);
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
    public async Task A_closed_process_gate_refuses_before_any_core_server_is_announced()
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
        gate.StopStarts();
        generation.Start();
        try
        {
            (await generation.Ready.WaitAsync(TestWait)).Should().BeFalse();
            await generation.Completion.WaitAsync(TestWait);
            generation.Snapshot.HandlePublished.Should().BeFalse();
            generation.Snapshot.Captured.Should().BeFalse();
            generation.Snapshot.WaitEntered.Should().BeFalse();
            generation.Snapshot.NormalChildrenJoined.Should().BeFalse();
            generation.Snapshot.OuterJoined.Should().BeFalse();
            generation.Snapshot.CleanupComplete.Should().BeTrue();
            generation.Snapshot.Draining.Should().BeTrue();
            generation.Snapshot.HeartbeatFault.Should().BeFalse();
            gate.State.Should().Be(BackgroundJobProcessState.Stopping);
            await AssertServersAsync(source);
            await AssertNoBusinessAdmissionAsync(source);
        }
        finally
        {
            using var callerBudget = new CancellationTokenSource(TestWait);
            await generation.StopAsync(callerBudget.Token);
        }
    }

    [Fact]
    public async Task Local_control_drain_preserves_other_real_pools_and_the_independent_shared_heartbeat()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var host = Host(database);
        await using var source = Source(database);
        using var globals = new GlobalFilterSnapshot();
        var topology = host.Services.GetRequiredService<BackgroundJobDefinitionRegistry>().Infrastructure;
        var gate = new BackgroundJobRuntimeGate(topology);
        var filters = HangfireRuntimeFilters.Create();
        var provider = HangfireStorageBootstrap.Create(source, topology, new HangfireBoundedLogProvider(), gate);
        var inventory = new HangfireComponentInventory(provider);
        var shared = new HangfireSharedComponents(inventory, gate, TimeProvider.System);
        var generations = topology.Pools.Values.ToDictionary(pool => pool.Name,
            pool => Generation(host.Services, provider, inventory, filters, pool, gate), StringComparer.Ordinal);
        shared.Start();
        foreach (var generation in generations.Values) generation.Start();
        try
        {
            var ready = await Task.WhenAll(generations.Values.Select(generation => generation.Ready)).WaitAsync(TestWait);
            ready.Should().OnlyContain(value => value);
            foreach (var generation in generations.Values) AssertReady(generation);
            await AssertServersAsync(source, topology.Pools.Values.ToArray());
            await EventuallyAsync(() => Task.FromResult(shared.Expiration.IsHealthy
                && shared.FetchedJobHeartbeat.IsHealthy));

            var control = generations["control"];
            control.RequestDrain(HangfireGenerationReason.HeartbeatTransient);
            await control.Completion.WaitAsync(TestWait);
            AssertJoined(control);
            control.Snapshot.Reason.Should().Be(HangfireGenerationReason.HeartbeatTransient);
            gate.IsOpen.Should().BeTrue();
            var healthyPools = topology.Pools.Values.Where(pool => pool.Kind != BackgroundJobWorkerPoolKind.Control).ToArray();
            await AssertServersAsync(source, healthyPools);

            // Actual closed wakeups prove both remaining worker pools still
            // service their disjoint queues after control cleanup has joined.
            foreach (var pool in healthyPools)
            {
                AssertReady(generations[pool.Name]);
                var vendor = CreateOrphanWakeup(provider, filters, pool);
                await EventuallyAsync(async () => await StateAsync(source, vendor) == "Succeeded");
            }
            await AssertNoBusinessAdmissionAsync(source);
            var heartbeatPasses = shared.FetchedJobHeartbeat.Invocations;
            await EventuallyAsync(() => Task.FromResult(shared.FetchedJobHeartbeat.Invocations > heartbeatPasses));
            shared.FetchedJobHeartbeat.IsHealthy.Should().BeTrue();
            shared.Expiration.IsHealthy.Should().BeTrue();
            shared.Completion.IsCompleted.Should().BeFalse();
            foreach (var pool in healthyPools) generations[pool.Name].Completion.IsCompleted.Should().BeFalse();
        }
        finally
        {
            // One caller deadline covers every generation and both shared
            // components; no per-pool deadline starts inside this loop.
            using var callerBudget = new CancellationTokenSource(TestWait);
            gate.StopStarts();
            foreach (var generation in generations.Values) generation.RequestDrain(HangfireGenerationReason.HostStop);
            shared.RequestStop();
            await Task.WhenAll(generations.Values.Select(generation => generation.StopAsync(callerBudget.Token))
                .Append(shared.StopAsync(callerBudget.Token)));
        }

        foreach (var generation in generations.Values) AssertJoined(generation);
        shared.Expiration.ActualJoined.Should().BeTrue();
        shared.Expiration.OwnsResources.Should().BeFalse();
        shared.FetchedJobHeartbeat.ActualJoined.Should().BeTrue();
        shared.FetchedJobHeartbeat.OwnsResources.Should().BeFalse();
        await AssertServersAsync(source);
    }

    private static HangfireGeneration Generation(IServiceProvider services, PostgreSqlStorage provider,
        HangfireComponentInventory inventory, HangfireRuntimeFilters filters,
        BackgroundJobWorkerPoolRegistration pool, BackgroundJobRuntimeGate gate, TimeProvider? timeProvider = null)
    {
        var topology = services.GetRequiredService<BackgroundJobDefinitionRegistry>().Infrastructure;
        var activator = HangfireInvocationContract.CreateActivator(pool, topology,
            services.GetRequiredService<PostgresBackgroundJobRoutingStore>(), services.GetRequiredService<JobExecutor>(),
            BackgroundJobRecurrenceRegistry.CreateEmpty());
        return new(provider, inventory, filters, activator, pool, gate,
            services.GetRequiredService<BackgroundJobRuntimeOptions>(), timeProvider ?? TimeProvider.System);
    }

    private static WebApplicationFactory<Program> Host(DisposableSchemaDatabase database) =>
        new ConstructionOnlyApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("ConnectionStrings:Default", database.AppConnectionString);
            builder.UseSetting("ConnectionStrings:OutboxDispatcher", database.OutboxConnectionString);
        });

    private static PrivilegedDataSource Source(DisposableSchemaDatabase database) =>
        new(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));

    private static string CreateOrphanWakeup(PostgreSqlStorage provider, HangfireRuntimeFilters filters,
        BackgroundJobWorkerPoolRegistration pool) => CreateWakeup(provider, filters, pool,
            BackgroundJobId.From(Guid.CreateVersion7()));

    private static string CreateWakeup(PostgreSqlStorage provider, HangfireRuntimeFilters filters,
        BackgroundJobWorkerPoolRegistration pool, BackgroundJobId id)
    {
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            var job = new Job(typeof(HangfireJobWakeup), HangfireInvocationContract.JobMethod,
                [id, 1L], pool.Queues[0]);
            var client = new BackgroundJobClient(provider, filters) { RetryAttempts = 0 };
            return client.Create(job, new EnqueuedState(pool.Queues[0]))
                ?? throw new InvalidOperationException("The stock client did not create the closed wakeup.");
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }

    private static void AssertReady(HangfireGeneration generation)
    {
        var snapshot = generation.Snapshot;
        snapshot.HandlePublished.Should().BeTrue();
        snapshot.Captured.Should().BeTrue();
        snapshot.WaitEntered.Should().BeTrue();
        snapshot.NormalChildrenJoined.Should().BeFalse();
        snapshot.OuterJoined.Should().BeFalse();
        snapshot.CleanupComplete.Should().BeFalse();
        snapshot.Draining.Should().BeFalse();
        snapshot.HeartbeatFault.Should().BeFalse();
        generation.Completion.IsCompleted.Should().BeFalse();
    }

    private static void AssertJoined(HangfireGeneration generation)
    {
        var snapshot = generation.Snapshot;
        snapshot.NormalChildrenJoined.Should().BeTrue();
        snapshot.OuterJoined.Should().BeTrue();
        snapshot.CleanupComplete.Should().BeTrue();
        snapshot.Draining.Should().BeTrue();
        snapshot.HeartbeatFault.Should().BeFalse();
        generation.Completion.IsCompletedSuccessfully.Should().BeTrue();
    }

    private static async Task AssertServersAsync(PrivilegedDataSource source,
        params BackgroundJobWorkerPoolRegistration[] expected)
    {
        await using var sql = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT id,data::text,current_user FROM public.server ORDER BY id", sql);
        await using var rows = await command.ExecuteReaderAsync();
        var observed = new HashSet<string>(StringComparer.Ordinal);
        while (await rows.ReadAsync())
        {
            rows.GetString(2).Should().Be(OutboxDispatcherDataSource.Role);
            var id = rows.GetString(0);
            var pool = expected.Should().ContainSingle(candidate => id.StartsWith("learnstack-" + candidate.Name + ":",
                StringComparison.Ordinal)).Which;
            observed.Add(pool.Name).Should().BeTrue("one actual generation is announced per pool");
            var fields = id.Split(':');
            fields.Should().HaveCount(3);
            fields[1].Should().Be(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            Guid.TryParse(fields[2], out _).Should().BeTrue();
            using var metadata = System.Text.Json.JsonDocument.Parse(rows.GetString(1));
            metadata.RootElement.GetProperty("WorkerCount").GetInt32().Should().Be(pool.Workers);
            metadata.RootElement.GetProperty("Queues").EnumerateArray().Select(queue => queue.GetString()).Should().Equal(pool.Queues);
            metadata.RootElement.EnumerateObject().Select(property => property.Name).Should()
                .BeEquivalentTo("WorkerCount", "Queues", "StartedAt");
        }
        observed.Should().BeEquivalentTo(expected.Select(pool => pool.Name));
    }

    private static async Task<string?> StateAsync(PrivilegedDataSource source, string vendor)
    {
        await using var sql = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT statename FROM public.job WHERE id=@id::bigint", sql);
        command.Parameters.AddWithValue("id", vendor);
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task AssertNoBusinessAdmissionAsync(PrivilegedDataSource source)
    {
        await using var sql = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT (SELECT count(*) FROM background_jobs) + (SELECT count(*) FROM background_job_attempts)", sql);
        (await command.ExecuteScalarAsync()).Should().Be(0L);
    }

    private static async Task EventuallyAsync(Func<Task<bool>> predicate)
    {
        var elapsed = Stopwatch.StartNew();
        while (!await predicate())
        {
            if (elapsed.Elapsed >= TestWait) throw new TimeoutException("The ordinary PostgreSQL generation state did not arrive.");
            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }
    }

    private sealed class GlobalFilterSnapshot : IDisposable
    {
        private readonly JobFilter[] _original = GlobalJobFilters.Filters.ToArray();
        public void Dispose()
        {
            GlobalJobFilters.Filters.Clear();
            foreach (var filter in _original) GlobalJobFilters.Filters.Add(filter.Instance, filter.Order);
        }
    }
}
