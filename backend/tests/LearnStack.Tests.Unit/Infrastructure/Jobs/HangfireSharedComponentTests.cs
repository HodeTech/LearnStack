using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using Hangfire.PostgreSql;
using Hangfire.Server;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

#pragma warning disable CS0618 // Exercise the pinned provider's public component seam.
public sealed class HangfireSharedComponentTests
{
    private static readonly AsyncLocal<string?> Ambient = new();
    private static readonly TimeSpan TestWait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task The_production_owner_has_only_the_exact_inventory_constructor_and_stop_before_start_acquires_nothing()
    {
        var constructor = typeof(HangfireSharedComponents).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Should().ContainSingle().Which;
        constructor.GetParameters().Select(parameter => parameter.ParameterType).Should().Equal(
            typeof(HangfireComponentInventory), typeof(BackgroundJobRuntimeGate), typeof(TimeProvider));
        using var source = new PrivilegedDataSource(() => throw new InvalidOperationException("Must not acquire"));
        var provider = new PostgreSqlStorage(new HangfireConnectionFactory(source), HangfireStorageBootstrap.Options());
        var owner = new HangfireSharedComponents(new(provider), Gate(), TimeProvider.System);

        await owner.StopAsync(CancellationToken.None);
        owner.Start();

        owner.Completion.IsCompletedSuccessfully.Should().BeTrue();
        owner.Expiration.Kind.Should().Be(HangfireSharedComponentKind.Expiration);
        owner.FetchedJobHeartbeat.Kind.Should().Be(HangfireSharedComponentKind.FetchedJobHeartbeat);
        owner.Expiration.Invocations.Should().Be(0);
        owner.FetchedJobHeartbeat.Invocations.Should().Be(0);
        source.IsValueCreated.Should().BeFalse();
    }

