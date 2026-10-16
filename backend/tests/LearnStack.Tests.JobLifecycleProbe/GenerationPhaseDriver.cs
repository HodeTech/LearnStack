using Hangfire;
using Hangfire.Logging;
using Hangfire.Processing;
using Hangfire.Server;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace LearnStack.Tests.JobLifecycleProbe;

/// <summary>
/// The fault machinery exists only in this disposable proof process. Every
/// fatal selection is reached from the actual production generation and Core.
/// </summary>
internal static class GenerationPhaseDriver
{
    internal static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(40);

    internal static async Task<int> RunAsync(string scenario, bool twin, string connectionString, string mutation)
    {
        var directory = GenerationPhaseProbe.DirectoryFor(scenario, twin);
        Directory.CreateDirectory(directory);
        Directory.SetCurrentDirectory(directory);
        var definitions = BackgroundJobDefinitionRegistry.CreateEmpty();
        var topology = definitions.Infrastructure;
        var pool = topology.Pools["transactional"];
        var gate = new BackgroundJobRuntimeGate(topology);
        var bounds = new BackgroundJobRuntimeOptions();
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(connectionString));
        var diagnostics = new HangfireBoundedLogProvider();
        var provider = HangfireStorageBootstrap.Create(source, topology, diagnostics, gate);
        var inventory = new HangfireComponentInventory(provider);
        var routing = new PostgresBackgroundJobRoutingStore(source, definitions, bounds, new SystemGuidFactory(), gate);
        // The database has no business jobs. These closed dependencies refuse
        // any accidental execution instead of introducing a business fixture.
        await using var services = new ServiceCollection().BuildServiceProvider();
        var executor = new JobExecutor(services.GetRequiredService<IServiceScopeFactory>(),
            new StaticTenantContextAccessor(null), new BackgroundJobExecutionAdmission(definitions, definitions, new RefusingAuditCatalog()), bounds);
        var activator = HangfireInvocationContract.CreateActivator(pool, topology, routing, executor,
            BackgroundJobRecurrenceRegistry.CreateEmpty());
        var clock = scenario.StartsWith("gen_stall_", StringComparison.Ordinal) ? new PhaseTimeProvider() : null;
        var state = new PhaseState(scenario, twin, gate, diagnostics, pool.Workers, clock);
        LogProvider.SetCurrentLogProvider(state);
        var generation = new HangfireGeneration(provider, inventory, HangfireRuntimeFilters.Create(),
            activator, pool, gate, bounds, (TimeProvider?)clock ?? TimeProvider.System);
        state.Generation = generation;
        if (scenario is "gen_process_exit" or "gen_process_exit_direct")
            source.RetainBackgroundJobsUntil(generation.Completion);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => GenerationPhaseProbe.Sentinel(directory, "process-exit.flag");
        try
        {
            state.Emit("armed");
            if (scenario == "gen_constructor_after")
            {
                // This closed synthetic prerequisite makes the unchanged Core
                // ctor fail at GetShutdownToken after CreateDispatcher returned.
                // It is not a genuine native thread-start failure.
                CoreFixtureAccess.PrepareAfterDispatcherConstructorFailure(dispose: !twin);
                state.Fault(captured: false, waited: false, requireHeld: false);
            }
            generation.Start();

            if (!twin && mutation is "sink" or "constructor" or "outer_completion")
            {
                await state.FaultSite.Task.WaitAsync(WaitBudget).ConfigureAwait(false);
                if (mutation == "sink")
                {
                    // The real Core Dispose callback returned past the patched
                    // sink. No test callback selected the production fatal gate.
                    await state.CaptureReturned.Task.WaitAsync(WaitBudget).ConfigureAwait(false);
                    Require(gate.State == BackgroundJobProcessState.Fatal && !gate.TryAdmit()
                        && !gate.TryAdmitStorage() && !generation.TryAdmitInvocation());
                    state.Emit("fatal_gate_returned");
                }
                else
                {
                    // Both snapshots are downstream of the removed call in
                    // their actual owner method; outer-joined alone is earlier
                    // than ObserveUnwind and is deliberately insufficient.
                    await EventuallyAsync(() => generation.Snapshot.Draining
                        && (mutation == "constructor" || generation.Snapshot.OuterJoined)).ConfigureAwait(false);
                    var retained = generation.Snapshot;
                    Require(gate.IsOpen && !generation.TryAdmitInvocation()
                        && !retained.NormalChildrenJoined && !retained.CleanupComplete
                        && (mutation != "constructor" || !retained.HandlePublished && !retained.Captured));
                    state.Emit(mutation == "constructor" ? "constructor_boundary_returned" : "outer_completion_returned");
                }
                // Native zero and working finally/exit sentinels are observed
                // by the supervisor; this return cannot fabricate that result.
                return 0;
            }

            if (clock is not null)
            {
                await state.StallEntered.Task.WaitAsync(WaitBudget).ConfigureAwait(false);
                if (!twin)
                {
                    clock.Advance(scenario == "gen_stall_postbuilder" ? TimeSpan.FromSeconds(15) : bounds.ProgressTimeout);
                    Require(!await generation.Ready.WaitAsync(WaitBudget).ConfigureAwait(false));
                    var retained = generation.Snapshot;
                    Require(retained.Draining && retained.Reason == HangfireGenerationReason.StartupStalled
                        && !retained.NormalChildrenJoined && !retained.OuterJoined && !retained.CleanupComplete
                        && !generation.Completion.IsCompleted && !generation.TryAdmitInvocation() && gate.IsOpen
                        && (scenario != "gen_stall_constructor" || !retained.HandlePublished));
                    state.Emit("startup_stalled");
                }
                state.ReleaseStall.TrySetResult();
                if (!twin)
                {
                    if (scenario == "gen_stall_constructor")
                    {
                        await CoreFixtureAccess.Publication(generation).WaitAsync(WaitBudget).ConfigureAwait(false);
                        Require(generation.Snapshot.HandlePublished && !generation.TryAdmitInvocation());
                        state.Emit("late_handle");
                    }
                    else
                    {
                        await EventuallyAsync(() => generation.Snapshot.WaitEntered).ConfigureAwait(false);
                        Require(!generation.Snapshot.NormalChildrenJoined && !generation.Completion.IsCompleted
                            && !generation.TryAdmitInvocation());
                        state.Emit("late_progress");
                    }
                    state.ReleaseRequired.TrySetResult();
                    await generation.Completion.WaitAsync(WaitBudget).ConfigureAwait(false);
                    var joined = generation.Snapshot;
                    Require(joined.OuterJoined && joined.CleanupComplete && !generation.TryAdmitInvocation()
                        && joined.Reason == HangfireGenerationReason.StartupStalled
                        && (scenario == "gen_stall_constructor" ? !joined.Captured && !joined.NormalChildrenJoined : joined.NormalChildrenJoined));
                    state.Emit("late_cleanup");
                    return 0;
                }
            }

            if (scenario is "gen_process_exit" or "gen_process_exit_direct")
                return await ProvePrivateCallbackExitAsync(generation, state, gate, scenario, twin).ConfigureAwait(false);

            if (twin)
            {
                await state.FaultSite.Task.WaitAsync(WaitBudget).ConfigureAwait(false);
                var ready = await generation.Ready.WaitAsync(WaitBudget).ConfigureAwait(false);
                Require(ready || scenario == "gen_host_stop");
                state.ReleaseRequired.TrySetResult();
                generation.RequestDrain(HangfireGenerationReason.HostStop);
                await generation.Completion.WaitAsync(WaitBudget).ConfigureAwait(false);
                var joined = generation.Snapshot;
                Require(joined.NormalChildrenJoined && joined.OuterJoined && joined.CleanupComplete
                    && !generation.TryAdmitInvocation());
                state.Emit("normal_cleanup");
                return 0;
            }

            if (scenario == "gen_final_exit")
            {
                await state.FaultSite.Task.WaitAsync(WaitBudget).ConfigureAwait(false);
                await EventuallyAsync(() => generation.Snapshot.OuterJoined).ConfigureAwait(false);
                var snapshot = generation.Snapshot;
                Require(gate.State == BackgroundJobProcessState.FinalExit && !gate.TryAdmit()
                    && !snapshot.NormalChildrenJoined && !snapshot.CleanupComplete
                    && !generation.TryAdmitInvocation() && !generation.Completion.IsCompleted);
                state.Emit("terminal_retained");
                return 0;
            }

            // The real boundary must terminate this child. A missing boundary
            // eventually returns a failing ordinary process result, not success.
            await Task.Delay(TimeSpan.FromSeconds(45)).ConfigureAwait(false);
            throw Refused();
        }
        finally
        {
            state.ReleaseStall.TrySetResult();
            if (scenario is not ("gen_process_exit" or "gen_process_exit_direct") || twin)
                state.ReleaseRequired.TrySetResult();
            GenerationPhaseProbe.Sentinel(directory, "finally.flag");
        }
    }

    private static async Task<int> ProvePrivateCallbackExitAsync(HangfireGeneration generation, PhaseState state,
        BackgroundJobRuntimeGate gate, string scenario, bool twin)
    {
        Require(await generation.Ready.WaitAsync(WaitBudget).ConfigureAwait(false));
        await state.HeldRequired.Task.WaitAsync(WaitBudget).ConfigureAwait(false);
        state.Fault(captured: true, waited: true, requireHeld: true);
        var invocation = generation.TryBeginWork() ?? throw Refused();
        var token = invocation.Token;
        var callback = new ExitPrivateCallback(state);
        var registration = token.UnsafeRegister(static value => ((ExitPrivateCallback)value!).Run(), callback);
        gate.StopStarts();
        // This calls the shipped private-source relay. No test cancellation
        // source or completion signal substitutes for its actual operation.
        await Task.Run(generation.RequestHostStop).WaitAsync(WaitBudget).ConfigureAwait(false);
        await callback.Entered.Task.WaitAsync(WaitBudget).ConfigureAwait(false);
        var cancellation = CoreFixtureAccess.PrivateCancellationObservation(invocation);
        var cleanup = invocation.CompleteAsync(cleanupConfirmed: true);
        Require(token.IsCancellationRequested && !cancellation.IsCompleted && !cleanup.IsCompleted
            && callback.IsHeld && generation.WorkSnapshot == new HangfireGenerationWorkSnapshot(0, 1, true));
        _ = token.WaitHandle;
        state.Emit("private_cancel_retained");

        using (var callerWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
        {
            var expired = false;
            try { await generation.StopAsync(callerWait.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (callerWait.IsCancellationRequested) { expired = true; }
            Require(expired && HeldBeforeExit());
        }
        state.Emit("finite_host_wait_ended");

        if (twin)
        {
            callback.Release.Set();
            await cleanup.WaitAsync(WaitBudget).ConfigureAwait(false);
            await registration.DisposeAsync().ConfigureAwait(false);
            Require(cancellation.IsCompletedSuccessfully && !callback.IsHeld);
            state.Emit("private_callback_joined");
            state.ReleaseRequired.TrySetResult();
            await generation.Completion.WaitAsync(WaitBudget).ConfigureAwait(false);
            var joined = generation.Snapshot;
            Require(joined.NormalChildrenJoined && joined.OuterJoined && joined.CleanupComplete
                && generation.WorkSnapshot == new HangfireGenerationWorkSnapshot(0, 0, true));
            callback.Release.Dispose();
            state.Emit("normal_cleanup");
            return 0;
        }

        if (scenario == "gen_process_exit_direct")
        {
            // Deliberately unsafe child-only control: Core synchronously joins
            // the actual private cleanup whose callback is still held. The
            // isolated supervisor must kill and join this child; that result
            // can never satisfy the ordinary native-exit predicate.
            _ = CoreFixtureAccess.Context(generation).ShutdownToken.UnsafeRegister(_ =>
            {
                Require(callback.IsHeld && !cleanup.IsCompleted && !cancellation.IsCompleted);
                state.Emit("direct_core_callback_entered");
                cleanup.GetAwaiter().GetResult();
            }, null);
        }

        // Registered after the real Core handler. Positive exit must reach this
        // observer while the actual private callback and its relay remain held.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            var snapshot = generation.Snapshot;
            if (gate.State == BackgroundJobProcessState.FinalExit && !gate.TryAdmit()
                && !snapshot.NormalChildrenJoined && !snapshot.CleanupComplete
                && !generation.Completion.IsCompleted && callback.IsHeld
                && !cancellation.IsCompleted && !cleanup.IsCompleted
                && generation.WorkSnapshot == new HangfireGenerationWorkSnapshot(0, 1, true))
                state.Emit("final_observed");
        };
        Require(HeldBeforeExit());
        state.Emit("main_returning_unresolved");
        // Retain the registrations, private source, actual cancellation/cleanup
        // tasks and callback barrier through final teardown. This is no join.
        return 0;

        bool HeldBeforeExit()
        {
            var snapshot = generation.Snapshot;
            return gate.State == BackgroundJobProcessState.Stopping && !generation.TryAdmitInvocation()
                && !snapshot.NormalChildrenJoined && !snapshot.OuterJoined && !snapshot.CleanupComplete
                && !generation.Completion.IsCompleted && callback.IsHeld
                && !cancellation.IsCompleted && !cleanup.IsCompleted;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
        Justification = "The benign twin disposes the barrier only after actual callback cleanup; the unresolved exit control retains it through process teardown.")]
    private sealed class ExitPrivateCallback(PhaseState state)
    {
        private int _held;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new();
        internal bool IsHeld => Volatile.Read(ref _held) != 0;

        internal void Run()
        {
            Require(Thread.CurrentThread.IsBackground);
            Volatile.Write(ref _held, 1);
            state.Emit("private_callback_held");
            Entered.TrySetResult();
            Release.Wait();
            Volatile.Write(ref _held, 0);
        }
    }

    private static async Task EventuallyAsync(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(WaitBudget);
        while (!predicate()) await Task.Delay(TimeSpan.FromMilliseconds(10), deadline.Token).ConfigureAwait(false);
    }

    internal static void Require(bool condition)
    {
        if (!condition) throw Refused();
    }

    internal static InvalidOperationException Refused() => new("The synthetic generation phase prerequisite was not established.");

    private sealed class RefusingAuditCatalog : IAuditCatalog
    {
        public IReadOnlyCollection<AuditCatalogEntry> All => [];
        public bool TryGet(Type requestType, out AuditRegistration registration) => throw Refused();
        public bool TryGetOffPath(string operation, out AuditCatalogEntry entry) => throw Refused();
    }
}

internal sealed class PhaseState(string scenario, bool twin, BackgroundJobRuntimeGate gate,
    HangfireBoundedLogProvider diagnostics, int workers, PhaseTimeProvider? clock) : ILogProvider
{
    private readonly object _outputGate = new();
    private readonly HashSet<string> _emitted = new(StringComparer.Ordinal);
    private int _preannounceFault, _installed, _suppliedFault, _requiredHeld;
    private int _confirmed, _requiredDisposed, _secondAnnouncement;
    internal HangfireGeneration Generation { get; set; } = null!;
    internal TaskCompletionSource FaultSite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource HeldRequired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource ReleaseRequired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource StallEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource ReleaseStall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource CaptureReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool HoldsRequired => scenario is not ("gen_constructor_before" or "gen_constructor_after" or "gen_announce_log" or "gen_stall_constructor");

    public ILog GetLogger(string name)
    {
        if (scenario == "gen_stall_constructor" && name == "Hangfire.Server.BackgroundProcessingServer")
            Stall(captured: false, requireHeld: false);
        if (scenario == "gen_constructor_before" && name == "Hangfire.Server.BackgroundProcessingServer")
        {
            Fault(captured: false, waited: false, requireHeld: false);
            if (!twin) throw GenerationPhaseDriver.Refused();
        }
        if (scenario is "gen_supplied_after_capture" or "gen_stall_supplied" && name == "Hangfire.Processing.BackgroundExecution"
            && Generation is not null && Generation.Snapshot.Captured
            && Interlocked.Exchange(ref _suppliedFault, 1) == 0)
        {
            if (scenario == "gen_stall_supplied") Stall(captured: true, requireHeld: true);
            else
            {
                Fault(captured: true, waited: false, requireHeld: true);
                if (!twin) throw GenerationPhaseDriver.Refused();
            }
        }
        return new PhaseLog(this, name, diagnostics.GetLogger(name));
    }

    internal void Emit(string value)
    {
        GenerationPhaseDriver.Require(GenerationPhaseProbe.Events.Contains(value, StringComparer.Ordinal));
        lock (_outputGate)
        {
            // Some Core paths log repeatedly. A phase receipt records the first
            // real observation once; the phase-specific counters below qualify it.
            if (!_emitted.Add(value)) return;
            Console.Out.WriteLine(GenerationPhaseProbe.EventPrefix + value);
            Console.Out.Flush();
        }
    }

    internal void Fault(bool captured, bool waited, bool requireHeld)
    {
        if (requireHeld) HeldRequired.Task.WaitAsync(GenerationPhaseDriver.WaitBudget).GetAwaiter().GetResult();
        var snapshot = Generation.Snapshot;
        GenerationPhaseDriver.Require(snapshot.Captured == captured && snapshot.WaitEntered == waited
            && !snapshot.NormalChildrenJoined && !snapshot.OuterJoined && !snapshot.CleanupComplete
            && (!requireHeld || Volatile.Read(ref _requiredHeld) == 1));
        Emit("fault_site");
        FaultSite.TrySetResult();
    }

    internal void Stall(bool captured, bool requireHeld)
    {
        Fault(captured, waited: false, requireHeld);
        GenerationPhaseDriver.Require(clock is not null && clock.HasStartupDeadline);
        StallEntered.TrySetResult();
        ReleaseStall.Task.GetAwaiter().GetResult();
    }

    private void Observe(string logger, LogLevel level, string? message, Exception? exception)
    {
        if (message is null) return;
        if (logger == "Hangfire.Processing.BackgroundExecution" && HoldsRequired
            && message.StartsWith("Execution loop ServerWatchdog:", StringComparison.Ordinal)
            && message.Contains(" has started in ", StringComparison.Ordinal))
        {
            GenerationPhaseDriver.Require(Interlocked.Exchange(ref _requiredHeld, 1) == 0);
            Emit("required_thread_held");
            HeldRequired.TrySetResult();
            ReleaseRequired.Task.GetAwaiter().GetResult();
            Volatile.Write(ref _requiredHeld, 0);
        }

        if (logger == "Hangfire.Server.BackgroundServerProcess")
        {
            if (message.EndsWith(" is announcing itself...", StringComparison.Ordinal))
            {
                if (scenario == "gen_notify_succeeded" && Interlocked.Exchange(ref _preannounceFault, 1) == 0)
                {
                    Emit("preannounce_fault");
                    // Both twins need actual fault state for NotifySucceeded.
                    // No owner announcement has been attempted at this point.
                    throw GenerationPhaseDriver.Refused();
                }
                if (Volatile.Read(ref _confirmed) != 0)
                {
                    Volatile.Write(ref _secondAnnouncement, 1);
                    Emit("second_announcement");
                }
            }
            else if (message.Contains(" successfully announced in ", StringComparison.Ordinal))
            {
                GenerationPhaseDriver.Require(CoreFixtureAccess.Confirmed(Generation));
                GenerationPhaseDriver.Require(Interlocked.Exchange(ref _confirmed, 1) == 0);
                Emit("first_announcement");
                if (scenario == "gen_announce_log")
                {
                    Fault(captured: false, waited: false, requireHeld: false);
                    if (!twin) throw GenerationPhaseDriver.Refused();
                }
            }
            else if (message.Contains(" is starting the registered dispatchers: ", StringComparison.Ordinal))
            {
                GenerationPhaseDriver.Require(Interlocked.Exchange(ref _installed, 1) == 0);
                CoreFixtureAccess.Decorate(Generation, workers, this, scenario);
                if (scenario == "gen_stall_postbuilder") clock!.Advance(TimeSpan.FromSeconds(60));
            }
            else if (message.EndsWith(" all the dispatchers started", StringComparison.Ordinal))
            {
                if (scenario is "gen_post_builder" or "gen_dispose_second_announce" or "gen_dispose_outer_complete"
                    or "gen_host_stop" or "gen_final_exit")
                {
                    if (scenario == "gen_host_stop")
                    {
                        gate.StopStarts();
                        Generation.RequestDrain(HangfireGenerationReason.HostStop);
                        CoreFixtureAccess.StopCompleted(Generation).WaitAsync(GenerationPhaseDriver.WaitBudget).GetAwaiter().GetResult();
                        GenerationPhaseDriver.Require(gate.State == BackgroundJobProcessState.Stopping && !gate.TryAdmit());
                        Emit("host_stopped");
                    }
                    if (scenario == "gen_final_exit" && !twin)
                    {
                        gate.BeginFinalExit();
                        GenerationPhaseDriver.Require(gate.State == BackgroundJobProcessState.FinalExit && !gate.TryAdmit());
                        Emit("final_selected");
                    }
                    Fault(captured: true, waited: false, requireHeld: true);
                    if (!twin) throw GenerationPhaseDriver.Refused();
                }
                else if (scenario == "gen_stall_postbuilder") Stall(captured: true, requireHeld: true);
            }
            else if (message.EndsWith(" is reporting itself as stopped...", StringComparison.Ordinal))
                Emit("removal_entered");
        }

        if (logger == "Hangfire.Processing.BackgroundExecution"
            && message.StartsWith("Execution BackgroundServerProcess recovered from the ", StringComparison.Ordinal)
            && scenario == "gen_notify_succeeded")
        {
            GenerationPhaseDriver.Require(Volatile.Read(ref _preannounceFault) == 1 && Volatile.Read(ref _confirmed) == 1);
            Emit("notify_entered");
            Fault(captured: true, waited: false, requireHeld: true);
            if (!twin) throw GenerationPhaseDriver.Refused();
        }

        if (scenario == "gen_dispose_outer_complete" && !twin && Volatile.Read(ref _requiredDisposed) != 0
            && logger == "Hangfire.Processing.BackgroundExecution" && exception is not null
            && Thread.CurrentThread.Name?.StartsWith("BackgroundServerProcess #", StringComparison.Ordinal) == true)
        {
            Emit("outer_failure");
            throw GenerationPhaseDriver.Refused();
        }
        if (logger == "Hangfire.Processing.BackgroundDispatcher" && level == LogLevel.Fatal
            && message.StartsWith("Dispatcher is stopped due to an exception", StringComparison.Ordinal))
            Emit("outer_dispatcher_fault");
        if (logger == "Hangfire.Processing.BackgroundExecution" && Volatile.Read(ref _secondAnnouncement) != 0
            && message.StartsWith("Execution loop BackgroundServerProcess:", StringComparison.Ordinal)
            && message.Contains(" stopped in ", StringComparison.Ordinal))
            Emit("second_announcement_returned");
    }

    internal void RequiredCreateReturned()
    {
        Fault(captured: false, waited: false, requireHeld: true);
        if (!twin) throw GenerationPhaseDriver.Refused();
    }

    internal void RequiredDisposeReturned()
    {
        if (twin) return;
        GenerationPhaseDriver.Require(Interlocked.Exchange(ref _requiredDisposed, 1) == 0);
        Emit("required_dispose_fault");
        throw GenerationPhaseDriver.Refused();
    }

    internal void ActualWaitEntered()
    {
        Fault(captured: true, waited: true, requireHeld: true);
        if (scenario == "gen_wait_entry" && !twin) throw GenerationPhaseDriver.Refused();
    }

    internal void CaptureDisposeReturned()
    {
        Emit("capture_dispose_returned");
        CaptureReturned.TrySetResult();
    }

    private sealed class PhaseLog(PhaseState owner, string name, ILog ordinary) : ILog
    {
        public bool Log(LogLevel logLevel, Func<string> messageFunc, Exception? exception = null)
        {
            if (messageFunc is null) return true;
            // Only the synthetic child evaluates vendor messages. Nothing from
            // the factory, exception or generated message is exported.
            owner.Observe(name, logLevel, messageFunc(), exception);
            return ordinary.Log(logLevel, messageFunc, exception);
        }
    }
}

internal static class CoreFixtureAccess
{
    private const System.Reflection.BindingFlags PrivateInstance = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

    internal static bool Confirmed(HangfireGeneration generation) => Field<int>(generation, "_confirmed") == 1;
    internal static Task StopCompleted(HangfireGeneration generation) => Field<TaskCompletionSource>(generation, "_stopCompleted").Task;
    internal static Task<BackgroundProcessingServer?> Publication(HangfireGeneration generation) =>
        Field<TaskCompletionSource<BackgroundProcessingServer?>>(generation, "_published").Task;

    internal static void PrepareAfterDispatcherConstructorFailure(bool dispose)
    {
        var type = typeof(BackgroundProcessingServer).Assembly.GetType("Hangfire.Server.AspNetShutdownDetector", throwOnError: true)!;
        var field = type.GetField("CancellationTokenSource", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        GenerationPhaseDriver.Require(field?.FieldType == typeof(CancellationTokenSource));
        var source = (CancellationTokenSource)field!.GetValue(null)!;
        GenerationPhaseDriver.Require(!source.IsCancellationRequested);
        if (dispose) source.Dispose();
    }

    internal static void Decorate(HangfireGeneration generation, int workers, PhaseState state, string scenario)
    {
        var publication = Field<TaskCompletionSource<BackgroundProcessingServer?>>(generation, "_published");
        var server = publication.Task.WaitAsync(GenerationPhaseDriver.WaitBudget).GetAwaiter().GetResult();
        GenerationPhaseDriver.Require(server is not null);
        var process = Field<object>(server!, "_process");
        GenerationPhaseDriver.Require(process.GetType().FullName == "Hangfire.Server.BackgroundServerProcess");
        var builders = Field<IBackgroundProcessDispatcherBuilder[]>(process, "_dispatcherBuilders");
        GenerationPhaseDriver.Require(builders.Length == workers + 4
            && builders[0].ToString() == "ServerWatchdog" && builders[1].ToString() == "ServerJobCancellationWatcher"
            && builders[2].GetType().Name == "CaptureBuilder" && builders[^1].GetType().Name == "WaitEntryBuilder"
            && builders.Skip(3).Take(workers).All(builder => builder.GetType().Name == "OwnedBuilder"));
        builders[2] = new DecoratedBuilder(builders[2], state, Placement.CaptureDisposal);
        if (scenario == "gen_required_before_capture")
            builders[1] = new DecoratedBuilder(builders[1], state, Placement.RequiredConstruction);
        if (scenario == "gen_stall_required")
            builders[1] = new DecoratedBuilder(builders[1], state, Placement.RequiredStall);
        if (scenario is "gen_dispose_second_announce" or "gen_dispose_outer_complete")
            builders[0] = new DecoratedBuilder(builders[0], state, Placement.RequiredDisposal);
        if (scenario is "gen_wait_entry" or "gen_process_exit" or "gen_process_exit_direct")
            builders[^1] = new DecoratedBuilder(builders[^1], state, Placement.WaitEntry);
    }

    internal static BackgroundServerContext Context(HangfireGeneration generation) => Field<BackgroundServerContext>(generation, "_context");

    // This exact production observer directly awaits the real CancelAsync task;
    // the callback hold above independently proves that cancellation is in flight.
    internal static Task<bool> PrivateCancellationObservation(HangfireGenerationWork.Invocation invocation) =>
        Field<Task<bool>>(invocation, "_cancellationObservation");

    private static T Field<T>(object owner, string name)
    {
        var field = owner.GetType().GetField(name, PrivateInstance);
        return field?.GetValue(owner) is T value ? value : throw GenerationPhaseDriver.Refused();
    }

    private enum Placement { CaptureDisposal, RequiredConstruction, RequiredDisposal, WaitEntry, RequiredStall }

    private sealed class DecoratedBuilder(IBackgroundProcessDispatcherBuilder inner, PhaseState state, Placement placement)
        : IBackgroundProcessDispatcherBuilder
    {
        public IBackgroundDispatcher Create(BackgroundServerContext context, BackgroundProcessingServerOptions options)
        {
            if (placement == Placement.RequiredStall) state.Stall(captured: false, requireHeld: true);
            var dispatcher = inner.Create(context, options);
            if (placement == Placement.RequiredConstruction) state.RequiredCreateReturned();
            return new DecoratedDispatcher(dispatcher, state, placement);
        }
    }

    private sealed class DecoratedDispatcher(IBackgroundDispatcher inner, PhaseState state, Placement placement) : IBackgroundDispatcher
    {
        public bool Wait(TimeSpan timeout) => inner.Wait(timeout);
        public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            var actual = inner.WaitAsync(timeout, cancellationToken);
            if (placement == Placement.WaitEntry) state.ActualWaitEntered();
            return actual;
        }
        public void Dispose()
        {
            if (placement == Placement.CaptureDisposal) state.Emit("capture_dispose");
            inner.Dispose();
            if (placement == Placement.CaptureDisposal) state.CaptureDisposeReturned();
            if (placement == Placement.RequiredDisposal) state.RequiredDisposeReturned();
        }
    }
}
