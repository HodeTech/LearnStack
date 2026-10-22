using System.Diagnostics.CodeAnalysis;
using LearnStack.Infrastructure.Jobs;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobPositiveSdkTests
{
    /// <summary>Schedules two actual pacing callbacks; all clocks, leases and stage timers stay real.</summary>
    [SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
        Justification = "The test joins runtime, actual timers and the factory before disposing barriers; an incomplete join retains them.")]
    private sealed class NativeSettlementTimeProvider : TimeProvider
    {
        internal static readonly TimeSpan RenewalPacing = TimeSpan.FromMilliseconds(1125);
        private readonly object _sync = new();
        private readonly NativeLeaseTimerObservation _leases = new();
        private readonly ManualResetEventSlim _firstArmed = new();
        private readonly CancellationTokenSource _abort = new();
        private readonly TaskCompletionSource<SettlementPacingTimer> _firstCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<SettlementPacingTimer> _secondCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _leaseReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private SettlementPacingTimer? _first, _second;
        private BackgroundJobExecutionControl? _original;
        private bool _closing, _leaseIssued;
        private int _fault, _leaseState, _cleanupState;

        internal BackgroundJobExecutionControl? Original { get { lock (_sync) return _original; } }
        internal ObservedNativeLeaseTimer Timer(BackgroundJobExecutionControl execution) => _leases.Timer(execution);
        public override long TimestampFrequency => System.TimestampFrequency;
        public override long GetTimestamp() => System.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (state is BackgroundJobExecutionControl execution)
            {
                var hold = false;
                lock (_sync)
                {
                    if (!_closing && !_leaseIssued)
                    {
                        _leaseIssued = true;
                        _original = execution;
                        hold = true;
                    }
                }
                try
                {
                    // Retain the real timer before waiting. Even an aborted or
                    // expired handshake returns its handle to the actual owner.
                    var actual = _leases.CreateTimer(callback, state, dueTime, period);
                    if (!hold) return actual;
                    Volatile.Write(ref _leaseState, 1);
                    if (execution.Identity is not null) RecordFault(PacingFault.Ambiguous);
                    try
                    {
                        if (_firstArmed.Wait(TimeSpan.FromSeconds(30), _abort.Token))
                            Volatile.Write(ref _leaseState, 2);
                        else { RecordFault(PacingFault.Timeout); Volatile.Write(ref _leaseState, 3); }
                    }
                    catch (OperationCanceledException) when (_abort.IsCancellationRequested)
                    { Volatile.Write(ref _leaseState, 3); }
                    return actual;
                }
                finally { if (hold) _leaseReturned.TrySetResult(); }
            }

            // This fixture deliberately uses a distinct validated cadence. The
            // native heartbeat/shared waits remain one second; typed execution
            // and invocation timers cannot be mistaken for pacing.
            if (state is HangfireGenerationWork.Invocation || dueTime != RenewalPacing || period != Timeout.InfiniteTimeSpan)
                return System.CreateTimer(callback, state, dueTime, period);
            SettlementPacingTimer? selected = null;
            TaskCompletionSource<SettlementPacingTimer>? publication = null;
            lock (_sync)
            {
                if (!_closing && _first is null)
                {
                    selected = _first = new(this, callback, state, TimeSpan.FromSeconds(30));
                    publication = _firstCreated;
                }
                else if (!_closing && _second is null && _first!.Forwarding)
                {
                    // The second watchdog covers the existing 10-second hint
                    // wait, 80-second completion wait and bounded observations.
                    // It does not extend any of those caller-owned deadlines.
                    selected = _second = new(this, callback, state, TimeSpan.FromMinutes(2));
                    publication = _secondCreated;
                }
                else if (!_closing && (_second is null || !_second.Released))
                    RecordFault(PacingFault.Ambiguous);
            }
            if (selected is null) return System.CreateTimer(callback, state, dueTime, period);
            try
            {
                selected.Create(dueTime, period);
                publication!.TrySetResult(selected);
                if (ReferenceEquals(selected, _first)) _firstArmed.Set();
                return selected;
            }
            catch (Exception error)
            {
                RecordFault(PacingFault.TimerFailed);
                publication!.TrySetException(error);
                throw;
            }
        }

        internal async Task WaitForFirstCallbackAsync(CancellationToken token)
        {
            var first = await _firstCreated.Task.WaitAsync(token);
            await first.Entered.WaitAsync(token);
            ThrowIfFaulted();
        }

        internal void ReleaseFirst()
        {
            SettlementPacingTimer first;
            lock (_sync) first = _first ?? throw new InvalidOperationException("The actual first pacing timer is absent.");
            first.Release();
        }

        internal async Task WaitForSecondTimerAsync(CancellationToken token)
        {
            await _secondCreated.Task.WaitAsync(token);
            AssertSecondHeld();
        }

        internal void AssertSecondHeld()
        {
            lock (_sync)
                if (_first is null || _second is null || !_first.Forwarding || _second.Released)
                    RecordFault(PacingFault.Ambiguous);
            ThrowIfFaulted();
        }

        internal void ReleaseAll()
        {
            SettlementPacingTimer? first, second;
            lock (_sync) { _closing = true; first = _first; second = _second; }
            // Sealing precedes every release: later factories always pass through.
            _abort.Cancel();
            first?.Release();
            second?.Release();
        }

        internal async Task JoinAsync(bool runtimeJoined)
        {
            Volatile.Write(ref _cleanupState, 2);
            SettlementPacingTimer? first, second;
            bool leaseIssued;
            lock (_sync) { first = _first; second = _second; leaseIssued = _leaseIssued; }
            try
            {
                await Task.WhenAll(first?.JoinAsync() ?? Task.CompletedTask,
                    second?.JoinAsync() ?? Task.CompletedTask, leaseIssued ? _leaseReturned.Task : Task.CompletedTask);
                if (!runtimeJoined) { Volatile.Write(ref _cleanupState, 2); return; }
                // Actual runtime completion closes the factory census. Timer
                // disposal joins queued callbacks, including cancellation before
                // entry; no callback-return signal is manufactured for that case.
                first?.DisposeBarrier();
                second?.DisposeBarrier();
                _firstArmed.Dispose();
                _abort.Dispose();
                Volatile.Write(ref _cleanupState, 1);
            }
            catch { Volatile.Write(ref _cleanupState, 2); throw; }
        }

        internal void ThrowIfFaulted()
        {
            if (Volatile.Read(ref _fault) != 0)
                throw new InvalidOperationException("Actual renewal pacing observation failed.");
        }

        internal string Diagnostic
        {
            get
            {
                lock (_sync)
                    return $"Native settlement pacing;lease={Volatile.Read(ref _leaseState) switch { 0 => "unseen", 1 => "waiting", 2 => "released", _ => "aborted" }};"
                        + $"first={_first?.State ?? "not_issued"};second={_second?.State ?? "not_issued"};"
                        + $"fault={(PacingFault)Volatile.Read(ref _fault) switch { PacingFault.None => "none", PacingFault.Ambiguous => "ambiguous", PacingFault.Timeout => "timeout", PacingFault.CallbackFailed => "callback_failed", _ => "timer_failed" }};"
                        + $"cleanup={Volatile.Read(ref _cleanupState) switch { 0 => "not_started", 1 => "joined", _ => "incomplete" }}.";
            }
        }

        private void RecordFault(PacingFault fault) => Interlocked.CompareExchange(ref _fault, (int)fault, 0);
        private enum PacingFault { None, Ambiguous, Timeout, CallbackFailed, TimerFailed }

        private sealed class SettlementPacingTimer(NativeSettlementTimeProvider owner, TimerCallback callback,
            object? callbackState, TimeSpan watchdog) : ITimer
        {
            private readonly ManualResetEventSlim _release = new();
            private readonly TaskCompletionSource<ITimer> _created = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private ITimer? _actual;
            private int _released, _forwarding, _returned, _joined, _joinFailed;
            internal Task Entered => _entered.Task;
            internal bool Forwarding => Volatile.Read(ref _forwarding) != 0;
            internal bool Released => Volatile.Read(ref _released) != 0;
            internal string State => Volatile.Read(ref _joinFailed) != 0 ? "incomplete"
                : Volatile.Read(ref _joined) != 0 ? _entered.Task.IsCompleted ? "joined" : "cancelled"
                : Volatile.Read(ref _returned) != 0 ? "returned" : _entered.Task.IsCompleted ? "entered" : "armed";

            internal void Create(TimeSpan dueTime, TimeSpan period)
            {
                try
                {
                    _actual = TimeProvider.System.CreateTimer(static state => ((SettlementPacingTimer)state!).Invoke(), this, dueTime, period);
                    _created.TrySetResult(_actual);
                }
                catch (Exception error) { _created.TrySetException(error); throw; }
            }

            private void Invoke()
            {
                _entered.TrySetResult();
                try
                {
                    if (!_release.Wait(watchdog)) owner.RecordFault(PacingFault.Timeout);
                    Volatile.Write(ref _forwarding, 1);
                    callback(callbackState);
                }
                catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
                { owner.RecordFault(PacingFault.CallbackFailed); }
                finally { Volatile.Write(ref _returned, 1); }
            }

            internal void Release() { Volatile.Write(ref _released, 1); _release.Set(); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => _actual!.Change(dueTime, period);
            // Task.Delay can dispose its timer inside this very callback. Never
            // wait for that callback from these synchronous disposal semantics.
            public void Dispose() => _actual!.Dispose();
            public ValueTask DisposeAsync() => _actual!.DisposeAsync();
            internal async Task JoinAsync()
            {
                try
                {
                    var actual = await _created.Task;
                    await actual.DisposeAsync();
                    if (_entered.Task.IsCompleted && Volatile.Read(ref _returned) == 0)
                        throw new InvalidOperationException("Actual pacing callback cleanup is incomplete.");
                    Volatile.Write(ref _joined, 1);
                }
                catch { Volatile.Write(ref _joinFailed, 1); throw; }
            }
            internal void DisposeBarrier() => _release.Dispose();
        }
    }
}
