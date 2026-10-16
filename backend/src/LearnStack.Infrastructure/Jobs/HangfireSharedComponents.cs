using System.Diagnostics.CodeAnalysis;
using Hangfire.Server;

namespace LearnStack.Infrastructure.Jobs;

internal enum HangfireSharedComponentKind { Expiration, FetchedJobHeartbeat }
internal enum HangfireSharedComponentState { NotStarted, Starting, Running, Waiting, Canceling, Retained, CleanupFault, Stopped }
internal enum HangfireSharedComponentOutcome { None, Completed, Canceled, Failed, StartRefused, StartFailed, CancellationFault, CleanupFailed }

internal sealed record HangfireSharedComponentSnapshot(HangfireSharedComponentKind Kind,
    HangfireSharedComponentState State, HangfireSharedComponentOutcome Outcome,
    long Invocations, bool ActualJoined, bool OwnsResources, bool StorageFault)
{
    internal bool IsHealthy => !StorageFault && (State is HangfireSharedComponentState.Running or HangfireSharedComponentState.Waiting)
        && Outcome is not (HangfireSharedComponentOutcome.Failed or HangfireSharedComponentOutcome.StartFailed
            or HangfireSharedComponentOutcome.CancellationFault or HangfireSharedComponentOutcome.CleanupFailed);
}

/// <summary>The API instance owns these two frozen provider components independently of pool generations.</summary>
internal sealed class HangfireSharedComponents
{
    private readonly HangfireSharedComponentWorker _expiration;
    private readonly HangfireSharedComponentWorker _heartbeat;

    internal HangfireSharedComponents(HangfireComponentInventory inventory, BackgroundJobRuntimeGate gate,
        TimeProvider timeProvider, HangfireBoundedLogProvider diagnostics)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        _expiration = new(inventory.Expiration, HangfireSharedComponentKind.Expiration, gate, timeProvider, diagnostics);
        _heartbeat = new(inventory.FetchedJobHeartbeat, HangfireSharedComponentKind.FetchedJobHeartbeat, gate, timeProvider, diagnostics);
        Completion = Task.WhenAll(_expiration.Completion, _heartbeat.Completion);
    }

    internal HangfireSharedComponentSnapshot Expiration => _expiration.Snapshot;
    internal HangfireSharedComponentSnapshot FetchedJobHeartbeat => _heartbeat.Snapshot;
    internal Task Completion { get; }

    internal void Start()
    {
        _expiration.Start();
        _heartbeat.Start();
    }

    // Only closed signals and asynchronous cancellation relays run here; one
    // component's native callback cannot delay the other component's stop.
    internal void RequestStop()
    {
        _expiration.RequestStop();
        _heartbeat.RequestStop();
    }

    internal async Task StopAsync(CancellationToken callerBudget)
    {
        RequestStop();
        await Completion.WaitAsync(callerBudget).ConfigureAwait(false);
    }
}

/// <summary>Closed framework helper; production construction is confined to the two frozen inventory sites above.</summary>
#pragma warning disable CS0618 // Preserve the exact pinned public provider component contract.
internal sealed class HangfireSharedComponentWorker
{
    internal static readonly TimeSpan InvocationWait = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan CleanupWait = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan ExpirationCadence = TimeSpan.FromHours(1);
    internal static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(1);

    private readonly object _sync = new();
    private readonly IServerComponent _component;
    private readonly HangfireSharedComponentKind _kind;
    private readonly BackgroundJobRuntimeGate _gate;
    private readonly TimeProvider _timeProvider;
    private readonly HangfireBoundedLogProvider _diagnostics;
    private long _confirmedFaults, _confirmedOwnershipLosses;
    private bool _storageFault;
    private readonly TaskCompletionSource _stop = Signal();
    private readonly TaskCompletionSource _completion = Signal();
    private bool _started;
    private bool _stopping;
    private long _invocations;
    private HangfireSharedComponentState _state;
    private HangfireSharedComponentOutcome _outcome;
    private Invocation? _current;
    private Task? _loop;

