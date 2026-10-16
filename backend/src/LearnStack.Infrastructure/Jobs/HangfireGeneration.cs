using System.Globalization;
using Hangfire;
using Hangfire.Client;
using Hangfire.PostgreSql;
using Hangfire.Processing;
using Hangfire.Server;
using Hangfire.States;
using Hangfire.Storage;
using Npgsql;

namespace LearnStack.Infrastructure.Jobs;

internal enum HangfireGenerationReason
{
    None, HostStop, Announcement, HeartbeatMissing, HeartbeatTransient,
    HeartbeatConfiguration, StartupStalled, BuilderFailure, Removed, CleanupFault,
}

internal sealed record HangfireGenerationSnapshot(bool HandlePublished, bool Captured,
    bool WaitEntered, bool NormalChildrenJoined, bool OuterJoined, bool CleanupComplete,
    bool Draining, bool HeartbeatFault, HangfireGenerationReason Reason);

/// <summary>Owns one actual Core generation; caller deadlines never dispose its work.</summary>
internal sealed class HangfireGeneration : PoolHangfireLifecycle
{
    internal static readonly TimeSpan HeartbeatCutoff = TimeSpan.FromMinutes(4);
    internal static readonly TimeSpan HeartbeatPacing = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan ReplacementBackoff = TimeSpan.FromSeconds(5);
    private readonly BackgroundJobRuntimeGate _gate;
    private readonly BackgroundJobRuntimeGate.PoolBoundary _boundary;
    private readonly BackgroundJobWorkerPoolRegistration _pool;
    private readonly PoolHangfireStorage _storage;
    private readonly IBackgroundProcessDispatcherBuilder[] _builders;
    private readonly BackgroundProcessingServerOptions _options;
    private readonly BackgroundJobRuntimeOptions _bounds;
    private readonly TimeProvider _time;
    private readonly HangfireGenerationWork _work;
    private readonly Thread _constructor;
    private readonly TaskCompletionSource<BackgroundProcessingServer?> _published = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _normalJoin = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _waitEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _constructorReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _neverConstructed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _startAssigned = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _stopRelay;
    private CancellationTokenRegistration _shutdownRegistration;
    private PoolHangfireLifecycleOperation? _retainedAnnounce, _retainedHeartbeat, _retainedRemove;
    private Task? _startupObserver;
    private Task? _startInvocation;
    private Task? _actualJoin;
    private Task? _ownerCompletion;
    private BackgroundServerContext? _context;
    private long _lastHeartbeat;
    private int _started, _announced, _confirmed, _preChildRefusal, _draining;
    private int _childrenJoined, _outerJoined, _cleanupComplete, _heartbeatFault, _cleanupFault, _removed;
    private int _reason;

    internal HangfireGeneration(PostgreSqlStorage provider, HangfireComponentInventory components,
        HangfireRuntimeFilters filters, JobActivator activator,
        BackgroundJobWorkerPoolRegistration pool, BackgroundJobRuntimeGate gate,
        BackgroundJobRuntimeOptions bounds, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(activator);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(bounds);
        ArgumentNullException.ThrowIfNull(timeProvider);
        bounds.Validate();
        if (!components.BelongsTo(provider)) throw new HangfireStorageRefusedException();
        _gate = gate;
        _boundary = gate.ForPool(pool);
        _pool = pool;
        _bounds = bounds;
        _time = timeProvider;
        _work = new(pool, gate);
        _storage = new PoolHangfireStorage(provider, this, components.Selected);
        _options = CreateOptions(pool);
        var factory = new BackgroundJobFactory(filters);
        var performer = new BackgroundJobPerformer(filters,
            HangfireInvocationContract.BindGeneration(activator, this), TaskScheduler.Default);
        var changer = new BackgroundJobStateChanger(filters);
        var builders = new List<IBackgroundProcessDispatcherBuilder> { new CaptureBuilder(this) };
        for (var index = 0; index < pool.Workers; index++)
            builders.Add(Builder(new Worker(pool.Queues, performer, changer)));
        if (pool.Kind == BackgroundJobWorkerPoolKind.Control)
        {
            builders.Add(Builder(new DelayedJobScheduler(TimeSpan.FromSeconds(15), changer)
            {
                TaskScheduler = TaskScheduler.Default,
                MaxDegreeOfParallelism = 1,
            }));
            builders.Add(Builder(new RecurringJobScheduler(factory, TimeSpan.FromSeconds(15), new DefaultTimeZoneResolver())
            {
                TaskScheduler = TaskScheduler.Default,
                MaxDegreeOfParallelism = 1,
            }));
        }
        builders.Add(new WaitEntryBuilder(this));
        _builders = builders.ToArray();
        _constructor = new Thread(Construct) { IsBackground = true, Name = "LearnStack Hangfire construction" };
        gate.EnsureFinalExitRegistered();
        _stopRelay = RelayStopAsync();
    }

