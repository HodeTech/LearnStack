using System.Diagnostics;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

public sealed class HangfireGenerationWorkTests
{
    private static readonly TimeSpan TestWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CallbackWait = TimeSpan.FromSeconds(30);
    private static readonly AsyncLocal<string?> Ambient = new();

    [Fact]
    public void Construction_requires_both_dependencies()
    {
        var missingPool = () => new HangfireGenerationWork(null!, Gate()); // Exercise the null boundary.
        var missingGate = () => new HangfireGenerationWork(Pool(), null!); // Exercise the null boundary.
        missingPool.Should().Throw<ArgumentNullException>().WithParameterName("pool");
        missingGate.Should().Throw<ArgumentNullException>().WithParameterName("gate");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(17)]
    [InlineData(int.MaxValue)]
    public void Construction_refuses_invalid_worker_bounds_before_any_start(int workers)
    {
        var construct = () => new HangfireGenerationWork(Pool(workers), Gate());
        construct.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Completion_requires_closed_starts_and_every_actual_cleanup()
    {
        var work = Work();
        var first = Begin(work);
        var second = Begin(work);
        var firstToken = first.Token;
        var secondToken = second.Token;

        await first.CompleteAsync(cleanupConfirmed: true).WaitAsync(TestWait);
        work.Completion.IsCompleted.Should().BeFalse("an open generation may still admit work");
        work.Snapshot.Should().Be(new HangfireGenerationWorkSnapshot(1, 0, false));
        work.CloseStarts();
        work.CloseStarts();
        work.TryBegin().Should().BeNull();
        work.Completion.IsCompleted.Should().BeFalse("the second actual cleanup is still owned");
        secondToken.IsCancellationRequested.Should().BeFalse();

        await second.CompleteAsync(cleanupConfirmed: true).WaitAsync(TestWait);
        await work.Completion.WaitAsync(TestWait);
        work.Snapshot.Should().Be(new HangfireGenerationWorkSnapshot(0, 0, true));
        var firstHandle = () => firstToken.WaitHandle;
        var secondHandle = () => secondToken.WaitHandle;
        firstHandle.Should().Throw<ObjectDisposedException>();
        secondHandle.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task Local_drain_preserves_running_work_and_other_pool_admission_until_host_stop()
    {
        var gate = Gate();
        var draining = Work(gate: gate);
        var healthy = Work(gate: gate);
        var first = Begin(draining);
        var second = Begin(healthy);
        var firstToken = first.Token;
        var secondToken = second.Token;

        draining.CloseStarts();
        firstToken.IsCancellationRequested.Should().BeFalse();
        secondToken.IsCancellationRequested.Should().BeFalse();
        gate.IsOpen.Should().BeTrue();
        var spare = Begin(healthy);
        await spare.CompleteAsync(cleanupConfirmed: true).WaitAsync(TestWait);

        // The process owner closes global starts before separately relaying
        // host cancellation to each generation's private execution sources.
        gate.StopStarts();
        healthy.TryBegin().Should().BeNull();
        draining.RequestHostStop();
        healthy.RequestHostStop();
        firstToken.IsCancellationRequested.Should().BeTrue();
        secondToken.IsCancellationRequested.Should().BeTrue();
        await Task.WhenAll(first.CompleteAsync(true), second.CompleteAsync(true)).WaitAsync(TestWait);
        await Task.WhenAll(draining.Completion, healthy.Completion).WaitAsync(TestWait);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_stop_returns_while_a_real_callback_remains_owned_through_success_or_fault(bool callbackFault)
    {
        var work = Work();
        var invocation = Begin(work);
        var token = invocation.Token;
        var entered = Signal();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        using var registration = token.Register(() =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            if (!release.Wait(CallbackWait)) throw new TimeoutException("The test callback was not released.");
            if (callbackFault) throw new InvalidOperationException("Synthetic late cancellation callback failure.");
        });
        Task? cleanup = null;
        try
        {
            await Task.Run(work.RequestHostStop).WaitAsync(TestWait);
            await entered.Task.WaitAsync(TestWait);
            work.RequestHostStop();
            cleanup = invocation.CompleteAsync(cleanupConfirmed: true);
            cleanup.IsCompleted.Should().BeFalse();
            work.Completion.IsCompleted.Should().BeFalse();
            work.Snapshot.Should().Be(new HangfireGenerationWorkSnapshot(0, 1, true));
            token.WaitHandle.Should().NotBeNull("callback ownership keeps the source alive");
            var duplicate = () => invocation.CompleteAsync(cleanupConfirmed: true);
            await duplicate.Should().ThrowAsync<InvalidOperationException>();
            using var callerBudget = new CancellationTokenSource();
            var waiting = work.Completion.WaitAsync(callerBudget.Token);
            callerBudget.Cancel();
            var wait = () => waiting;
            await wait.Should().ThrowAsync<OperationCanceledException>();
            work.Completion.IsCompleted.Should().BeFalse("ending the caller wait supplies no cleanup witness");
        }
        finally
        {
            release.Set();
            cleanup ??= invocation.CompleteAsync(cleanupConfirmed: true);
            await cleanup.WaitAsync(TestWait);
        }

        calls.Should().Be(1);
        if (callbackFault)
        {
            work.Completion.IsCompleted.Should().BeFalse();
            work.Snapshot.Should().Be(new HangfireGenerationWorkSnapshot(0, 1, true));
            token.WaitHandle.Should().NotBeNull();
        }
        else
        {
            await work.Completion.WaitAsync(TestWait);
            var handle = () => token.WaitHandle;
            handle.Should().Throw<ObjectDisposedException>();
        }
    }

    [Fact]
    public async Task Cancellation_orchestration_does_not_flow_the_host_callers_ambient_context()
    {
        var work = Work();
        var invocation = Begin(work);
        var observed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = invocation.Token.UnsafeRegister(_ => observed.TrySetResult(Ambient.Value), null);
        var previous = Ambient.Value;
        try
        {
            Ambient.Value = "host-stop-context";
            work.RequestHostStop();
        }
        finally { Ambient.Value = previous; }

        (await observed.Task.WaitAsync(TestWait)).Should().BeNull();
        await invocation.CompleteAsync(cleanupConfirmed: true).WaitAsync(TestWait);
        await work.Completion.WaitAsync(TestWait);
    }

    [Fact]
    public async Task Cancellation_request_accepts_an_already_suppressed_execution_context()
    {
        var work = Work();
        var invocation = Begin(work);
        var token = invocation.Token;
        using (ExecutionContext.SuppressFlow()) work.RequestHostStop();
        token.IsCancellationRequested.Should().BeTrue();
        await invocation.CompleteAsync(cleanupConfirmed: true).WaitAsync(TestWait);
        await work.Completion.WaitAsync(TestWait);
    }

    [Fact]
    public async Task A_callback_fault_is_observed_and_retained_even_without_wrapper_completion()
    {
        var work = Work();
        var invocation = Begin(work);
        var token = invocation.Token;
        var entered = Signal();
        using var registration = token.Register(() =>
        {
            entered.TrySetResult();
            throw new InvalidOperationException("Synthetic cancellation callback failure.");
        });

        work.RequestHostStop();
        await entered.Task.WaitAsync(TestWait);
        await Eventually(() => work.Snapshot.Retained == 1);
        work.Snapshot.Should().Be(new HangfireGenerationWorkSnapshot(0, 1, true));
        work.Completion.IsCompleted.Should().BeFalse();
        token.WaitHandle.Should().NotBeNull();

        // Actual execution returning cannot repair failed callback cleanup.
        await invocation.CompleteAsync(cleanupConfirmed: true).WaitAsync(TestWait);
        work.RequestHostStop();
        invocation.MarkRetained();
        work.Completion.IsCompleted.Should().BeFalse();
        work.Snapshot.Should().Be(new HangfireGenerationWorkSnapshot(0, 1, true));
        token.WaitHandle.Should().NotBeNull();
        work.TryBegin().Should().BeNull();
    }

    [Fact]
    public async Task Unknown_cleanup_retains_the_source_and_can_later_report_positive_actual_cleanup()
    {
        var work = Work();
        var invocation = Begin(work);
        var token = invocation.Token;
        invocation.MarkRetained();
        invocation.MarkRetained();
        await invocation.CompleteAsync(cleanupConfirmed: false).WaitAsync(TestWait);
        await invocation.CompleteAsync(cleanupConfirmed: false).WaitAsync(TestWait);
        work.CloseStarts();
        work.Snapshot.Should().Be(new HangfireGenerationWorkSnapshot(0, 1, true));
        work.Completion.IsCompleted.Should().BeFalse();
        token.IsCancellationRequested.Should().BeFalse();
        token.WaitHandle.Should().NotBeNull();

        await invocation.CompleteAsync(cleanupConfirmed: true).WaitAsync(TestWait);
        await work.Completion.WaitAsync(TestWait);
        work.Snapshot.Should().Be(new HangfireGenerationWorkSnapshot(0, 0, true));
        var duplicate = () => invocation.CompleteAsync(cleanupConfirmed: true);
        await duplicate.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Wrapper_expiry_closes_late_admission_before_return_and_retains_its_actual_cancellation_callback()
    {
        var work = Work();
        var invocation = Begin(work);
        var token = invocation.Token;
        var entered = Signal();
        using var release = new ManualResetEventSlim();
        using var registration = token.Register(() =>
        {
            entered.TrySetResult();
            if (!release.Wait(CallbackWait)) throw new TimeoutException("The expired invocation callback was not released.");
        });
        Task? completion = null;
        try
        {
            await Task.Run(invocation.ExpireWait).WaitAsync(TestWait);
            await entered.Task.WaitAsync(TestWait);
            invocation.TryAdmitContinuation().Should().BeFalse();
            token.IsCancellationRequested.Should().BeTrue();
            token.WaitHandle.Should().NotBeNull();
            work.Snapshot.Should().Be(new HangfireGenerationWorkSnapshot(0, 1, false));
            var healthy = Begin(work);
            healthy.TryAdmitContinuation().Should().BeTrue("expiry closes this invocation, not its healthy pool");
            await healthy.CompleteAsync(true).WaitAsync(TestWait);

            completion = invocation.CompleteAsync(true);
            completion.IsCompleted.Should().BeFalse("the real callback still owns the private source");
            work.CloseStarts();
            work.Completion.IsCompleted.Should().BeFalse();
            invocation.ExpireWait();
            work.Snapshot.Should().Be(new HangfireGenerationWorkSnapshot(0, 1, true));
        }
        finally
        {
            release.Set();
            completion ??= invocation.CompleteAsync(true);
            await completion.WaitAsync(TestWait);
        }
        await work.Completion.WaitAsync(TestWait);
        var handle = () => token.WaitHandle;
        handle.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task Host_stop_still_cancels_an_overrun_after_its_wrapper_has_reported_unknown_cleanup()
    {
        var work = Work();
        var invocation = Begin(work);
        var token = invocation.Token;
        var canceled = Signal();
        using var registration = token.Register(() => canceled.TrySetResult());
        await invocation.CompleteAsync(cleanupConfirmed: false).WaitAsync(TestWait);
        work.RequestHostStop();
        await canceled.Task.WaitAsync(TestWait);
        token.IsCancellationRequested.Should().BeTrue();
        work.Completion.IsCompleted.Should().BeFalse();

        await invocation.CompleteAsync(cleanupConfirmed: true).WaitAsync(TestWait);
        await work.Completion.WaitAsync(TestWait);
    }

    [Fact]
    public async Task Four_retained_overruns_stop_new_starts_until_actual_cleanup_restores_capacity()
    {
        var work = Work(workers: 2);
        var active = Enumerable.Range(0, 6).Select(_ => Begin(work)).ToList();
        try
        {
            foreach (var invocation in active.Take(3)) invocation.ExpireWait();
            work.Snapshot.Should().Be(new HangfireGenerationWorkSnapshot(3, 3, false));
            work.TryBegin().Should().BeNull("the actual workers-plus-four bound is already occupied");

            // Free a running entry: below four retained, a fresh entry can use it.
            var running = active[^1];
            await running.CompleteAsync(cleanupConfirmed: true).WaitAsync(TestWait);
            active.Remove(running);
            var replacement = Begin(work);
            active.Add(replacement);
            replacement.ExpireWait();
            replacement.ExpireWait();
            work.Snapshot.Should().Be(new HangfireGenerationWorkSnapshot(2, 4, false));

            running = active[3];
            await running.CompleteAsync(cleanupConfirmed: true).WaitAsync(TestWait);
            active.Remove(running);
            work.TryBegin().Should().BeNull("four retained stops new starts despite a free actual-work slot");
            var retained = active[0];
            await retained.CompleteAsync(cleanupConfirmed: true).WaitAsync(TestWait);
            active.Remove(retained);
            active.Add(Begin(work));
            work.Snapshot.Should().Be(new HangfireGenerationWorkSnapshot(2, 3, false));
        }
        finally
        {
            work.CloseStarts();
            await Task.WhenAll(active.Select(invocation => invocation.CompleteAsync(true))).WaitAsync(TestWait);
        }
        await work.Completion.WaitAsync(TestWait);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(16)]
    public async Task Concurrent_admissions_obey_the_configured_workers_plus_four_hard_bound(int workers)
    {
        var work = Work(workers);
        var start = Signal();
        var attempts = Enumerable.Range(0, 64).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            return work.TryBegin();
        })).ToArray();
        start.TrySetResult();
        var admitted = (await Task.WhenAll(attempts).WaitAsync(TestWait))
            .OfType<HangfireGenerationWork.Invocation>().ToArray();
        try
        {
            admitted.Should().HaveCount(BackgroundJobRuntimeOptions.RetainedHardBound(workers));
            work.Snapshot.Running.Should().Be(admitted.Length);
            work.Snapshot.Retained.Should().Be(0);
            work.TryBegin().Should().BeNull();
        }
        finally
        {
            work.CloseStarts();
            await Task.WhenAll(admitted.Select(invocation => invocation.CompleteAsync(true))).WaitAsync(TestWait);
        }
        await work.Completion.WaitAsync(TestWait);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closure_racing_admission_retains_every_accepted_invocation_and_refuses_later_starts(bool hostStop)
    {
        for (var round = 0; round < 20; round++)
        {
            var work = Work();
            var existing = Begin(work);
            var start = Signal();
            var attempts = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
            {
                await start.Task;
                return work.TryBegin();
            })).ToArray();
            var closing = Task.Run(async () =>
            {
                await start.Task;
                if (hostStop) work.RequestHostStop();
                else work.CloseStarts();
            });
            start.TrySetResult();
            await closing.WaitAsync(TestWait);
            var admitted = (await Task.WhenAll(attempts).WaitAsync(TestWait))
                .OfType<HangfireGenerationWork.Invocation>().Append(existing).ToArray();
            try
            {
                work.Snapshot.Closed.Should().BeTrue();
                work.Snapshot.Running.Should().Be(hostStop ? 0 : admitted.Length);
                work.Snapshot.Retained.Should().Be(hostStop ? admitted.Length : 0);
                (work.Snapshot.Running + work.Snapshot.Retained).Should().Be(admitted.Length);
                work.TryBegin().Should().BeNull();
                admitted.Should().OnlyContain(invocation => invocation.Token.IsCancellationRequested == hostStop);
                admitted.Should().OnlyContain(invocation => invocation.ReturnRequested.IsCompleted == hostStop);
                work.Completion.IsCompleted.Should().Be(admitted.Length == 0);
            }
            finally
            {
                await Task.WhenAll(admitted.Select(invocation => invocation.CompleteAsync(true))).WaitAsync(TestWait);
            }
            await work.Completion.WaitAsync(TestWait);
        }
    }

