using System.Diagnostics;
using System.Globalization;
using System.Reflection;
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

/// <summary>Actual guarded PostgreSQL generations under the same closed replacement owner.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class HangfirePoolOwnerTests(SchemaFixture schema)
{
    private static readonly TimeSpan TestWait = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_pool_replaces_only_after_actual_cleanup_and_backoff_then_runs_a_real_wakeup()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var host = Host(database);
        await using var source = Source(database);
        using var globals = new GlobalFilterSnapshot();
        var topology = host.Services.GetRequiredService<BackgroundJobDefinitionRegistry>().Infrastructure;
        var gate = new BackgroundJobRuntimeGate(topology);
        var filters = HangfireRuntimeFilters.Create();
        var provider = HangfireStorageBootstrap.Create(source, topology, new HangfireBoundedLogProvider(), gate);
        var pool = topology.Pools["transactional"];
        var time = new ManualTimeProvider();
        var owner = Owner(host.Services, provider, new(provider), filters, pool, gate, time);
        owner.Start();
        try
        {
            await EventuallyAsync(() => Task.FromResult(owner.Snapshot.State == HangfirePoolOwnerState.Running));
            var prior = Current(owner);
            (await prior.Ready.WaitAsync(TestWait)).Should().BeTrue();
            owner.Snapshot.Pool.Should().Be(pool.Name);
            owner.Snapshot.Generations.Should().Be(1);
            await AssertServersAsync(source, pool);

            prior.RequestDrain(HangfireGenerationReason.HeartbeatTransient);
            await prior.Completion.WaitAsync(TestWait);
            await EventuallyAsync(() => Task.FromResult(owner.Snapshot.State == HangfirePoolOwnerState.Backoff
                && time.HasDelay(HangfireGeneration.ReplacementBackoff)));
            AssertJoined(prior);
            owner.Completion.IsCompleted.Should().BeFalse();
            owner.Snapshot.Generation.Should().Be(prior.Snapshot);
            await AssertServersAsync(source);

            // The timer is armed only after actual cleanup. Advance to one tick
            // before its deadline while an actual stock wakeup remains queued.
            var vendor = CreateOrphanWakeup(provider, filters, pool);
            time.Advance(HangfireGeneration.ReplacementBackoff - TimeSpan.FromTicks(1));
            owner.Snapshot.Generations.Should().Be(1);
            Current(owner).Should().BeSameAs(prior);
            (await StateAsync(source, vendor)).Should().Be("Enqueued");
            await AssertServersAsync(source);

            time.Advance(TimeSpan.FromTicks(1));
            await EventuallyAsync(() => Task.FromResult(owner.Snapshot is
            { State: HangfirePoolOwnerState.Running, Generations: 2 }));
            var replacement = Current(owner);
            replacement.Should().NotBeSameAs(prior);
            prior.Completion.IsCompletedSuccessfully.Should().BeTrue();
            (await replacement.Ready.WaitAsync(TestWait)).Should().BeTrue();
            await AssertServersAsync(source, pool);
            await EventuallyAsync(async () => await StateAsync(source, vendor) == "Succeeded");
            await AssertNoBusinessAdmissionAsync(source);
            gate.IsOpen.Should().BeTrue();
        }
        finally
        {
            using var callerBudget = new CancellationTokenSource(TestWait);
            gate.StopStarts();
            owner.RequestStop();
            await owner.StopAsync(callerBudget.Token);
        }
        owner.Snapshot.State.Should().Be(HangfirePoolOwnerState.Stopped);
        owner.Snapshot.Generations.Should().Be(2);
        owner.Completion.IsCompletedSuccessfully.Should().BeTrue();
        Supervision(owner).IsCompletedSuccessfully.Should().BeTrue();
        time.ActiveTimers.Should().Be(0);
        await AssertServersAsync(source);
    }

    [Fact]
    public async Task Host_stop_during_backoff_cancels_the_timer_and_never_constructs_a_replacement()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var host = Host(database);
        await using var source = Source(database);
        using var globals = new GlobalFilterSnapshot();
        var topology = host.Services.GetRequiredService<BackgroundJobDefinitionRegistry>().Infrastructure;
        var gate = new BackgroundJobRuntimeGate(topology);
        var provider = HangfireStorageBootstrap.Create(source, topology, new HangfireBoundedLogProvider(), gate);
        var time = new ManualTimeProvider();
        var owner = Owner(host.Services, provider, new(provider), HangfireRuntimeFilters.Create(),
            topology.Pools["transactional"], gate, time);
        owner.Start();
        try
        {
            await EventuallyAsync(() => Task.FromResult(owner.Snapshot.State == HangfirePoolOwnerState.Running));
            var prior = Current(owner);
            prior.RequestDrain(HangfireGenerationReason.HeartbeatMissing);
            await prior.Completion.WaitAsync(TestWait);
            await EventuallyAsync(() => Task.FromResult(owner.Snapshot.State == HangfirePoolOwnerState.Backoff
                && time.HasDelay(HangfireGeneration.ReplacementBackoff)));

            using var callerBudget = new CancellationTokenSource(TestWait);
            gate.StopStarts();
            owner.RequestStop();
            await owner.StopAsync(callerBudget.Token);
            owner.Start();
            time.Advance(HangfireGeneration.ReplacementBackoff + HangfireGeneration.ReplacementBackoff);
            Current(owner).Should().BeSameAs(prior);
            owner.Snapshot.Generations.Should().Be(1);
            owner.Snapshot.State.Should().Be(HangfirePoolOwnerState.Stopped);
            owner.Completion.IsCompletedSuccessfully.Should().BeTrue();
            time.ActiveTimers.Should().Be(0);
            AssertJoined(prior);
            await AssertServersAsync(source);
        }
        finally
        {
            using var callerBudget = new CancellationTokenSource(TestWait);
            gate.StopStarts();
            await owner.StopAsync(callerBudget.Token);
        }
    }

    [Fact]
    public async Task A_closed_global_gate_at_the_backoff_deadline_prevents_replacement()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var host = Host(database);
        await using var source = Source(database);
        using var globals = new GlobalFilterSnapshot();
        var topology = host.Services.GetRequiredService<BackgroundJobDefinitionRegistry>().Infrastructure;
        var gate = new BackgroundJobRuntimeGate(topology);
        var provider = HangfireStorageBootstrap.Create(source, topology, new HangfireBoundedLogProvider(), gate);
        var time = new ManualTimeProvider();
        var owner = Owner(host.Services, provider, new(provider), HangfireRuntimeFilters.Create(),
            topology.Pools["transactional"], gate, time);
        owner.Start();
        try
        {
            await EventuallyAsync(() => Task.FromResult(owner.Snapshot.State == HangfirePoolOwnerState.Running));
            var prior = Current(owner);
            prior.RequestDrain(HangfireGenerationReason.HeartbeatTransient);
            await prior.Completion.WaitAsync(TestWait);
            await EventuallyAsync(() => Task.FromResult(time.HasDelay(HangfireGeneration.ReplacementBackoff)));

            gate.StopStarts();
            time.Advance(HangfireGeneration.ReplacementBackoff);
            await owner.Completion.WaitAsync(TestWait);
            owner.Snapshot.State.Should().Be(HangfirePoolOwnerState.Stopped);
            owner.Snapshot.Generations.Should().Be(1);
            Current(owner).Should().BeSameAs(prior);
            AssertJoined(prior);
            await AssertServersAsync(source);
        }
        finally
        {
            using var callerBudget = new CancellationTokenSource(TestWait);
            gate.StopStarts();
            await owner.StopAsync(callerBudget.Token);
        }
    }

    [Fact]
    public async Task Startup_stall_remains_closed_after_the_actual_late_cleanup_joins()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var host = Host(database);
        await using var source = Source(database);
        using var globals = new GlobalFilterSnapshot();
        var topology = host.Services.GetRequiredService<BackgroundJobDefinitionRegistry>().Infrastructure;
        var bounds = host.Services.GetRequiredService<BackgroundJobRuntimeOptions>();
        var gate = new BackgroundJobRuntimeGate(topology);
        var provider = HangfireStorageBootstrap.Create(source, topology, new HangfireBoundedLogProvider(), gate);
        var time = new ManualTimeProvider();
        var owner = Owner(host.Services, provider, new(provider), HangfireRuntimeFilters.Create(),
            topology.Pools["transactional"], gate, time);
        await using var sql = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var transaction = await sql.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("LOCK TABLE public.server IN ACCESS EXCLUSIVE MODE", sql, transaction))
            await command.ExecuteNonQueryAsync();
        var released = false;
        owner.Start();
        try
        {
            // Real announcement SQL cannot pass the test-owned lock. Only the
            // startup observer's monotonic clock advances; its native I/O keeps
            // the same finite provider budget and is released promptly below.
            await EventuallyAsync(() => Task.FromResult(owner.Snapshot.Generation is
            { HandlePublished: true, WaitEntered: false } && time.HasDelay(bounds.ProgressTimeout)));
            var generation = Current(owner);
            time.Advance(bounds.ProgressTimeout);
            await EventuallyAsync(() => Task.FromResult(owner.Snapshot is
            {
                State: HangfirePoolOwnerState.Retained,
                Generation.Reason: HangfireGenerationReason.StartupStalled
            }));
            generation.Completion.IsCompleted.Should().BeFalse();
            owner.Snapshot.Generations.Should().Be(1);

            await transaction.RollbackAsync();
            released = true;
            await generation.Completion.WaitAsync(TestWait);
            await EventuallyAsync(() => Task.FromResult(owner.Snapshot.State == HangfirePoolOwnerState.Retained));
            generation.Snapshot.CleanupComplete.Should().BeTrue();
            time.Advance(HangfireGeneration.ReplacementBackoff + HangfireGeneration.ReplacementBackoff);
            owner.Snapshot.State.Should().Be(HangfirePoolOwnerState.Retained);
            owner.Snapshot.Generations.Should().Be(1);
            Current(owner).Should().BeSameAs(generation);
            owner.Completion.IsCompleted.Should().BeFalse();
            gate.IsOpen.Should().BeTrue();
            await AssertServersAsync(source);
            await AssertNoBusinessAdmissionAsync(source);
        }
        finally
        {
            using var callerBudget = new CancellationTokenSource(TestWait);
            if (!released) await transaction.RollbackAsync(callerBudget.Token);
            gate.StopStarts();
            await owner.StopAsync(callerBudget.Token);
        }
        owner.Completion.IsCompletedSuccessfully.Should().BeTrue();
        owner.Snapshot.State.Should().Be(HangfirePoolOwnerState.Stopped);
        owner.Snapshot.Generations.Should().Be(1);
        time.ActiveTimers.Should().Be(0);
    }

    [Fact]
    public async Task Stop_before_start_positively_completes_without_constructing_any_generation()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var host = Host(database);
        await using var source = Source(database);
        using var globals = new GlobalFilterSnapshot();
        var topology = host.Services.GetRequiredService<BackgroundJobDefinitionRegistry>().Infrastructure;
        var gate = new BackgroundJobRuntimeGate(topology);
        var provider = HangfireStorageBootstrap.Create(source, topology, new HangfireBoundedLogProvider(), gate);
        var owner = Owner(host.Services, provider, new(provider), HangfireRuntimeFilters.Create(),
            topology.Pools["transactional"], gate, TimeProvider.System);

        owner.Snapshot.State.Should().Be(HangfirePoolOwnerState.NotStarted);
        owner.RequestStop();
        owner.Start();
        using var callerBudget = new CancellationTokenSource(TestWait);
        await owner.StopAsync(callerBudget.Token);
        owner.Snapshot.State.Should().Be(HangfirePoolOwnerState.Stopped);
        owner.Snapshot.Generations.Should().Be(0);
        owner.Snapshot.Generation.Should().BeNull();
        owner.Completion.IsCompletedSuccessfully.Should().BeTrue();
        await AssertServersAsync(source);
        await AssertNoBusinessAdmissionAsync(source);
    }

    [Fact]
    public async Task Prevalidation_fault_stays_retained_and_stop_consumes_only_the_callers_existing_budget()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var host = Host(database);
        await using var source = Source(database);
        using var globals = new GlobalFilterSnapshot();
        var topology = host.Services.GetRequiredService<BackgroundJobDefinitionRegistry>().Infrastructure;
        var gate = new BackgroundJobRuntimeGate(topology);
        var provider = HangfireStorageBootstrap.Create(source, topology, new HangfireBoundedLogProvider(), gate);
        var time = new ManualTimeProvider();
        var bounds = host.Services.GetRequiredService<BackgroundJobRuntimeOptions>() with { ShutdownTimeout = TimeSpan.Zero };
        var owner = Owner(host.Services, provider, new(provider), HangfireRuntimeFilters.Create(),
            topology.Pools["transactional"], gate, time, bounds);
        owner.Start();
        await EventuallyAsync(() => Task.FromResult(owner.Snapshot.State == HangfirePoolOwnerState.Faulted));

        using var callerBudget = new CancellationTokenSource();
        await callerBudget.CancelAsync();
        var wait = owner.StopAsync(callerBudget.Token);
        wait.IsCompleted.Should().BeTrue("an exhausted caller budget is never restarted by the owner");
        Func<Task> stop = () => wait;
        await stop.Should().ThrowAsync<OperationCanceledException>();
        time.Advance(HangfireGeneration.ReplacementBackoff + HangfireGeneration.ReplacementBackoff);
        owner.Snapshot.State.Should().Be(HangfirePoolOwnerState.Faulted);
        owner.Snapshot.Generations.Should().Be(0);
        owner.Snapshot.Generation.Should().BeNull();
        owner.Completion.IsCompleted.Should().BeFalse();
        time.ActiveTimers.Should().Be(0);
        gate.State.Should().Be(BackgroundJobProcessState.Running);
        gate.StopStarts();
        await AssertServersAsync(source);
        await AssertNoBusinessAdmissionAsync(source);
    }

    private static HangfirePoolOwner Owner(IServiceProvider services, PostgreSqlStorage provider,
        HangfireComponentInventory inventory, HangfireRuntimeFilters filters,
        BackgroundJobWorkerPoolRegistration pool, BackgroundJobRuntimeGate gate,
        TimeProvider time, BackgroundJobRuntimeOptions? bounds = null)
    {
        var topology = services.GetRequiredService<BackgroundJobDefinitionRegistry>().Infrastructure;
        var activator = HangfireInvocationContract.CreateActivator(pool, topology,
            services.GetRequiredService<PostgresBackgroundJobRoutingStore>(), services.GetRequiredService<JobExecutor>(),
            BackgroundJobRecurrenceRegistry.CreateEmpty());
        return new(provider, inventory, filters, activator, pool, gate,
            bounds ?? services.GetRequiredService<BackgroundJobRuntimeOptions>(), time);
    }

    private static HangfireGeneration Current(HangfirePoolOwner owner) =>
        // Observe identity only; reflection never changes the production owner,
        // supplies a generation factory or manufactures any completion witness.
        typeof(HangfirePoolOwner).GetField("_generation", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(owner) as HangfireGeneration
        ?? throw new InvalidOperationException("The actual generation has not been published.");

    private static Task Supervision(HangfirePoolOwner owner) =>
        typeof(HangfirePoolOwner).GetField("_loop", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(owner) as Task
        ?? throw new InvalidOperationException("The actual supervision task has not been published.");

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
        BackgroundJobWorkerPoolRegistration pool)
    {
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            var job = new Job(typeof(HangfireJobWakeup), HangfireInvocationContract.JobMethod,
                [BackgroundJobId.From(Guid.CreateVersion7()), 1L], pool.Queues[0]);
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

    private static void AssertJoined(HangfireGeneration generation)
    {
        generation.Completion.IsCompletedSuccessfully.Should().BeTrue();
        generation.Snapshot.NormalChildrenJoined.Should().BeTrue();
        generation.Snapshot.OuterJoined.Should().BeTrue();
        generation.Snapshot.CleanupComplete.Should().BeTrue();
        generation.Snapshot.Draining.Should().BeTrue();
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
            using var metadata = System.Text.Json.JsonDocument.Parse(rows.GetString(1));
            metadata.RootElement.GetProperty("WorkerCount").GetInt32().Should().Be(pool.Workers);
            metadata.RootElement.GetProperty("Queues").EnumerateArray().Select(queue => queue.GetString()).Should().Equal(pool.Queues);
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
        await using var command = new NpgsqlCommand(
            "SELECT (SELECT count(*) FROM background_jobs) + (SELECT count(*) FROM background_job_attempts)", sql);
        (await command.ExecuteScalarAsync()).Should().Be(0L);
    }

    private static async Task EventuallyAsync(Func<Task<bool>> predicate)
    {
        var elapsed = Stopwatch.StartNew();
        while (!await predicate())
        {
            if (elapsed.Elapsed >= TestWait) throw new TimeoutException("The actual PostgreSQL pool-owner state did not arrive.");
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

    // Only owner/observer timers use this clock; Core workers, guarded provider
    // calls, actual joins and stock transport execution remain real operations.
    internal sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _sync = new();
        private readonly HashSet<ManualTimer> _timers = [];
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_sync) return _ticks; }
        public override DateTimeOffset GetUtcNow() { lock (_sync) return DateTimeOffset.UnixEpoch.AddTicks(_ticks); }
        internal int ActiveTimers { get { lock (_sync) return _timers.Count; } }
        internal bool HasDelay(TimeSpan delay) { lock (_sync) return _timers.Any(timer => timer.Duration == delay); }
        internal int DelayCount(TimeSpan delay) { lock (_sync) return _timers.Count(timer => timer.Duration == delay); }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            period.Should().Be(Timeout.InfiniteTimeSpan);
            lock (_sync)
            {
                var timer = new ManualTimer(this, callback, state, dueTime, _ticks + dueTime.Ticks);
                _timers.Add(timer);
                return timer;
            }
        }

        internal void Advance(TimeSpan duration)
        {
            ManualTimer[] due;
            lock (_sync)
            {
                _ticks += duration.Ticks;
                due = _timers.Where(timer => timer.Due <= _ticks).ToArray();
            }
            foreach (var timer in due) timer.Fire();
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state,
            TimeSpan duration, long due) : ITimer
        {
            private bool _disposed;
            internal TimeSpan Duration { get; private set; } = duration;
            internal long Due { get; private set; } = due;

            internal void Fire()
            {
                lock (owner._sync)
                    if (_disposed || !owner._timers.Remove(this)) return;
                callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                period.Should().Be(Timeout.InfiniteTimeSpan);
                lock (owner._sync)
                {
                    if (_disposed) return false;
                    Duration = dueTime;
                    Due = owner._ticks + dueTime.Ticks;
                    owner._timers.Add(this);
                    return true;
                }
            }

            public void Dispose()
            {
                lock (owner._sync)
                {
                    _disposed = true;
                    owner._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
