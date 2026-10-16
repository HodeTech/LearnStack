using System.Diagnostics.CodeAnalysis;

namespace LearnStack.Infrastructure.Jobs;

internal sealed record HangfireGenerationWorkSnapshot(int Running, int Retained, bool Closed);

/// <summary>Owns the private cancellation and cleanup of work admitted by exactly one generation.</summary>
internal sealed class HangfireGenerationWork
{
    private readonly object _sync = new();
    private readonly HashSet<Invocation> _active = [];
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly BackgroundJobRuntimeGate _gate;
    private readonly int _hardBound;
    private bool _closed;
    private int _retained;

    internal HangfireGenerationWork(BackgroundJobWorkerPoolRegistration pool, BackgroundJobRuntimeGate gate)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(gate);
        _hardBound = BackgroundJobRuntimeOptions.RetainedHardBound(pool.Workers);
        _gate = gate;
    }

    internal Task Completion => _completion.Task;
    internal bool CanStart
    {
        get { lock (_sync) return !_closed && _retained < BackgroundJobRuntimeOptions.RetainedStopThreshold; }
    }
    internal HangfireGenerationWorkSnapshot Snapshot
    {
        get { lock (_sync) return new(_active.Count - _retained, _retained, _closed); }
    }

    internal Invocation? TryBegin()
    {
        lock (_sync)
        {
            if (_closed || _retained >= BackgroundJobRuntimeOptions.RetainedStopThreshold
                || _active.Count >= _hardBound || !_gate.TryAdmit()) return null;
            var invocation = new Invocation(this);
            _active.Add(invocation);
            return invocation;
        }
    }

    internal void CloseStarts()
    {
        lock (_sync)
        {
            _closed = true;
            CompleteIfEmpty();
        }
    }

    internal void RequestHostStop()
    {
        Invocation[] active;
        lock (_sync)
        {
            _closed = true;
            active = _active.ToArray();
            CompleteIfEmpty();
        }
        // No module callback runs under the owner lock or on a Core token callback.
        foreach (var invocation in active) invocation.RequestCancellation();
    }

    private void MarkRetained(Invocation invocation)
    {
        lock (_sync)
            if (_active.Contains(invocation) && invocation.MarkRetainedOnce()) _retained++;
    }

    private void Release(Invocation invocation)
    {
        lock (_sync)
        {
            if (!_active.Remove(invocation)) throw new InvalidOperationException("A job invocation has one cleanup owner.");
            if (invocation.IsRetained) _retained--;
            CompleteIfEmpty();
        }
    }

    private void CompleteIfEmpty()
    {
        if (_closed && _active.Count == 0) _completion.TrySetResult();
    }

    [SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
        Justification = "Only actual work completion can dispose its private source after cancellation callbacks; caller disposal cannot prove cleanup.")]
    internal sealed class Invocation(HangfireGenerationWork owner)
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _private = new();
        private Task<bool>? _cancellationObservation;
        // These concrete owned tasks retain their completed failure results and
        // unresolved resource owners after a wrapper returns. No delegate runs here.
        private Task<PostgresBackgroundJobRoutingStore.OwnedRoutingRead>? _routing;
        private Task<JobStartExecutionResult>? _admission;
        private bool _completing;
        private int _retained;

        internal CancellationToken Token => _private.Token;
        internal bool IsRetained => Volatile.Read(ref _retained) != 0;
        internal bool MarkRetainedOnce() => Interlocked.Exchange(ref _retained, 1) == 0;

        internal void HoldRouting(Task<PostgresBackgroundJobRoutingStore.OwnedRoutingRead> task)
        {
            ArgumentNullException.ThrowIfNull(task);
            lock (_sync)
            {
                if (_completing || _routing is not null) throw new InvalidOperationException("A job invocation owns one routing operation.");
                _routing = task;
            }
        }

        internal void HoldAdmission(Task<JobStartExecutionResult> task)
        {
            ArgumentNullException.ThrowIfNull(task);
            lock (_sync)
            {
                if (_completing || _admission is not null) throw new InvalidOperationException("A job invocation owns one admission operation.");
                _admission = task;
            }
        }

        // The execution owner records an overrun before returning its wrapper
        // slot. It still owns the actual execution and calls CompleteAsync later.
        internal void MarkRetained() => owner.MarkRetained(this);

        internal void RequestCancellation()
        {
            lock (_sync)
            {
                if (_completing || _cancellationObservation is not null) return;
                if (ExecutionContext.IsFlowSuppressed()) StartCancellationObservation();
                else { using (ExecutionContext.SuppressFlow()) StartCancellationObservation(); }
            }
        }

        // False records a missing cleanup witness and permits a later positive
        // report. Positive confirmation is claimed once, including while callbacks
        // remain in flight; failed callback/source cleanup never releases ownership.
        internal async Task CompleteAsync(bool cleanupConfirmed)
        {
            Task<bool>? cancellation;
            lock (_sync)
            {
                if (_completing) throw new InvalidOperationException("A job invocation confirms cleanup once.");
                if (!cleanupConfirmed)
                {
                    owner.MarkRetained(this);
                    return;
                }
                _completing = true;
                cancellation = _cancellationObservation;
            }
            try
            {
                if (cancellation is { IsCompleted: false }) owner.MarkRetained(this);
                if (cancellation is not null && !await cancellation.ConfigureAwait(false)) return;
                _private.Dispose();
                lock (_sync) { _routing = null; _admission = null; }
                owner.Release(this);
            }
            catch (Exception exception) when (Catchable(exception))
            {
                // A returned wrapper is not positive callback/source cleanup.
                owner.MarkRetained(this);
            }
        }

        private void StartCancellationObservation()
        {
            try
            {
                // Observe immediately, independently of any eventual wrapper
                // completion. A fault must retain ownership even if it never returns.
                _cancellationObservation = ObserveCancellationAsync(_private.CancelAsync());
            }
            catch (Exception exception) when (Catchable(exception))
            {
                owner.MarkRetained(this);
                _cancellationObservation = Task.FromResult(false);
            }
        }

        private async Task<bool> ObserveCancellationAsync(Task cancellation)
        {
            try { await cancellation.ConfigureAwait(false); return true; }
            catch (Exception exception) when (Catchable(exception))
            {
                owner.MarkRetained(this);
                return false;
            }
        }

        private static bool Catchable(Exception exception) => exception.GetType() != typeof(OutOfMemoryException)
            && exception.GetType() != typeof(StackOverflowException);
    }
}
