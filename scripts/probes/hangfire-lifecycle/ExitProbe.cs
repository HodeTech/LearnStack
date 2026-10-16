using System.Diagnostics;
using System.Text.Json;
using Hangfire;
using Hangfire.Common;
using Hangfire.Logging;
using Hangfire.Processing;
using Hangfire.Server;
using Hangfire.Storage;

// Decision support only: actual pinned Core, fake storage, no production owner or SQL proof.
public static class ExitProbe
{
    public static async Task RunAsync(string mode)
    {
        if (mode is not ("exit-direct-callback" or "exit-owned-relay"))
            throw new ArgumentException("Unknown process-exit proof mode.");
        LogProvider.SetCurrentLogProvider(new ExitSilentLogProvider());
        var state = new ExitState(mode);
        var capture = new ExitCapture(state);
        var process = new ExitHeldProcess(state);
        var storage = new ExitStorage();
        var options = new BackgroundProcessingServerOptions
        {
            ServerName = "exit-proof",
            HeartbeatInterval = TimeSpan.FromHours(1),
            ServerCheckInterval = TimeSpan.FromHours(1),
            CancellationCheckInterval = TimeSpan.FromHours(1),
            StopTimeout = TimeSpan.Zero,
            ShutdownTimeout = Timeout.InfiniteTimeSpan,
            LastChanceTimeout = TimeSpan.Zero,
            ExcludeStorageProcesses = true,
        };
        state.Emit("starting", new
        {
            core = typeof(BackgroundProcessingServer).Assembly.FullName,
            claim = "Actual Core process-exit behavior; fake storage; no cleanup or PostgreSQL qualification"
        });
        state.Server = new BackgroundProcessingServer(storage,
            new IBackgroundProcessDispatcherBuilder[] { capture, process.UseBackgroundPool(1) },
            new Dictionary<string, object>(), options);

        // Test-only observer comes after Core's handler. It proves the positive
        // exited through Core's forced shutdown path; it grants no cleanup witness.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ObserveExit(state);
        await state.Captured.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await state.ProcessStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Require(storage.Announced == 1 && Volatile.Read(ref state.ProcessLive) == 1,
            "One actual generation must be running before the host stop.");
        state.Server.SendStop();
        if (state.IsRelay)
        {
            await state.ModuleCallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await state.CancellationPublished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Require(state.ModuleCancellation is not null && !state.ModuleCancellation.IsCompleted,
                "The actual private cancellation task must remain retained.");
            Require(state.Relay is not null && !state.Relay.IsCompleted,
                "The owned relay must remain live while the module callback is held.");
            Require(Volatile.Read(ref state.ModuleCallbackLive) == 1,
                "The actual private callback must remain held.");
        }
        state.ActualJoin = state.Server.WaitForShutdownAsync(CancellationToken.None);
        var timedOut = false;
        try { await state.ActualJoin.WaitAsync(TimeSpan.FromMilliseconds(150)); }
        catch (TimeoutException) { timedOut = true; }
        Require(timedOut && !state.ActualJoin.IsCompleted,
            "The finite caller wait must end while the actual stock operation remains held.");
        Require(!state.ShutdownSignal.Task.IsCompleted,
            "Infinite normal shutdown must not have signaled the captured shutdown token.");
        foreach (var token in new[] { state.Context!.StoppingToken, state.Context.StoppedToken, state.Context.ShutdownToken })
            _ = token.WaitHandle;
        state.Emit("finite_host_wait_ended", new
        {
            stock_process_live = Volatile.Read(ref state.ProcessLive),
            actual_join_completed = state.ActualJoin.IsCompleted,
            module_callback_live = Volatile.Read(ref state.ModuleCallbackLive),
            relay_completed = state.Relay?.IsCompleted,
            shutdown_signaled = state.ShutdownSignal.Task.IsCompleted,
            context_handles_accessible = true,
        });
        state.Emit("main_returning_unresolved", new
        {
            no_cleanup_claim = true,
            no_quiescence_claim = true,
            expected_process_exit = state.IsRelay,
        });
        // Intentionally retain all unresolved resources. The child-process owner
        // terminates/joins the dirty control; the positive ends through ProcessExit.
    }