    internal Task<bool> Ready => _ready.Task;
    internal Task Completion => _completion.Task;
    // The current framework invocation is short work; its external wait reuses
    // the validated transactional bound while each SQL operation keeps its own.
    internal TimeSpan InvocationWait => _bounds.TransactionTimeout;
    internal TimeProvider InvocationTime => _time;
    internal HangfireGenerationWorkSnapshot WorkSnapshot => _work.Snapshot;
    internal bool TryAdmitInvocation() => _ready.Task.IsCompletedSuccessfully && _ready.Task.Result && _work.CanStart && _gate.TryAdmit()
        && Interlocked.CompareExchange(ref _draining, 0, 0) == 0;
    internal HangfireGenerationWork.Invocation? TryBeginWork() => TryAdmitInvocation() ? _work.TryBegin() : null;
    internal HangfireGenerationSnapshot Snapshot => new(
        _published.Task.IsCompletedSuccessfully && _published.Task.Result is not null,
        Volatile.Read(ref _context) is not null, _waitEntered.Task.IsCompleted,
        HasNormalChildJoin, Volatile.Read(ref _outerJoined) != 0,
        Volatile.Read(ref _cleanupComplete) != 0, Volatile.Read(ref _draining) != 0,
        Volatile.Read(ref _heartbeatFault) != 0, (HangfireGenerationReason)Volatile.Read(ref _reason));