    [Fact]
    public async Task Host_stop_racing_confirmed_cleanup_never_cancels_a_disposed_source_or_loses_ownership()
    {
        for (var round = 0; round < 32; round++)
        {
            var work = Work();
            var invocation = Begin(work);
            var token = invocation.Token;
            using var registration = token.Register(static () => { });
            var start = Signal();
            var cleanup = Task.Run(async () =>
            {
                await start.Task;
                await invocation.CompleteAsync(cleanupConfirmed: true);
            });
            var stopping = Task.Run(async () =>
            {
                await start.Task;
                work.RequestHostStop();
            });
            start.TrySetResult();
            await Task.WhenAll(cleanup, stopping).WaitAsync(TestWait);
            await work.Completion.WaitAsync(TestWait);
            work.Snapshot.Should().Be(new HangfireGenerationWorkSnapshot(0, 0, true));
            var handle = () => token.WaitHandle;
            handle.Should().Throw<ObjectDisposedException>();
        }
    }

    private static BackgroundJobWorkerPoolRegistration Pool(int workers = 2) =>
        new("test", BackgroundJobWorkerPoolKind.Transactional, workers, ["test"]);

    private static BackgroundJobRuntimeGate Gate() => new(BackgroundJobInfrastructureRegistry.CreateDefault());

    private static HangfireGenerationWork Work(int workers = 2, BackgroundJobRuntimeGate? gate = null) =>
        new(Pool(workers), gate ?? Gate());

    private static HangfireGenerationWork.Invocation Begin(HangfireGenerationWork work) =>
        work.TryBegin() ?? throw new InvalidOperationException("The fixture could not admit an invocation.");

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task Eventually(Func<bool> predicate)
    {
        var elapsed = Stopwatch.StartNew();
        while (!predicate())
        {
            if (elapsed.Elapsed >= TestWait) throw new TimeoutException("The owned invocation state did not arrive.");
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
    }
}