    private static void ObserveExit(ExitState state)
    {
        if (!state.IsRelay)
        {
            Environment.ExitCode = 1;
            state.Emit("assertion_failed", new { reason = "unsafe_callback_did_not_hold_core_exit" });
            return;
        }
        var correct = state.ShutdownSignal.Task.IsCompleted
            && Volatile.Read(ref state.ModuleCallbackLive) == 1
            && Volatile.Read(ref state.ProcessLive) == 1
            && state.ModuleCancellation is { IsCompleted: false }
            && state.Relay is { IsCompleted: false };
        if (!correct) Environment.ExitCode = 1;
        state.Emit(correct ? "process_exit_observed" : "assertion_failed", new
        {
            core_shutdown_signaled = state.ShutdownSignal.Task.IsCompleted,
            stock_process_live = Volatile.Read(ref state.ProcessLive),
            module_callback_live = Volatile.Read(ref state.ModuleCallbackLive),
            actual_join_completed = state.ActualJoin?.IsCompleted,
            process_exit_is_not_cleanup = true,
        });
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ExitState(string mode)
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        public bool IsRelay => mode == "exit-owned-relay";
        public readonly TaskCompletionSource Captured = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ProcessStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource StopSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ShutdownSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource CancellationPublished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ModuleCallbackEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim HoldActual = new(false);
        public readonly ManualResetEventSlim HoldCallback = new(false);
        public readonly CancellationTokenSource ModuleCancellationSource = new();
        public readonly List<CancellationTokenRegistration> Registrations = new();
        public BackgroundProcessingServer? Server;
        public BackgroundServerContext? Context;
        public Task? Relay, ModuleCancellation, ActualJoin;
        public int ProcessLive, ModuleCallbackLive;
        public void Emit(string name, object values) => Console.WriteLine(JsonSerializer.Serialize(new
        {
            elapsed_ms = _watch.ElapsedMilliseconds,
            mode,
            event_name = name,
            values,
        }));
    }

    private sealed class ExitCapture(ExitState state) : IBackgroundProcessDispatcherBuilder
    {
        public IBackgroundDispatcher Create(BackgroundServerContext context, BackgroundProcessingServerOptions options)
        {
            state.Context = context;
            if (state.IsRelay)
            {
                state.Registrations.Add(state.ModuleCancellationSource.Token.Register(() =>
                {
                    Interlocked.Increment(ref state.ModuleCallbackLive);
                    state.Emit("private_module_callback_held", new { background_thread = Thread.CurrentThread.IsBackground });
                    state.ModuleCallbackEntered.TrySetResult();
                    state.HoldCallback.Wait();
                    Interlocked.Decrement(ref state.ModuleCallbackLive);
                }));
                state.Relay = Task.Run(async () =>
                {
                    await state.StopSignal.Task.ConfigureAwait(false);
                    var actualCancellation = state.ModuleCancellationSource.CancelAsync();
                    state.ModuleCancellation = actualCancellation;
                    state.CancellationPublished.TrySetResult();
                    await actualCancellation.ConfigureAwait(false);
                });
                // The only callbacks reachable synchronously from Core are closed
                // asynchronous-completion signals. No module callback or linked CTS.
                state.Registrations.Add(context.StoppingToken.Register(() => state.StopSignal.TrySetResult()));
                state.Registrations.Add(context.ShutdownToken.Register(() =>
                {
                    state.ShutdownSignal.TrySetResult();
                    state.StopSignal.TrySetResult();
                }));
            }
            else
            {
                // Deliberately unsafe dirty control: Core itself executes the hold.
                state.Registrations.Add(context.ShutdownToken.Register(() =>
                {
                    state.ShutdownSignal.TrySetResult();
                    state.Emit("unsafe_core_shutdown_callback_entered", new { direct_callback = true });
                    state.HoldCallback.Wait();
                }));
            }
            state.Captured.TrySetResult();
            return new ExitCompletedDispatcher();
        }
    }

    private sealed class ExitCompletedDispatcher : IBackgroundDispatcher
    {
        public bool Wait(TimeSpan timeout) => true;
        public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class ExitHeldProcess(ExitState state) : IBackgroundProcess
    {
        public void Execute(BackgroundProcessContext context)
        {
            Interlocked.Increment(ref state.ProcessLive);
            state.ProcessStarted.TrySetResult();
            state.HoldActual.Wait();
            Interlocked.Decrement(ref state.ProcessLive);
        }
    }

    private sealed class ExitStorage : JobStorage
    {
        public int Announced;
        public override IMonitoringApi GetMonitoringApi() => throw new NotSupportedException();
        public override IStorageConnection GetConnection() => new ExitConnection(this);
    }

    private sealed class ExitConnection(ExitStorage storage) : JobStorageConnection
    {
        public override void AnnounceServer(string id, ServerContext context) => Interlocked.Increment(ref storage.Announced);
        public override void RemoveServer(string id) { }
        public override void Heartbeat(string id) { }
        public override int RemoveTimedOutServers(TimeSpan timeout) => 0;
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

    private sealed class ExitSilentLogProvider : ILogProvider
    {
        public ILog GetLogger(string name) => new ExitSilentLog();
        private sealed class ExitSilentLog : ILog
        {
            public bool Log(LogLevel level, Func<string> message, Exception? exception = null) => message is null;
        }
    }
}
