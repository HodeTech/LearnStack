using System.Data;
using System.Reflection;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobBridgeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Actual_bridge_ACK_classifies_old_hint_before_old_expired_token_and_never_calls_unmarked_first_ever(
        bool hint, bool expiredToken)
    {
        await using var database = await DatabaseAsync();
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        await using var source = Source(database);
        var store = Store(source);
        var first = (await store.TryClaimAsync([]))!;
        first.Kind.Should().Be(BackgroundJobBridgeClaimKind.Unmarked);
        if (hint) (await store.AcknowledgeAsync(first, "99")).Should().BeTrue();
        else if (!expiredToken) (await store.DeferAsync(first, BackgroundJobBridgeDeferral.CreationFailed)).Should().BeTrue();
        await using var sql = await source.OpenConnectionAsync();
        if (hint && expiredToken)
        {
            await ExecuteAsync(sql, "UPDATE background_jobs SET handoff_check_at=clock_timestamp()-interval '1 second' WHERE id=@id", ("id", job.Value));
            var abandoned = (await store.TryClaimAsync([]))!;
            abandoned.Kind.Should().Be(BackgroundJobBridgeClaimKind.PriorAcknowledgedHint);
        }
        await ExecuteAsync(sql, expiredToken
            ? "UPDATE background_jobs SET bridge_until=clock_timestamp()-interval '1 second',handoff_check_at=clock_timestamp()-interval '1 second' WHERE id=@id"
            : "UPDATE background_jobs SET handoff_check_at=clock_timestamp()-interval '1 second' WHERE id=@id", ("id", job.Value));
        var client = new ImmediateClient();
        var bridge = new BackgroundJobBridge(store, new HangfireWakeupClient(client), new(), TimeProvider.System);
        try
        {
            (await bridge.RunQuantumAsync()).Should().BeTrue();
            await EventuallyAsync(() => bridge.Snapshot.Outstanding == 0);
            client.Calls.Should().Be(1);
            bridge.Snapshot.Acknowledged.Should().Be(1);
            bridge.Snapshot.PriorHintRepairsAcknowledged.Should().Be(hint ? 1 : 0);
            bridge.Snapshot.ExpiredTokenRepairsAcknowledged.Should().Be(!hint && expiredToken ? 1 : 0);
            bridge.Snapshot.Failed.Should().Be(0);
            var row = await ReadAsync(database, job);
            row.Generation.Should().Be(1);
            row.Attempts.Should().Be(0);
            row.Vendor.Should().Be("1");
            (await bridge.RunQuantumAsync()).Should().BeFalse();
            bridge.Snapshot.Acknowledged.Should().Be(1);
        }
        finally { await bridge.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task Actual_native_return_with_a_replaced_bridge_fence_counts_no_ACK_or_repair()
    {
        await using var database = await DatabaseAsync();
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        await using var source = Source(database);
        var store = Store(source);
        _ = (await store.TryClaimAsync([]))!;
        await using var sql = await source.OpenConnectionAsync();
        const string expire = "UPDATE background_jobs SET bridge_until=clock_timestamp()-interval '1 second',handoff_check_at=clock_timestamp()-interval '1 second' WHERE id=@id";
        await ExecuteAsync(sql, expire, ("id", job.Value));
        using var held = new HeldClient(new AsyncLocal<string?>());
        var bridge = new BackgroundJobBridge(store, new HangfireWakeupClient(held), new(), TimeProvider.System);
        try
        {
            (await bridge.RunQuantumAsync()).Should().BeTrue();
            await EventuallyAsync(() => held.Calls == 1);
            bridge.Snapshot.Acknowledged.Should().Be(0);
            await ExecuteAsync(sql, expire, ("id", job.Value));
            var replacement = (await store.TryClaimAsync([]))!;
            replacement.Kind.Should().Be(BackgroundJobBridgeClaimKind.ExpiredPriorBridgeToken);
            held.Release();
            await EventuallyAsync(() => bridge.Snapshot.Outstanding == 0);
            bridge.Snapshot.Acknowledged.Should().Be(0);
            bridge.Snapshot.PriorHintRepairsAcknowledged.Should().Be(0);
            bridge.Snapshot.ExpiredTokenRepairsAcknowledged.Should().Be(0);
            bridge.Snapshot.Failed.Should().Be(1);
            (await ReadAsync(database, job)).BridgeToken.Should().Be(replacement.Token);
        }
        finally { held.Release(); await bridge.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task Actual_mismatch_deferral_with_an_expired_fence_is_not_counted_as_a_durable_deferral()
    {
        await using var database = await DatabaseAsync();
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString, BackgroundJobRuntimeFixture.PendingKey);
        await using var source = Source(database);
        var store = Store(source);
        var never = new NeverClient();
        var bridge = new BackgroundJobBridge(store, new HangfireWakeupClient(never), new(), TimeProvider.System);
        await using var observer = await source.OpenConnectionAsync();
        await using var locker = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var transaction = await locker.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("LOCK TABLE background_jobs IN ACCESS EXCLUSIVE MODE", locker, transaction))
            await command.ExecuteNonQueryAsync();
        var quantum = bridge.RunQuantumAsync();
        NpgsqlConnection? actual = null;
        var released = false;
        var closed = 0;
        StateChangeEventHandler? onClose = null;
        try
        {
            await WaitForBridgeLockAsync(observer, locker.ProcessID);
            actual = ActualBridgeConnection(store);
            onClose = (_, change) =>
            {
                if (change.CurrentState != ConnectionState.Closed || Interlocked.Increment(ref closed) != 1) return;
                using var expire = new NpgsqlCommand("UPDATE background_jobs SET bridge_until=clock_timestamp()-interval '1 second' WHERE id=@id", observer)
                { CommandTimeout = 5 };
                expire.Parameters.AddWithValue("id", job.Value);
                expire.ExecuteNonQuery().Should().Be(1);
            };
            actual.StateChange += onClose;
            await transaction.RollbackAsync();
            released = true;
            (await quantum.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
            closed.Should().Be(1);
            bridge.Snapshot.Deferred.Should().Be(0, "the real Defer task returned false after claim cleanup expired its fence");
            never.Calls.Should().Be(0);
            var row = await ReadAsync(database, job);
            row.Error.Should().BeNull();
            row.BridgeToken.Should().NotBeNull();
        }
        finally
        {
            if (!released) await transaction.RollbackAsync();
            try { await quantum.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally
            {
                if (actual is not null && onClose is not null) actual.StateChange -= onClose;
                await bridge.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_bridge_status_cleanup_must_return_before_visible_ACK_or_repair_accounting(bool failCleanup)
    {
        await using var database = await DatabaseAsync();
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        await using var source = Source(database);
        var store = Store(source);
        var original = (await store.TryClaimAsync([]))!;
        (await store.AcknowledgeAsync(original, "99")).Should().BeTrue();
        await using var observer = await source.OpenConnectionAsync();
        await ExecuteAsync(observer, "UPDATE background_jobs SET handoff_check_at=clock_timestamp()-interval '1 second' WHERE id=@id", ("id", job.Value));
        using var native = new HeldClient(new AsyncLocal<string?>());
        var bridge = new BackgroundJobBridge(store, new HangfireWakeupClient(native), new(), TimeProvider.System);
        await using var locker = await source.OpenConnectionAsync();
        await using var transaction = await locker.BeginTransactionAsync();
        using var releaseCleanup = new ManualResetEventSlim();
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lockReleased = false;
        NpgsqlConnection? actual = null;
        StateChangeEventHandler? onClose = null;
        Task? actualObserver = null;
        try
        {
            (await bridge.RunQuantumAsync()).Should().BeTrue();
            await EventuallyAsync(() => native.Calls == 1);
            var gate = typeof(BackgroundJobBridge).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!;
            lock (gate)
            {
                var operation = ((System.Collections.IEnumerable)typeof(BackgroundJobBridge)
                    .GetField("_operations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!).Cast<object>().Single();
                actualObserver = (Task)operation.GetType().GetProperty("Observer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(operation)!;
            }
            await using (var hold = new NpgsqlCommand("SELECT id FROM background_jobs WHERE id=@id FOR UPDATE", locker, transaction))
            {
                hold.Parameters.AddWithValue("id", job.Value);
                (await hold.ExecuteScalarAsync()).Should().Be(job.Value);
            }
            native.Release();
            await WaitForBridgeLockAsync(observer, locker.ProcessID);
            actual = ActualBridgeConnection(store);
            onClose = (_, change) =>
            {
                if (change.CurrentState != ConnectionState.Closed) return;
                cleanupEntered.TrySetResult();
                releaseCleanup.Wait();
                if (failCleanup) throw new InvalidOperationException("Deliberate actual bridge connection cleanup refusal.");
            };
            actual.StateChange += onClose;
            await transaction.RollbackAsync();
            lockReleased = true;
            await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            (await ReadAsync(database, job)).Vendor.Should().Be("1", "the real matching ACK committed before connection cleanup");
            bridge.Snapshot.Acknowledged.Should().Be(0, "the bool status API has not returned through actual cleanup yet");
            bridge.Snapshot.PriorHintRepairsAcknowledged.Should().Be(0);
            bridge.Snapshot.Outstanding.Should().Be(1);
            actualObserver.IsCompleted.Should().BeFalse();
            releaseCleanup.Set();
            await actualObserver.WaitAsync(TimeSpan.FromSeconds(10));
            store.HasUnconfirmedCleanup.Should().Be(failCleanup);
            bridge.Snapshot.Acknowledged.Should().Be(failCleanup ? 0 : 1);
            bridge.Snapshot.PriorHintRepairsAcknowledged.Should().Be(failCleanup ? 0 : 1);
            bridge.Snapshot.ExpiredTokenRepairsAcknowledged.Should().Be(0);
            bridge.Snapshot.Outstanding.Should().Be(failCleanup ? 1 : 0);
            if (failCleanup)
            {
                bridge.Snapshot.Failed.Should().Be(1);
                (await bridge.RunQuantumAsync()).Should().BeFalse();
                bridge.Snapshot.Acknowledged.Should().Be(0, "a durable row cannot reconstruct the hidden bool return");
            }
        }
        finally
        {
            native.Release();
            releaseCleanup.Set();
            if (!lockReleased) await transaction.RollbackAsync();
            try { if (actualObserver is not null) await actualObserver.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally
            {
                if (actual is not null && onClose is not null) actual.StateChange -= onClose;
                if (store.HasUnconfirmedCleanup)
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                    await FluentActions.Awaiting(() => bridge.StopAsync(deadline.Token)).Should().ThrowAsync<OperationCanceledException>();
                }
                else await bridge.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    private static NpgsqlConnection ActualBridgeConnection(PostgresBackgroundJobBridgeStore store)
    {
        var type = typeof(PostgresBackgroundJobBridgeStore);
        lock (type.GetField("_acquisitionGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!)
        {
            var owned = type.GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)
                ?? throw new InvalidOperationException("The actual bridge connection is not owned.");
            return (NpgsqlConnection)owned.GetType().GetProperty("Connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owned)!;
        }
    }

    private static async Task WaitForBridgeLockAsync(NpgsqlConnection observer, int blocker)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var query = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database()
                AND usename='learnstack_outbox_admin' AND @blocker=ANY(pg_blocking_pids(pid))
                AND query LIKE '%background_jobs%')
            """, observer);
        query.Parameters.AddWithValue("blocker", blocker);
        while (!(bool)(await query.ExecuteScalarAsync(deadline.Token))!) await Task.Delay(10, deadline.Token);
    }
}
