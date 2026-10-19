using System.Collections.Concurrent;
using System.Text.Json;
using Hangfire;
using Hangfire.Common;
using Hangfire.Logging;
using Hangfire.Processing;
using Hangfire.Server;
using Hangfire.Storage;

// Actual pinned Core with fake lifecycle storage; decision support, not production proof.
static class BoundaryProbe
{
    public static async Task RunAsync(string mode)
    {
        LogProvider.SetCurrentLogProvider(new QuietLogs());
        if (mode is not ("announce-raw" or "announce-owned" or "acquire-owned" or "dispose-owned" or "normal-raw" or "normal-owned" or "builder-owned" or "drain-infinite" or "drain-finite" or "remove-owned" or "remove-acquire-owned" or "remove-dispose-owned"))
            throw new ArgumentException("unknown mode");
        var state = new State(mode);
        var owner = new Owner(state);
        var storage = new Facade(state, owner);
        var builders = new List<IBackgroundProcessDispatcherBuilder> { new Capture(state), new Guard(state, owner, new Held(state).UseBackgroundPool(1)) };
        if (mode == "builder-owned")
        {
            builders.Add(new Guard(state, owner, new Failing(state)));
            builders.Add(new Guard(state, owner, new Forbidden(state)));
        }
        var options = new BackgroundProcessingServerOptions
        {
            ExcludeStorageProcesses = true,
            HeartbeatInterval = TimeSpan.FromMilliseconds(20),
            ServerCheckInterval = TimeSpan.FromHours(1),
            CancellationCheckInterval = TimeSpan.FromHours(1),
            StopTimeout = mode == "drain-finite" ? TimeSpan.FromMilliseconds(20) : Timeout.InfiniteTimeSpan,
            ShutdownTimeout = Timeout.InfiniteTimeSpan,
            LastChanceTimeout = TimeSpan.Zero,
            RestartDelay = TimeSpan.FromMilliseconds(20)
        };
        var server = new BackgroundProcessingServer(storage, builders, new Dictionary<string, object>(), options);
        Task? join = null;
        try
        {
            if (state.AnnounceFault && state.Owned)
            {
                // Deliberately publish owner handle after Core's fault. No sentinel/context yet.
                await state.FaultEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(100);
                Require(state.AnnounceAttempts == 1 && state.Captures == 0 && !owner.StopCompleted.Task.IsCompleted,
                    "Pre-publication fault must stay contained without context or repeat.");
                state.Emit("fault_before_publication_held", new { state.AnnounceAttempts, state.Captures });
            }
            owner.Publish(server);
            if (state.AnnounceFault)
            {
                if (state.Owned)
                {
                    await owner.StopCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    join = server.WaitForShutdownAsync(CancellationToken.None);
                    await join.WaitAsync(TimeSpan.FromSeconds(5));
                    Require(state.AnnounceAttempts == 1 && state.Captures == 0, "No reannouncement or builder after contained startup fault.");
                }
                else
                {
                    await state.SecondAnnounce.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Require(state.AnnounceAttempts >= 2 && state.Captures == 0, "Raw announce fault must auto-retry before capture.");
                }
                state.Emit("announce_result", new { state.AnnounceAttempts, state.Captures, stopped = owner.StopCompleted.Task.IsCompleted });
            }
            else if (state.NormalReturn)
            {
                if (state.Owned)
                {
                    await owner.StopCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    join = server.WaitForShutdownAsync(CancellationToken.None);
                    await join.WaitAsync(TimeSpan.FromSeconds(5));
                    Require(state.AnnounceAttempts == 1 && state.Removes == 1, "Remove fence must suppress normal outer repeat.");
                    Require(state.RemoveFaults == (mode.StartsWith("remove-", StringComparison.Ordinal) ? 1 : 0), "Removal fault premise must execute exactly once.");
                }
                else
                {
                    await state.SecondAnnounce.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Require(state.AnnounceAttempts >= 2 && state.Removes >= 1, "Raw normal return must repeat.");
                }
                state.Emit("normal_return_result", new { state.AnnounceAttempts, state.Removes, state.Captures, state.RemoveFaults });
            }
            else
            {
                await state.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (mode == "builder-owned")
                {
                    await state.BuilderFaultCaught.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Require(state.BuilderFaults == 1, "Managed failing builder must actually throw and be caught exactly once.");
                }
                else owner.RequestStop();
                await owner.StopCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                join = server.WaitForShutdownAsync(CancellationToken.None);
                await Task.Delay(150);
                Require(!join.IsCompleted && state.Live == 1 && state.AnnounceAttempts == 1, "Local drain must retain actual work.");
                var context = state.Context!;
                foreach (var token in new[] { context.StoppingToken, context.StoppedToken, context.ShutdownToken }) _ = token.WaitHandle;
                Require(context.StoppingToken.IsCancellationRequested && !context.ShutdownToken.IsCancellationRequested, "Stopping must not force shutdown.");
                Require(context.StoppedToken.IsCancellationRequested == (mode == "drain-finite"), "StopTimeout behavior differs from expectation.");
                Require(!state.PrivateCancellation.IsCancellationRequested, "Pool-local drain cannot cancel private running work.");
                Require(state.ForbiddenStarts == 0, "No supplied starts after managed fault.");
                state.Emit("local_drain_retained", new
                {
                    state.Live,
                    stopped = context.StoppedToken.IsCancellationRequested,
                    private_cancelled = state.PrivateCancellation.IsCancellationRequested,
                    state.ForbiddenStarts,
                    state.BuilderFaults,
                    join_completed = join.IsCompleted
                });
                if (mode.StartsWith("drain-", StringComparison.Ordinal))
                {
                    // Host signal owns its own relay; never linked to Core StoppingToken.
                    owner.HostStop();
                    await state.CallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Require(owner.HostRelay is { IsCompleted: false } && state.ActualCancellation is { IsCompleted: false }, "Retain actual async cancellation and relay.");
                    Require(state.PrivateCancellation.IsCancellationRequested && !join.IsCompleted, "Host cancellation is separate from actual join.");
                    state.Emit("host_relay_retained", new
                    {
                        private_cancelled = true,
                        core_stop_completed = owner.StopCompleted.Task.IsCompleted,
                        cancellation_completed = state.ActualCancellation!.IsCompleted,
                        relay_completed = owner.HostRelay!.IsCompleted
                    });
                    state.ReleaseCallback.Set();
                    await owner.HostRelay.WaitAsync(TimeSpan.FromSeconds(5));
                }
                state.Release.Set();
                await join.WaitAsync(TimeSpan.FromSeconds(5));
                Require(state.Live == 0 && state.AnnounceAttempts == 1, "Cleanup must leave no actual work or replacement.");
            }
        }
        finally
        {
            owner.Publish(server);
            state.Release.Set();
            state.ReleaseCallback.Set();
            owner.RequestStop();
            await owner.StopCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            join ??= server.WaitForShutdownAsync(CancellationToken.None);
            await join.WaitAsync(TimeSpan.FromSeconds(5));
            if (owner.HostRelay is not null) await owner.HostRelay.WaitAsync(TimeSpan.FromSeconds(5));
            await owner.StopOperation!.WaitAsync(TimeSpan.FromSeconds(5));
            foreach (var thread in state.ActualThreads.Values)
                if (!thread.Join(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Actual process thread did not join.");
            server.Dispose();
            state.PrivateRegistration.Dispose();
            state.PrivateCancellation.Dispose();
            state.Release.Dispose();
            state.ReleaseCallback.Dispose();
        }
        state.Emit("assertions_passed", new { state.AnnounceAttempts, state.Removes, state.Live });

        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }

    sealed class State
    {
        public State(string mode)
        {
            Mode = mode;
            PrivateRegistration = PrivateCancellation.Token.Register(() => { CallbackEntered.TrySetResult(); ReleaseCallback.Wait(); });
        }
        public string Mode { get; }
        public bool Owned => !Mode.EndsWith("raw", StringComparison.Ordinal);
        public bool NormalReturn => Mode.StartsWith("normal-", StringComparison.Ordinal) || Mode.StartsWith("remove-", StringComparison.Ordinal);
        public readonly ConcurrentDictionary<int, Thread> ActualThreads = new();
        public readonly TaskCompletionSource BuilderFaultCaught = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool AnnounceFault => Mode.StartsWith("announce-", StringComparison.Ordinal) || Mode is "acquire-owned" or "dispose-owned";
        public readonly TaskCompletionSource FaultEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource SecondAnnounce = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource CallbackEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim Release = new(false), ReleaseCallback = new(false);
        public readonly CancellationTokenSource PrivateCancellation = new();
        public readonly CancellationTokenRegistration PrivateRegistration;
        public BackgroundServerContext? Context;
        public Task? ActualCancellation;
        public int AnnounceAttempts, Captures, Removes, Live, ForbiddenStarts, HeartbeatFaults, BuilderFaults, RemoveFaults;
        public void Emit(string name, object values) => Console.WriteLine(JsonSerializer.Serialize(new { mode = Mode, event_name = name, values }));
    }

    sealed class Owner(State state)
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource<BackgroundProcessingServer> _published = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _host = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource StopCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? StopOperation, HostRelay;
        public bool Draining { get { lock (_gate) return StopOperation is not null; } }
        public void Publish(BackgroundProcessingServer server) => _published.TrySetResult(server);
        public void RequestStop()
        {
            lock (_gate)
            {
                StopOperation ??= Task.Run(async () =>
                {
                    try { var server = await _published.Task.ConfigureAwait(false); server.SendStop(); StopCompleted.TrySetResult(); }
                    catch (Exception ex) { StopCompleted.TrySetException(ex); throw; }
                });
            }
        }
        public void Fence() { RequestStop(); StopCompleted.Task.GetAwaiter().GetResult(); }
        public void HostStop()
        {
            RequestStop();
            lock (_gate)
            {
                HostRelay ??= Task.Run(async () =>
                {
                    await _host.Task.ConfigureAwait(false);
                    state.ActualCancellation = state.PrivateCancellation.CancelAsync();
                    await state.ActualCancellation.ConfigureAwait(false);
                });
            }
            _host.TrySetResult();
        }
    }

    sealed class Capture(State state) : IBackgroundProcessDispatcherBuilder
    {
        public IBackgroundDispatcher Create(BackgroundServerContext context, BackgroundProcessingServerOptions options)
        {
            Interlocked.Increment(ref state.Captures); state.Context = context; return new Done();
        }
    }
    sealed class Guard(State state, Owner owner, IBackgroundProcessDispatcherBuilder inner) : IBackgroundProcessDispatcherBuilder
    {
        public IBackgroundDispatcher Create(BackgroundServerContext context, BackgroundProcessingServerOptions options)
        {
            if (owner.Draining || context.StoppingToken.IsCancellationRequested) return new Done();
            try { return inner.Create(context, options); }
            // Exact pinned Core exclusion shape, ExceptionTypeHelper.cs:23-34. This
            // fake failure creates no partial resources; it proves the broader filter,
            // never arbitrary partial construction cleanup.
            catch (Exception ex) when (ex.GetType() != typeof(OutOfMemoryException) && ex.GetType() != typeof(StackOverflowException))
            {
                owner.RequestStop();
                Interlocked.Increment(ref state.BuilderFaults);
                state.BuilderFaultCaught.TrySetResult();
                return new Done();
            }
        }
    }
    sealed class Failing(State state) : IBackgroundProcessDispatcherBuilder
    {
        public IBackgroundDispatcher Create(BackgroundServerContext context, BackgroundProcessingServerOptions options)
        { state.Started.Task.GetAwaiter().GetResult(); throw new ManagedProbeFailure(); }
    }
    sealed class Forbidden(State state) : IBackgroundProcessDispatcherBuilder
    {
        public IBackgroundDispatcher Create(BackgroundServerContext context, BackgroundProcessingServerOptions options)
        { Interlocked.Increment(ref state.ForbiddenStarts); throw new ManagedProbeFailure(); }
    }
    sealed class Done : IBackgroundDispatcher
    {
        public bool Wait(TimeSpan timeout) => true;
        public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }
    }
    sealed class Held(State state) : IBackgroundProcess
    {
        public void Execute(BackgroundProcessContext context)
        {
            state.ActualThreads.TryAdd(Environment.CurrentManagedThreadId, Thread.CurrentThread);
            Interlocked.Increment(ref state.Live); state.Started.TrySetResult();
            if (state.NormalReturn) context.StoppingToken.WaitHandle.WaitOne();
            else state.Release.Wait();
            Interlocked.Decrement(ref state.Live);
            context.StoppingToken.ThrowIfCancellationRequested();
        }
    }
    sealed class ManagedProbeFailure : Exception { }