    internal HangfireSharedComponentWorker(IServerComponent component, HangfireSharedComponentKind kind,
        BackgroundJobRuntimeGate gate, TimeProvider timeProvider, HangfireBoundedLogProvider diagnostics)
    {
        _component = component ?? throw new ArgumentNullException(nameof(component));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        _kind = kind;
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    internal Task Completion => _completion.Task;

    internal HangfireSharedComponentSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                var retainedOutcome = _current?.RetentionOutcome ?? HangfireSharedComponentOutcome.None;
                return new(_kind, retainedOutcome == HangfireSharedComponentOutcome.None
                        ? _state : HangfireSharedComponentState.CleanupFault,
                    retainedOutcome == HangfireSharedComponentOutcome.None ? _outcome : retainedOutcome,
                    _invocations, _current?.ActualJoined ?? true, _current is { Completion.IsCompleted: false }, HasStorageFault());
            }
        }
    }

    internal void Start()
    {
        lock (_sync)
        {
            if (_started || _stopping) return;
            _started = true;
        }
        // Thread.Start can itself wait for runtime initialization. Never hold
        // the fixed-state stop lock across that startup or asynchronous work.
        if (ExecutionContext.IsFlowSuppressed()) _loop = RunAsync();
        else
        {
            using var suppression = ExecutionContext.SuppressFlow();
            _loop = RunAsync();
        }
    }

    internal void RequestStop()
    {
        Invocation? current;
        lock (_sync)
        {
            _stopping = true;
            current = _current;
            if (!_started)
            {
                _state = HangfireSharedComponentState.Stopped;
                _completion.TrySetResult();
            }
        }
        _stop.TrySetResult();
        // The orchestration may already have faulted. Relay independently of
        // its continuation, without holding the fixed-state stop lock.
        current?.RequestCancellation();
    }

    internal async Task StopAsync(CancellationToken callerBudget)
    {
        RequestStop();
        await Completion.WaitAsync(callerBudget).ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                Invocation invocation;
                lock (_sync)
                {
                    if (_stopping || !_gate.IsOpen) break;
                    invocation = new(this);
                    _current = invocation;
                    _state = HangfireSharedComponentState.Starting;
                }
                invocation.Start();

                var winner = await WaitAsync(InvocationWait, invocation.Completion, invocation.Retained,
                    _stop.Task).ConfigureAwait(false);
                if (winner != invocation.Completion && winner != invocation.Retained)
                {
                    SetState(HangfireSharedComponentState.Canceling);
                    invocation.RequestCancellation();
                    winner = await WaitAsync(CleanupWait, invocation.Completion, invocation.Retained).ConfigureAwait(false);
                }
                if (winner != invocation.Completion)
                {
                    SetState(invocation.RetentionOutcome == HangfireSharedComponentOutcome.None
                        ? HangfireSharedComponentState.Retained : HangfireSharedComponentState.CleanupFault,
                        invocation.RetentionOutcome);
                    await Task.WhenAny(invocation.Completion, invocation.Retained).ConfigureAwait(false);
                    if (invocation.Retained.IsCompleted)
                        SetState(HangfireSharedComponentState.CleanupFault, invocation.RetentionOutcome);
                    // A timeout, callback failure or caller abandonment cannot
                    // manufacture a native join or authorize another invocation.
                    await invocation.Completion.ConfigureAwait(false);
                }

                var result = invocation.Outcome;
                lock (_sync)
                {
                    _current = null;
                    _outcome = result;
                    _state = HangfireSharedComponentState.Waiting;
                    if (_stopping || !_gate.IsOpen) break;
                }
                var delay = result == HangfireSharedComponentOutcome.Completed
                    ? _kind == HangfireSharedComponentKind.Expiration ? ExpirationCadence : TimeSpan.Zero
                    : FailureBackoff;
                if (delay > TimeSpan.Zero)
                    await WaitAsync(delay, _stop.Task).ConfigureAwait(false);
            }
            SetState(HangfireSharedComponentState.Stopped);
            _completion.TrySetResult();
        }
        catch (Exception exception) when (Catchable(exception))
        {
            // An unexpected owner failure closes this lane permanently. Retain
            // its resources and the actual loop; do not retry ownership blindly.
            lock (_sync)
            {
                _stopping = true;
                _state = HangfireSharedComponentState.CleanupFault;
                _outcome = HangfireSharedComponentOutcome.Failed;
            }
        }
    }

    private bool TryIssueInvocation(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_stopping || cancellationToken.IsCancellationRequested || !_gate.TryAdmit()) return false;
            _state = HangfireSharedComponentState.Running;
            _invocations++;
            return true;
        }
    }

    private bool HasStorageFault()
    {
        if (_kind != HangfireSharedComponentKind.FetchedJobHeartbeat) return false;
        var current = _diagnostics.FetchedHeartbeat;
        return _storageFault || current.Faults != _confirmedFaults || current.OwnershipLosses != _confirmedOwnershipLosses;
    }

    private HangfireSharedComponentOutcome ObserveReturn(HangfireFetchedHeartbeatDiagnostics before)
    {
        if (_kind != HangfireSharedComponentKind.FetchedJobHeartbeat) return HangfireSharedComponentOutcome.Completed;
        var after = _diagnostics.FetchedHeartbeat;
        lock (_sync)
        {
            if (after.Faults != before.Faults || after.OwnershipLosses != before.OwnershipLosses)
                _storageFault = true;
            else if (after.Renewals > before.Renewals)
            {
                // An empty/not-yet-due pass cannot erase a known fault. A clean
                // pass containing real renewal SQL is positive recovery evidence.
                _storageFault = false;
                _confirmedFaults = after.Faults;
                _confirmedOwnershipLosses = after.OwnershipLosses;
            }
            return HasStorageFault() ? HangfireSharedComponentOutcome.Failed : HangfireSharedComponentOutcome.Completed;
        }
    }

    private void SetState(HangfireSharedComponentState state,
        HangfireSharedComponentOutcome outcome = HangfireSharedComponentOutcome.None)
    {
        lock (_sync)
        {
            _state = state;
            if (outcome != HangfireSharedComponentOutcome.None) _outcome = outcome;
        }
    }

    private async Task<Task> WaitAsync(TimeSpan duration, params Task[] alternatives)
    {
        using var cancellation = new CancellationTokenSource();
        var delay = Task.Delay(duration, _timeProvider, cancellation.Token);
        var winner = await Task.WhenAny(alternatives.Append(delay)).ConfigureAwait(false);
        // This private source has only Task.Delay's closed internal callback.
        await cancellation.CancelAsync().ConfigureAwait(false);
        return winner;
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static bool Catchable(Exception exception) => exception.GetType() != typeof(OutOfMemoryException)
        && exception.GetType() != typeof(StackOverflowException);

    [SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
        Justification = "Only JoinAndCleanup may dispose cancellation after actual startup return, native join and callback completion; caller disposal could invalidate live work.")]
    private sealed class Invocation
    {
        private readonly object _sync = new();
        private readonly HangfireSharedComponentWorker _owner;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TaskCompletionSource _startupPublished = Signal();
        private readonly TaskCompletionSource _completion = Signal();
        private readonly TaskCompletionSource _retained = Signal();
        private readonly Thread _native;
        private readonly Thread _join;
        private Task? _startup;
        private Task? _cancellationTask;
        private Task? _cancellationObservation;
        private bool _startFailed;
        private bool _cleanupStarted;
        private bool _actualJoined;
        private HangfireSharedComponentOutcome _outcome;
        private HangfireSharedComponentOutcome _retentionOutcome;

        internal Invocation(HangfireSharedComponentWorker owner)
        {
            _owner = owner;
            _native = new(Execute)
            {
                IsBackground = true,
                Name = owner._kind == HangfireSharedComponentKind.Expiration
                ? "LearnStack Hangfire expiration" : "LearnStack Hangfire fetched heartbeat"
            };
            _join = new(JoinAndCleanup)
            {
                IsBackground = true,
                Name = owner._kind == HangfireSharedComponentKind.Expiration
                ? "LearnStack expiration join" : "LearnStack fetched heartbeat join"
            };
        }

        internal Task Completion => _completion.Task;
        internal Task Retained => _retained.Task;
        internal HangfireSharedComponentOutcome Outcome { get { lock (_sync) return _outcome; } }
        internal HangfireSharedComponentOutcome RetentionOutcome { get { lock (_sync) return _retentionOutcome; } }
        internal bool ActualJoined { get { lock (_sync) return _actualJoined; } }

        internal void Start()
        {
            try
            {
                // At most one startup operation is retained per component. Its
                // potentially stalled Thread.Start calls cannot delay the other
                // lane or prevent the five-minute observation from being armed.
                Task startup;
                if (ExecutionContext.IsFlowSuppressed()) startup = Task.Run(StartThreads);
                else
                {
                    using var suppression = ExecutionContext.SuppressFlow();
                    startup = Task.Run(StartThreads);
                }
                lock (_sync) _startup = startup;
            }
            catch (Exception exception) when (Catchable(exception))
            {
                // A failed Thread.Start is not a positive cleanup witness.
                lock (_sync) _startFailed = true;
                Retain(HangfireSharedComponentOutcome.StartFailed);
            }
            finally { _startupPublished.TrySetResult(); }
        }

        private void StartThreads()
        {
            try
            {
                // Start the join owner first, so actual work never precedes it.
                _join.Start();
                _native.Start();
            }
            catch (Exception exception) when (Catchable(exception))
            {
                lock (_sync) _startFailed = true;
                Retain(HangfireSharedComponentOutcome.StartFailed);
            }
        }

        private void Execute()
        {
            var result = HangfireSharedComponentOutcome.StartRefused;
            try
            {
                // The whole-host gate is checked on this actual native thread,
                // immediately before issuing the selected stock invocation.
                if (!_cancellation.IsCancellationRequested && _owner.TryIssueInvocation(_cancellation.Token))
                {
                    var before = _owner._diagnostics.FetchedHeartbeat;
                    _owner._component.Execute(_cancellation.Token);
                    result = _owner.ObserveReturn(before);
                }
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                result = HangfireSharedComponentOutcome.Canceled;
            }
            catch (Exception exception) when (Catchable(exception))
            {
                result = HangfireSharedComponentOutcome.Failed;
            }
            finally { lock (_sync) _outcome = result; }
        }

        internal void RequestCancellation()
        {
            try
            {
                lock (_sync)
                {
                    if (_cleanupStarted || _cancellationTask is not null) return;
                    // CancelAsync owns arbitrary native registrations separately;
                    // no supplied callback executes on RequestStop's calling thread.
                    if (ExecutionContext.IsFlowSuppressed()) BeginCancellation();
                    else
                    {
                        using var suppression = ExecutionContext.SuppressFlow();
                        BeginCancellation();
                    }
                }
            }
            catch (Exception exception) when (Catchable(exception))
            {
                Retain(HangfireSharedComponentOutcome.CancellationFault);
            }
        }

        private void BeginCancellation()
        {
            _cancellationTask = _cancellation.CancelAsync();
            _cancellationObservation = ObserveCancellationAsync(_cancellationTask);
        }

        private async Task ObserveCancellationAsync(Task cancellation)
        {
            try { await cancellation.ConfigureAwait(false); }
            catch (Exception exception) when (Catchable(exception))
            {
                Retain(HangfireSharedComponentOutcome.CancellationFault);
            }
        }

        private void JoinAndCleanup()
        {
            try
            {
                _startupPublished.Task.GetAwaiter().GetResult();
                Task? startup;
                lock (_sync) startup = _startup;
                // Publication only identifies the operation. Its actual return
                // is required even if the native invocation has already exited.
                startup?.GetAwaiter().GetResult();
                lock (_sync) if (_startFailed) return;
                _native.Join();
                Task? cancellation;
                Task? observation;
                lock (_sync)
                {
                    if (_native.IsAlive) throw new InvalidOperationException("The component thread has not joined.");
                    _actualJoined = true;
                    _cleanupStarted = true;
                    cancellation = _cancellationTask;
                    observation = _cancellationObservation;
                }
                // This is an owned background join thread, never a synchronous
                // stop callback or an unbounded set of thread-pool waiters.
                try { cancellation?.GetAwaiter().GetResult(); }
                catch (Exception exception) when (Catchable(exception))
                {
                    Retain(HangfireSharedComponentOutcome.CancellationFault);
                    return;
                }
                observation?.GetAwaiter().GetResult();
                lock (_sync) if (_retentionOutcome != HangfireSharedComponentOutcome.None) return;
                _cancellation.Dispose();
                _completion.TrySetResult();
            }
            catch (Exception exception) when (Catchable(exception))
            {
                Retain(HangfireSharedComponentOutcome.CleanupFailed);
            }
        }

        private void Retain(HangfireSharedComponentOutcome outcome)
        {
            lock (_sync)
                if (_retentionOutcome == HangfireSharedComponentOutcome.None) _retentionOutcome = outcome;
            _retained.TrySetResult();
        }
    }
}
#pragma warning restore CS0618
