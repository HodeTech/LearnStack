using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Hangfire;
using Hangfire.Common;
using Hangfire.PostgreSql;
using Hangfire.Processing;
using Hangfire.Server;
using Hangfire.Storage;

static class BuilderProbe
{
    public static async Task RunAsync(string mode)
    {
        if (mode is not ("builder-before" or "builder-after" or "builder-throw" or "builder-throw-raw"))
            throw new ArgumentException("Unknown builder proof mode.");
        var watch = Stopwatch.StartNew();
        void Emit(object row) => Console.WriteLine(JsonSerializer.Serialize(new { elapsed_ms = watch.ElapsedMilliseconds, mode, row }));
        var state = new BuilderProbeState(mode, Emit);
        var owner = new DrainOwner(state);
        var storage = new BuilderStorage(state, owner);
        var held = new BuilderHeldProcess(state);
        var capture = new CaptureBuilder(state, owner);
        var builders = new List<IBackgroundProcessDispatcherBuilder> { capture,
            new ManagedBuilderGuard(owner, held.UseBackgroundPool(1)) };
        if (mode is "builder-throw" or "builder-throw-raw")
        {
            IBackgroundProcessDispatcherBuilder failing = new ThrowBuilder(state, owner, raw: mode == "builder-throw-raw");
            builders.Add(mode == "builder-throw-raw" ? failing : new ManagedBuilderGuard(owner, failing));
            builders.Add(new ManagedBuilderGuard(owner, new MustNeverStartBuilder(state)));
        }
        var options = new BackgroundProcessingServerOptions
        {
            ServerName = "builder-proof",
            HeartbeatInterval = TimeSpan.FromMilliseconds(25),
            ServerTimeout = TimeSpan.FromMinutes(1),
            ServerCheckInterval = TimeSpan.FromHours(1),
            CancellationCheckInterval = TimeSpan.FromHours(1),
            StopTimeout = TimeSpan.Zero,
            ShutdownTimeout = Timeout.InfiniteTimeSpan,
            RestartDelay = TimeSpan.FromMilliseconds(25),
            ExcludeStorageProcesses = true,
        };
        Emit(new
        {
            core = typeof(BackgroundProcessingServer).Assembly.FullName,
            provider = typeof(PostgreSqlStorage).Assembly.FullName,
            claim = "Actual stock Core with public supplied builders; fake storage; no PostgreSQL qualification"
        });
        var server = new BackgroundProcessingServer(storage, builders, new Dictionary<string, object>(), options);
        owner.PublishServer(server);
        Task? actualJoin = null;
        try
        {
            if (mode == "builder-before")
            {
                await state.CaptureEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await state.WatchdogStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                state.EnableHeartbeatFailure = true;
                await owner.DrainCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Require(!owner.Captured.Task.IsCompleted, "Capture must still be paused after actual SendStop.");
                Require(state.WatchdogLive == 1, "Required stock watchdog must remain inside actual held storage work.");
                Emit(new
                {
                    event_name = "stopped_before_synchronous_capture",
                    captured = owner.Captured.Task.IsCompleted,
                    watchdog_live = state.WatchdogLive,
                    announced = storage.Announced.Count
                });
                state.AllowCapture.Set();
            }
            else
            {
                await state.HeldStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (mode == "builder-after") state.EnableHeartbeatFailure = true;
                await owner.DrainCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            var generation = await owner.Captured.Task.WaitAsync(TimeSpan.FromSeconds(10));
            actualJoin = server.WaitForShutdownAsync(CancellationToken.None);
            if (mode == "builder-throw-raw")
            {
                await actualJoin.WaitAsync(TimeSpan.FromSeconds(10));
                var rawTokens = TokenHandles(generation.Context);
                Require(state.HeldLive == 1, "Raw builder failure must leave actual held work alive.");
                Require(rawTokens.Values.All(value => value == "ObjectDisposedException"), "Raw builder failure must reproduce premature token disposal.");
                Require(!generation.ShutdownWitness.Task.IsCompleted, "Raw builder failure must skip the normal shutdown witness.");
                Emit(new
                {
                    event_name = "raw_builder_failure_abandons_actual_work",
                    held_live = state.HeldLive,
                    actual_join_completed = actualJoin.IsCompleted,
                    token_wait_handles = rawTokens,
                    shutdown_witness = generation.ShutdownWitness.Task.IsCompleted
                });
            }
            else
            {
                using var boundedWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
                var timedOut = false;
                try { await actualJoin.WaitAsync(boundedWait.Token); }
                catch (OperationCanceledException) when (boundedWait.IsCancellationRequested) { timedOut = true; }
                Require(timedOut && !actualJoin.IsCompleted, "Finite caller wait must end without completing the retained actual join.");
                var tokens = TokenHandles(generation.Context);
                Require(tokens.Values.All(value => value == "accessible"), "Core context tokens must remain owned while actual work remains.");
                Require(storage.Announced.Count == 1 && storage.Removed.Count == 0, "No new generation or early server removal is allowed.");
                Require(!generation.ShutdownWitness.Task.IsCompleted, "Shutdown witness must not signal before actual children finish.");
                Require(state.ForbiddenBuilderStarts == 0, "Later supplied builders must not activate after drain.");
                if (mode == "builder-before")
                    Require(state.WatchdogLive == 1 && state.HeldStarts == 0, "Early stop retains stock watchdog and prevents supplied worker activation.");
                else Require(state.HeldLive == 1 && state.HeldStarts == 1, "Exactly one actual held worker must remain live.");
                Emit(new
                {
                    event_name = "owned_generation_after_finite_wait",
                    held_live = state.HeldLive,
                    watchdog_live = state.WatchdogLive,
                    held_starts = state.HeldStarts,
                    token_wait_handles = tokens,
                    actual_join_completed = actualJoin.IsCompleted,
                    announced = storage.Announced.Count,
                    heartbeat_returned = state.HeartbeatReturned.Task.IsCompleted,
                    managed_builder_fault = state.BuilderThrew.Task.IsCompleted
                });
            }
            state.ReleaseActual.Set();
            await actualJoin.WaitAsync(TimeSpan.FromSeconds(10));
            if (mode != "builder-before") await state.HeldFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (mode == "builder-before") await state.WatchdogFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await owner.DrainTask!.WaitAsync(TimeSpan.FromSeconds(10));
            if (mode is "builder-before" or "builder-after")
                await state.HeartbeatReturned.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Require(storage.Announced.Count == 1, "Actual completion must leave exactly one generation.");
            Require(state.HeldLive == 0 && state.WatchdogLive == 0, "All admitted actual work must finish.");
            if (mode != "builder-throw-raw")
            {
                Require(generation.ShutdownWitness.Task.IsCompleted, "Normal child join must signal generation shutdown.");
                Require(generation.LiveAtShutdown == 0, "Shutdown witness must occur only after all actual children finish.");
            }
            Emit(new
            {
                event_name = "actual_cleanup_complete",
                held_live = state.HeldLive,
                watchdog_live = state.WatchdogLive,
                generation_count = storage.Announced.Count,
                actual_join_completed = actualJoin.IsCompleted,
                shutdown_witness = generation.ShutdownWitness.Task.IsCompleted,
                live_at_shutdown = generation.LiveAtShutdown,
                forbidden_builder_starts = state.ForbiddenBuilderStarts
            });
        }
        finally
        {
            state.AllowCapture.Set();
            state.ReleaseActual.Set();
            owner.RequestDrain("final_cleanup");
            await owner.DrainCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            actualJoin ??= server.WaitForShutdownAsync(CancellationToken.None);
            await actualJoin.WaitAsync(TimeSpan.FromSeconds(10));
            if (owner.Captured.Task.IsCompletedSuccessfully)
                owner.Captured.Task.Result.Registration.Dispose();
            foreach (var thread in state.ActualThreads.Values)
                if (!thread.Join(TimeSpan.FromSeconds(10))) throw new InvalidOperationException("Held stock thread did not finish.");
            server.Dispose();
            state.AllowCapture.Dispose();
            state.ReleaseActual.Dispose();
        }
        Emit(new { event_name = "assertions_passed" });
    }

    private static Dictionary<string, string> TokenHandles(BackgroundServerContext context)
    {
        var result = new Dictionary<string, string>();
        foreach (var pair in new[] { ("stopping", context.StoppingToken), ("stopped", context.StoppedToken), ("shutdown", context.ShutdownToken) })
        {
            try { _ = pair.Item2.WaitHandle; result[pair.Item1] = "accessible"; }
            catch (ObjectDisposedException) { result[pair.Item1] = "ObjectDisposedException"; }
        }
        return result;
    }
    internal static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

sealed class BuilderProbeState(string mode, Action<object> emit)
{
    public string Mode { get; } = mode;
    public Action<object> Emit { get; } = emit;
    public readonly ManualResetEventSlim AllowCapture = new(mode != "builder-before");
    public readonly ManualResetEventSlim ReleaseActual = new(false);
    public readonly TaskCompletionSource CaptureEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource WatchdogStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource WatchdogFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource HeldStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource HeldFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource BuilderThrew = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource HeartbeatReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public volatile bool EnableHeartbeatFailure;
    public int HeldStarts, HeldLive, WatchdogLive, ForbiddenBuilderStarts;
    public readonly ConcurrentDictionary<int, Thread> ActualThreads = new();
}
sealed class CapturedGeneration
{
    public BackgroundServerContext Context { get; }
    public readonly TaskCompletionSource ShutdownWitness = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly CancellationTokenRegistration Registration;
    public int LiveAtShutdown = -1;
    public CapturedGeneration(BackgroundServerContext context, BuilderProbeState state)
    {
        Context = context;
        Registration = context.ShutdownToken.Register(() =>
        {
            LiveAtShutdown = Volatile.Read(ref state.HeldLive) + Volatile.Read(ref state.WatchdogLive);
            state.Emit(new { event_name = "generation_shutdown_witness", live = LiveAtShutdown });
            ShutdownWitness.TrySetResult();
        });
    }
}
sealed class DrainOwner(BuilderProbeState state)
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource<BackgroundProcessingServer> _published = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource<CapturedGeneration> Captured = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource DrainCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task? DrainTask { get; private set; }
    public bool IsDraining { get { lock (_gate) return DrainTask is not null; } }
    public void PublishServer(BackgroundProcessingServer server) => _published.SetResult(server);
    public void RequestDrain(string cause)
    {
        lock (_gate)
        {
            if (DrainTask is not null) return;
            DrainTask = Task.Run(async () =>
            {
                var server = await _published.Task.ConfigureAwait(false);
                state.Emit(new { event_name = "request_drain", cause });
                server.SendStop();
                DrainCompleted.TrySetResult();
            });
        }
    }
    public void Capture(BackgroundServerContext context)
    {
        BuilderProbe.Require(Captured.TrySetResult(new CapturedGeneration(context, state)), "Second generation capture refused.");
        state.Emit(new { event_name = "synchronous_generation_capture", stopping = context.StoppingToken.IsCancellationRequested });
    }
    public void HoldHeartbeatFault()
    {
        RequestDrain("heartbeat_fault");
        var generation = Captured.Task.GetAwaiter().GetResult();
        generation.ShutdownWitness.Task.GetAwaiter().GetResult();
        state.HeartbeatReturned.TrySetResult();
    }
}
sealed class CaptureBuilder(BuilderProbeState state, DrainOwner owner) : IBackgroundProcessDispatcherBuilder
{
    public IBackgroundDispatcher Create(BackgroundServerContext context, BackgroundProcessingServerOptions options)
    {
        state.CaptureEntered.TrySetResult();
        state.AllowCapture.Wait(); // Test-only gate: force heartbeat fault before synchronous capture.
        owner.Capture(context);
        return new CompletedDispatcher();
    }
}
sealed class ManagedBuilderGuard(DrainOwner owner, IBackgroundProcessDispatcherBuilder inner) : IBackgroundProcessDispatcherBuilder
{
    public IBackgroundDispatcher Create(BackgroundServerContext context, BackgroundProcessingServerOptions options)
    {
        if (owner.IsDraining || context.StoppingToken.IsCancellationRequested) return new CompletedDispatcher();
        try { return inner.Create(context, options); }
        catch (InvalidOperationException)
        {
            // Concrete probe failure. Production must enumerate its bounded managed-failure contract.
            owner.RequestDrain("managed_supplied_builder_failure");
            return new CompletedDispatcher();
        }
    }
}
sealed class ThrowBuilder(BuilderProbeState state, DrainOwner owner, bool raw) : IBackgroundProcessDispatcherBuilder
{
    public IBackgroundDispatcher Create(BackgroundServerContext context, BackgroundProcessingServerOptions options)
    {
        state.HeldStarted.Task.GetAwaiter().GetResult();
        if (raw)
        {
            owner.RequestDrain("raw_builder_failure_control");
            owner.DrainCompleted.Task.GetAwaiter().GetResult();
        }
        state.BuilderThrew.TrySetResult();
        throw new InvalidOperationException("Controlled managed supplied-builder failure.");
    }
}
sealed class MustNeverStartBuilder(BuilderProbeState state) : IBackgroundProcessDispatcherBuilder
{
    public IBackgroundDispatcher Create(BackgroundServerContext context, BackgroundProcessingServerOptions options)
    {
        Interlocked.Increment(ref state.ForbiddenBuilderStarts);
        throw new InvalidOperationException("Post-failure builder must not start.");
    }
}
sealed class CompletedDispatcher : IBackgroundDispatcher
{
    public bool Wait(TimeSpan timeout) => true;
    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.CompletedTask;
    public void Dispose() { }
}
sealed class BuilderHeldProcess(BuilderProbeState state) : IBackgroundProcess
{
    public void Execute(BackgroundProcessContext context)
    {
        state.ActualThreads.TryAdd(Environment.CurrentManagedThreadId, Thread.CurrentThread);
        Interlocked.Increment(ref state.HeldStarts);
        Interlocked.Increment(ref state.HeldLive);
        state.HeldStarted.TrySetResult();
        state.ReleaseActual.Wait();
        Interlocked.Decrement(ref state.HeldLive);
        state.HeldFinished.TrySetResult();
        context.StoppingToken.ThrowIfCancellationRequested();
    }
}
sealed class BuilderStorage(BuilderProbeState state, DrainOwner owner) : JobStorage
{
    public readonly ConcurrentBag<string> Announced = new();
    public readonly ConcurrentBag<string> Removed = new();
    private int _heartbeatFaulted;
    public override IMonitoringApi GetMonitoringApi() => throw new NotSupportedException();
    public override IStorageConnection GetConnection() => new BuilderConnection(this, state);
    public void Heartbeat()
    {
        if (state.EnableHeartbeatFailure && Interlocked.CompareExchange(ref _heartbeatFaulted, 1, 0) == 0)
        {
            state.Emit(new { event_name = "heartbeat_server_gone", captured = owner.Captured.Task.IsCompleted });
            owner.HoldHeartbeatFault();
            throw new BackgroundServerGoneException();
        }
    }
}
sealed class BuilderConnection(BuilderStorage storage, BuilderProbeState state) : JobStorageConnection
{
    public override void AnnounceServer(string id, ServerContext context) => storage.Announced.Add(id);
    public override void RemoveServer(string id) => storage.Removed.Add(id);
    public override void Heartbeat(string id) => storage.Heartbeat();
    public override int RemoveTimedOutServers(TimeSpan timeout)
    {
        if (state.Mode != "builder-before") return 0;
        state.ActualThreads.TryAdd(Environment.CurrentManagedThreadId, Thread.CurrentThread);
        Interlocked.Increment(ref state.WatchdogLive);
        state.WatchdogStarted.TrySetResult();
        state.ReleaseActual.Wait();
        Interlocked.Decrement(ref state.WatchdogLive);
        state.WatchdogFinished.TrySetResult();
        return 0;
    }
    public override IWriteOnlyTransaction CreateWriteTransaction() => throw new NotSupportedException();
    public override IDisposable AcquireDistributedLock(string key, TimeSpan timeout) => throw new NotSupportedException();
    public override string CreateExpiredJob(Job job, IDictionary<string, string> parameters, DateTime created, TimeSpan expire) => throw new NotSupportedException();
    public override IFetchedJob FetchNextJob(string[] queues, CancellationToken token) => throw new NotSupportedException();
    public override void SetJobParameter(string id, string name, string value) => throw new NotSupportedException();
    public override string GetJobParameter(string id, string name) => throw new NotSupportedException();
    public override JobData GetJobData(string id) => throw new NotSupportedException();
    public override StateData GetStateData(string id) => throw new NotSupportedException();
    public override HashSet<string> GetAllItemsFromSet(string key) => new();
    public override string GetFirstByLowestScoreFromSet(string key, double lower, double upper) => null!;
    public override void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> pairs) => throw new NotSupportedException();
    public override Dictionary<string, string> GetAllEntriesFromHash(string key) => new();
}