    [Fact]
    public async Task A_held_pass_retains_actual_resources_after_wait_expiry_without_overlap_or_request_context()
    {
        var time = new ManualTimeProvider();
        using var component = new HeldComponent();
        var worker = Worker(component, HangfireSharedComponentKind.Expiration, time);
        try
        {
            Ambient.Value = "request context must not flow";
            worker.Start();
            worker.Start();
            Ambient.Value = null;
            await component.Entered.Task.WaitAsync(TestWait);
            component.NativeThread!.IsBackground.Should().BeTrue();
            component.AmbientValue.Should().BeNull();
            await Eventually(() => time.HasDelay(HangfireSharedComponentWorker.InvocationWait));

            time.Advance(HangfireSharedComponentWorker.InvocationWait);
            await component.CancellationObserved.Task.WaitAsync(TestWait);
            await Eventually(() => time.HasDelay(HangfireSharedComponentWorker.CleanupWait));
            time.Advance(HangfireSharedComponentWorker.CleanupWait);
            await Eventually(() => worker.Snapshot.State == HangfireSharedComponentState.Retained);

            worker.Snapshot.IsHealthy.Should().BeFalse();
            worker.Snapshot.ActualJoined.Should().BeFalse();
            worker.Snapshot.OwnsResources.Should().BeTrue();
            worker.Completion.IsCompleted.Should().BeFalse();
            component.NativeThread.IsAlive.Should().BeTrue();
            component.Token.WaitHandle.Should().NotBeNull();
            time.Advance(TimeSpan.FromDays(2));
            component.Invocations.Should().Be(1);

            var stop = () => worker.StopAsync(new CancellationToken(canceled: true));
            await stop.Should().ThrowAsync<OperationCanceledException>();
            worker.Completion.IsCompleted.Should().BeFalse();
            component.Token.WaitHandle.Should().NotBeNull();
        }
        finally
        {
            Ambient.Value = null;
            worker.RequestStop();
            component.Release.Set();
            await worker.Completion.WaitAsync(TestWait);
        }

        component.NativeThread!.IsAlive.Should().BeFalse();
        worker.Snapshot.ActualJoined.Should().BeTrue();
        worker.Snapshot.OwnsResources.Should().BeFalse();
        worker.Snapshot.State.Should().Be(HangfireSharedComponentState.Stopped);
        var disposedToken = () => component.Token.WaitHandle;
        disposedToken.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task Expiration_cadence_starts_after_actual_completion_and_is_cancelable()
    {
        var time = new ManualTimeProvider();
        using var component = new HeldComponent();
        var worker = Worker(component, HangfireSharedComponentKind.Expiration, time);
        try
        {
            worker.Start();
            await component.Entered.Task.WaitAsync(TestWait);
            time.Advance(TimeSpan.FromMinutes(2));
            component.Invocations.Should().Be(1);
            time.HasDelay(HangfireSharedComponentWorker.ExpirationCadence).Should().BeFalse();
            component.Release.Set();
            await Eventually(() => time.HasDelay(HangfireSharedComponentWorker.ExpirationCadence));
            worker.Snapshot.ActualJoined.Should().BeTrue();
            worker.Snapshot.OwnsResources.Should().BeFalse();

            time.Advance(TimeSpan.FromMinutes(59));
            component.Invocations.Should().Be(1);
            time.Advance(TimeSpan.FromMinutes(1));
            await Eventually(() => component.Invocations == 2);
        }
        finally
        {
            component.Release.Set();
            await worker.StopAsync(CancellationToken.None).WaitAsync(TestWait);
        }
        time.ActiveTimers.Should().Be(0);
    }

    [Fact]
    public async Task Failed_passes_use_the_fixed_cancelable_backoff_instead_of_spinning()
    {
        var time = new ManualTimeProvider();
        var component = new FailingComponent();
        var worker = Worker(component, HangfireSharedComponentKind.FetchedJobHeartbeat, time);
        try
        {
            worker.Start();
            await Eventually(() => time.HasDelay(HangfireSharedComponentWorker.FailureBackoff));
            component.Invocations.Should().Be(1);
            worker.Snapshot.Outcome.Should().Be(HangfireSharedComponentOutcome.Failed);
            worker.Snapshot.IsHealthy.Should().BeFalse();

            time.Advance(TimeSpan.FromMilliseconds(999));
            component.Invocations.Should().Be(1);
            time.Advance(TimeSpan.FromMilliseconds(1));
            await Eventually(() => component.Invocations == 2);
            await Eventually(() => time.HasDelay(HangfireSharedComponentWorker.FailureBackoff));
        }
        finally { await worker.StopAsync(CancellationToken.None).WaitAsync(TestWait); }
        time.ActiveTimers.Should().Be(0);
    }

    [Fact]
    public async Task The_actual_stock_fetched_heartbeat_keeps_its_native_pacing_without_an_expiration_cadence()
    {
        using var source = new PrivilegedDataSource(() => throw new InvalidOperationException("Must not acquire"));
        var provider = new PostgreSqlStorage(new HangfireConnectionFactory(source), HangfireStorageBootstrap.Options());
        var inventory = new HangfireComponentInventory(provider);
        var time = new ManualTimeProvider();
        var worker = Worker(inventory.FetchedJobHeartbeat, HangfireSharedComponentKind.FetchedJobHeartbeat, time);
        try
        {
            worker.Start();
            await Eventually(() => worker.Snapshot.Invocations >= 2);
            time.HasDelay(HangfireSharedComponentWorker.ExpirationCadence).Should().BeFalse();
            source.IsValueCreated.Should().BeFalse();
        }
        finally { await worker.StopAsync(CancellationToken.None).WaitAsync(TestWait); }
        worker.Snapshot.ActualJoined.Should().BeTrue();
        time.ActiveTimers.Should().Be(0);
    }

    [Fact]
    public async Task One_blocked_cancellation_callback_cannot_block_the_other_lane_or_dispose_late_cleanup()
    {
        using var first = new HeldComponent(blockCancellation: true);
        using var second = new HeldComponent();
        var firstWorker = Worker(first, HangfireSharedComponentKind.Expiration, TimeProvider.System);
        var secondWorker = Worker(second, HangfireSharedComponentKind.FetchedJobHeartbeat, TimeProvider.System);
        using var callerBudget = new CancellationTokenSource();
        try
        {
            firstWorker.Start();
            secondWorker.Start();
            await Task.WhenAll(first.Entered.Task, second.Entered.Task).WaitAsync(TestWait);

            // These synchronous signal methods must return even while the first
            // native cancellation callback is held on separate owned work.
            firstWorker.RequestStop();
            secondWorker.RequestStop();
            await Task.WhenAll(first.CancellationObserved.Task, second.CancellationObserved.Task).WaitAsync(TestWait);
            var waiting = Task.WhenAll(firstWorker.StopAsync(callerBudget.Token), secondWorker.StopAsync(callerBudget.Token));
            callerBudget.Cancel();
            var stop = () => waiting;
            await stop.Should().ThrowAsync<OperationCanceledException>();
            firstWorker.Completion.IsCompleted.Should().BeFalse();
            secondWorker.Completion.IsCompleted.Should().BeFalse();

            first.Release.Set();
            second.Release.Set();
            await secondWorker.Completion.WaitAsync(TestWait);
            await Eventually(() => firstWorker.Snapshot.ActualJoined);
            first.NativeThread!.IsAlive.Should().BeFalse();
            firstWorker.Snapshot.OwnsResources.Should().BeTrue();
            firstWorker.Completion.IsCompleted.Should().BeFalse();
            first.Token.WaitHandle.Should().NotBeNull();
        }
        finally
        {
            firstWorker.RequestStop();
            secondWorker.RequestStop();
            first.Release.Set();
            second.Release.Set();
            first.ReleaseCancellation.Set();
            await Task.WhenAll(firstWorker.Completion, secondWorker.Completion).WaitAsync(TestWait);
        }
        var disposedToken = () => first.Token.WaitHandle;
        disposedToken.Should().Throw<ObjectDisposedException>();
        firstWorker.Snapshot.OwnsResources.Should().BeFalse();
        first.Invocations.Should().Be(1);
        second.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task A_faulted_cancellation_callback_stays_unhealthy_and_cannot_authorize_another_pass()
    {
        var time = new ManualTimeProvider();
        var component = new ThrowingCancellationComponent();
        var worker = Worker(component, HangfireSharedComponentKind.FetchedJobHeartbeat, time);
        worker.Start();
        await component.Entered.Task.WaitAsync(TestWait);
        await Eventually(() => time.HasDelay(HangfireSharedComponentWorker.InvocationWait));
        time.Advance(HangfireSharedComponentWorker.InvocationWait);
        await Eventually(() => worker.Snapshot.State == HangfireSharedComponentState.CleanupFault
            && worker.Snapshot.ActualJoined);

        worker.Snapshot.Outcome.Should().Be(HangfireSharedComponentOutcome.CancellationFault);
        worker.Snapshot.ActualJoined.Should().BeTrue();
        worker.Snapshot.OwnsResources.Should().BeTrue();
        worker.Snapshot.IsHealthy.Should().BeFalse();
        worker.Completion.IsCompleted.Should().BeFalse();
        component.NativeThread!.IsAlive.Should().BeFalse();
        component.Token.WaitHandle.Should().NotBeNull();
        time.Advance(TimeSpan.FromDays(2));
        component.Invocations.Should().Be(1);
        worker.RequestStop();
        var stop = () => worker.StopAsync(new CancellationToken(canceled: true));
        await stop.Should().ThrowAsync<OperationCanceledException>();
        worker.Snapshot.State.Should().Be(HangfireSharedComponentState.CleanupFault);
    }

    [Fact]
    public async Task A_closed_process_gate_refuses_even_the_first_component_invocation()
    {
        var component = new FailingComponent();
        var gate = Gate();
        gate.StopStarts();
        var worker = new HangfireSharedComponentWorker(component, HangfireSharedComponentKind.Expiration,
            gate, TimeProvider.System);
        worker.Start();
        await worker.Completion.WaitAsync(TestWait);
        component.Invocations.Should().Be(0);
        worker.Snapshot.State.Should().Be(HangfireSharedComponentState.Stopped);
        worker.Snapshot.OwnsResources.Should().BeFalse();
    }

    [Fact]
    public async Task Stop_relays_cancellation_to_retained_work_after_the_owner_loop_has_faulted()
    {
        using var component = new HeldComponent();
        var worker = Worker(component, HangfireSharedComponentKind.Expiration,
            new FaultAfterNativeEntryTimeProvider(component.Entered.Task));
        try
        {
            worker.Start();
            await component.Entered.Task.WaitAsync(TestWait);
            await Eventually(() => worker.Snapshot.State == HangfireSharedComponentState.CleanupFault);
            component.CancellationObserved.Task.IsCompleted.Should().BeFalse();

            worker.RequestStop();
            await component.CancellationObserved.Task.WaitAsync(TestWait);
            component.Token.WaitHandle.Should().NotBeNull();
            worker.Snapshot.ActualJoined.Should().BeFalse();
            worker.Snapshot.OwnsResources.Should().BeTrue();
            var stop = () => worker.StopAsync(new CancellationToken(canceled: true));
            await stop.Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            worker.RequestStop();
            component.Release.Set();
            await Eventually(() => worker.Snapshot.ActualJoined && !worker.Snapshot.OwnsResources);
        }

        component.NativeThread!.IsAlive.Should().BeFalse();
        worker.Snapshot.State.Should().Be(HangfireSharedComponentState.CleanupFault);
        worker.Snapshot.IsHealthy.Should().BeFalse();
        component.Invocations.Should().Be(1);
        var disposedToken = () => component.Token.WaitHandle;
        disposedToken.Should().Throw<ObjectDisposedException>();
    }

    private static BackgroundJobRuntimeGate Gate() => new(BackgroundJobInfrastructureRegistry.CreateDefault());
    private static HangfireSharedComponentWorker Worker(IServerComponent component, HangfireSharedComponentKind kind,
        TimeProvider timeProvider) => new(component, kind, Gate(), timeProvider);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task Eventually(Func<bool> predicate)
    {
        var deadline = Stopwatch.StartNew();
        while (!predicate())
        {
            if (deadline.Elapsed >= TestWait) throw new TimeoutException("The owned component state did not arrive.");
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
    }

    private sealed class HeldComponent(bool blockCancellation = false) : IServerComponent, IDisposable
    {
        private int _invocations;
        internal TaskCompletionSource Entered { get; } = Signal();
        internal TaskCompletionSource CancellationObserved { get; } = Signal();
        internal ManualResetEventSlim Release { get; } = new();
        internal ManualResetEventSlim ReleaseCancellation { get; } = new();
        internal CancellationToken Token { get; private set; }
        internal Thread? NativeThread { get; private set; }
        internal string? AmbientValue { get; private set; }
        internal int Invocations => Volatile.Read(ref _invocations);

        public void Execute(CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            NativeThread = Thread.CurrentThread;
            AmbientValue = Ambient.Value;
            Interlocked.Increment(ref _invocations);
            // The source owns this registration through its callback join.
            cancellationToken.Register(() =>
            {
                CancellationObserved.TrySetResult();
                if (blockCancellation) ReleaseCancellation.Wait();
            });
            Entered.TrySetResult();
            Release.Wait(CancellationToken.None);
        }

        public void Dispose()
        {
            Release.Dispose();
            ReleaseCancellation.Dispose();
        }
    }

    private sealed class FailingComponent : IServerComponent
    {
        private int _invocations;
        internal int Invocations => Volatile.Read(ref _invocations);
        public void Execute(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _invocations);
            throw new InvalidOperationException("Fixture pass failure");
        }
    }

    private sealed class ThrowingCancellationComponent : IServerComponent
    {
        private int _invocations;
        internal TaskCompletionSource Entered { get; } = Signal();
        internal CancellationToken Token { get; private set; }
        internal Thread? NativeThread { get; private set; }
        internal int Invocations => Volatile.Read(ref _invocations);

        public void Execute(CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            NativeThread = Thread.CurrentThread;
            Interlocked.Increment(ref _invocations);
            cancellationToken.Register(() => throw new InvalidOperationException("Fixture cancellation failure"));
            Entered.TrySetResult();
            cancellationToken.WaitHandle.WaitOne();
        }
    }

    private sealed class FaultAfterNativeEntryTimeProvider(Task nativeEntry) : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            // Synthetic owner fault after real native entry, without replacing
            // the production component selector or native Thread.Start calls.
            nativeEntry.WaitAsync(TestWait).GetAwaiter().GetResult();
            throw new InvalidOperationException("Fixture observation timer failure");
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _sync = new();
        private readonly HashSet<ManualTimer> _timers = [];
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_sync) return _ticks; }
        public override DateTimeOffset GetUtcNow() { lock (_sync) return DateTimeOffset.UnixEpoch.AddTicks(_ticks); }
        internal int ActiveTimers { get { lock (_sync) return _timers.Count; } }
        internal bool HasDelay(TimeSpan delay) { lock (_sync) return _timers.Any(timer => timer.Duration == delay); }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            period.Should().Be(Timeout.InfiniteTimeSpan);
            lock (_sync)
            {
                var timer = new ManualTimer(this, callback, state, dueTime, _ticks + dueTime.Ticks);
                _timers.Add(timer);
                return timer;
            }
        }

        internal void Advance(TimeSpan duration)
        {
            ManualTimer[] due;
            lock (_sync)
            {
                _ticks += duration.Ticks;
                due = _timers.Where(timer => timer.Due <= _ticks).ToArray();
            }
            foreach (var timer in due) timer.Fire();
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state,
            TimeSpan duration, long due) : ITimer
        {
            private bool _disposed;
            internal TimeSpan Duration { get; private set; } = duration;
            internal long Due { get; private set; } = due;

            internal void Fire()
            {
                lock (owner._sync)
                {
                    if (_disposed || !owner._timers.Remove(this)) return;
                }
                callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._sync)
                {
                    if (_disposed) return false;
                    Duration = dueTime;
                    Due = owner._ticks + dueTime.Ticks;
                    owner._timers.Add(this);
                    return true;
                }
            }

            public void Dispose()
            {
                lock (owner._sync)
                {
                    _disposed = true;
                    owner._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
#pragma warning restore CS0618
