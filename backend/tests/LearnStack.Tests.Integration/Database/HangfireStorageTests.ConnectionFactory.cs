using System.Collections.Concurrent;
using System.Data;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class HangfireStorageTests
{
    [Fact]
    public async Task Synchronous_vendor_factory_joins_the_actual_async_acquisition_without_context_dispatch()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<NpgsqlConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var syncCalls = 0;
        var asyncCalls = 0;
        var context = new DeferredFactoryContext();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var builder = new NpgsqlDataSourceBuilder(schema.Postgres.OutboxConnectionString);
        builder.UsePhysicalConnectionInitializer(_ =>
        {
            Interlocked.Increment(ref syncCalls);
            throw new InvalidOperationException("The planted synchronous acquisition path must not be selected.");
        }, async _ =>
        {
            Interlocked.Increment(ref asyncCalls);
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
        });
        // This planted initializer isolates the driver's public path selection
        // and actual join. The next case uses the unmodified production guard.
        await using var source = new PrivilegedDataSource(builder.Build);
        var factory = new HangfireConnectionFactory(source);
        var caller = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try { completed.TrySetResult(factory.GetOrCreateConnection()); }
            catch (Exception failure) { completed.TrySetException(failure); }
            finally { SynchronizationContext.SetSynchronizationContext(null); }
        })
        { IsBackground = true, Name = "vendor-factory-context-fixture" };
        caller.Start();
        try
        {
            await Task.WhenAny(entered.Task, completed.Task).WaitAsync(deadline.Token);
            entered.Task.IsCompletedSuccessfully.Should().BeTrue("the old synchronous path refuses before the async initializer");
            completed.Task.IsCompleted.Should().BeFalse("an entered initializer is not a completed acquisition");
            release.TrySetResult();
            var connection = await completed.Task.WaitAsync(deadline.Token);
            connection.State.Should().Be(ConnectionState.Open);
            syncCalls.Should().Be(0);
            asyncCalls.Should().Be(1);
            context.Posts.Should().Be(0);
        }
        finally
        {
            release.TrySetResult();
            // A context-capturing mutant remains owned: pump only this fixture's
            // queued continuations so its real caller/connection can be joined.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (caller.IsAlive)
            {
                context.Pump();
                await Task.Delay(TimeSpan.FromMilliseconds(10), cleanup.Token);
            }
            caller.Join();
            if (completed.Task.IsCompletedSuccessfully) await (await completed.Task).DisposeAsync();
            else if (completed.Task.IsFaulted) _ = completed.Task.Exception;
        }
    }

    [Fact]
    public async Task Synchronous_vendor_factory_keeps_the_guard_on_every_new_physical_connection()
    {
        await using var source = Source();
        var factory = new HangfireConnectionFactory(source);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var first = factory.GetOrCreateConnection();
        await AssertDispatcherRoleAsync(first, deadline.Token);
        await AssertBudgetsAsync(first);
        try
        {
            await schema.Postgres.ExecuteAsSuperuserAsync("ALTER ROLE learnstack_outbox_admin NOBYPASSRLS");
            await using var raw = (NpgsqlConnection)await PostgresFixture.OpenAsync(schema.Postgres.OutboxConnectionString, deadline.Token);
            (await ScalarAsync(raw, "SELECT NOT rolbypassrls FROM pg_roles WHERE rolname=current_user")).Should().Be(true);
            var acquire = () => { using var connection = factory.GetOrCreateConnection(); };
            acquire.Should().Throw<InvalidOperationException>().Which.InnerException.Should().BeNull();
        }
        finally { await schema.Postgres.ExecuteAsSuperuserAsync("ALTER ROLE learnstack_outbox_admin BYPASSRLS"); }
        await using var restored = factory.GetOrCreateConnection();
        restored.ProcessID.Should().NotBe(first.ProcessID, "the occupied connector cannot hide a missing physical role check");
        await AssertDispatcherRoleAsync(restored, deadline.Token);
        await AssertBudgetsAsync(restored);
    }

    private sealed class DeferredFactoryContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        private int _posts;
        internal int Posts => Volatile.Read(ref _posts);
        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref _posts);
            _callbacks.Enqueue((callback, state));
        }
        internal void Pump()
        {
            while (_callbacks.TryDequeue(out var callback)) callback.Callback(callback.State);
        }
    }
}
