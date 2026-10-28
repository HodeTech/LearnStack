using System.Diagnostics.CodeAnalysis;
using LearnStack.Infrastructure.Jobs;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobPositiveSdkTests
{
    /// <summary>Holds one actual renewal pacing callback after the old-lease proof; all clocks remain real.</summary>
    [SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
        Justification = "The test joins control, the actual timer and its factory before disposing the barrier; an incomplete join retains it.")]
    private sealed class NativeRecoveryTimeProvider : TimeProvider
    {
        internal static readonly TimeSpan RenewalPacing = TimeSpan.FromMilliseconds(1125);
        private readonly object _sync = new();
        private readonly NativeLeaseTimerObservation _leases = new();
        private readonly TaskCompletionSource<RecoveryPacingTimer> _selected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private RecoveryPacingTimer? _held;
        private bool _holdRequested, _closing;
        private int _fault;

        internal ObservedNativeLeaseTimer Timer(BackgroundJobExecutionControl execution) => _leases.Timer(execution);
        public override long TimestampFrequency => System.TimestampFrequency;
        public override long GetTimestamp() => System.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            // The distinct validated cadence cannot select the one-second SQL
            // waits or typed lease/stage timers. Earlier pacing passes through.
            if (state is BackgroundJobExecutionControl or HangfireGenerationWork.Invocation
                || dueTime != RenewalPacing || period != Timeout.InfiniteTimeSpan)
                return _leases.CreateTimer(callback, state, dueTime, period);
            RecoveryPacingTimer? selected = null;
            lock (_sync)
                if (!_closing && _holdRequested && _held is null)
                    selected = _held = new(this, callback, state);
            if (selected is null) return System.CreateTimer(callback, state, dueTime, period);
            try
            {
                // RunRenewalAsync creates this delay only after the previous
                // batch has returned, been applied and released its flight.
                selected.Create(dueTime, period);
                _selected.TrySetResult(selected);
                return selected;
            }
            catch (Exception error)
            {
                RecordFault();
                _selected.TrySetException(error);
                throw;
            }
            finally { selected.MarkFactoryReturned(); }
        }

        internal async Task HoldNextCallbackAsync(CancellationToken token)
        {
            lock (_sync)
            {
                if (_closing || _holdRequested) throw new InvalidOperationException("Recovery pacing can be held only once.");
                _holdRequested = true;
            }
            var selected = await _selected.Task.WaitAsync(token);
            await selected.Entered.WaitAsync(token);
            AssertHeld();
        }

        internal void AssertHeld()
        {
            lock (_sync)
                if (_held is null || !_held.Entered.IsCompleted || _held.Released || _held.Forwarding)
                    RecordFault();
            ThrowIfFaulted();
        }

        internal void ReleaseAll()
        {
            RecoveryPacingTimer? held;
            lock (_sync) { _closing = true; held = _held; }
            // Seal selection before release; a concurrent factory remains rooted
            // in _held and later factories return ordinary system timers.
            held?.Release();
        }

        internal async Task JoinAsync(bool controlJoined)
        {
            ReleaseAll();
            RecoveryPacingTimer? held;
            lock (_sync) held = _held;
            if (held is null) return;
            await held.JoinAsync();
            // Runtime completion closes the factory census. A failed control
            // join retains the barrier even when this timer has been disposed.
            if (controlJoined) held.DisposeBarrier();
        }

        internal void ThrowIfFaulted()
        {
            if (Volatile.Read(ref _fault) != 0)
                throw new InvalidOperationException("Actual recovery renewal pacing observation failed.");
        }

        private void RecordFault() => Interlocked.Exchange(ref _fault, 1);

        private sealed class RecoveryPacingTimer(NativeRecoveryTimeProvider owner, TimerCallback callback,
            object? callbackState) : ITimer
        {
            private readonly object _disposeSync = new();
            private readonly ManualResetEventSlim _release = new();
            private readonly TaskCompletionSource<ITimer> _created = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _factoryReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private ITimer? _actual;
            private Task? _disposal;
            private int _released, _forwarding, _returned;
            internal Task Entered => _entered.Task;
            internal bool Released => Volatile.Read(ref _released) != 0;
            internal bool Forwarding => Volatile.Read(ref _forwarding) != 0;

            internal void Create(TimeSpan dueTime, TimeSpan period)
            {
                try
                {
                    _actual = TimeProvider.System.CreateTimer(static state => ((RecoveryPacingTimer)state!).Invoke(), this, dueTime, period);
                    _created.TrySetResult(_actual);
                }
                catch (Exception error) { _created.TrySetException(error); throw; }
            }

            private void Invoke()
            {
                _entered.TrySetResult();
                try
                {
                    // This safety watchdog cannot extend the caller's shared
                    // 15-second settlement budget or prove callback disposal.
                    if (!_release.Wait(TimeSpan.FromSeconds(30))) owner.RecordFault();
                    Volatile.Write(ref _forwarding, 1);
                    callback(callbackState);
                }
                catch (Exception error) when (NativeWaitCatchable(error)) { owner.RecordFault(); }
                finally { Volatile.Write(ref _returned, 1); }
            }

            internal void Release() { Volatile.Write(ref _released, 1); _release.Set(); }
            internal void MarkFactoryReturned() => _factoryReturned.TrySetResult();
            public bool Change(TimeSpan dueTime, TimeSpan period) => _actual!.Change(dueTime, period);
            // Task.Delay may dispose its timer inside this callback. Begin and
            // retain actual asynchronous disposal without waiting on ourselves;
            // the final join still owns this callback after synchronous Dispose.
            public void Dispose() => _ = DisposeActual();
            public ValueTask DisposeAsync() => new(DisposeActual());
            private Task DisposeActual()
            {
                lock (_disposeSync) return _disposal ??= _actual!.DisposeAsync().AsTask();
            }
            internal async Task JoinAsync()
            {
                await _factoryReturned.Task;
                await _created.Task;
                await DisposeActual();
                if (_entered.Task.IsCompleted && Volatile.Read(ref _returned) == 0)
                    throw new InvalidOperationException("Actual recovery pacing callback cleanup is incomplete.");
            }
            internal void DisposeBarrier() => _release.Dispose();
        }
    }
}