    // GetConnection returns an inert public facade. Lifecycle method encloses actual
    // provider acquisition + method + disposal, so pre-method acquisition cannot escape.
    sealed class Facade(State state, Owner owner) : JobStorage
    {
        public override IMonitoringApi GetMonitoringApi() => throw new NotSupportedException();
        public override IStorageConnection GetConnection() => new Connection(state, owner);
    }
    sealed class Connection(State state, Owner owner) : JobStorageConnection
    {
        public override void AnnounceServer(string id, ServerContext context)
        {
            var attempt = Interlocked.Increment(ref state.AnnounceAttempts);
            if (attempt == 2) state.SecondAnnounce.TrySetResult();
            try
            {
                using var provider = Acquire();
                if (state.Mode.StartsWith("announce-", StringComparison.Ordinal)) throw new ManagedProbeFailure();
            }
            catch (ManagedProbeFailure)
            {
                state.FaultEntered.TrySetResult();
                if (state.Owned) owner.Fence();
                throw;
            }
        }
        private IDisposable Acquire()
        {
            if (state.Mode == "acquire-owned") throw new ManagedProbeFailure();
            return new ProviderLease(state.Mode == "dispose-owned");
        }
        public override void RemoveServer(string id)
        {
            // Fence before provider acquisition, not only before the RemoveServer call.
            if (state.Owned) owner.Fence();
            Interlocked.Increment(ref state.Removes);
            try
            {
                if (state.Mode == "remove-acquire-owned") throw new ManagedProbeFailure();
                using var provider = new ProviderLease(state.Mode == "remove-dispose-owned");
                if (state.Mode == "remove-owned") throw new ManagedProbeFailure();
            }
            catch (ManagedProbeFailure) { Interlocked.Increment(ref state.RemoveFaults); throw; }
        }
        public override void Heartbeat(string id)
        {
            // Deliberately uncontained in normal-return pair to drive stock restartCts.
            // It tests outer normal repetition/fence only, not supported heartbeat containment.
            if (state.NormalReturn && state.Started.Task.IsCompleted
                && Interlocked.CompareExchange(ref state.HeartbeatFaults, 1, 0) == 0) throw new BackgroundServerGoneException();
        }
        public override void Dispose() { } // no deferred provider disposal outside method fence
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
    sealed class ProviderLease(bool failDispose) : IDisposable
    {
        public void Dispose() { if (failDispose) throw new ManagedProbeFailure(); }
    }
    sealed class QuietLogs : ILogProvider
    {
        public ILog GetLogger(string name) => new QuietLog();
        private sealed class QuietLog : ILog
        { public bool Log(LogLevel level, Func<string> message, Exception? exception = null) => message is null; }
    }
}