    internal void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("A generation starts once.");
        _startupObserver = ObserveStartupAsync();
        try
        {
            if (ExecutionContext.IsFlowSuppressed()) _startInvocation = Task.Run(StartConstructor);
            else { using (ExecutionContext.SuppressFlow()) _startInvocation = Task.Run(StartConstructor); }
        }
        catch (Exception failure) when (Catchable(failure))
        {
            Volatile.Write(ref _cleanupFault, 1);
            RequestDrain(HangfireGenerationReason.BuilderFailure);
        }
        finally { _startAssigned.TrySetResult(); }
    }

    private void StartConstructor()
    {
        if (!_gate.TryAdmit() || Volatile.Read(ref _draining) != 0)
        {
            CompleteWithoutCore();
            _constructorReturned.TrySetResult();
            return;
        }
        try { _constructor.Start(); }
        catch (Exception failure) when (Catchable(failure))
        {
            RequestDrain(HangfireGenerationReason.BuilderFailure);
            // No Core constructor was entered when this thread remains unstarted.
            // An uncertain native-start outcome stays retained rather than guessed joined.
            if ((_constructor.ThreadState & ThreadState.Unstarted) != 0)
            {
                CompleteWithoutCore();
                _constructorReturned.TrySetResult();
            }
        }
    }

    internal void RequestDrain(HangfireGenerationReason reason)
    {
        _work.CloseStarts();
        if (Interlocked.Exchange(ref _draining, 1) == 0)
            Interlocked.CompareExchange(ref _reason, (int)reason, 0);
        _ready.TrySetResult(false);
        _stopRequested.TrySetResult();
        if (Interlocked.CompareExchange(ref _started, 1, 0) == 0)
        {
            // Stop won before any startup operation was issued. This is a
            // positive no-Core state, not a timeout-derived cleanup claim.
            _startAssigned.TrySetResult();
            _constructorReturned.TrySetResult();
            CompleteWithoutCore();
        }
    }

    internal async Task StopAsync(CancellationToken callerBudget)
    {
        RequestHostStop();
        await Completion.WaitAsync(callerBudget).ConfigureAwait(false);
    }

    internal void RequestHostStop()
    {
        RequestDrain(HangfireGenerationReason.HostStop);
        _work.RequestHostStop();
    }

    internal override void AnnounceServer(PoolHangfireLifecycleOperation operation)
    {
        if (Interlocked.Increment(ref _announced) != 1)
        {
            ObserveUnwind();
            RequestDrain(HangfireGenerationReason.Announcement);
            WaitStopFence();
            throw Refused();
        }
        if (!_gate.TryAdmit() || Volatile.Read(ref _draining) != 0)
        {
            Volatile.Write(ref _preChildRefusal, 1);
            RequestDrain(HangfireGenerationReason.Announcement);
            WaitStopFence();
            throw Refused();
        }
        var result = operation.ExecuteAttempt();
        if (!result.Succeeded)
        {
            Volatile.Write(ref _preChildRefusal, 1);
            RetainDisposal(operation);
            RequestDrain(HangfireGenerationReason.Announcement);
            WaitStopFence();
            throw Refused();
        }
        try { Interlocked.Exchange(ref _lastHeartbeat, _time.GetTimestamp()); }
        catch (Exception failure) when (Catchable(failure))
        {
            Volatile.Write(ref _preChildRefusal, 1);
            RequestDrain(HangfireGenerationReason.Announcement);
            WaitStopFence();
            throw Refused();
        }
        Volatile.Write(ref _confirmed, 1);
    }

    internal override void Heartbeat(PoolHangfireLifecycleOperation operation)
    {
        try
        {
            if (TryHeartbeat(operation)) return;
        }
        catch (Exception failure) when (Catchable(failure))
        {
            // Timer/clock/owner faults must be contained just like provider
            // faults: none may reach Core's independent restart timer.
            Volatile.Write(ref _heartbeatFault, 1);
            RetainDisposal(operation);
            RequestDrain(HangfireGenerationReason.HeartbeatConfiguration);
        }
        RequestDrain(HangfireGenerationReason.HostStop);
        // Returning a synthetic success, or throwing before the real child join,
        // would re-enable Core's independent heartbeat restart path.
        Task.WhenAny(_normalJoin.Task, _gate.FinalExitSignal).GetAwaiter().GetResult();
        throw Refused();
    }

    private bool TryHeartbeat(PoolHangfireLifecycleOperation operation)
    {
        while (Volatile.Read(ref _draining) == 0 && _gate.IsOpen)
        {
            if (_time.GetElapsedTime(Interlocked.Read(ref _lastHeartbeat)) >= HeartbeatCutoff)
            {
                RequestDrain(HangfireGenerationReason.HeartbeatTransient);
                break;
            }
            if (!_gate.TryAdmit()) break;
            var result = operation.ExecuteAttempt();
            if (result.Succeeded)
            {
                Interlocked.Exchange(ref _lastHeartbeat, _time.GetTimestamp());
                Volatile.Write(ref _heartbeatFault, 0);
                return true;
            }
            Volatile.Write(ref _heartbeatFault, 1);
            RetainDisposal(operation);
            if (result.DisposalFailure is not null || !Transient(result.Failure))
            {
                RequestDrain(result.Failure is BackgroundServerGoneException
                    ? HangfireGenerationReason.HeartbeatMissing : HangfireGenerationReason.HeartbeatConfiguration);
                break;
            }
            Task.WhenAny(Task.Delay(HeartbeatPacing, _time), _stopRequested.Task).GetAwaiter().GetResult();
        }
        return false;
    }

    internal override void RemoveServer(PoolHangfireLifecycleOperation operation)
    {
        ObserveUnwind();
        RequestDrain(HangfireGenerationReason.Removed);
        WaitStopFence();
        if (_gate.State is BackgroundJobProcessState.Fatal or BackgroundJobProcessState.FinalExit) throw Refused();
        var result = operation.ExecuteAttempt();
        if (result.Succeeded)
        {
            Volatile.Write(ref _removed, 1);
            return;
        }
        RetainDisposal(operation);
        Volatile.Write(ref _cleanupFault, 1);
        Interlocked.Exchange(ref _reason, (int)HangfireGenerationReason.CleanupFault);
        throw Refused();
    }

    private void Construct()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        if (!_gate.TryAdmit() || Volatile.Read(ref _draining) != 0)
        {
            CompleteWithoutCore();
            _constructorReturned.TrySetResult();
            return;
        }
        BackgroundProcessingServer server;
        try
        {
            var properties = new Dictionary<string, object>
            {
                ["Queues"] = _pool.Queues.ToArray(),
                ["WorkerCount"] = _pool.Workers,
            };
            server = new BackgroundProcessingServer(_storage, _builders, properties, _options);
        }
        catch (Exception failure) when (Catchable(failure))
        {
            _boundary.Terminate(HangfireFatalStage.Constructor);
            // Only final process teardown can suppress this termination selection.
            _published.TrySetResult(null);
            RequestDrain(HangfireGenerationReason.CleanupFault);
            _constructorReturned.TrySetResult();
            return;
        }
        try
        {
            _published.TrySetResult(server);
            TryReady();
            // This actual uncanceled join is retained independently of all caller waits.
            _actualJoin = server.WaitForShutdownAsync(CancellationToken.None);
            _ownerCompletion = ObserveCompletionAsync(server, _actualJoin);
        }
        catch (Exception failure) when (Catchable(failure))
        {
            Volatile.Write(ref _cleanupFault, 1);
            RequestDrain(HangfireGenerationReason.CleanupFault);
        }
        finally { _constructorReturned.TrySetResult(); }
    }

    private async Task ObserveCompletionAsync(BackgroundProcessingServer server, Task actualJoin)
    {
        try
        {
            await actualJoin.ConfigureAwait(false);
            Volatile.Write(ref _outerJoined, 1);
            ObserveUnwind();
            if (Volatile.Read(ref _confirmed) != 0 && Volatile.Read(ref _removed) == 0)
                Volatile.Write(ref _cleanupFault, 1);
            RequestDrain(HangfireGenerationReason.Removed);
            await _stopRelay.ConfigureAwait(false);
            await _constructorReturned.Task.ConfigureAwait(false);
            await _startAssigned.Task.ConfigureAwait(false);
            if (_startInvocation is not null) await _startInvocation.ConfigureAwait(false);
            while (_constructor.IsAlive) await Task.Delay(TimeSpan.FromMilliseconds(1)).ConfigureAwait(false);
            if (_startupObserver is not null) await _startupObserver.ConfigureAwait(false);
            await _work.Completion.ConfigureAwait(false);
            if (Volatile.Read(ref _cleanupFault) != 0 || _gate.State is BackgroundJobProcessState.Fatal or BackgroundJobProcessState.FinalExit)
                return;
            server.Dispose();
            await _shutdownRegistration.DisposeAsync().ConfigureAwait(false);
            Volatile.Write(ref _cleanupComplete, 1);
            _completion.TrySetResult();
        }
        catch (Exception failure) when (Catchable(failure))
        {
            Volatile.Write(ref _cleanupFault, 1);
            Interlocked.Exchange(ref _reason, (int)HangfireGenerationReason.CleanupFault);
            RequestDrain(HangfireGenerationReason.CleanupFault);
        }
    }

    private async Task RelayStopAsync()
    {
        await _stopRequested.Task.ConfigureAwait(false);
        var server = await _published.Task.ConfigureAwait(false);
        try
        {
            server?.SendStop();
            _stopCompleted.TrySetResult();
        }
        catch (Exception failure) when (Catchable(failure))
        {
            Volatile.Write(ref _cleanupFault, 1);
            Interlocked.Exchange(ref _reason, (int)HangfireGenerationReason.CleanupFault);
        }
    }

    private void CompleteWithoutCore()
    {
        _published.TrySetResult(null);
        _neverConstructed.TrySetResult();
        RequestDrain(HangfireGenerationReason.HostStop);
        _ownerCompletion = CompleteWithoutCoreAsync();
    }

    private async Task CompleteWithoutCoreAsync()
    {
        await _stopRelay.ConfigureAwait(false);
        await _startAssigned.Task.ConfigureAwait(false);
        if (_startInvocation is not null) await _startInvocation.ConfigureAwait(false);
        await _constructorReturned.Task.ConfigureAwait(false);
        while (_constructor.IsAlive) await Task.Delay(TimeSpan.FromMilliseconds(1)).ConfigureAwait(false);
        if (_startupObserver is not null) await _startupObserver.ConfigureAwait(false);
        await _work.Completion.ConfigureAwait(false);
        if (Volatile.Read(ref _cleanupFault) != 0) return;
        Volatile.Write(ref _cleanupComplete, 1);
        _completion.TrySetResult();
    }

    private async Task ObserveStartupAsync()
    {
        try
        {
            using var timer = new CancellationTokenSource();
            var deadline = Task.Delay(_bounds.ProgressTimeout, _time, timer.Token);
            if (await Task.WhenAny(_waitEntered.Task, _neverConstructed.Task, _stopRequested.Task, deadline).ConfigureAwait(false) == deadline)
                RequestDrain(HangfireGenerationReason.StartupStalled);
            else await timer.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception failure) when (Catchable(failure))
        {
            RequestDrain(HangfireGenerationReason.StartupStalled);
        }
    }

    private void Capture(BackgroundServerContext context)
    {
        if (Interlocked.CompareExchange(ref _context, context, null) is not null)
            throw new InvalidOperationException("A generation captures its context once.");
        _shutdownRegistration = context.ShutdownToken.UnsafeRegister(static state =>
        {
            var owner = (HangfireGeneration)state!;
            if (owner._gate.State is BackgroundJobProcessState.FinalExit or BackgroundJobProcessState.Fatal
                || !owner._waitEntered.Task.IsCompleted) return;
            Volatile.Write(ref owner._childrenJoined, 1);
            owner._normalJoin.TrySetResult();
        }, this);
    }

    private void MarkWaitEntry()
    {
        _waitEntered.TrySetResult();
        TryReady();
    }

    private void TryReady()
    {
        if (_published.Task.IsCompletedSuccessfully && _published.Task.Result is not null
            && _waitEntered.Task.IsCompleted && _gate.IsOpen && Volatile.Read(ref _draining) == 0)
            _ready.TrySetResult(true);
    }

    private void WaitReady()
    {
        Task.WhenAny(_ready.Task, _stopRequested.Task).GetAwaiter().GetResult();
        if (_ready.Task.IsCompletedSuccessfully && _ready.Task.Result && Volatile.Read(ref _draining) == 0 && _gate.TryAdmit()) return;
        RequestDrain(HangfireGenerationReason.HostStop);
        WaitStopFence();
    }

    private void WaitStopFence() => _stopCompleted.Task.GetAwaiter().GetResult();

    private void ObserveUnwind()
    {
        if (Volatile.Read(ref _confirmed) == 0 || Volatile.Read(ref _preChildRefusal) != 0
            || HasNormalChildJoin) return;
        _boundary.Terminate(_waitEntered.Task.IsCompleted ? HangfireFatalStage.LifecycleUnwind : HangfireFatalStage.StartupUnwind);
    }

    // Final teardown invalidates the conditional join witness even if its
    // memory callback raced with that process-wide terminal selection.
    private bool HasNormalChildJoin => Volatile.Read(ref _childrenJoined) != 0
        && _gate.State is not (BackgroundJobProcessState.FinalExit or BackgroundJobProcessState.Fatal);

    private void RetainDisposal(PoolHangfireLifecycleOperation operation)
    {
        if (!operation.RetainsConnection) return;
        switch (operation.Method)
        {
            case PoolHangfireLifecycleMethod.Announce: Interlocked.CompareExchange(ref _retainedAnnounce, operation, null); break;
            case PoolHangfireLifecycleMethod.Heartbeat: Interlocked.CompareExchange(ref _retainedHeartbeat, operation, null); break;
            case PoolHangfireLifecycleMethod.Remove: Interlocked.CompareExchange(ref _retainedRemove, operation, null); break;
        }
        Volatile.Write(ref _cleanupFault, 1);
    }

    private OwnedBuilder Builder(IBackgroundProcess process) =>
        new OwnedBuilder(this, new AdmittedProcess(this, process).UseBackgroundPool(1));

    private static BackgroundProcessingServerOptions CreateOptions(BackgroundJobWorkerPoolRegistration pool) => new()
    {
        ServerName = "learnstack-" + pool.Name,
        HeartbeatInterval = TimeSpan.FromSeconds(30),
        ServerTimeout = TimeSpan.FromMinutes(5),
        ServerCheckInterval = TimeSpan.FromMinutes(5),
        CancellationCheckInterval = TimeSpan.FromSeconds(5),
        StopTimeout = Timeout.InfiniteTimeSpan,
        ShutdownTimeout = Timeout.InfiniteTimeSpan,
        LastChanceTimeout = TimeSpan.Zero,
        RestartDelay = TimeSpan.FromSeconds(15),
        ExcludeStorageProcesses = true,
    };

    private static bool Catchable(Exception failure) => failure.GetType() != typeof(OutOfMemoryException)
        && failure.GetType() != typeof(StackOverflowException);
    private static bool Transient(Exception? failure) => failure is TimeoutException or IOException or OperationCanceledException
        // PostgreSQL statement_timeout reports query_canceled, which Npgsql's
        // generic IsTransient list omits. It is this owned callback's bounded
        // storage timeout, not a schema/configuration refusal.
        || failure is PostgresException { SqlState: PostgresErrorCodes.QueryCanceled }
        || failure is NpgsqlException { IsTransient: true };
    private static InvalidOperationException Refused() => new("The owned Hangfire lifecycle refused continuation.");

    private sealed class AdmittedProcess(HangfireGeneration owner, IBackgroundProcess inner) : IBackgroundProcess
    {
        public void Execute(BackgroundProcessContext context)
        {
            owner.WaitReady();
            if (context.IsStopping || Volatile.Read(ref owner._draining) != 0) return;
            inner.Execute(context);
        }
    }

    private sealed class OwnedBuilder(HangfireGeneration owner, IBackgroundProcessDispatcherBuilder inner)
        : IBackgroundProcessDispatcherBuilder
    {
        public IBackgroundDispatcher Create(BackgroundServerContext context, BackgroundProcessingServerOptions options)
        {
            if (Volatile.Read(ref owner._draining) != 0 || !owner._gate.IsOpen) return new NoWorkDispatcher();
            try { return new OwnedDispatcher(owner, inner.Create(context, options)); }
            catch (Exception failure) when (Catchable(failure))
            {
                owner.RequestDrain(HangfireGenerationReason.BuilderFailure);
                // Private inner partial construction exposes no complete cleanup
                // handle. Let Core unwind be observed; never fabricate a joined placeholder.
                throw;
            }
        }
    }

    private sealed class OwnedDispatcher(HangfireGeneration owner, IBackgroundDispatcher inner) : IBackgroundDispatcher
    {
        public bool Wait(TimeSpan timeout) => inner.Wait(timeout);
        public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => inner.WaitAsync(timeout, cancellationToken);
        public void Dispose()
        {
            owner.ObserveUnwind();
            try { inner.Dispose(); }
            catch (Exception failure) when (Catchable(failure))
            {
                Volatile.Write(ref owner._cleanupFault, 1);
                owner.RequestDrain(HangfireGenerationReason.CleanupFault);
                throw;
            }
        }
    }

    private sealed class CaptureBuilder(HangfireGeneration owner) : IBackgroundProcessDispatcherBuilder
    {
        public IBackgroundDispatcher Create(BackgroundServerContext context, BackgroundProcessingServerOptions options)
        {
            owner.Capture(context);
            return new CaptureDispatcher(owner);
        }
    }

    private sealed class CaptureDispatcher(HangfireGeneration owner) : IBackgroundDispatcher
    {
        public bool Wait(TimeSpan timeout) => true;
        public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() => owner.ObserveUnwind();
    }

    private sealed class WaitEntryBuilder(HangfireGeneration owner) : IBackgroundProcessDispatcherBuilder
    {
        public IBackgroundDispatcher Create(BackgroundServerContext context, BackgroundProcessingServerOptions options) => new WaitEntryDispatcher(owner);
    }

    private sealed class WaitEntryDispatcher(HangfireGeneration owner) : IBackgroundDispatcher
    {
        public bool Wait(TimeSpan timeout) => true;
        public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            owner.MarkWaitEntry();
            return Task.CompletedTask;
        }
        public void Dispose() => owner.ObserveUnwind();
    }

    private sealed class NoWorkDispatcher : IBackgroundDispatcher
    {
        public bool Wait(TimeSpan timeout) => true;
        public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }
    }
}
