using System.Globalization;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Hangfire.Server;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRuntimeFacadeTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public async Task Source_disposal_after_stop_keeps_actual_native_cleanup_and_later_Core_removal_usable()
    {
        await using var database = await DatabaseAsync();
        var physical = new BackgroundJobPhysicalPoolSettings(true, 45);
        var applicationName = "facade_cleanup_" + Guid.NewGuid().ToString("N");
        await using var source = Source(database, physical, applicationName);
        var cleanup = new FacadeCleanupSignals();
        var observation = new BackgroundJobAdmissionObservation
        {
            OnRead = (_query, services) =>
            {
                _ = services.GetRequiredService<FacadeHeldReadCleanup>();
                return Task.CompletedTask;
            },
        };
        await using var host = Fixture.Host(database, new BackgroundJobFixtureProbe(), configure: services =>
        {
            observation.Configure(services);
            services.AddScoped(_ => new FacadeHeldReadCleanup(cleanup));
        });
        using var globals = new GlobalFilterSnapshot();
        var runtime = Runtime(host.Services, source, physical: physical);
        var disposal = SourceDisposalObserver(source);
        try
        {
            await runtime.StartAsync();
            await EventuallyAsync(() => Task.FromResult(runtime.Snapshot.IsHealthy));
            var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
            await cleanup.Entered.Task.WaitAsync(TestWait);
            var transactional = ActualGeneration(runtime, "transactional");
            transactional.WorkSnapshot.Running.Should().Be(1);

            // This is actual native wakeup/framework execution. Only the DI
            // disposable is test-owned and deliberately ignores cancellation;
            // no synthetic generation ticket substitutes for the real work.
            runtime.RequestStop();
            transactional.Snapshot.NormalChildrenJoined.Should().BeFalse();
            transactional.Snapshot.OuterJoined.Should().BeFalse();
            source.Dispose();
            using var callerWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await FluentActions.Awaiting(() => runtime.StopAsync(callerWait.Token))
                .Should().ThrowAsync<OperationCanceledException>();

            cleanup.Joined.Task.IsCompleted.Should().BeFalse();
            runtime.Completion.IsCompleted.Should().BeFalse();
            runtime.Snapshot.CleanupConfirmed.Should().BeFalse();
            source.IsDisposalComplete.Should().BeFalse();
            disposal.IsCompleted.Should().BeFalse();
            source.HasUnconfirmedCleanup.Should().BeFalse("pending ownership is not a disposal fault");
            await AssertGuardedPoolAsync(source, physical);
            (await IndependentServerCountAsync(database)).Should().BeGreaterThan(0,
                "the held native worker still needs its later Core removal acquisition");
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(0,
                "stop closes new admission while the actual pre-read scope is still unwinding");

            // Repeated container-style requests reuse the one pending observer.
            await source.DisposeAsync();
            SourceDisposalObserver(source).Should().BeSameAs(disposal);
            source.IsDisposalComplete.Should().BeFalse();
        }
        finally
        {
            cleanup.Release.TrySetResult();
            using var deadline = new CancellationTokenSource(TestWait);
            await runtime.StopAsync(deadline.Token);
            await runtime.Completion.WaitAsync(TestWait);
            await DisposePinnedSourceAsync(source);
        }

        cleanup.Joined.Task.IsCompletedSuccessfully.Should().BeTrue();
        observation.ReadScopeDisposed.Should().BeTrue();
        runtime.Snapshot.CleanupConfirmed.Should().BeTrue();
        (await IndependentServerCountAsync(database)).Should().Be(0);
        await EventuallyAsync(async () => await PoolBackendCountAsync(database, applicationName) == 0);
        await source.DisposeAsync();
        source.Dispose();
        SourceDisposalObserver(source).Should().BeSameAs(disposal);
        source.IsDisposalComplete.Should().BeTrue();
        var reopen = async () => await source.OpenConnectionAsync();
        await reopen.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task Pooled_boundary_45_keeps_actual_expiration_checkout_after_caller_timeout_while_transactional_admission_progresses()
    {
        await using var database = await DatabaseAsync();
        var physical = new BackgroundJobPhysicalPoolSettings(true, 45);
        var applicationName = "facade_capacity_" + Guid.NewGuid().ToString("N");
        await using var source = Source(database, physical, applicationName);
        var probe = new BackgroundJobFixtureProbe();
        await using var host = Fixture.Host(database, probe);
        using var globals = new GlobalFilterSnapshot();
        var runtime = Runtime(host.Services, source, physical: physical);
        HangfireSharedComponentWorker? expiration = null;
        try
        {
            // Deliberate competing database ownership, not a synthetic owner
            // token or callback. The future timestamp keeps the valid lock row
            // from expiring during this test. The exact stock acquisition loop
            // retains one actual checkout across its individual bounded SQL calls.
            await SetExpirationContentionAsync(database, held: true);
            await runtime.StartAsync();
            await EventuallyAsync(() => Task.FromResult(runtime.Snapshot.IsHealthy));
            await AssertGuardedPoolAsync(source, physical);
            expiration = ActualExpiration(runtime);
            await EventuallyAsync(() => Task.FromResult(expiration.Snapshot is
            { Invocations: 1, OwnsResources: true, ActualJoined: false }));

            using var callerWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await FluentActions.Awaiting(() => expiration.StopAsync(callerWait.Token))
                .Should().ThrowAsync<OperationCanceledException>();
            await EventuallyAsync(() => Task.FromResult(expiration.Snapshot.State == HangfireSharedComponentState.Retained));
            expiration.Completion.IsCompleted.Should().BeFalse();
            PoolStatistics(source).Busy.Should().BeInRange(1, 45);

            var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
            await EventuallyAsync(async () =>
            {
                if (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job) != 1) return false;
                var vendor = await VendorAsync(source, job);
                return vendor is not null && await StateAsync(source, vendor) == "Succeeded";
            });

            (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!.NormalAttempts.Should().Be(1);
            expiration.Snapshot.Should().BeEquivalentTo(new
            {
                State = HangfireSharedComponentState.Retained,
                Invocations = 1L,
                OwnsResources = true,
                ActualJoined = false,
            });
            expiration.Completion.IsCompleted.Should().BeFalse();
            PoolStatistics(source).Busy.Should().BeInRange(1, 45);
            runtime.Snapshot.RunningPools.Should().Be(3);
            ActualGeneration(runtime, "transactional").Snapshot.Draining.Should().BeFalse();
            probe.Constructions.Should().BeEmpty("Step 4 admits framework work without activating business handlers");
            // Actual other-pool admission at the supported boundary is proved;
            // simultaneous saturation of every reserved lane is not asserted.
        }
        finally
        {
            runtime.RequestStop();
            await SetExpirationContentionAsync(database, held: false);
            using var deadline = new CancellationTokenSource(TestWait);
            await runtime.StopAsync(deadline.Token);
            await runtime.Completion.WaitAsync(TestWait);
            await DisposePinnedSourceAsync(source);
        }

        var joinedExpiration = expiration ?? throw new InvalidOperationException("The actual expiration owner was not observed.");
        joinedExpiration.Snapshot.ActualJoined.Should().BeTrue();
        joinedExpiration.Snapshot.OwnsResources.Should().BeFalse();
        joinedExpiration.Snapshot.Invocations.Should().Be(1);
        (await IndependentServerCountAsync(database)).Should().Be(0);
        await EventuallyAsync(async () => await PoolBackendCountAsync(database, applicationName) == 0);
    }

    private static BackgroundJobDefinitionRegistry MaximumManifest()
    {
        var pools = Enumerable.Range(0, 8).Select(index => new BackgroundJobWorkerPoolRegistration(
            "pool_" + index.ToString(CultureInfo.InvariantCulture), index == 0 ? BackgroundJobWorkerPoolKind.Transactional
                : index == 1 ? BackgroundJobWorkerPoolKind.External : BackgroundJobWorkerPoolKind.Control,
            4, ["queue_" + index.ToString(CultureInfo.InvariantCulture)]));
        var topology = BackgroundJobInfrastructureRegistry.Create(pools, [], [], []);
        return BackgroundJobDefinitionRegistry.Create([], BackgroundJobDefinitionRegistry.CreateEmpty().All, [], infrastructure: topology);
    }

    private static async Task AssertActualManifestAsync(PrivilegedDataSource source, BackgroundJobRuntime runtime,
        BackgroundJobInfrastructureRegistry topology)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT id,data::text FROM public.server ORDER BY id", connection);
        await using var rows = await command.ExecuteReaderAsync();
        var observed = new HashSet<string>(StringComparer.Ordinal);
        while (await rows.ReadAsync())
        {
            var id = rows.GetString(0);
            var pool = topology.Pools.Values.Should().ContainSingle(candidate =>
                id.StartsWith("learnstack-" + candidate.Name + ":", StringComparison.Ordinal)).Which;
            observed.Add(pool.Name).Should().BeTrue();
            using var metadata = JsonDocument.Parse(rows.GetString(1));
            metadata.RootElement.GetProperty("WorkerCount").GetInt32().Should().Be(pool.Workers);
            metadata.RootElement.GetProperty("Queues").EnumerateArray().Select(queue => queue.GetString())
                .Should().Equal(pool.Queues);
            var generation = ActualGeneration(runtime, pool.Name);
            generation.Ready.IsCompletedSuccessfully.Should().BeTrue();
            generation.Ready.Result.Should().BeTrue();
            generation.Snapshot.Captured.Should().BeTrue();
            var builders = typeof(HangfireGeneration).GetField("_builders", PrivateInstance)?.GetValue(generation)
                as IBackgroundProcessDispatcherBuilder[]
                ?? throw new InvalidOperationException("The actual generation dispatcher manifest is missing.");
            // Two fixed capture/wait builders, the configured real workers,
            // and both native schedulers for every configured Control pool.
            builders.Should().HaveCount(pool.Workers + 2 + (pool.Kind == BackgroundJobWorkerPoolKind.Control ? 2 : 0));
        }
        observed.Should().BeEquivalentTo(topology.Pools.Keys);
    }

    private static async Task AssertGuardedPoolAsync(PrivilegedDataSource source, BackgroundJobPhysicalPoolSettings physical)
    {
        await using var connection = await source.OpenConnectionAsync();
        var settings = new NpgsqlConnectionStringBuilder(connection.ConnectionString);
        settings.Pooling.Should().Be(physical.Pooling);
        settings.MaxPoolSize.Should().Be(physical.MaxPoolSize);
        await using var command = new NpgsqlCommand("""
            SELECT current_user = @role
                AND current_setting('statement_timeout') = '5s'
                AND current_setting('lock_timeout') = '5s'
                AND current_setting('idle_in_transaction_session_timeout') = '5s'
            """, connection);
        command.Parameters.AddWithValue("role", OutboxDispatcherDataSource.Role);
        (await command.ExecuteScalarAsync()).Should().Be(true);
    }

    private static HangfireGeneration ActualGeneration(BackgroundJobRuntime runtime, string poolName)
    {
        var pools = typeof(BackgroundJobRuntime).GetField("_pools", PrivateInstance)?.GetValue(runtime)
            as IEnumerable<HangfirePoolOwner>
            ?? throw new InvalidOperationException("The actual facade pool inventory is missing.");
        var pool = pools.Single(candidate => candidate.Snapshot.Pool == poolName);
        return typeof(HangfirePoolOwner).GetField("_generation", PrivateInstance)?.GetValue(pool) as HangfireGeneration
            ?? throw new InvalidOperationException("The actual pool generation is missing.");
    }

    private static HangfireSharedComponentWorker ActualExpiration(BackgroundJobRuntime runtime)
    {
        var shared = typeof(BackgroundJobRuntime).GetField("_shared", PrivateInstance)?.GetValue(runtime)
            as HangfireSharedComponents
            ?? throw new InvalidOperationException("The actual shared component owner is missing.");
        return typeof(HangfireSharedComponents).GetField("_expiration", PrivateInstance)?.GetValue(shared)
            as HangfireSharedComponentWorker
            ?? throw new InvalidOperationException("The actual expiration owner is missing.");
    }

    private static (int Total, int Idle, int Busy) PoolStatistics(PrivilegedDataSource source)
    {
        // Read-only exact-pinned driver observation; no source field, resource,
        // task, owner or cleanup result is replaced by this fixture.
        var lazy = typeof(PrivilegedDataSource).GetField("_source", PrivateInstance)?.GetValue(source)
            as Lazy<NpgsqlDataSource>
            ?? throw new InvalidOperationException("The actual guarded source is missing.");
        return typeof(NpgsqlDataSource).GetProperty("Statistics", PrivateInstance)?.GetValue(lazy.Value)
            is ValueTuple<int, int, int> statistics ? statistics
            : throw new InvalidOperationException("The pinned physical pool statistics are missing.");
    }

    private static Task SourceDisposalObserver(PrivilegedDataSource source) =>
        typeof(PrivilegedDataSource).GetField("_retentionObserver", PrivateInstance)?.GetValue(source) as Task
        ?? throw new InvalidOperationException("The actual source disposal observer is missing.");

    private static async Task DisposePinnedSourceAsync(PrivilegedDataSource source)
    {
        source.Dispose();
        await SourceDisposalObserver(source).WaitAsync(TestWait);
        source.IsDisposalComplete.Should().BeTrue();
        source.HasUnconfirmedCleanup.Should().BeFalse();
    }

    private static async Task SetExpirationContentionAsync(DisposableSchemaDatabase database, bool held)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await using var command = new NpgsqlCommand(held
            ? "INSERT INTO public.lock(resource,acquired) VALUES ('locks:expirationmanager',clock_timestamp()+interval '1 hour')"
            : "DELETE FROM public.lock WHERE resource='locks:expirationmanager'", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> IndependentServerCountAsync(DisposableSchemaDatabase database)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM public.server", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> PoolBackendCountAsync(DisposableSchemaDatabase database, string applicationName)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM pg_stat_activity
            WHERE datname=current_database() AND application_name=@application
            """, connection);
        command.Parameters.AddWithValue("application", applicationName);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed class FacadeCleanupSignals
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Joined { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class FacadeHeldReadCleanup(FacadeCleanupSignals state) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            state.Entered.TrySetResult();
            await state.Release.Task.ConfigureAwait(false);
            state.Joined.TrySetResult();
        }
    }
}
