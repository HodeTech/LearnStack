using System.Globalization;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Hangfire.Server;
using LearnStack.Api.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRuntimeFacadeTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_framework_wait_retains_actual_cleanup_and_releases_its_slot_before_host_stop(bool synchronousPrefix)
    {
        await using var database = await DatabaseAsync();
        var physical = new BackgroundJobPhysicalPoolSettings(true, 50);
        var applicationName = "facade_cleanup_" + Guid.NewGuid().ToString("N");
        await using var source = Source(database, physical, applicationName);
        var cleanup = new FacadeCleanupSignals();
        using var prefixRelease = new ManualResetEventSlim();
        var reads = 0;
        var heldConstructed = 0;
        var observation = new BackgroundJobAdmissionObservation
        {
            OnRead = (_query, services) =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                {
                    _ = services.GetRequiredService<FacadeHeldReadCleanup>();
                    if (synchronousPrefix)
                    {
                        // Synchronous handler code belongs to the retained
                        // operation, independently of the native wrapper wait.
                        cleanup.Entered.TrySetResult();
                        if (!prefixRelease.Wait(TestWait)) throw new TimeoutException("The framework prefix was not released.");
                    }
                }
                return Task.CompletedTask;
            },
        };
        await using var host = Fixture.Host(database, new BackgroundJobFixtureProbe(), configure: services =>
        {
            observation.Configure(services);
            services.AddScoped(_ =>
            {
                Interlocked.Increment(ref heldConstructed);
                return new FacadeHeldReadCleanup(cleanup);
            });
        });
        using var globals = new GlobalFilterSnapshot();
        var bounds = new BackgroundJobRuntimeOptions { TransactionTimeout = TimeSpan.FromSeconds(2) };
        var time = new HangfirePoolOwnerTests.ManualTimeProvider();
        var runtime = Runtime(host.Services, source, bounds, physical, time);
        var disposal = SourceDisposalObserver(source);
        LearnStack.SharedKernel.Jobs.BackgroundJobId job;
        try
        {
            using (var startup = new CancellationTokenSource(TestWait))
                await runtime.StartAsync(startup.Token);
            await EventuallyAsync(() => Task.FromResult(runtime.Snapshot.IsHealthy));
            // Bridge, control and terminal are the three ControlPoll timers.
            // Observe their empty quanta parked before creating either native
            // wakeup; advancing the owner clock must not introduce a bridge ACK
            // race into this framework-cleanup proof.
            await EventuallyAsync(() => Task.FromResult(time.DelayCount(bounds.ControlPoll) == 3));
            var transactional = ActualGeneration(runtime, "transactional");
            var provider = ActualProvider(runtime);
            var transactionalPool = Fixture.Registry.Infrastructure.Pools["transactional"];
            job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
            var heldVendor = CreateQualificationWakeup(provider, transactionalPool, job);
            try { await cleanup.Entered.Task.WaitAsync(TestWait); }
            catch (TimeoutException failure)
            {
                // Fixed in-memory facts only. Absence now is not evidence that
                // a clean, already-released invocation never existed.
                throw new InvalidOperationException(
                    $"Native cleanup-entry premise failed; synchronousPrefix={synchronousPrefix};"
                    + $"reads={Volatile.Read(ref reads)};heldConstructed={Volatile.Read(ref heldConstructed)};"
                    + $"readUnit={observation.ReadUnit is not null};readDisposed={observation.ReadScopeDisposed};"
                    + $"startUnit={observation.StartUnit is not null};startDisposed={observation.StartScopeDisposed};"
                    + $"entered={cleanup.Entered.Task.Status};joined={cleanup.Joined.Task.Status};"
                    + $"wrapperTimer={time.HasDelay(bounds.TransactionTimeout)};pollTimers={time.DelayCount(bounds.ControlPoll)};"
                    + $"generation={transactional.Snapshot};work={transactional.WorkSnapshot};runtime={runtime.Snapshot}", failure);
            }
            // Both the asynchronous disposal and synchronous handler-prefix
            // cases reach their actual barrier before the wrapper deadline.
            // The SQL deadlines and caller/final-cleanup waits remain real.
            await EventuallyAsync(() => Task.FromResult(time.HasDelay(bounds.TransactionTimeout)));
            time.Advance(bounds.TransactionTimeout);
            await EventuallyAsync(() => Task.FromResult(transactional.WorkSnapshot.Retained == 1));
            transactional.WorkSnapshot.Should().Be(new HangfireGenerationWorkSnapshot(0, 1, false));
            runtime.Snapshot.IsHealthy.Should().BeFalse();
            await EventuallyAsync(async () => await StateAsync(source, heldVendor) == "Succeeded");
            cleanup.Joined.Task.IsCompleted.Should().BeFalse();
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(0);

            // The one native transactional slot is usable while fewer than four
            // prior framework invocations remain retained in this generation.
            // The clock advance also wakes the three polling loops. Their next
            // parked quanta and the bridge timestamp show that only the first,
            // routing-owned row was visible before the second native wakeup.
            var resumedAt = time.GetTimestamp();
            await EventuallyAsync(() => Task.FromResult(time.DelayCount(bounds.ControlPoll) == 3
                && runtime.Snapshot.Bridge is { Outstanding: 0, LastProgressTimestamp: { } progress }
                && progress == resumedAt));
            var positive = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
            _ = CreateQualificationWakeup(provider, transactionalPool, positive);
            await EventuallyAsync(() => NativeBusinessCompletedAsync(database, source, positive));
            transactional.WorkSnapshot.Should().Be(new HangfireGenerationWorkSnapshot(0, 1, false));

            // This is actual native wakeup/framework execution. Only the DI
            // disposable is test-owned and deliberately ignores cancellation;
            // no synthetic generation ticket substitutes for the real work.
            runtime.RequestStop();
            await EventuallyAsync(() => Task.FromResult(transactional.Snapshot.OuterJoined));
            transactional.Snapshot.NormalChildrenJoined.Should().BeTrue();
            transactional.Completion.IsCompleted.Should().BeFalse("stock completion cannot join retained private framework cleanup");
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
            prefixRelease.Set();
            using var deadline = new CancellationTokenSource(TestWait);
            await runtime.StopAsync(deadline.Token);
            await runtime.Completion.WaitAsync(TestWait);
            await DisposePinnedSourceAsync(source);
        }

        cleanup.Joined.Task.IsCompletedSuccessfully.Should().BeTrue();
        observation.ReadScopeDisposed.Should().BeTrue();
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(0,
            "late actual cleanup cannot reopen admission after the wrapper deadline");
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
    public async Task Pooled_boundary_50_keeps_actual_expiration_checkout_after_caller_timeout_while_transactional_admission_progresses()
    {
        await using var database = await DatabaseAsync();
        var physical = new BackgroundJobPhysicalPoolSettings(true, 50);
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
            PoolStatistics(source).Busy.Should().BeInRange(1, 50);

            var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
            await EventuallyAsync(() => NativeBusinessCompletedAsync(database, source, job));

            (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!.NormalAttempts.Should().Be(1);
            expiration.Snapshot.Should().BeEquivalentTo(new
            {
                State = HangfireSharedComponentState.Retained,
                Invocations = 1L,
                OwnsResources = true,
                ActualJoined = false,
            });
            expiration.Completion.IsCompleted.Should().BeFalse();
            PoolStatistics(source).Busy.Should().BeInRange(1, 50);
            runtime.Snapshot.RunningPools.Should().Be(3);
            var health = await new BackgroundJobHealthCheck(runtime).CheckHealthAsync(new HealthCheckContext());
            health.Status.Should().Be(HealthStatus.Unhealthy);
            health.Data["maintenance:expiration"].Should().BeOfType<BackgroundJobMaintenanceHealth>()
                .Which.Should().BeEquivalentTo(new
                {
                    State = "Retained",
                    ActualJoined = false,
                    OwnsResources = true,
                    IsHealthy = false,
                });
            health.Data["maintenance:fetched_job_heartbeat"].Should().BeOfType<BackgroundJobMaintenanceHealth>()
                .Which.IsHealthy.Should().BeTrue("shared fetched-job renewal remains independent of the held expiration pass");
            ActualGeneration(runtime, "transactional").Snapshot.Draining.Should().BeFalse();
            probe.Constructions.Should().ContainSingle().Which.Should().Be("TenantRoot",
                "the healthy native wakeup now executes the admitted business owner");
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
