namespace LearnStack.Infrastructure.Jobs;

internal enum BackgroundJobInvocationStage
{
    Framework, Transactional, Prepare, ExternalRead, Arm, Execute, Outcome, Finalize,
    PrepareHistory, ArmHistory, OutcomeHistory, FinalizeHistory,
    RecoveryAcquire, RecoveryConfirmation, Reconcile, NoStart, RecoveryDisposition,
}

internal sealed partial class HangfireGenerationWork
{
    internal sealed partial class Invocation
    {
        private readonly TaskCompletionSource _returnRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ITimer? _stageTimer;
        private long _stageWaitUntil;
        private BackgroundJobInvocationStage? _stageWaitStage;
        private bool _reconcileWaitExpired;
        private bool _stageWaitClosed;
        private bool _stageTimerInitializing, _stageTimerFaulted;

        internal Task ReturnRequested => _returnRequested.Task;
        internal bool ReconcileWaitExpired { get { lock (_sync) return _reconcileWaitExpired; } }

        // Wait policy only: this grants no root, checkpoint or provider entry.
        // The exact runtime call sites arm it BEFORE a possible synchronous prefix.
        internal bool BeginStageWait(BackgroundJobInvocationStage stage)
        {
            var timeout = stage switch
            {
                BackgroundJobInvocationStage.Framework or BackgroundJobInvocationStage.Transactional
                    or BackgroundJobInvocationStage.Prepare or BackgroundJobInvocationStage.Finalize => owner._bounds.TransactionTimeout,
                BackgroundJobInvocationStage.ExternalRead or BackgroundJobInvocationStage.Arm
                    or BackgroundJobInvocationStage.Outcome or BackgroundJobInvocationStage.RecoveryAcquire
                    or BackgroundJobInvocationStage.NoStart or BackgroundJobInvocationStage.RecoveryDisposition => owner._bounds.SqlTimeout,
                BackgroundJobInvocationStage.Execute => owner._bounds.ExternalWaitTimeout,
                BackgroundJobInvocationStage.PrepareHistory or BackgroundJobInvocationStage.ArmHistory
                    or BackgroundJobInvocationStage.OutcomeHistory or BackgroundJobInvocationStage.FinalizeHistory
                    or BackgroundJobInvocationStage.RecoveryConfirmation or BackgroundJobInvocationStage.Reconcile => owner._bounds.ProbeTimeout,
                _ => throw new ArgumentOutOfRangeException(nameof(stage)),
            };
            ITimer? actual;
            long desiredUntil = 0;
            var expired = false;
            lock (_sync)
            {
                if (_completing || _stageWaitClosed || Volatile.Read(ref _admissionClosed) != 0) return false;
                if (_stageTimerInitializing) throw new InvalidOperationException("One actual stage timer update is already owned.");
                var now = owner._time.GetTimestamp();
                if (_stageTimer is not null && StageClockClosesWait(now))
                {
                    // This belongs to the elapsed predecessor, never the stage
                    // whose start is about to be refused.
                    RecordStageClockClosure();
                    expired = true;
                }
                else
                {
                    desiredUntil = checked(now + (long)(timeout.TotalSeconds * owner._time.TimestampFrequency));
                    // While rearm is in flight either old or new callbacks may
                    // fire. Neither may borrow the other's later deadline.
                    if (_stageTimer is null || desiredUntil < _stageWaitUntil)
                    {
                        _stageWaitUntil = desiredUntil;
                        // This prospective shorter wait has not accepted its
                        // stage yet. A callback during Change cannot call it
                        // an elapsed, already-issued Reconcile operation.
                        _stageWaitStage = null;
                    }
                    _stageTimerInitializing = true;
                    SignalStageWake();
                }
                actual = _stageTimer;
            }
            if (expired) { ExpireWait(); return false; }
            var created = actual is null;
            try
            {
                // Neither timer factory nor Change may hold the fixed-state
                // stop lock. The actual caller retains this update until return.
                actual ??= owner._time.CreateTimer(static state => ((Invocation)state!).StageWaitElapsed(),
                    this, timeout, Timeout.InfiniteTimeSpan);
                TimeSpan remaining;
                lock (_sync)
                {
                    _stageTimer = actual;
                    if (_completing || _stageWaitClosed || Volatile.Read(ref _admissionClosed) != 0)
                    { _stageTimerInitializing = false; return false; }
                    remaining = owner._time.GetElapsedTime(owner._time.GetTimestamp(), desiredUntil);
                }
                var changed = remaining > TimeSpan.Zero
                    && (created && remaining == timeout || actual.Change(remaining, Timeout.InfiniteTimeSpan));
                lock (_sync)
                {
                    _stageTimerInitializing = false;
                    if (!changed && remaining > TimeSpan.Zero) _stageTimerFaulted = true;
                    if (changed && !_completing && !_stageWaitClosed && Volatile.Read(ref _admissionClosed) == 0
                        && !StageClockClosesWait(owner._time.GetTimestamp()))
                    {
                        _stageWaitUntil = desiredUntil;
                        _stageWaitStage = stage;
                        SignalStageWake();
                        return true;
                    }
                    if (!_completing && !_stageWaitClosed && !_stageTimerFaulted
                        && StageClockClosesWait(owner._time.GetTimestamp())) RecordStageClockClosure();
                }
                ExpireWait();
                return false;
            }
            catch (Exception failure) when (Catchable(failure))
            {
                lock (_sync)
                {
                    _stageTimerInitializing = false;
                    _stageTimerFaulted = true;
                }
                ExpireWait();
                throw;
            }
        }

        private void StageWaitElapsed()
        {
            try
            {
                lock (_sync)
                {
                    if (_completing || _stageWaitClosed) return;
                    if (!StageClockClosesWait(owner._time.GetTimestamp()))
                    {
                        // Timer duration/clock quantization can deliver the
                        // only one-shot callback early. Its remaining wait is
                        // owned, never discarded or treated as elapsed time.
                        StartStageWake();
                        return;
                    }
                    RecordStageClockClosure();
                }
                ExpireWait();
            }
            catch (Exception failure) when (Catchable(failure))
            {
                // A faulted timeout observer supplies no cleanup witness.
                lock (_sync) _stageTimerFaulted = true;
                ExpireWait();
            }
        }

        private bool StageClockClosesWait(long now) =>
            now >= _stageWaitUntil;

        // Only an elapsed current stage clock records this cause. Host stop,
        // lease loss and timer faults do not create it.
        private void RecordStageClockClosure()
        {
            if (_stageWaitStage == BackgroundJobInvocationStage.Reconcile) _reconcileWaitExpired = true;
            _stageWaitClosed = true;
        }

        internal async Task<bool> JoinReconcileTimeoutCancellationAsync(BackgroundJobExecutionControl execution)
        {
            lock (_sync)
                if (!ReferenceEquals(_execution, execution) || _completing || !_reconcileWaitExpired) return false;
            // Timer closure can precede publication of the cancellation relay
            // by one continuation. Start/join that same once-owned relay here;
            // do not dispose the invocation or claim enclosing work has ended.
            RequestCancellation();
            Task<bool>? actual;
            lock (_sync) actual = _cancellationObservation;
            return actual is not null && await actual.ConfigureAwait(false);
        }
    }
}
