using System.Collections;
using System.Reflection;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Identifiers;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobBridgeTests
{
    [Fact]
    public async Task Native_completion_cannot_release_a_slot_before_the_actual_owned_startup_task_returns()
    {
        await using var database = await DatabaseAsync();
        var first = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        await using var source = Source(database);
        var options = new BackgroundJobRuntimeOptions { BridgeWaitTimeout = TimeSpan.FromMilliseconds(1) };
        var store = Store(source, options);
        var claim = (await store.TryClaimAsync([]))!;
        claim.JobId.Should().Be(first);
        var client = new ImmediateClient();
        var wakeups = new HangfireWakeupClient(client);
        var bridge = new BackgroundJobBridge(store, wakeups, options, TimeProvider.System);
        var operation = new OwnedBridgeProbe(bridge, claim, wakeups);
        var releaseStartup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // This isolated observer control holds the actual scheduled invocation
        // after the real Thread.Start. Production exposes no startup delegate.
        var startup = Task.Run(async () =>
        {
            operation.Start();
            await releaseStartup.Task;
        });
        operation.BindStartup(startup);
        try
        {
            await operation.Result.WaitAsync(TimeSpan.FromSeconds(10));
            await EventuallyAsync(() => !operation.Thread.IsAlive && bridge.Snapshot.Retained == 1);
            operation.Completion.IsCompleted.Should().BeFalse();
            bridge.Snapshot.Outstanding.Should().Be(1);
            bridge.Snapshot.PriorHintRepairsAcknowledged.Should().Be(0);
            bridge.Snapshot.ExpiredTokenRepairsAcknowledged.Should().Be(0);
            (await bridge.RunQuantumAsync().WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
            await EventuallyAsync(() => client.Calls == 2 && bridge.Snapshot.Acknowledged == 1);
            bridge.Snapshot.Outstanding.Should().Be(1, "another quantum cannot prune the held startup owner");
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            var stop = () => bridge.StopAsync(deadline.Token);
            await stop.Should().ThrowAsync<OperationCanceledException>();
            bridge.Snapshot.Outstanding.Should().Be(1);
        }
        finally
        {
            releaseStartup.TrySetResult();
            await startup.WaitAsync(TimeSpan.FromSeconds(10));
            await bridge.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        bridge.Snapshot.Outstanding.Should().Be(0);
        (await ReadAsync(database, first)).BridgeToken.Should().Be(claim.Token,
            "host stop preserves late handoff repair rather than starting an ACK");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_retains_queued_or_unpublished_startup_and_refuses_its_late_native_start(bool publishStartup)
    {
        await using var database = await DatabaseAsync();
        for (var index = 0; index < 5; index++) await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        await using var source = Source(database);
        var store = Store(source);
        var client = new ImmediateClient();
        var wakeups = new HangfireWakeupClient(client);
        var bridge = new BackgroundJobBridge(store, wakeups, new(), TimeProvider.System);
        var releaseStartup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = new List<(OwnedBridgeProbe Operation, Task Startup)>();
        for (var index = 0; index < 4; index++)
        {
            var claim = (await store.TryClaimAsync([]))!;
            var operation = new OwnedBridgeProbe(bridge, claim, wakeups);
            var startup = Task.Run(async () =>
            {
                await releaseStartup.Task;
                operation.Start();
            });
            queued.Add((operation, startup));
            if (publishStartup) operation.BindStartup(startup);
        }
        try
        {
            bridge.Snapshot.Outstanding.Should().Be(4);
            (await bridge.RunQuantumAsync()).Should().BeFalse("queued startups already consume the four bridge slots");
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            var stop = () => bridge.StopAsync(deadline.Token);
            await stop.Should().ThrowAsync<OperationCanceledException>();
            queued.Should().OnlyContain(item => !item.Operation.Completion.IsCompleted);
            bridge.Snapshot.Outstanding.Should().Be(4);
            (await bridge.RunQuantumAsync()).Should().BeFalse();
            client.Calls.Should().Be(0);
        }
        finally
        {
            releaseStartup.TrySetResult();
            // Even an unpublished handle remains owned until it is known and
            // its actual queued startup invocation has returned.
            foreach (var item in queued) item.Operation.BindStartup(item.Startup);
            await bridge.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        queued.Should().OnlyContain(item => !item.Operation.Thread.IsAlive
            && (item.Operation.Thread.ThreadState & ThreadState.Unstarted) != 0);
        client.Calls.Should().Be(0);
        bridge.Snapshot.Outstanding.Should().Be(0);
        bridge.Snapshot.PriorHintRepairsAcknowledged.Should().Be(0);
        bridge.Snapshot.ExpiredTokenRepairsAcknowledged.Should().Be(0);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("host")]
    [InlineData("final")]
    [InlineData("bridge")]
    public async Task Actual_native_create_rechecks_gate_closure_after_native_thread_start(string closure)
    {
        await using var database = await DatabaseAsync();
        var id = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        await using var source = Source(database);
        var gate = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobBridgeStore(source, Fixture.Registry, new(), new SystemGuidFactory(), gate);
        var claim = (await store.TryClaimAsync([]))!;
        var client = new ImmediateClient();
        var wakeups = new HangfireWakeupClient(client);
        var bridge = new BackgroundJobBridge(store, wakeups, new(), TimeProvider.System, gate);
        var operation = new OwnedBridgeProbe(bridge, claim, wakeups);
        Task? stopping = null;
        // Bypass only the starter's earlier admission to isolate the native
        // body's real Create boundary. Its owner lock keeps that body queued
        // until closure; no production callback or Thread.Start hook is added.
        lock (operation.OwnerGate)
        {
            using (ExecutionContext.SuppressFlow()) operation.Thread.Start();
            operation.BindStartup(Task.CompletedTask);
            if (closure == "host") gate.StopStarts();
            else if (closure == "final") gate.BeginFinalExit();
            else if (closure == "bridge") stopping = bridge.StopAsync();
        }
        try
        {
            await operation.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            client.Calls.Should().Be(closure == "open" ? 1 : 0);
            operation.Thread.IsAlive.Should().BeFalse();
            bridge.Snapshot.Outstanding.Should().Be(0);
            var row = await ReadAsync(database, id);
            row.Attempts.Should().Be(0);
            if (closure == "open") bridge.Snapshot.Acknowledged.Should().Be(1,
                "the same native body must issue a real Create while its gates are open");
            else if (closure is "final" or "bridge") row.BridgeToken.Should().Be(claim.Token,
                "final teardown and local stop refuse the observer's later SQL acquisition too");
            else row.BridgeToken.Should().BeNull("ordinary global drain permits cleanup of an already-owned handoff");
        }
        finally
        {
            if (stopping is not null) await stopping.WaitAsync(TimeSpan.FromSeconds(10));
            await bridge.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task A_synthetic_native_join_timer_fault_retains_the_owner_after_its_observer_returns()
    {
        await using var database = await DatabaseAsync();
        var id = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        await using var source = Source(database);
        var store = Store(source);
        var claim = (await store.TryClaimAsync([]))!;
        using var held = new HeldClient(new AsyncLocal<string?>());
        var wakeups = new HangfireWakeupClient(held);
        var bridge = new BackgroundJobBridge(store, wakeups, new(), new FailedJoinTimer());
        var operation = new OwnedBridgeProbe(bridge, claim, wakeups);
        var startup = Task.Run(operation.Start);
        operation.BindStartup(startup);
        try
        {
            await EventuallyAsync(() => held.Calls == 1);
            await startup.WaitAsync(TimeSpan.FromSeconds(10));
            // Synthetic private-boundary placement makes the result-before-
            // native-end interval deterministic. The real native call stays held.
            operation.SignalNativeResult(null);
            await operation.Observer.WaitAsync(TimeSpan.FromSeconds(10));
            operation.Thread.IsAlive.Should().BeTrue();
            operation.Completion.IsCompleted.Should().BeFalse("observer return cannot supply native-join evidence");
            bridge.Snapshot.Outstanding.Should().Be(1);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            var stop = () => bridge.StopAsync(deadline.Token);
            await stop.Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            held.Release();
            await startup.WaitAsync(TimeSpan.FromSeconds(10));
            await EventuallyAsync(() => !operation.Thread.IsAlive);
        }
        operation.Completion.IsCompleted.Should().BeFalse("a failed observer grants no later cleanup inference");
        bridge.Snapshot.Outstanding.Should().Be(1);
        bridge.Snapshot.PriorHintRepairsAcknowledged.Should().Be(0);
        bridge.Snapshot.ExpiredTokenRepairsAcknowledged.Should().Be(0);
        (await ReadAsync(database, id)).BridgeToken.Should().Be(claim.Token);
    }

    private sealed class OwnedBridgeProbe
    {
        private readonly object _operation;
        private readonly Type _type;

        internal OwnedBridgeProbe(BackgroundJobBridge owner, BackgroundJobBridgeClaim claim, HangfireWakeupClient client)
        {
            _type = typeof(BackgroundJobBridge).GetNestedType("BridgeOperation", BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The concrete bridge owner is missing.");
            _operation = Activator.CreateInstance(_type, BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, [claim, client, TimeProvider.System.GetTimestamp(), owner], culture: null)
                ?? throw new InvalidOperationException("The concrete bridge owner could not be constructed.");
            OwnerGate = Field(owner, "_gate");
            lock (OwnerGate) ((IList)Field(owner, "_operations")).Add(_operation);
        }

        internal object OwnerGate { get; }
        internal Thread Thread => (Thread)Property("Thread");
        internal Task Observer => (Task)Property("Observer");
        internal Task Completion => (Task)Property("Completion");
        internal Task<string?> Result => ((TaskCompletionSource<string?>)Property("Result")).Task;

        internal void Start() => (_type.GetMethod("Start", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The native bridge startup is missing.")).Invoke(_operation, null);

        internal void BindStartup(Task startup) => ((TaskCompletionSource<Task>)Field(_operation, "_startup")).TrySetResult(startup);
        internal void SignalNativeResult(string? result) => ((TaskCompletionSource<string?>)Property("Result")).TrySetResult(result);

        private object Property(string name) => _type.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_operation)
            ?? throw new InvalidOperationException("The bridge observation is missing: " + name);

        private static object Field(object instance, string name) => instance.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
            ?? throw new InvalidOperationException("The bridge owner field is missing: " + name);
    }

    private sealed class ImmediateClient : IBackgroundJobClient
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        public string Create(Job job, IState state) => Interlocked.Increment(ref _calls).ToString(System.Globalization.CultureInfo.InvariantCulture);
        public bool ChangeState(string jobId, IState state, string expectedState) => throw new NotSupportedException();
    }

    private sealed class FailedJoinTimer : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            throw new InvalidOperationException("The synthetic native-join timer failed.");
    }
}
