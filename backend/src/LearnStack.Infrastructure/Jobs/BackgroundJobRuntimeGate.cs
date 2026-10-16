using System.Collections.Frozen;

namespace LearnStack.Infrastructure.Jobs;

internal enum BackgroundJobProcessState { Running, Stopping, FinalExit, Fatal }
internal enum HangfireFatalStage { Constructor, StartupUnwind, LifecycleUnwind }

/// <summary>One process-wide admission boundary; neither a module port nor a cleanup witness.</summary>
public sealed class BackgroundJobRuntimeGate
{
    private sealed record TerminalState(BackgroundJobProcessState Kind, string? Diagnostic = null);
    private static readonly TerminalState Running = new(BackgroundJobProcessState.Running);
    private static readonly TerminalState Stopping = new(BackgroundJobProcessState.Stopping);
    private static readonly TerminalState FinalExit = new(BackgroundJobProcessState.FinalExit);
    private readonly FrozenDictionary<string, PoolBoundary> _pools;
    private readonly TaskCompletionSource _finalExit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _exitRegistration = new();
    private readonly EventHandler _onFinalExit;
    private TerminalState _state = Running;
    private int _exitRegistered;

    internal BackgroundJobRuntimeGate(BackgroundJobInfrastructureRegistry topology)
    {
        ArgumentNullException.ThrowIfNull(topology);
        _onFinalExit = OnFinalExit;
        _pools = topology.Pools.Values.ToFrozenDictionary(pool => pool.Name,
            pool => new PoolBoundary(this, pool), StringComparer.Ordinal);
    }

    internal BackgroundJobProcessState State => Volatile.Read(ref _state).Kind;
    internal bool IsOpen => ReferenceEquals(Volatile.Read(ref _state), Running);
    internal Task FinalExitSignal => _finalExit.Task;

    // The atomic read is the issuance linearization point. Callers use it at the
    // actual start/acquisition, not before queuing an asynchronous continuation.
    internal bool TryAdmit() => ReferenceEquals(Interlocked.CompareExchange(ref _state, Running, Running), Running);

    internal bool TryAdmitStorage()
    {
        var observed = Volatile.Read(ref _state);
        // Ordinary host drain still needs stock completion/removal SQL. Fatal
        // and final teardown close this same gate at actual factory issuance.
        return observed.Kind is BackgroundJobProcessState.Running or BackgroundJobProcessState.Stopping
            && ReferenceEquals(Interlocked.CompareExchange(ref _state, observed, observed), observed);
    }

    internal void StopStarts() => Interlocked.CompareExchange(ref _state, Stopping, Running);

    // This process-lifetime subscription precedes every actual Core constructor.
    // It has no cancellation callbacks, cleanup, logger or caller-supplied work.
    internal void EnsureFinalExitRegistered()
    {
        lock (_exitRegistration)
        {
            if (_exitRegistered == 1) return;
            if (_exitRegistered == -1) throw new InvalidOperationException("The job final-exit registration failed.");
            try
            {
                AppDomain.CurrentDomain.ProcessExit += _onFinalExit;
                AppDomain.CurrentDomain.DomainUnload += _onFinalExit;
                _exitRegistered = 1;
            }
            catch
            {
                _exitRegistered = -1;
                throw;
            }
        }
    }

    private void OnFinalExit(object? sender, EventArgs args) => BeginFinalExit();

    internal void BeginFinalExit()
    {
        while (true)
        {
            var observed = Volatile.Read(ref _state);
            if (observed.Kind is BackgroundJobProcessState.Fatal or BackgroundJobProcessState.FinalExit) return;
            if (!ReferenceEquals(Interlocked.CompareExchange(ref _state, FinalExit, observed), observed)) continue;
            _finalExit.TrySetResult();
            return;
        }
    }

    internal PoolBoundary ForPool(BackgroundJobWorkerPoolRegistration pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        if (!_pools.TryGetValue(pool.Name, out var boundary) || !ReferenceEquals(boundary.Pool, pool))
            throw new InvalidOperationException("The job pool is not part of the frozen runtime.");
        return boundary;
    }

    private void Terminate(TerminalState selected)
    {
        while (true)
        {
            var observed = Volatile.Read(ref _state);
            if (observed.Kind == BackgroundJobProcessState.FinalExit) return;
            if (observed.Kind == BackgroundJobProcessState.Fatal)
            {
                FailFast(observed.Diagnostic!);
                return;
            }
            if (!ReferenceEquals(Interlocked.CompareExchange(ref _state, selected, observed), observed)) continue;
            // Every consumer sees the same sealed state before the sole sink.
            // No cancellation, observer, I/O, logging or resource disposal here.
            FailFast(selected.Diagnostic!);
            return;
        }
    }

    private static void FailFast(string diagnostic) => Environment.FailFast(diagnostic);

    internal sealed class PoolBoundary
    {
        private readonly BackgroundJobRuntimeGate _gate;
        private readonly TerminalState[] _fatal;

        internal PoolBoundary(BackgroundJobRuntimeGate gate, BackgroundJobWorkerPoolRegistration pool)
        {
            _gate = gate;
            Pool = pool;
            _fatal =
            [
                new(BackgroundJobProcessState.Fatal, $"LearnStack jobs fatal: {pool.Name}:constructor"),
                new(BackgroundJobProcessState.Fatal, $"LearnStack jobs fatal: {pool.Name}:startup_unwind"),
                new(BackgroundJobProcessState.Fatal, $"LearnStack jobs fatal: {pool.Name}:lifecycle_unwind"),
            ];
        }

        internal BackgroundJobWorkerPoolRegistration Pool { get; }

        internal void Terminate(HangfireFatalStage stage)
        {
            if (!Enum.IsDefined(stage)) throw new ArgumentOutOfRangeException(nameof(stage));
            _gate.Terminate(_fatal[(int)stage]);
        }
    }
}
