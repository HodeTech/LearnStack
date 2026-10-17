using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

public sealed class HangfireInvocationWaitTests
{
    [Fact]
    public async Task A_queued_short_stage_callback_cannot_expire_the_long_external_successor()
    {
        var time = new ControlledStageTime();
        var work = Work(time);
        var invocation = work.TryBegin()!;
        invocation.BeginStageWait(BackgroundJobInvocationStage.Framework).Should().BeTrue();
        time.Advance(TimeSpan.FromSeconds(1));
        invocation.BeginStageWait(BackgroundJobInvocationStage.Execute).Should().BeTrue();
        time.Advance(TimeSpan.FromSeconds(30));
        time.Timer.FireQueued();
        invocation.ReturnRequested.IsCompleted.Should().BeFalse();
        invocation.Token.IsCancellationRequested.Should().BeFalse();
        work.Snapshot.Retained.Should().Be(0);
        time.Timer.Changes.Should().Be(1);
        time.Advance(TimeSpan.FromHours(1));
        time.Timer.FireQueued();
        await invocation.ReturnRequested.WaitAsync(TimeSpan.FromSeconds(10));
        invocation.Token.IsCancellationRequested.Should().BeTrue();
        work.Snapshot.Retained.Should().Be(1);
        invocation.BeginStageWait(BackgroundJobInvocationStage.Outcome).Should().BeFalse();
        await invocation.CompleteAsync(true);
        work.CloseStarts();
        await work.Completion;
        time.Timer.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task An_elapsed_predecessor_cannot_borrow_an_unfired_timer_to_start_a_new_stage()
    {
        var time = new ControlledStageTime();
        var work = Work(time);
        var invocation = work.TryBegin()!;
        invocation.BeginStageWait(BackgroundJobInvocationStage.Prepare).Should().BeTrue();
        time.Advance(TimeSpan.FromSeconds(31));
        invocation.BeginStageWait(BackgroundJobInvocationStage.Execute).Should().BeFalse();
        invocation.ReturnRequested.IsCompletedSuccessfully.Should().BeTrue();
        time.Timer.Changes.Should().Be(0);
        work.Snapshot.Retained.Should().Be(1);
        await invocation.CompleteAsync(true);
        work.CloseStarts();
        await work.Completion;
    }

    [Fact]
    public async Task Actual_timer_disposal_and_a_blocked_cancellation_callback_both_precede_private_source_release()
    {
        var time = new ControlledStageTime(holdDisposal: true);
        var work = Work(time);
        var invocation = work.TryBegin()!;
        var token = invocation.Token;
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callback = token.Register(() =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("The held stage callback was not released.");
        });
        Task? cleanup = null;
        try
        {
            invocation.BeginStageWait(BackgroundJobInvocationStage.Arm).Should().BeTrue();
            time.Advance(TimeSpan.FromSeconds(6));
            time.Timer.FireQueued();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            invocation.ReturnRequested.IsCompletedSuccessfully.Should().BeTrue();
            work.Snapshot.Retained.Should().Be(1);
            work.CloseStarts();
            cleanup = invocation.CompleteAsync(true);
            await time.Timer.DisposalEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cleanup.IsCompleted.Should().BeFalse();
            work.Completion.IsCompleted.Should().BeFalse();
            time.Timer.ReleaseDisposal.TrySetResult();
            await time.Timer.DisposedSignal.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Func<Task> prematureCleanup = () => cleanup.WaitAsync(TimeSpan.FromMilliseconds(250));
            await prematureCleanup.Should().ThrowAsync<TimeoutException>("actual timer disposal alone cannot join the held cancellation callback");
            work.Completion.IsCompleted.Should().BeFalse();
            token.WaitHandle.Should().NotBeNull();
        }
        finally
        {
            release.Set();
            time.Timer.ReleaseDisposal.TrySetResult();
            if (cleanup is not null) await cleanup.WaitAsync(TimeSpan.FromSeconds(10));
        }
        await work.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var handle = () => token.WaitHandle;
        handle.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task Undefined_stage_refuses_before_creating_any_timer()
    {
        var time = new ControlledStageTime();
        var work = Work(time);
        var invocation = work.TryBegin()!;
        var invalid = () => invocation.BeginStageWait((BackgroundJobInvocationStage)int.MaxValue);
        invalid.Should().Throw<ArgumentOutOfRangeException>();
        time.Created.Should().Be(0);
        await invocation.CompleteAsync(true);
        work.CloseStarts();
        await work.Completion;
    }

    [Theory]
    [InlineData("host_stop")]
    [InlineData("ordinary_expiry")]
    [InlineData("reconcile_expiry")]
    public async Task Only_actual_reconcile_stage_expiry_records_the_probe_timeout_cause(string mode)
    {
        var time = new ControlledStageTime();
        var work = Work(time);
        var invocation = work.TryBegin()!;
        invocation.BeginStageWait(mode == "ordinary_expiry"
            ? BackgroundJobInvocationStage.Prepare : BackgroundJobInvocationStage.Reconcile).Should().BeTrue();
        if (mode == "host_stop") work.RequestHostStop();
        else
        {
            time.Advance(TimeSpan.FromSeconds(mode == "ordinary_expiry" ? 31 : 11));
            time.Timer.FireQueued();
        }
        await invocation.ReturnRequested.WaitAsync(TimeSpan.FromSeconds(10));
        invocation.ReconcileWaitExpired.Should().Be(mode == "reconcile_expiry");
        invocation.Token.IsCancellationRequested.Should().BeTrue();
        await invocation.CompleteAsync(true);
        work.CloseStarts();
        await work.Completion;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_elapsed_predecessor_retains_its_own_cause_when_the_successor_is_refused(bool reconcileFirst)
    {
        var time = new ControlledStageTime();
        var work = Work(time);
        var invocation = work.TryBegin()!;
        invocation.BeginStageWait(reconcileFirst ? BackgroundJobInvocationStage.Reconcile : BackgroundJobInvocationStage.Prepare)
            .Should().BeTrue();
        time.Advance(TimeSpan.FromSeconds(31));
        invocation.BeginStageWait(reconcileFirst ? BackgroundJobInvocationStage.Outcome : BackgroundJobInvocationStage.Reconcile)
            .Should().BeFalse();
        invocation.ReconcileWaitExpired.Should().Be(reconcileFirst,
            "a rejected Reconcile start cannot borrow another stage's elapsed clock");
        await invocation.CompleteAsync(true);
        work.CloseStarts();
        await work.Completion;
    }

    [Fact]
    public async Task A_queued_reconcile_callback_cannot_label_its_live_successor_as_a_probe_timeout()
    {
        var time = new ControlledStageTime();
        var work = Work(time);
        var invocation = work.TryBegin()!;
        invocation.BeginStageWait(BackgroundJobInvocationStage.Reconcile).Should().BeTrue();
        time.Advance(TimeSpan.FromSeconds(1));
        invocation.BeginStageWait(BackgroundJobInvocationStage.Execute).Should().BeTrue();
        time.Advance(TimeSpan.FromSeconds(20));
        time.Timer.FireQueued();
        invocation.ReturnRequested.IsCompleted.Should().BeFalse();
        invocation.ReconcileWaitExpired.Should().BeFalse();
        time.Advance(TimeSpan.FromHours(1));
        time.Timer.FireQueued();
        invocation.ReconcileWaitExpired.Should().BeFalse("the actual elapsed stage is Execute");
        await invocation.CompleteAsync(true);
        work.CloseStarts();
        await work.Completion;
    }

    [Theory]
    [InlineData(5_000)]
    [InlineData(12_880)]
    [InlineData(50_000)]
    public async Task An_early_one_shot_callback_keeps_one_owned_remaining_wake_without_expiring_early(long remainingTicks)
    {
        var time = new ControlledStageTime();
        var work = Work(time);
        var invocation = work.TryBegin()!;
        invocation.BeginStageWait(BackgroundJobInvocationStage.Reconcile).Should().BeTrue();
        time.Advance(TimeSpan.FromSeconds(10) - TimeSpan.FromTicks(remainingTicks));
        time.Timer.FireQueued();
        var remaining = await time.RemainingTimer.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await remaining.WaitForChangeAsync(0);
        invocation.ReturnRequested.IsCompleted.Should().BeFalse("an early wake is not elapsed monotonic time");
        invocation.ReconcileWaitExpired.Should().BeFalse();
        time.Advance(TimeSpan.FromTicks(remainingTicks));
        remaining.FireQueued();
        await invocation.ReturnRequested.WaitAsync(TimeSpan.FromSeconds(10));
        invocation.ReconcileWaitExpired.Should().BeTrue();
        await invocation.CompleteAsync(true);
        work.CloseStarts();
        await work.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        time.Created.Should().Be(2);
        time.Timer.Disposed.Should().BeTrue();
        remaining.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task A_positive_sub_TimeSpan_tick_remainder_cannot_become_a_reconcile_timeout()
    {
        var time = new ControlledStageTime(timestampFrequency: 1_000_000_000);
        var work = Work(time);
        var invocation = work.TryBegin()!;
        invocation.BeginStageWait(BackgroundJobInvocationStage.Reconcile).Should().BeTrue();
        time.AdvanceTimestamp(10 * time.TimestampFrequency - 1);
        time.Timer.FireQueued();
        var first = await Task.WhenAny(time.RemainingTimer.Task, invocation.ReturnRequested).WaitAsync(TimeSpan.FromSeconds(10));
        first.Should().BeSameAs(time.RemainingTimer.Task, "TimeSpan conversion must not round a positive timestamp remainder into expiry");
        var remaining = await time.RemainingTimer.Task;
        await remaining.WaitForChangeAsync(0);
        invocation.ReturnRequested.IsCompleted.Should().BeFalse();
        time.AdvanceTimestamp(1);
        remaining.FireQueued();
        await invocation.ReturnRequested.WaitAsync(TimeSpan.FromSeconds(10));
        invocation.ReconcileWaitExpired.Should().BeTrue();
        await invocation.CompleteAsync(true);
        work.CloseStarts();
        await work.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Repeated_early_remaining_wakes_recheck_time_and_do_not_create_more_timers()
    {
        var time = new ControlledStageTime();
        var work = Work(time);
        var invocation = work.TryBegin()!;
        invocation.BeginStageWait(BackgroundJobInvocationStage.Reconcile).Should().BeTrue();
        time.Advance(TimeSpan.FromSeconds(9));
        time.Timer.FireQueued();
        var remaining = await time.RemainingTimer.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await remaining.WaitForChangeAsync(0);
        for (var i = 0; i < 3; i++)
        {
            var changes = remaining.Changes;
            remaining.FireQueued();
            await remaining.WaitForChangeAsync(changes);
            invocation.ReturnRequested.IsCompleted.Should().BeFalse();
        }
        time.Created.Should().Be(2);
        time.Advance(TimeSpan.FromSeconds(1));
        remaining.FireQueued();
        await invocation.ReturnRequested.WaitAsync(TimeSpan.FromSeconds(10));
        await invocation.CompleteAsync(true);
        work.CloseStarts();
        await work.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_remaining_wake_rearms_for_the_current_successor_and_joins_its_actual_disposal()
    {
        var time = new ControlledStageTime(holdRemainingDisposal: true);
        var work = Work(time);
        var invocation = work.TryBegin()!;
        var token = invocation.Token;
        invocation.BeginStageWait(BackgroundJobInvocationStage.Reconcile).Should().BeTrue();
        time.Advance(TimeSpan.FromSeconds(1));
        time.Timer.FireQueued();
        var remaining = await time.RemainingTimer.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await remaining.WaitForChangeAsync(0);
        var before = remaining.Changes;
        invocation.BeginStageWait(BackgroundJobInvocationStage.Execute).Should().BeTrue();
        await remaining.WaitForChangeAsync(before);
        time.Advance(TimeSpan.FromSeconds(10));
        before = remaining.Changes;
        remaining.FireQueued();
        await remaining.WaitForChangeAsync(before);
        invocation.ReturnRequested.IsCompleted.Should().BeFalse();
        invocation.ReconcileWaitExpired.Should().BeFalse();
        work.RequestHostStop();
        var cleanup = invocation.CompleteAsync(true);
        try
        {
            await remaining.DisposalEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cleanup.IsCompleted.Should().BeFalse();
            work.Completion.IsCompleted.Should().BeFalse();
            token.WaitHandle.Should().NotBeNull();
        }
        finally
        {
            remaining.ReleaseDisposal.TrySetResult();
            await cleanup.WaitAsync(TimeSpan.FromSeconds(10));
        }
        await work.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var handle = () => token.WaitHandle;
        handle.Should().Throw<ObjectDisposedException>();
    }

    [Theory]
    [InlineData("create")]
    [InlineData("change")]
    public async Task Host_stop_closes_promptly_but_keeps_a_held_remaining_timer_operation_owned(string held)
    {
        var time = new ControlledStageTime(holdRemainingOperation: held);
        var work = Work(time);
        var invocation = work.TryBegin()!;
        var token = invocation.Token;
        invocation.BeginStageWait(BackgroundJobInvocationStage.Reconcile).Should().BeTrue();
        time.Advance(TimeSpan.FromSeconds(9));
        time.Timer.FireQueued();
        ControlledStageTimer? remaining = null;
        Task? cleanup = null;
        try
        {
            if (held == "create") await time.RemainingCreationEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            else
            {
                remaining = await time.RemainingTimer.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await remaining.ChangeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            var stop = Task.Run(work.RequestHostStop);
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
            invocation.ReturnRequested.IsCompletedSuccessfully.Should().BeTrue();
            invocation.ReconcileWaitExpired.Should().BeFalse();
            cleanup = invocation.CompleteAsync(true);
            cleanup.IsCompleted.Should().BeFalse("the actual supplementary operation is still held");
            work.Completion.IsCompleted.Should().BeFalse();
            token.WaitHandle.Should().NotBeNull();
        }
        finally
        {
            time.ReleaseRemainingCreation.TrySetResult();
            remaining ??= await time.RemainingTimer.Task.WaitAsync(TimeSpan.FromSeconds(10));
            remaining.ReleaseChange.TrySetResult();
            if (cleanup is not null) await cleanup.WaitAsync(TimeSpan.FromSeconds(10));
        }
        await work.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        remaining.Disposed.Should().BeTrue();
        var handle = () => token.WaitHandle;
        handle.Should().Throw<ObjectDisposedException>();
    }

    [Theory]
    [InlineData("create")]
    [InlineData("change")]
    [InlineData("refused_change")]
    [InlineData("dispose")]
    public async Task A_faulted_remaining_timer_never_proves_cleanup_or_a_reconcile_timeout(string failure)
    {
        var time = new ControlledStageTime(remainingFailure: failure);
        var work = Work(time);
        var invocation = work.TryBegin()!;
        invocation.BeginStageWait(BackgroundJobInvocationStage.Reconcile).Should().BeTrue();
        time.Advance(TimeSpan.FromSeconds(9));
        time.Timer.FireQueued();
        if (failure == "dispose")
        {
            var remaining = await time.RemainingTimer.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await remaining.WaitForChangeAsync(0);
            work.RequestHostStop();
        }
        else await invocation.ReturnRequested.WaitAsync(TimeSpan.FromSeconds(10));
        var observer = (Task<bool>)typeof(HangfireGenerationWork.Invocation)
            .GetField("_stageWakeObservation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(invocation)!;
        (await observer.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeFalse();
        invocation.ReconcileWaitExpired.Should().BeFalse();
        await invocation.CompleteAsync(true);
        work.CloseStarts();
        work.Completion.IsCompleted.Should().BeFalse();
        work.Snapshot.Retained.Should().Be(1);
        invocation.Token.WaitHandle.Should().NotBeNull("failed actual cleanup cannot release the private source");
    }

    private static HangfireGenerationWork Work(TimeProvider time) =>
        new(new("test", BackgroundJobWorkerPoolKind.External, 2, ["test"]),
            new(BackgroundJobInfrastructureRegistry.CreateDefault()), time: time);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_held_timer_change_preserves_the_cause_of_its_effective_deadline(bool longerSuccessor)
    {
        var time = new ControlledStageTime();
        var work = Work(time);
        var invocation = work.TryBegin()!;
        invocation.BeginStageWait(BackgroundJobInvocationStage.Reconcile).Should().BeTrue();
        time.Advance(TimeSpan.FromSeconds(1));
        time.Timer.HoldChange = true;
        var change = Task.Run(() => invocation.BeginStageWait(longerSuccessor
            ? BackgroundJobInvocationStage.Execute : BackgroundJobInvocationStage.Outcome));
        try
        {
            await time.Timer.ChangeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            time.Advance(TimeSpan.FromSeconds(longerSuccessor ? 11 : 6));
            if (!longerSuccessor) time.Timer.FireQueued();
        }
        finally { time.Timer.ReleaseChange.TrySetResult(); }
        (await change.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeFalse();
        invocation.ReconcileWaitExpired.Should().Be(longerSuccessor,
            "the prospective shorter Outcome deadline cannot become the previous Reconcile's timeout");
        await invocation.CompleteAsync(true);
        work.CloseStarts();
        await work.Completion;
    }

    private sealed class ControlledStageTime(bool holdDisposal = false, bool holdRemainingDisposal = false,
        string? holdRemainingOperation = null, string? remainingFailure = null, long timestampFrequency = TimeSpan.TicksPerSecond) : TimeProvider
    {
        private long _ticks;
        private int _created;
        internal TaskCompletionSource RemainingCreationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseRemainingCreation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Created => Volatile.Read(ref _created);
        internal TaskCompletionSource<ControlledStageTimer> RemainingTimer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ControlledStageTimer Timer { get; private set; } = null!;
        public override long TimestampFrequency => timestampFrequency;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        internal void Advance(TimeSpan duration) => AdvanceTimestamp(checked(duration.Ticks * (timestampFrequency / TimeSpan.TicksPerSecond)));
        internal void AdvanceTimestamp(long ticks) => Interlocked.Add(ref _ticks, ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            period.Should().Be(Timeout.InfiniteTimeSpan);
            var number = Interlocked.Increment(ref _created);
            if (number == 2)
            {
                RemainingCreationEntered.TrySetResult();
                if (holdRemainingOperation == "create") ReleaseRemainingCreation.Task.GetAwaiter().GetResult();
                if (remainingFailure == "create") throw new IOException("The supplementary timer factory failed.");
            }
            var timer = new ControlledStageTimer(callback, state, number == 1 ? holdDisposal : holdRemainingDisposal,
                number == 2 ? remainingFailure : null)
            { HoldChange = number == 2 && holdRemainingOperation == "change" };
            if (number == 1) Timer = timer;
            else RemainingTimer.TrySetResult(timer);
            number.Should().BeLessThanOrEqualTo(2, "one invocation owns at most one supplementary timer");
            return timer;
        }
    }

    private sealed class ControlledStageTimer(TimerCallback callback, object? state, bool holdDisposal, string? failure = null) : ITimer
    {
        internal TaskCompletionSource DisposalEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseDisposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed { get; private set; }
        private readonly object _changesSync = new();
        private int _changes;
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Changes => Volatile.Read(ref _changes);
        internal async Task WaitForChangeAsync(int previous)
        {
            Task signal;
            lock (_changesSync)
            {
                if (_changes > previous) return;
                signal = _changed.Task;
            }
            await signal.WaitAsync(TimeSpan.FromSeconds(10));
        }
        internal bool HoldChange { get; set; }
        internal TaskCompletionSource ChangeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseChange { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void FireQueued() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (Disposed) return false;
            dueTime.Should().BePositive();
            period.Should().Be(Timeout.InfiniteTimeSpan);
            lock (_changesSync)
            {
                _changes++;
                _changed.TrySetResult();
                _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            if (failure == "change") throw new IOException("The supplementary timer change failed.");
            if (failure == "refused_change") return false;
            if (HoldChange)
            {
                ChangeEntered.TrySetResult();
                ReleaseChange.Task.GetAwaiter().GetResult();
            }
            return true;
        }
        public void Dispose() => throw new InvalidOperationException("Owned timer disposal must be asynchronous.");
        public async ValueTask DisposeAsync()
        {
            DisposalEntered.TrySetResult();
            if (holdDisposal) await ReleaseDisposal.Task;
            if (failure == "dispose") throw new IOException("The supplementary timer cleanup failed.");
            Disposed = true;
            DisposedSignal.TrySetResult();
        }
    }
}
