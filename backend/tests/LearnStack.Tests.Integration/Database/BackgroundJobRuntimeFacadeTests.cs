using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Direct facade and actual API-composition proofs with explicitly labelled fixture cleanup holds.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed partial class BackgroundJobRuntimeFacadeTests(SchemaFixture schema, ITestOutputHelper testOutput)
{
    private static readonly TimeSpan TestWait = TimeSpan.FromSeconds(30);
    private static BackgroundJobRuntimeFixture Fixture => BackgroundJobRuntimeFixture.Shared;

    [Theory]
    [InlineData(false, 3, 4, 1, 16, 50)]
    [InlineData(true, 8, 32, 6, 64, 156)]
    public async Task Direct_real_composition_uses_one_pool_manifest_and_joins_all_owners_before_source_disposal(
        bool maximum, int pools, int workers, int controls, int invocations, int connections)
    {
        await using var database = await DatabaseAsync();
        var definitions = maximum ? MaximumManifest() : Fixture.Registry;
        var physical = new BackgroundJobPhysicalPoolSettings(true, connections);
        var applicationName = "facade_manifest_" + Guid.NewGuid().ToString("N");
        await using var host = Fixture.Host(database, new BackgroundJobFixtureProbe(), configure: services =>
        {
            services.RemoveAll<BackgroundJobDefinitionRegistry>();
            services.AddSingleton(definitions);
        });
        await using var source = Source(database, physical, applicationName);
        using var globals = new GlobalFilterSnapshot();
        var runtime = Runtime(host.Services, source, physical: physical);
        try
        {
            await runtime.StartAsync();
            await EventuallyAsync(() => Task.FromResult(runtime.Snapshot.IsHealthy));
            var snapshot = runtime.Snapshot;
            snapshot.Capacity.Should().Be(new BackgroundJobPhysicalCapacity(pools, workers, controls, invocations, connections));
            snapshot.RunningPools.Should().Be(pools);
            snapshot.ExpirationHealthy.Should().BeTrue();
            snapshot.FetchedJobHeartbeatHealthy.Should().BeTrue();
            snapshot.BootstrapJoined.Should().BeTrue();
            snapshot.CleanupConfirmed.Should().BeFalse();
            await AssertGuardedPoolAsync(source, physical);
            await AssertActualManifestAsync(source, runtime, definitions.Infrastructure);
        }
        finally
        {
            using var deadline = new CancellationTokenSource(TestWait);
            await runtime.StopAsync(deadline.Token);
            await runtime.Completion.WaitAsync(TestWait);
            await DisposePinnedSourceAsync(source);
        }
        runtime.Snapshot.State.Should().Be(BackgroundJobRuntimeState.Stopped);
        runtime.Snapshot.CleanupConfirmed.Should().BeTrue();
        runtime.Snapshot.IsHealthy.Should().BeFalse();
        (await IndependentServerCountAsync(database)).Should().Be(0);
        await EventuallyAsync(async () => await PoolBackendCountAsync(database, applicationName) == 0);
        // These actual generations qualify the current manifest and cleanup;
        // this is neither simultaneous saturation nor future Steps 6–8 SQL coverage.
    }

    [Fact]
    public async Task Bootstrap_wait_and_stop_deadlines_retain_actual_SQL_and_late_publication_starts_no_pool()
    {
        await using var database = await DatabaseAsync();
        await using var host = Fixture.Host(database, new BackgroundJobFixtureProbe());
        await using var source = Source(database);
        using var globals = new GlobalFilterSnapshot();
        var bounds = new BackgroundJobRuntimeOptions { ProbeTimeout = TimeSpan.FromSeconds(2) };
        var runtime = Runtime(host.Services, source, bounds);
        await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        await SchemaQueries.ExecuteAsync(owner, transaction, "LOCK TABLE public.\"schema\" IN ACCESS EXCLUSIVE MODE");
        var start = runtime.StartAsync();
        try
        {
            await WaitForBootstrapLockAsync(database.OutboxConnectionString);
            await FluentActions.Awaiting(() => start).Should().ThrowAsync<HangfireStorageRefusedException>();
            runtime.Snapshot.State.Should().Be(BackgroundJobRuntimeState.Retained);
            runtime.Snapshot.BootstrapJoined.Should().BeFalse();
            runtime.Completion.IsCompleted.Should().BeFalse();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await FluentActions.Awaiting(() => runtime.StopAsync(deadline.Token)).Should().ThrowAsync<OperationCanceledException>();
            runtime.Completion.IsCompleted.Should().BeFalse("the external stop wait cannot join held bootstrap SQL");
            (await ServerCountAsync(source)).Should().Be(0);
        }
        finally
        {
            await transaction.RollbackAsync();
            try { await start; }
            catch (HangfireStorageRefusedException) { }
            using var deadline = new CancellationTokenSource(TestWait);
            await runtime.StopAsync(deadline.Token);
            await runtime.Completion.WaitAsync(TestWait);
        }
        runtime.Snapshot.BootstrapJoined.Should().BeTrue();
        runtime.Snapshot.CleanupConfirmed.Should().BeTrue();
        runtime.Snapshot.RunningPools.Should().Be(0);
        runtime.Snapshot.Bridge.Should().BeNull("late provider publication receives no runtime-start authority");
        (await ServerCountAsync(source)).Should().Be(0);
        await DisposePinnedSourceAsync(source);
    }

    [Fact]
    public async Task Independent_bridge_repairs_a_due_delayed_job_while_the_actual_control_generation_is_held()
    {
        await using var database = await DatabaseAsync();
        await using var host = Fixture.Host(database, new BackgroundJobFixtureProbe());
        await using var source = Source(database);
        using var globals = new GlobalFilterSnapshot();
        var bounds = new BackgroundJobRuntimeOptions { HandoffRepairGrace = TimeSpan.FromSeconds(15) };
        var runtime = Runtime(host.Services, source, bounds);
        HangfireGenerationWork.Invocation? held = null;
        try
        {
            await runtime.StartAsync();
            await EventuallyAsync(() => Task.FromResult(runtime.Snapshot.IsHealthy));
            var control = ControlGeneration(runtime);
            // A narrow synthetic owner hold isolates generation/control independence;
            // native workers, delayed handoff and durable repair remain real PostgreSQL.
            held = control.TryBeginWork() ?? throw new InvalidOperationException("The ready control generation refused its owner probe.");
            held.MarkRetained();
            control.RequestDrain(HangfireGenerationReason.HeartbeatTransient);
            await EventuallyAsync(() => Task.FromResult(control.Snapshot.NormalChildrenJoined));
            control.Completion.IsCompleted.Should().BeFalse();
            runtime.Snapshot.RetainedInvocations.Should().Be(1);
            runtime.Snapshot.IsHealthy.Should().BeFalse();

            var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
                due: DateTimeOffset.UtcNow.AddSeconds(2));
            string? scheduledVendor = null;
            await EventuallyAsync(async () =>
            {
                scheduledVendor = await VendorAsync(source, job);
                return scheduledVendor is not null && await StateAsync(source, scheduledVendor) == "Scheduled";
            });
            await EventuallyAsync(async () =>
                (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))?.NormalAttempts == 1
                && await VendorAsync(source, job) != scheduledVendor && runtime.Snapshot.Bridge?.Acknowledged >= 2);
            control.Completion.IsCompleted.Should().BeFalse("repair must not require a control replacement or vendor tick");
            var row = await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job)
                ?? throw new InvalidOperationException("The repaired job is missing.");
            row.NormalAttempts.Should().Be(1);
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
            (await VendorAsync(source, job)).Should().NotBe(scheduledVendor,
                "the bridge repairs with a same-generation immediate wakeup while the original remains delayed");
            var bridge = runtime.Snapshot.Bridge ?? throw new InvalidOperationException("The independent bridge is missing.");
            bridge.Acknowledged.Should().BeGreaterThanOrEqualTo(2);
        }
        finally
        {
            if (held is not null) await held.CompleteAsync(cleanupConfirmed: true);
            using var deadline = new CancellationTokenSource(TestWait);
            await runtime.StopAsync(deadline.Token);
            await runtime.Completion.WaitAsync(TestWait);
            await DisposePinnedSourceAsync(source);
        }
    }

    private async Task<DisposableSchemaDatabase> DatabaseAsync()
    {
        var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        return database;
    }

    private static PrivilegedDataSource Source(DisposableSchemaDatabase database,
        BackgroundJobPhysicalPoolSettings? physical = null, string? applicationName = null)
    {
        var settings = new NpgsqlConnectionStringBuilder(database.OutboxConnectionString);
        if (physical is not null)
        {
            settings.Pooling = physical.Pooling;
            settings.MaxPoolSize = physical.MaxPoolSize;
        }
        if (applicationName is not null) settings.ApplicationName = applicationName;
        var connectionString = settings.ConnectionString;
        return new(() => OutboxDispatcherDataSource.Build(connectionString));
    }

    private static BackgroundJobRuntime Runtime(IServiceProvider services, PrivilegedDataSource source,
        BackgroundJobRuntimeOptions? bounds = null, BackgroundJobPhysicalPoolSettings? physical = null,
        TimeProvider? timeProvider = null)
    {
        bounds ??= new();
        var definitions = services.GetRequiredService<BackgroundJobDefinitionRegistry>();
        var gate = new BackgroundJobRuntimeGate(definitions.Infrastructure);
        var ids = new SystemGuidFactory();
        var routing = new PostgresBackgroundJobRoutingStore(source, definitions, bounds, ids, gate);
        var runtime = new BackgroundJobRuntime(source, definitions, BackgroundJobRecurrenceRegistry.CreateEmpty(),
            services.GetRequiredService<JobExecutor>(), routing, gate, bounds, ids, timeProvider ?? TimeProvider.System,
            physical ?? new(false, 100), new HangfireBoundedLogProvider(),
            services.GetRequiredService<ApplicationDataSourceLifetime>());
        // The direct host removes both API messaging workers before startup.
        // Each completed task is a no-issued-work fact, never a substitute for
        // an actual production processor's startup/main-loop completion.
        runtime.BindMessagingLifetime(Task.CompletedTask, Task.CompletedTask);
        return runtime;
    }

    private static HangfireGeneration ControlGeneration(BackgroundJobRuntime runtime)
    {
        var pools = typeof(BackgroundJobRuntime).GetField("_pools", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(runtime) as IEnumerable<HangfirePoolOwner>
            ?? throw new InvalidOperationException("The actual facade pool inventory is missing.");
        var control = pools.Single(pool => pool.Snapshot.Pool == "control");
        return typeof(HangfirePoolOwner).GetField("_generation", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(control) as HangfireGeneration
            ?? throw new InvalidOperationException("The actual control generation is missing.");
    }

    private static async Task<long> ServerCountAsync(PrivilegedDataSource source)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM public.server", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> NativeBusinessCompletedAsync(DisposableSchemaDatabase database,
        PrivilegedDataSource source, BackgroundJobId job)
    {
        var row = await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job);
        if (row is not { Phase: "completed", NormalAttempts: 1 }) return false;
        // Completion deliberately clears the mutable vendor hint. Observe the
        // actual opaque stock wakeup independently instead of waiting for that
        // hint to reappear or equating native success with business completion.
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM public.job
                WHERE arguments->>0=@job AND arguments->>1='1' AND statename='Succeeded')
            """, connection);
        command.Parameters.AddWithValue("job", System.Text.Json.JsonSerializer.Serialize(job.Value));
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string?> VendorAsync(PrivilegedDataSource source, BackgroundJobId job)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT vendor_job_id FROM background_jobs WHERE id=@id", connection);
        command.Parameters.AddWithValue("id", job.Value);
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task<string?> StateAsync(PrivilegedDataSource source, string vendor)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT statename FROM public.job WHERE id=@id::bigint", connection);
        command.Parameters.AddWithValue("id", vendor);
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task WaitForBootstrapLockAsync(string connectionString)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
        await EventuallyAsync(async () =>
        {
            await using var command = new NpgsqlCommand("""
                SELECT count(*) FROM pg_stat_activity WHERE datname = current_database()
                    AND usename = 'learnstack_outbox_admin' AND wait_event_type = 'Lock'
                    AND query LIKE '%min(version)%'
                """, connection);
            return (long)(await command.ExecuteScalarAsync())! > 0;
        });
    }

    private static async Task EventuallyAsync(Func<Task<bool>> predicate)
    {
        var elapsed = Stopwatch.StartNew();
        while (!await predicate())
        {
            if (elapsed.Elapsed >= TestWait) throw new TimeoutException("The real facade ownership state did not arrive.");
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
