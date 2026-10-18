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
