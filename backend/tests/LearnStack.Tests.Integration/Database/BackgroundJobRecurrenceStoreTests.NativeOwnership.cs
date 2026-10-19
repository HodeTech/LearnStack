using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRecurrenceStoreTests
{
    [Fact]
    public async Task A_held_actual_native_callback_preserves_durable_progress_and_both_sources_until_real_return()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
            await SeedLifecycleTenantAsync(app, SchemaFixture.TenantA, SchemaFixture.OrgA1, "Active", false, true);
        var today = await TodayAsync(database);
        var manifest = Fixture.Manifest(today.AddDays(-1), today.AddDays(-1), period: TimeSpan.FromDays(30));
        var second = Fixture.Manifest(today.AddDays(-1), today.AddDays(-1), period: TimeSpan.FromDays(30),
            key: "jobsrecurrencefixture.second");
        var registry = Fixture.Recurrences(manifest, second);
        var executed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new BackgroundJobFixtureProbe
        {
            Body = (_, _, _) => { executed.TrySetResult(); return Task.FromResult(Result.Ok(None.Value)); },
        };
        await using var host = RecipientHost(database, probe,
            configure: services => services.AddSingleton<LearnStack.SharedKernel.Audit.IAuditCatalogSource>(new RecurrenceFixtureAuditSource()));
        await using var source = Source(database);
        using var globals = new RecurrenceGlobalFilters();
        var bounds = new BackgroundJobRuntimeOptions
        {
            ControlPoll = TimeSpan.FromMilliseconds(50),
            BridgeWaitTimeout = TimeSpan.FromMilliseconds(100),
        };
        var runtime = RecurrenceRuntime(host.Services, source, registry, bounds);
        var control = NativeHoldField<BackgroundJobControl>(runtime, "_jobs");
        var recurrence = NativeHoldField<BackgroundJobRecurrenceControl>(control, "_recurrence");
        var application = host.Services.GetRequiredService<ApplicationDataSourceLifetime>();
        var entered = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var nativeCallbacks = 0;
        // The synthetic diagnostic callback holds a real stock SDK acquisition
        // on its actual owned native thread. It is not a network-fault proof.
        using var listener = new ActivityListener
        {
            ShouldListenTo = candidate => candidate.Name == "Npgsql",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
            ActivityStarted = _ =>
            {
                if (Thread.CurrentThread.Name != "learnstack-recurrence-hint") return;
                if (Interlocked.Increment(ref nativeCallbacks) != 1) return;
                entered.TrySetResult(Thread.CurrentThread);
                try
                {
                    if (!release.Wait(TimeSpan.FromMinutes(2)))
                        throw new TimeoutException("The native callback fixture was not released.");
                }
                finally { returned.TrySetResult(); }
            },
        };
        ActivitySource.AddActivityListener(listener);
        try
        {
            await runtime.StartAsync();
            var heldThread = await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await RecurrenceEventuallyAsync(() => Task.FromResult(runtime.Snapshot.Control!.Recurrence!.NativeRetained));
            await executed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await RecurrenceEventuallyAsync(async () =>
            {
                await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
                return await ReadAsync<long>(dispatcher, "SELECT count(*) FROM background_jobs WHERE phase='completed'") == 2;
            });
            // The second key has now passed actual registration and durable
            // fan-out. Its independent cooldown cannot mask a missing hard
            // single-flight guard. Read the published owner under its lock;
            // callback scheduling alone is not a native-start witness.
            lock (NativeHoldField<object>(recurrence, "_sync"))
            {
                NativeHoldField<Dictionary<string, long>>(recurrence, "_nativeAfter").Keys
                    .Should().Equal([registry.Keys[0]], because: "a held native flight excludes every other eligible key");
                NativeHoldField<Thread>(NativeHoldField<BackgroundJobNativeRecurrenceOperation>(recurrence, "_native"), "_thread")
                    .Should().BeSameAs(heldThread);
            }
            runtime.Snapshot.Control!.Recurrence!.NativeOutstanding.Should().BeTrue();
            runtime.Snapshot.Control.Recurrence.SqlHealthy.Should().BeTrue();
            runtime.Snapshot.IsHealthy.Should().BeFalse();
            returned.Task.IsCompleted.Should().BeFalse();
            Volatile.Read(ref nativeCallbacks).Should().Be(1, "the held actual native call cannot be replaced");
            await RecurrenceEventuallyAsync(() => Task.FromResult(recurrence.Snapshot.ApplicationOwners == 0));

            using var callerWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await FluentActions.Awaiting(() => runtime.StopAsync(callerWait.Token)).Should().ThrowAsync<OperationCanceledException>();
            await JoinNativeHoldSiblingsAsync(runtime, control, recurrence);
            // Every other real owner has positively joined. A bounded negative
            // observation now tests the parent's child pin, rather than merely
            // observing ordinary pool/maintenance shutdown still in progress.
            await FluentActions.Awaiting(() => control.Completion.WaitAsync(TimeSpan.FromMilliseconds(250)))
                .Should().ThrowAsync<TimeoutException>();
            runtime.Completion.IsCompleted.Should().BeFalse();
            runtime.Snapshot.Control!.Recurrence!.CleanupConfirmed.Should().BeFalse();
            source.Dispose();
            application.Dispose();
            source.IsDisposalComplete.Should().BeFalse();
            application.IsDisposalComplete.Should().BeFalse();
            returned.Task.IsCompleted.Should().BeFalse();
        }
        finally
        {
            release.Set();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await runtime.StopAsync(cleanup.Token);
                await runtime.Completion.WaitAsync(cleanup.Token);
            }
            finally
            {
                if (entered.Task.IsCompletedSuccessfully)
                    await RecurrenceEventuallyAsync(() => Task.FromResult(!entered.Task.Result.IsAlive));
            }
        }
        returned.Task.IsCompletedSuccessfully.Should().BeTrue();
        runtime.Snapshot.CleanupConfirmed.Should().BeTrue();
        await RecurrenceEventuallyAsync(() => Task.FromResult(source.IsDisposalComplete && application.IsDisposalComplete));
        source.HasUnconfirmedCleanup.Should().BeFalse();
        application.HasUnconfirmedCleanup.Should().BeFalse();
    }

    private static T NativeHoldField<T>(object owner, string name) =>
        (T)(owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner)
            ?? throw new InvalidOperationException($"The actual native-hold owner field {name} is missing."));

    private static async Task JoinNativeHoldSiblingsAsync(BackgroundJobRuntime runtime,
        BackgroundJobControl control, BackgroundJobRecurrenceControl recurrence)
    {
        await RecurrenceEventuallyAsync(() => Task.FromResult(
            typeof(BackgroundJobRuntime).GetField("_bridgeStop", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(runtime) is Task));
        var loops = await NativeHoldField<TaskCompletionSource<Task[]>>(control, "_loopsPublished").Task;
        var recurrenceLoop = await NativeHoldField<TaskCompletionSource<Task?>>(recurrence, "_loopPublished").Task;
        var siblings = NativeHoldField<List<HangfirePoolOwner>>(runtime, "_pools").Select(pool => pool.Completion)
            .Concat(loops).Append(NativeHoldField<Task>(control, "_stopRelay"))
            .Append(NativeHoldField<Task>(recurrence, "_stopRelay"))
            .Append(NativeHoldField<HangfireSharedComponents>(runtime, "_shared").Completion)
            .Append(NativeHoldField<Task>(runtime, "_bridgeLoop"))
            .Append(NativeHoldField<Task>(runtime, "_bridgeStop"))
            .Append(NativeHoldField<Task>(runtime, "_controlCancellation"));
        if (recurrenceLoop is not null) siblings = siblings.Append(recurrenceLoop);
        await Task.WhenAll(siblings).WaitAsync(TimeSpan.FromSeconds(30));
        runtime.Snapshot.BootstrapJoined.Should().BeTrue();
        control.Snapshot.Executions.Should().Be(0);
        control.Snapshot.PendingOutcomes.Should().Be(0);
    }
}
