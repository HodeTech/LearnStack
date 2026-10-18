using System.Diagnostics.CodeAnalysis;
using LearnStack.Application.Jobs;

namespace LearnStack.Infrastructure.Jobs;

internal sealed record HangfireGenerationWorkSnapshot(int Running, int Retained, bool Closed);

/// <summary>Owns the private cancellation and cleanup of work admitted by exactly one generation.</summary>
internal sealed partial class HangfireGenerationWork
{
    private readonly object _sync = new();
    private readonly HashSet<Invocation> _active = [];
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly BackgroundJobRuntimeGate _gate;
    private readonly BackgroundJobRuntimeOptions _bounds;
    private readonly TimeProvider _time;
    private readonly int _hardBound;
    private bool _closed;
    private int _retained;

    internal HangfireGenerationWork(BackgroundJobWorkerPoolRegistration pool, BackgroundJobRuntimeGate gate,
        BackgroundJobRuntimeOptions? bounds = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(gate);
        _hardBound = BackgroundJobRuntimeOptions.RetainedHardBound(pool.Workers);
        _gate = gate;
        _bounds = bounds ?? new();
        _bounds.Validate();
        _time = time ?? TimeProvider.System;
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
        foreach (var invocation in active) invocation.ExpireWait();
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
    internal sealed partial class Invocation(HangfireGenerationWork owner) : BackgroundJobActualCleanup.MintingSource
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _private = new();
        private readonly TaskCompletionSource<Task<BackgroundJobActualCleanup?>> _cleanupPublished =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _cleanupStarted;
        private Task<bool>? _cancellationObservation;
        // These concrete owned tasks retain their completed failure results and
        // unresolved resource owners after a wrapper returns. No delegate runs here.
        private Task<PostgresBackgroundJobRoutingStore.OwnedRoutingRead>? _routing;
        private Task<JobStartExecutionResult>? _admission;
        private Task<JobBusinessExecutionResult>? _business;
        private BackgroundJobExecutionControl? _execution;
        private HangfireInvocationContract.BoundInvocation? _framework;
        private bool _completing;
        private int _retained;
        private int _admissionClosed;

        internal CancellationToken Token => _private.Token;
        internal bool IsRetained => Volatile.Read(ref _retained) != 0;
        internal bool MarkRetainedOnce() => Interlocked.Exchange(ref _retained, 1) == 0;
        internal bool TryAdmitContinuation() => Interlocked.CompareExchange(ref _admissionClosed, 0, 0) == 0;

        internal void HoldInvocation(HangfireInvocationContract.BoundInvocation invocation)
        {
            ArgumentNullException.ThrowIfNull(invocation);
            lock (_sync)
            {
                if (_completing || _framework is not null) throw new InvalidOperationException("A job invocation owns one framework operation.");
                _framework = invocation;
            }
        }

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

        internal void HoldExecution(BackgroundJobExecutionControl execution)
        {
            ArgumentNullException.ThrowIfNull(execution);
            lock (_sync)
            {
                if (_completing || _execution is not null) throw new InvalidOperationException("A generation owns one business control.");
                _execution = execution;
                if (Volatile.Read(ref _admissionClosed) != 0) execution.RunGate.Close();
            }
        }

        internal void HoldBusiness(Task<JobBusinessExecutionResult> task)
        {
            ArgumentNullException.ThrowIfNull(task);
            lock (_sync)
            {
                if (_completing || _business is not null) throw new InvalidOperationException("A generation owns one business operation.");
                _business = task;
            }
        }

        internal Task<BackgroundJobActualCleanup?> PrepareCapacityCleanupAsync(BackgroundJobExecutionControl execution)
        {
            lock (_sync)
            {
                if (!ReferenceEquals(_execution, execution) || _completing)
                    throw new InvalidOperationException("Capacity cleanup belongs to the actual held execution.");
                if (_cleanupStarted) return _cleanupPublished.Task.Unwrap();
                _cleanupStarted = true;
            }
            // Publish the owned join before any asynchronous disposal can return.
            var actual = PrepareCapacityCleanupCoreAsync(execution);
            _cleanupPublished.TrySetResult(actual);
            return _cleanupPublished.Task.Unwrap();
        }

        private async Task<BackgroundJobActualCleanup?> PrepareCapacityCleanupCoreAsync(BackgroundJobExecutionControl execution)
        {
            try
            {
                JobStartExecutionResult admission;
                JobBusinessExecutionResult? business;
                lock (_sync)
                {
                    if (_admission is not { IsCompletedSuccessfully: true } actualAdmission
                        || actualAdmission.Result.CleanupFailed) return null;
                    admission = actualAdmission.Result;
                    if (_business is not null && (!_business.IsCompletedSuccessfully || _business.Result.CleanupFailed)) return null;
                    business = _business?.Result;
                }
                var envelope = admission.Receipt?.Envelope ?? admission.ValidatedEnvelope;
                if (envelope is not { Profile: "transactional", CapacityGroup: not null }
                    || admission.Outcome is not (JobStartExecutionOutcome.Started or JobStartExecutionOutcome.Indeterminate)
                    || !admission.SourceOwner.CleanupConfirmed
                    || business is { CommitAcknowledged: true }
                    || business is not null && !ReferenceEquals(business.SourceOwner, admission.SourceOwner)) return null;
                if (business is null && !execution.Gate.TrySealUnusedExecution()) return null;
                execution.Gate.Close();
                if (!await execution.JoinBusinessObserversAsync().ConfigureAwait(false)) return null;
                RequestCancellation();
                Task<bool>? cancellation;
                lock (_sync) cancellation = _cancellationObservation;
                if (cancellation is null || !await cancellation.ConfigureAwait(false)
                    || !admission.SourceOwner.CleanupConfirmed) return null;
                return MintCleanup(execution, admission, envelope, business);
            }
            catch (Exception failure) when (Catchable(failure))
            {
                owner.MarkRetained(this);
                return null;
            }
        }

        // The execution owner records an overrun before returning its wrapper
        // slot. It still owns the actual execution and calls CompleteAsync later.
        internal void MarkRetained() => owner.MarkRetained(this);

        internal void ExpireWait()
        {
            lock (_sync) { _stageWaitClosed = true; SignalStageWake(); }
            Interlocked.Exchange(ref _admissionClosed, 1);
            owner.MarkRetained(this);
            RequestCancellation();
            // Publish only after fixed-state closure, retention and the owned
            // async cancellation relay have been established.
            _returnRequested.TrySetResult();
        }

        internal void RequestCancellation()
        {
            Interlocked.Exchange(ref _admissionClosed, 1);
            lock (_sync)
            {
                _execution?.RunGate.Close();
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
            Task<bool>? stageWake;
            lock (_sync)
            {
                if (_completing) throw new InvalidOperationException("A job invocation confirms cleanup once.");
                if (!cleanupConfirmed || _stageTimerInitializing || _stageTimerFaulted)
                {
                    owner.MarkRetained(this);
                    return;
                }
                _completing = true;
                _stageWaitClosed = true;
                SignalStageWake();
                cancellation = _cancellationObservation;
                stageWake = _stageWakeObservation;
            }
            try
            {
                if (_stageTimer is not null) await _stageTimer.DisposeAsync().ConfigureAwait(false);
                if (stageWake is { IsCompleted: false }) owner.MarkRetained(this);
                if (stageWake is not null && !await stageWake.ConfigureAwait(false)) return;
                if (cancellation is { IsCompleted: false }) owner.MarkRetained(this);
                if (cancellation is not null && !await cancellation.ConfigureAwait(false)) return;
                _private.Dispose();
                ObserveLateSettlement();
                _execution?.ReleaseAfterPrivateCleanup();
                lock (_sync) { _routing = null; _admission = null; _business = null; _execution = null; _framework = null; }
                owner.Release(this);
            }
            catch (Exception exception) when (Catchable(exception))
            {
                // A returned wrapper is not positive callback/source cleanup.
                owner.MarkRetained(this);
            }
        }

        // The finite wait only supplies provenance. Actual framework, execution,
        // timer and private callback cleanup must all precede this process fact.
        private void ObserveLateSettlement()
        {
            BackgroundJobInvocationStage stage;
            HangfireInvocationContract.BoundInvocation? framework;
            BackgroundJobExecutionControl? execution;
            lock (_sync)
            {
                if (_elapsedStageConsumed || _elapsedStage is not { } elapsed) return;
                stage = elapsed;
                framework = _framework;
                execution = _execution;
            }
            if (framework?.HasSettledStage(stage, execution) != true) return;
            lock (_sync)
            {
                if (_elapsedStageConsumed) return;
                _elapsedStageConsumed = true;
            }
            framework.ObserveLateSettlement(stage);
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
