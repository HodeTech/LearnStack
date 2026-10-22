using System.Diagnostics.CodeAnalysis;
using LearnStack.Infrastructure.Jobs;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobPositiveSdkTests
{
    /// <summary>Schedules two actual pacing callbacks; all clocks, leases and stage timers stay real.</summary>
    [SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
        Justification = "The test joins runtime, actual timers and the factory before disposing barriers; an incomplete join retains them.")]
    private sealed class NativeSettlementTimeProvider(bool retainedRecovery) : TimeProvider
    {
        internal static readonly TimeSpan RenewalPacing = TimeSpan.FromMilliseconds(1125);
        private readonly object _sync = new();
        private readonly NativeLeaseTimerObservation _leases = new();
        private readonly ManualResetEventSlim _firstArmed = new();
        private readonly ManualResetEventSlim _allowFirstCreation = new();
        private readonly CancellationTokenSource _abort = new();
        private readonly AsyncLocal<DeadlineFactorySlot?> _deadlineSlot = new();
        private readonly TaskCompletionSource<SettlementPacingTimer> _deadlineCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _deadlineWaiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstFactoryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<SettlementPacingTimer> _firstCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<SettlementPacingTimer> _secondCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _leaseReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private SettlementPacingTimer? _deadline, _first, _second;
        private BackgroundJobExecutionControl? _original;
        private bool _closing, _leaseIssued, _normalSlotIssued;
        private int _fault, _leaseState, _deadlineWaitState, _cleanupState;

        internal BackgroundJobExecutionControl? Original { get { lock (_sync) return _original; } }
        internal ObservedNativeLeaseTimer Timer(BackgroundJobExecutionControl execution) => _leases.Timer(execution);
        public override long TimestampFrequency => System.TimestampFrequency;
        public override long GetTimestamp() => System.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            // ObserveDeadlineAsync creates its Task.Delay timer in the next
            // synchronous prefix after the normal lease factory returns. The
            // shared consumed bit also closes copies of this execution context.
            if (_deadlineSlot.Value is { } slot && Interlocked.CompareExchange(ref slot.Consumed, 1, 0) == 0)
                return CreateNormalDeadlineTimer(slot, callback, state, dueTime, period);
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
                    // Normal publication must return this real lease promptly:
                    // its already-computed deadline timer is created next.
                    var actual = _leases.CreateTimer(callback, state, dueTime, period);
                    if (!hold) return actual;
                    if (execution.Identity is not null) RecordFault(PacingFault.Ambiguous);
                    if (!retainedRecovery)
                    {
                        lock (_sync)
                            if (!_closing)
                            {
                                _deadlineSlot.Value = new(execution);
                                _normalSlotIssued = true;
                            }
                        Volatile.Write(ref _leaseState, 2);
                        return actual;
                    }
                    // Recovery re-debits its lease after this actual factory
                    // returns and has no normal deadline observer to postpone.
                    Volatile.Write(ref _leaseState, 1);
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
                if (ReferenceEquals(selected, _first) && !retainedRecovery)
                {
                    _firstFactoryEntered.TrySetResult();
                    try
                    {
                        if (!_allowFirstCreation.Wait(TimeSpan.FromSeconds(30), _abort.Token))
                            RecordFault(PacingFault.Timeout);
                    }
                    catch (OperationCanceledException) when (_abort.IsCancellationRequested) { }
                }
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
            finally { selected.MarkFactoryReturned(); }
        }

        private ITimer CreateNormalDeadlineTimer(DeadlineFactorySlot slot, TimerCallback callback,
            object? state, TimeSpan dueTime, TimeSpan period)
        {
            SettlementPacingTimer? selected = null;
            lock (_sync)
            {
                if (!_closing)
                {
                    if (retainedRecovery || !ReferenceEquals(slot.Execution, _original) || slot.Execution.Identity is not null
                        || _deadline is not null || state is BackgroundJobExecutionControl or HangfireGenerationWork.Invocation
                        || dueTime <= TimeSpan.Zero || period != Timeout.InfiniteTimeSpan)
                        RecordFault(PacingFault.Ambiguous);
                    else selected = _deadline = new(this, callback, state, TimeSpan.Zero, holdCallback: false);
                }
            }
            if (selected is null)
            {
                _deadlineCreated.TrySetException(new InvalidOperationException("The actual normal deadline timer was not selected."));
                return System.CreateTimer(callback, state, dueTime, period);
            }
            try
            {
                // Arm the original System timer before the publication hold.
                // Its callback is never gated, and its duration is unchanged.
                selected.Create(dueTime, period);
                _deadlineCreated.TrySetResult(selected);
                Volatile.Write(ref _deadlineWaitState, 1);
                _deadlineWaiting.TrySetResult();
                try
                {
                    if (_firstArmed.Wait(TimeSpan.FromSeconds(30), _abort.Token))
                        Volatile.Write(ref _deadlineWaitState, 2);
                    else { RecordFault(PacingFault.Timeout); Volatile.Write(ref _deadlineWaitState, 3); }
                }
                catch (OperationCanceledException) when (_abort.IsCancellationRequested)
                { Volatile.Write(ref _deadlineWaitState, 3); }
                return selected;
            }
            catch (Exception error)
            {
                RecordFault(PacingFault.TimerFailed);
                _deadlineCreated.TrySetException(error);
                throw;
            }
            finally { selected.MarkFactoryReturned(); }
        }

        internal async Task ProveNormalDeadlineArmedBeforePacingAsync(CancellationToken token)
        {
            await _deadlineCreated.Task.WaitAsync(token);
            await Task.WhenAll(_deadlineWaiting.Task, _firstFactoryEntered.Task).WaitAsync(token);
            AssertNormalDeadlineHeld();
            var before = System.GetTimestamp();
            // Deliberately spend real time under the existing startup budget.
            // The old lease-first hold cannot reach this armed-deadline witness.
            await Task.Delay(TimeSpan.FromMilliseconds(125), System, token);
            if (System.GetElapsedTime(before) < TimeSpan.FromMilliseconds(100)) RecordFault(PacingFault.Ambiguous);
            AssertNormalDeadlineHeld();
            _allowFirstCreation.Set();
        }

        private void AssertNormalDeadlineHeld()
        {
            lock (_sync)
            {
                if (retainedRecovery || _original?.Identity is not null || !_leaseReturned.Task.IsCompleted
                    || _deadline is null || !_deadline.Created || _deadline.FactoryReturned
                    || Volatile.Read(ref _deadlineWaitState) != 1 || _first is null || _first.Created)
                    RecordFault(PacingFault.Ambiguous);
                if (_original is not null) _ = _leases.Timer(_original);
                else RecordFault(PacingFault.Ambiguous);
            }
            ThrowIfFaulted();
        }

        private sealed class DeadlineFactorySlot(BackgroundJobExecutionControl execution)
        {
            internal BackgroundJobExecutionControl Execution { get; } = execution;
            internal int Consumed;
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
            _allowFirstCreation.Set();
            first?.Release();
            second?.Release();
        }

        internal async Task JoinAsync(bool runtimeJoined)
        {
            Volatile.Write(ref _cleanupState, 2);
            SettlementPacingTimer? deadline, first, second;
            bool leaseIssued, normalSlotIssued;
            lock (_sync)
            { deadline = _deadline; first = _first; second = _second; leaseIssued = _leaseIssued; normalSlotIssued = _normalSlotIssued; }
            try
            {
                await Task.WhenAll(deadline?.JoinAsync() ?? Task.CompletedTask, first?.JoinAsync() ?? Task.CompletedTask,
                    second?.JoinAsync() ?? Task.CompletedTask, leaseIssued ? _leaseReturned.Task : Task.CompletedTask);
                if (!runtimeJoined) { Volatile.Write(ref _cleanupState, 2); return; }
                if (normalSlotIssued && deadline is null) RecordFault(PacingFault.Ambiguous);
                // Actual runtime completion closes the factory census. Timer
                // disposal joins queued callbacks, including cancellation before
                // entry; no callback-return signal is manufactured for that case.
                deadline?.DisposeBarrier();
                first?.DisposeBarrier();
                second?.DisposeBarrier();
                _firstArmed.Dispose();
                _allowFirstCreation.Dispose();
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
                        + $"deadline={_deadline?.State ?? "not_issued"};"
                        + $"first={_first?.State ?? "not_issued"};second={_second?.State ?? "not_issued"};"
                        + $"fault={(PacingFault)Volatile.Read(ref _fault) switch { PacingFault.None => "none", PacingFault.Ambiguous => "ambiguous", PacingFault.Timeout => "timeout", PacingFault.CallbackFailed => "callback_failed", _ => "timer_failed" }};"
                        + $"cleanup={Volatile.Read(ref _cleanupState) switch { 0 => "not_started", 1 => "joined", _ => "incomplete" }}.";
            }
        }

        private void RecordFault(PacingFault fault) => Interlocked.CompareExchange(ref _fault, (int)fault, 0);
        private enum PacingFault { None, Ambiguous, Timeout, CallbackFailed, TimerFailed }

        private sealed class SettlementPacingTimer(NativeSettlementTimeProvider owner, TimerCallback callback,
            object? callbackState, TimeSpan watchdog, bool holdCallback = true) : ITimer
        {
            private readonly ManualResetEventSlim _release = new();
            private readonly TaskCompletionSource<ITimer> _created = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _factoryReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private ITimer? _actual;
            private int _released, _forwarding, _returned, _joined, _joinFailed;
            internal Task Entered => _entered.Task;
            internal bool Created => _created.Task.IsCompletedSuccessfully;
            internal bool FactoryReturned => _factoryReturned.Task.IsCompleted;
            internal bool Forwarding => Volatile.Read(ref _forwarding) != 0;
            internal bool Released => Volatile.Read(ref _released) != 0;
            internal string State => Volatile.Read(ref _joinFailed) != 0 ? "incomplete"
                : Volatile.Read(ref _joined) != 0 ? _entered.Task.IsCompleted ? "joined" : "cancelled"
                : Volatile.Read(ref _returned) != 0 ? "returned" : _entered.Task.IsCompleted ? "entered" : Created ? "armed" : "creating";

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
                    if (holdCallback && !_release.Wait(watchdog)) owner.RecordFault(PacingFault.Timeout);
                    Volatile.Write(ref _forwarding, 1);
                    callback(callbackState);
                }
                catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
                { owner.RecordFault(PacingFault.CallbackFailed); }
                finally { Volatile.Write(ref _returned, 1); }
            }

            internal void Release() { Volatile.Write(ref _released, 1); _release.Set(); }
            internal void MarkFactoryReturned() => _factoryReturned.TrySetResult();
            public bool Change(TimeSpan dueTime, TimeSpan period) => _actual!.Change(dueTime, period);
            // Task.Delay can dispose its timer inside this very callback. Never
            // wait for that callback from these synchronous disposal semantics.
            public void Dispose() => _actual!.Dispose();
            public ValueTask DisposeAsync() => _actual!.DisposeAsync();
            internal async Task JoinAsync()
            {
                try
                {
                    await _factoryReturned.Task;
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
