using System.Reflection;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRecurrenceStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stopped_recurrence_child_keeps_late_setup_neutral_without_issuing_work(bool bind)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var host = RecipientHost(database, new());
        await using var source = Source(database);
        using var globals = new RecurrenceGlobalFilters();
        var runtime = RecurrenceRuntime(host.Services, source, Fixture.Recurrences(), new());
        var control = NativeHoldField<BackgroundJobControl>(runtime, "_jobs");
        var recurrence = NativeHoldField<BackgroundJobRecurrenceControl>(control, "_recurrence");
        var client = new HangfireRecurrenceClient(NativeStorage(source), HangfireRuntimeFilters.Create());
        var application = host.Services.GetRequiredService<ApplicationDataSourceLifetime>();
        try
        {
            runtime.RequestStop();
            Action late = () =>
            {
                if (bind) recurrence.BindNativeClient(client);
                else recurrence.Start();
            };
            late.Should().NotThrow("the actual stopped child has a positive no-issued-loop witness");
            await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            AssertStoppedRecurrenceHasNoIssuedWork(recurrence);
            recurrence.Snapshot.NativeOutstanding.Should().BeFalse();
            recurrence.Snapshot.ApplicationOwners.Should().Be(0);
            recurrence.Snapshot.CleanupConfirmed.Should().BeTrue();
        }
        finally
        {
            runtime.RequestStop();
            await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            source.Dispose();
            application.Dispose();
        }
        await RecurrenceEventuallyAsync(() => Task.FromResult(source.IsDisposalComplete && application.IsDisposalComplete));
        source.HasUnconfirmedCleanup.Should().BeFalse();
        application.HasUnconfirmedCleanup.Should().BeFalse();
    }

    [Fact]
    public async Task Parent_start_already_admitted_before_child_stop_joins_without_false_cleanup_failure()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var host = RecipientHost(database, new());
        await using var source = Source(database);
        var runtime = RecurrenceRuntime(host.Services, source, Fixture.Recurrences(), new());
        var parent = NativeHoldField<BackgroundJobControl>(runtime, "_jobs");
        var child = NativeHoldField<BackgroundJobRecurrenceControl>(parent, "_recurrence");
        var application = host.Services.GetRequiredService<ApplicationDataSourceLifetime>();
        Task? originalStart = null;
        try
        {
            // One synchronous holder keeps the actual child monitor across
            // stop. The parent's real started flag witnesses its passed guard.
            var childLock = NativeHoldField<object>(child, "_sync");
            Monitor.Enter(childLock);
            try
            {
                originalStart = Task.Run(parent.Start);
                SpinWait.SpinUntil(() =>
                {
                    lock (NativeHoldField<object>(parent, "_sync"))
                        return NativeHoldField<bool>(parent, "_started");
                }, TimeSpan.FromSeconds(10)).Should().BeTrue("parent Start must pass its actual admission guard");
                runtime.RequestStop();
            }
            finally { Monitor.Exit(childLock); }
            await originalStart.WaitAsync(TimeSpan.FromSeconds(30));
            Action repeat = parent.Start;
            repeat.Should().Throw<InvalidOperationException>("an admitted parent retains its duplicate-start refusal after stop");
            await parent.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            AssertStoppedRecurrenceHasNoIssuedWork(child);
            parent.Snapshot.CleanupConfirmed.Should().BeTrue();
            runtime.Snapshot.CleanupConfirmed.Should().BeTrue();
        }
        finally
        {
            runtime.RequestStop();
            if (originalStart is not null) await originalStart.WaitAsync(TimeSpan.FromSeconds(30));
            await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            source.Dispose();
            application.Dispose();
        }
        await RecurrenceEventuallyAsync(() => Task.FromResult(source.IsDisposalComplete && application.IsDisposalComplete));
        source.HasUnconfirmedCleanup.Should().BeFalse();
        application.HasUnconfirmedCleanup.Should().BeFalse();
    }

    [Fact]
    public async Task Open_child_requires_its_client_and_preserves_duplicate_setup_refusals_after_stop()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var host = RecipientHost(database, new());
        await using var source = Source(database);
        using var globals = new RecurrenceGlobalFilters();
        var today = await TodayAsync(database);
        var manifest = Fixture.Manifest(today.AddDays(1), today);
        var runtime = RecurrenceRuntime(host.Services, source, Fixture.Recurrences(manifest), new());
        var child = NativeHoldField<BackgroundJobRecurrenceControl>(NativeHoldField<BackgroundJobControl>(runtime, "_jobs"), "_recurrence");
        var application = host.Services.GetRequiredService<ApplicationDataSourceLifetime>();
        var client = new HangfireRecurrenceClient(NativeStorage(source), HangfireRuntimeFilters.Create());
        try
        {
            Action start = child.Start;
            start.Should().Throw<InvalidOperationException>("an open nonempty child requires its native client");
            NativeHoldField<bool>(child, "_started").Should().BeFalse();
            child.BindNativeClient(client);
            Action bind = () => child.BindNativeClient(client);
            bind.Should().Throw<InvalidOperationException>();
            child.Start();
            start.Should().Throw<InvalidOperationException>();
            runtime.RequestStop();
            start.Should().Throw<InvalidOperationException>("stop cannot erase an already-issued start");
            bind.Should().Throw<InvalidOperationException>("stop cannot erase an already-bound client");
            await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            runtime.RequestStop();
            await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            source.Dispose();
            application.Dispose();
        }
        await RecurrenceEventuallyAsync(() => Task.FromResult(source.IsDisposalComplete && application.IsDisposalComplete));
        source.HasUnconfirmedCleanup.Should().BeFalse();
        application.HasUnconfirmedCleanup.Should().BeFalse();
    }

    [Fact]
    public async Task Stopped_parent_keeps_late_first_start_neutral_without_issuing_control_loops()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var host = RecipientHost(database, new());
        await using var source = Source(database);
        var runtime = RecurrenceRuntime(host.Services, source, Fixture.Recurrences(), new());
        var parent = NativeHoldField<BackgroundJobControl>(runtime, "_jobs");
        var application = host.Services.GetRequiredService<ApplicationDataSourceLifetime>();
        try
        {
            runtime.RequestStop();
            Action late = parent.Start;
            late.Should().NotThrow("stop already published the actual no-loop witness before first parent Start");
            Task<Task[]> published;
            lock (NativeHoldField<object>(parent, "_sync"))
            {
                NativeHoldField<bool>(parent, "_started").Should().BeFalse();
                published = NativeHoldField<TaskCompletionSource<Task[]>>(parent, "_loopsPublished").Task;
                published.IsCompletedSuccessfully.Should().BeTrue();
            }
            (await published.WaitAsync(TimeSpan.FromSeconds(30))).Should().BeEmpty();
            await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            parent.Snapshot.CleanupConfirmed.Should().BeTrue();
        }
        finally
        {
            runtime.RequestStop();
            await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            source.Dispose();
            application.Dispose();
        }
        await RecurrenceEventuallyAsync(() => Task.FromResult(source.IsDisposalComplete && application.IsDisposalComplete));
        source.HasUnconfirmedCleanup.Should().BeFalse();
        application.HasUnconfirmedCleanup.Should().BeFalse();
    }

    [Fact]
    public async Task Actual_facade_start_after_final_admission_joins_when_stop_wins_the_parent_start_lock()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var host = RecipientHost(database, new());
        var source = Source(database);
        using var globals = new RecurrenceGlobalFilters();
        var runtime = RecurrenceRuntime(host.Services, source, Fixture.Recurrences(), new());
        var parent = NativeHoldField<BackgroundJobControl>(runtime, "_jobs");
        var application = host.Services.GetRequiredService<ApplicationDataSourceLifetime>();
        Task? startup = null;
        try
        {
            var parentLock = NativeHoldField<object>(parent, "_sync");
            Monitor.Enter(parentLock);
            try
            {
                startup = Task.Run(() => runtime.StartAsync());
                // The actual facade sets Starting only after preallocation and
                // its final MayStart guard. Parent Start cannot pass our lock.
                SpinWait.SpinUntil(() =>
                {
                    lock (NativeHoldField<object>(runtime, "_sync"))
                        return NativeHoldField<BackgroundJobRuntimeState>(runtime, "_state") == BackgroundJobRuntimeState.Starting;
                }, TimeSpan.FromSeconds(30)).Should().BeTrue("the original facade startup must pass its final admission");
                NativeHoldField<bool>(parent, "_started").Should().BeFalse();
                runtime.RequestStop();
            }
            finally { Monitor.Exit(parentLock); }
            try { await startup.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (HangfireStorageRefusedException) { } // Stopping may win the issued-start signal.
            await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            parent.Snapshot.CleanupConfirmed.Should().BeTrue();
            runtime.Snapshot.CleanupConfirmed.Should().BeTrue();
            source.Dispose();
            application.Dispose();
            await RecurrenceEventuallyAsync(() => Task.FromResult(source.IsDisposalComplete && application.IsDisposalComplete));
            source.HasUnconfirmedCleanup.Should().BeFalse();
            application.HasUnconfirmedCleanup.Should().BeFalse();
        }
        finally
        {
            // Failed cleanup remains a failure. Bound even the test's disposal
            // observation; do not manufacture completion/reset sticky facts.
            runtime.RequestStop();
            try
            {
                if (startup is not null)
                {
                    try { await startup.WaitAsync(TimeSpan.FromSeconds(30)); }
                    catch (HangfireStorageRefusedException) { }
                }
                await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally
            {
                source.Dispose();
                application.Dispose();
                await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
    }

    private static void AssertStoppedRecurrenceHasNoIssuedWork(BackgroundJobRecurrenceControl child)
    {
        lock (NativeHoldField<object>(child, "_sync"))
        {
            child.GetType().GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(child).Should().BeNull();
            NativeHoldField<bool>(child, "_started").Should().BeFalse();
            child.GetType().GetField("_native", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(child).Should().BeNull();
            ((System.Collections.IDictionary)NativeHoldField<object>(child, "_recipients")).Count.Should().Be(0);
            var published = NativeHoldField<TaskCompletionSource<Task?>>(child, "_loopPublished").Task;
            published.IsCompletedSuccessfully.Should().BeTrue("stop published the actual no-loop witness");
            published.Result.Should().BeNull();
        }
    }
}
