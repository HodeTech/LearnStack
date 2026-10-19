namespace LearnStack.Infrastructure.Jobs;

internal sealed partial class HangfireGenerationWork
{
    internal sealed partial class Invocation
    {
        private Task<bool>? _stageWakeObservation;
        private TaskCompletionSource _stageWakeSignal = NewStageWakeSignal();

        private static TaskCompletionSource NewStageWakeSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Called under the fixed-state lock; continuations never run inline.
        private void SignalStageWake() => _stageWakeSignal.TrySetResult();

        private void StartStageWake()
        {
            SignalStageWake();
            if (_stageWakeObservation is not null) return;
            // Publish the once-owned task before it can pass the state lock.
            // No ambient module context or callback is carried into this wait.
            if (ExecutionContext.IsFlowSuppressed()) _stageWakeObservation = Task.Run(ObserveStageWakeAsync);
            else
            {
                using (ExecutionContext.SuppressFlow()) _stageWakeObservation = Task.Run(ObserveStageWakeAsync);
            }
        }

        private async Task<bool> ObserveStageWakeAsync()
        {
            ITimer? timer = null;
            var clean = true;
            try
            {
                while (true)
                {
                    Task signal;
                    TimeSpan remaining;
                    var elapsed = false;
                    lock (_sync)
                    {
                        if (_completing || _stageWaitClosed) break;
                        var now = owner._time.GetTimestamp();
                        remaining = owner._time.GetElapsedTime(now, _stageWaitUntil);
                        if (StageClockClosesWait(now))
                        {
                            RecordStageClockClosure();
                            elapsed = true;
                        }
                        _stageWakeSignal = NewStageWakeSignal();
                        signal = _stageWakeSignal.Task;
                    }
                    if (elapsed) { ExpireWait(); break; }
                    // Only this observer changes/disposes this supplementary
                    // timer. Its callback merely signals, so neither queued old
                    // callbacks nor a concurrent primary Change borrow a stage.
                    timer ??= owner._time.CreateTimer(static state => ((Invocation)state!).StageWakeElapsed(),
                        this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                    lock (_sync)
                        if (_completing || _stageWaitClosed) break;
                    var rounded = TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(remaining.TotalMilliseconds)));
                    if (!timer.Change(rounded, Timeout.InfiniteTimeSpan))
                        throw new InvalidOperationException("The owned stage wake timer refused its remaining wait.");
                    lock (_sync)
                    {
                        if (!_completing && !_stageWaitClosed && StageClockClosesWait(owner._time.GetTimestamp()))
                        {
                            RecordStageClockClosure();
                            elapsed = true;
                        }
                    }
                    if (elapsed) { ExpireWait(); break; }
                    await signal.ConfigureAwait(false);
                }
            }
            catch (Exception failure) when (Catchable(failure))
            {
                clean = false;
                lock (_sync) _stageTimerFaulted = true;
                ExpireWait();
            }
            finally
            {
                if (timer is not null)
                {
                    try { await timer.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception failure) when (Catchable(failure)) { clean = false; }
                }
                if (!clean) owner.MarkRetained(this);
            }
            return clean;
        }

        private void StageWakeElapsed()
        {
            lock (_sync) SignalStageWake();
        }
    }
}
