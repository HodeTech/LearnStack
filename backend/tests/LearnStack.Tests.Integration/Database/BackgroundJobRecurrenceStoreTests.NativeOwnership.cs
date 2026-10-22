using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
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
        var boundary = "start";
        ExceptionDispatchInfo? failure = null;
        string? failureBoundary = null;
        string? failureFacts = null;
        try
        {
            await runtime.StartAsync();
            boundary = "callback_entry";
            var heldThread = await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            boundary = "native_retained";
            await RecurrenceEventuallyAsync(() => Task.FromResult(runtime.Snapshot.Control!.Recurrence!.NativeRetained));
            boundary = "execution";
            await executed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            boundary = "durable_progress";
            await RecurrenceEventuallyAsync(async () =>
            {
                await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
                return await ReadAsync<long>(dispatcher, "SELECT count(*) FROM background_jobs WHERE phase='completed'") == 2;
            });
            // The second key has now passed actual registration and durable
            // fan-out. Its independent cooldown cannot mask a missing hard
            // single-flight guard. Read the published owner under its lock;
            // callback scheduling alone is not a native-start witness.
            boundary = "held_assertions";
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
            boundary = "applications";
            await RecurrenceEventuallyAsync(() => Task.FromResult(recurrence.Snapshot.ApplicationOwners == 0));

            boundary = "cancelled_stop";
            using var callerWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await FluentActions.Awaiting(() => runtime.StopAsync(callerWait.Token)).Should().ThrowAsync<OperationCanceledException>();
            await JoinNativeHoldSiblingsAsync(runtime, control, recurrence, value => boundary = value);
            // Every other real owner has positively joined. A bounded negative
            // observation now tests the parent's child pin, rather than merely
            // observing ordinary pool/maintenance shutdown still in progress.
            boundary = "parent_pin";
            await FluentActions.Awaiting(() => control.Completion.WaitAsync(TimeSpan.FromMilliseconds(250)))
                .Should().ThrowAsync<TimeoutException>();
            runtime.Completion.IsCompleted.Should().BeFalse();
            runtime.Snapshot.Control!.Recurrence!.CleanupConfirmed.Should().BeFalse();
            boundary = "source_pin";
            source.Dispose();
            application.Dispose();
            source.IsDisposalComplete.Should().BeFalse();
            application.IsDisposalComplete.Should().BeFalse();
            returned.Task.IsCompleted.Should().BeFalse();
        }
        catch (Exception error)
        {
            CaptureFailure(error, boundary);
        }
        finally
        {
            release.Set();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            // Keep the original shared stop/completion deadline and independent
            // physical-thread wait. A later cleanup failure cannot replace the
            // first fault, and one failed cleanup step cannot suppress another.
            await CleanupStepAsync("cleanup_stop", () => runtime.StopAsync(cleanup.Token));
            await CleanupStepAsync("cleanup_completion", () => runtime.Completion.WaitAsync(cleanup.Token));
            if (entered.Task.IsCompletedSuccessfully)
                await CleanupStepAsync("cleanup_thread", () => RecurrenceEventuallyAsync(
                    () => Task.FromResult(!entered.Task.Result.IsAlive)));
            // Early faults have not reached the held-source assertions. Request
            // the same real disposal on that path; only the owners' positive
            // completion can release either retained source.
            await CleanupStepAsync("cleanup_sources", () => { source.Dispose(); return Task.CompletedTask; });
            await CleanupStepAsync("cleanup_sources", () => { application.Dispose(); return Task.CompletedTask; });
            await CleanupStepAsync("cleanup_sources", () => RecurrenceEventuallyAsync(
                () => Task.FromResult(source.IsDisposalComplete && application.IsDisposalComplete)));
        }
        try
        {
            boundary = "post_cleanup";
            returned.Task.IsCompletedSuccessfully.Should().BeTrue();
            runtime.Snapshot.CleanupConfirmed.Should().BeTrue();
            source.HasUnconfirmedCleanup.Should().BeFalse();
            application.HasUnconfirmedCleanup.Should().BeFalse();
        }
        catch (Exception error) { CaptureFailure(error, boundary); }
        if (failure is not null)
        {
            try
            {
                output.WriteLine("Recurrence native failure;boundary={0};{1}.", failureBoundary, failureFacts);
                output.WriteLine("Recurrence native cleanup;{0}.", Facts());
            }
            catch (Exception) { } // Diagnostic transport cannot replace the test fault.
            failure.Throw();
        }

        string Facts() => NativeHoldFacts(runtime, control, recurrence, source, application,
            entered.Task, returned.Task, executed.Task, Volatile.Read(ref nativeCallbacks));

        void CaptureFailure(Exception error, string at)
        {
            if (failure is not null) return;
            failure = ExceptionDispatchInfo.Capture(error);
            failureBoundary = at;
            try { failureFacts = Facts(); }
            catch (Exception) { } // Even a failed snapshot keeps the original fault.
        }

        async Task CleanupStepAsync(string at, Func<Task> step)
        {
            try { await step(); }
            catch (Exception error) { CaptureFailure(error, at); }
        }
    }

    private static T NativeHoldField<T>(object owner, string name) =>
        (T)(owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner)
            ?? throw new InvalidOperationException($"The actual native-hold owner field {name} is missing."));

    private static async Task JoinNativeHoldSiblingsAsync(BackgroundJobRuntime runtime,
        BackgroundJobControl control, BackgroundJobRecurrenceControl recurrence, Action<string>? boundary = null)
    {
        boundary?.Invoke("bridge_stop_publication");
        await RecurrenceEventuallyAsync(() => Task.FromResult(
            typeof(BackgroundJobRuntime).GetField("_bridgeStop", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(runtime) is Task));
        boundary?.Invoke("control_loops_publication");
        var loops = await NativeHoldField<TaskCompletionSource<Task[]>>(control, "_loopsPublished").Task;
        boundary?.Invoke("recurrence_loop_publication");
        var recurrenceLoop = await NativeHoldField<TaskCompletionSource<Task?>>(recurrence, "_loopPublished").Task;
        var siblings = NativeHoldField<List<HangfirePoolOwner>>(runtime, "_pools").Select(pool => pool.Completion)
            .Concat(loops).Append(NativeHoldField<Task>(control, "_stopRelay"))
            .Append(NativeHoldField<Task>(recurrence, "_stopRelay"))
            .Append(NativeHoldField<HangfireSharedComponents>(runtime, "_shared").Completion)
            .Append(NativeHoldField<Task>(runtime, "_bridgeLoop"))
            .Append(NativeHoldField<Task>(runtime, "_bridgeStop"))
            .Append(NativeHoldField<Task>(runtime, "_controlCancellation"));
        if (recurrenceLoop is not null) siblings = siblings.Append(recurrenceLoop);
        boundary?.Invoke("sibling_join");
        await Task.WhenAll(siblings).WaitAsync(TimeSpan.FromSeconds(30));
        boundary?.Invoke("sibling_assertions");
        runtime.Snapshot.BootstrapJoined.Should().BeTrue();
        control.Snapshot.Executions.Should().Be(0);
        control.Snapshot.PendingOutcomes.Should().Be(0);
    }

    // Failure-only bounded facts: no I/O, SQL, payload, raw error, identifier or
    // reflection name crosses this diagnostic boundary. Each getter can fail
    // independently without hiding the actual test exception.
    private static string NativeHoldFacts(BackgroundJobRuntime runtime, BackgroundJobControl control,
        BackgroundJobRecurrenceControl recurrence, PrivilegedDataSource source, ApplicationDataSourceLifetime application,
        Task<Thread> entered, Task returned, Task executed, int callbacks)
    {
        return string.Join(';',
        [
            "callbacks=" + Count(callbacks), "entered=" + State(entered), "returned=" + State(returned),
            "executed=" + State(executed), "thread=" + Read(() => entered.IsCompletedSuccessfully
                ? entered.Result.IsAlive ? "alive" : "returned" : "not_entered"),
            "nativeOutstanding=" + Read(() => Boolean(recurrence.Snapshot.NativeOutstanding)),
            "nativeRetained=" + Read(() => Boolean(recurrence.Snapshot.NativeRetained)),
            "sqlHealthy=" + Read(() => Boolean(recurrence.Snapshot.SqlHealthy)),
            "applications=" + Read(() => Count(recurrence.Snapshot.ApplicationOwners)),
            "retainedApplications=" + Read(() => Count(recurrence.Snapshot.RetainedApplications)),
            "pendingHistory=" + Read(() => Count(recurrence.Snapshot.PendingHistory)),
            "executions=" + Read(() => Count(control.Snapshot.Executions)),
            "pendingOutcomes=" + Read(() => Count(control.Snapshot.PendingOutcomes)),
            "bootstrap=" + Read(() => Boolean(runtime.Snapshot.BootstrapJoined)),
            "pools=" + Read(() =>
            {
                lock (NativeHoldField<object>(runtime, "_sync"))
                    return States(NativeHoldField<List<HangfirePoolOwner>>(runtime, "_pools")
                        .Select(pool => pool.Completion).ToArray(), 8);
            }),
            "loops=" + Read(() =>
            {
                lock (NativeHoldField<object>(control, "_sync"))
                {
                    var published = NativeHoldField<TaskCompletionSource<Task[]>>(control, "_loopsPublished").Task;
                    // ScheduleLoops publishes renewal, control, terminal in this
                    // exact order. A partial startup remains a shorter list.
                    return published.IsCompletedSuccessfully ? States(published.Result, 3) : Published(published);
                }
            }),
            "controlRelay=" + Read(() => State(NativeHoldField<Task>(control, "_stopRelay"))),
            "recurrenceRelay=" + Read(() => State(NativeHoldField<Task>(recurrence, "_stopRelay"))),
            "shared=" + Read(() =>
            {
                lock (NativeHoldField<object>(runtime, "_sync"))
                    return State(Field<HangfireSharedComponents>(runtime, "_shared")?.Completion);
            }),
            "bridgeLoop=" + RuntimeTask("_bridgeLoop"), "bridgeStop=" + RuntimeTask("_bridgeStop"),
            "controlCancellation=" + RuntimeTask("_controlCancellation"),
            "recurrenceLoop=" + Read(() =>
            {
                lock (NativeHoldField<object>(recurrence, "_sync"))
                {
                    var published = NativeHoldField<TaskCompletionSource<Task?>>(recurrence, "_loopPublished").Task;
                    return published.IsCompletedSuccessfully ? State(published.Result) : Published(published);
                }
            }),
            "control=" + State(control.Completion), "recurrence=" + State(recurrence.Completion),
            "runtime=" + State(runtime.Completion),
            "sourceDisposed=" + Read(() => Boolean(source.IsDisposalComplete)),
            "applicationDisposed=" + Read(() => Boolean(application.IsDisposalComplete)),
            "sourceUnconfirmed=" + Read(() => Boolean(source.HasUnconfirmedCleanup)),
            "applicationUnconfirmed=" + Read(() => Boolean(application.HasUnconfirmedCleanup)),
        ]);

        string RuntimeTask(string field) => Read(() =>
        {
            lock (NativeHoldField<object>(runtime, "_sync")) return State(Field<Task>(runtime, field));
        });

        static T? Field<T>(object owner, string name) where T : class =>
            (T?)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner);
        static string Read(Func<string> read) { try { return read(); } catch (Exception) { return "unavailable"; } }
        static string Boolean(bool value) => value ? "true" : "false";
        static string Count(int value) => value is >= 0 and <= 64 ? value.ToString(CultureInfo.InvariantCulture) : "out_of_range";
        static string State(Task? task) => task is null ? "not_issued" : task.IsCompletedSuccessfully ? "succeeded"
            : task.IsFaulted ? "faulted" : task.IsCanceled ? "canceled" : "pending";
        static string Published(Task task) => task.IsCompleted ? State(task) : "unpublished";
        static string States(Task[] tasks, int limit) => tasks.Length == 0 ? "not_issued"
            : tasks.Length > limit ? "unavailable" : string.Join(',', tasks.Select(State));
    }
}
