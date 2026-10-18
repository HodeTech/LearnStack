using System.Diagnostics.CodeAnalysis;
using Hangfire.PostgreSql;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Infrastructure.Jobs;

public enum BackgroundJobRuntimeState { NotStarted, Bootstrapping, Starting, Running, Stopping, Retained, Stopped }

public sealed record BackgroundJobPoolHealth(string Pool, string State, string Reason, int RetainedInvocations,
    bool MetadataHeartbeatFault)
{
    public int RunningInvocations { get; init; }
    public long Generations { get; init; }
    public long? MetadataHeartbeatTimestamp { get; init; }
    public bool GenerationCaptured { get; init; }
    public bool NormalChildrenJoined { get; init; }
    public bool OuterJoined { get; init; }
    public bool GenerationCleanupComplete { get; init; }
}

public sealed record BackgroundJobMaintenanceHealth(string Component, string State, string Outcome,
    long Invocations, bool ActualJoined, bool OwnsResources, bool StorageFault, bool IsHealthy);

public sealed record BackgroundJobRuntimeSnapshot(BackgroundJobRuntimeState State,
    BackgroundJobPhysicalCapacity Capacity, int RunningPools, int RetainedPools,
    int RetainedInvocations, BackgroundJobBridgeSnapshot? Bridge, bool BridgeHealthy, bool ExpirationHealthy,
    bool FetchedJobHeartbeatHealthy, bool BootstrapJoined, bool CleanupConfirmed,
    IReadOnlyList<BackgroundJobPoolHealth> Pools, IReadOnlyList<BackgroundJobMaintenanceHealth> Maintenance,
    BackgroundJobControlHealth? Control = null, BackgroundJobApplicationCapacity? ApplicationCapacity = null)
{
    public bool InstrumentationHealthy { get; init; } = true;
    public bool IsHealthy => InstrumentationHealthy && State == BackgroundJobRuntimeState.Running && BridgeHealthy
        && ExpirationHealthy && FetchedJobHeartbeatHealthy && RunningPools == Capacity.Pools
        && RetainedPools == 0 && RetainedInvocations == 0 && Pools.All(pool => !pool.MetadataHeartbeatFault)
        && Control is
        {
            RenewalHealthy: true, RecoveryHealthy: true, TerminalHealthy: true,
            ObservationHealthy: true, HasQuarantinedJobs: false, HasQuarantinedControl: false,
            HasDeferredWork: false, Recurrence.IsHealthy: true
        };
}

/// <summary>API-instance owner; actual joins alone release bootstrap, pools, shared work and bridge.</summary>
/// <remarks>The shared source stays pinned until this owner and both messaging owners positively finish.</remarks>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Only actual owner completion can dispose the private control source; a caller deadline is not cleanup authority.")]
public sealed class BackgroundJobRuntime
{
    private readonly object _sync = new();
    private readonly PrivilegedDataSource _source;
    private readonly BackgroundJobDefinitionRegistry _definitions;
    private readonly BackgroundJobRecurrenceRegistry _recurrences;
    private readonly JobExecutor _executor;
    private readonly PostgresBackgroundJobRoutingStore _routing;
    private readonly BackgroundJobRuntimeGate _gate;
    private readonly BackgroundJobRuntimeOptions _bounds;
    private readonly IGuidFactory _ids;
    private readonly TimeProvider _time;
    private readonly HangfireBoundedLogProvider _diagnostics;
    private readonly BackgroundJobPhysicalCapacity _capacity;
    private readonly BackgroundJobApplicationCapacity _applicationCapacity;
    private readonly BackgroundJobControl _jobs;
    private readonly CancellationTokenSource _control = new();
    private readonly TaskCompletionSource _stop = Signal();
    private readonly TaskCompletionSource _completion;
    private readonly TaskCompletionSource _messagingCompletion;
    private readonly TaskCompletionSource<bool> _issued = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Task?> _runPublished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<HangfirePoolOwner> _pools = [];
    private readonly BootstrapInvocation _bootstrap;
    private readonly Task _completionObservation;
    private readonly Task _sourceLifetime;
    private Task? _messagingObservation;
    private Task? _outboxCompletion, _deliveryCompletion;
    private HangfireSharedComponents? _shared;
    private BackgroundJobBridge? _bridge;
    private Task? _bridgeLoop;
    private Task? _bridgeStop;
    private Task? _controlCancellation;
    private bool _started, _stopping, _cleanupFault, _waitExpired, _bridgeFault, _messagingBound;
    private BackgroundJobRuntimeState _state;

    internal BackgroundJobRuntime(PrivilegedDataSource source, BackgroundJobDefinitionRegistry definitions,
        BackgroundJobRecurrenceRegistry recurrences, JobExecutor executor, PostgresBackgroundJobRoutingStore routing,
        BackgroundJobRuntimeGate gate, BackgroundJobRuntimeOptions bounds, IGuidFactory ids, TimeProvider time,
        BackgroundJobPhysicalPoolSettings poolSettings, HangfireBoundedLogProvider diagnostics,
        ApplicationDataSourceLifetime? applicationSource = null, BackgroundJobLifecycleProjection? lifecycleProjection = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(recurrences);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(bounds);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(diagnostics);
        bounds.Validate();
        _capacity = BackgroundJobPhysicalCapacity.Calculate(definitions.Infrastructure);
        _capacity.Validate(poolSettings);
        _applicationCapacity = BackgroundJobApplicationCapacity.Calculate(definitions.Infrastructure);
        if (applicationSource is not null) _applicationCapacity.Validate(applicationSource.PoolSettings);
        _source = source;
        _definitions = definitions;
        _recurrences = recurrences;
        _executor = executor;
        _routing = routing;
        _gate = gate;
        _bounds = bounds;
        _ids = ids;
        _time = time;
        _diagnostics = diagnostics;
        _jobs = new(source, definitions, executor, gate, bounds, ids, time, recurrences, lifecycleProjection);
        // The source pin retains these tasks. Their state must retain the
        // concrete owner even after an observer returns with cleanup unresolved.
        _completion = new(this, TaskCreationOptions.RunContinuationsAsynchronously);
        _messagingCompletion = new(this, TaskCreationOptions.RunContinuationsAsynchronously);
        _bootstrap = new(this);
        // Bind observation before any scheduling. No source is built here.
        if (ExecutionContext.IsFlowSuppressed())
        {
            _completionObservation = ObserveRuntimeAsync();
            _sourceLifetime = ObserveSourceLifetimeAsync();
        }
        else
        {
            using (ExecutionContext.SuppressFlow())
            {
                _completionObservation = ObserveRuntimeAsync();
                _sourceLifetime = ObserveSourceLifetimeAsync();
            }
        }
        // The callback can now see every initialized readonly owner. Bind once
        // before pinning a source; a duplicate observer cannot strand a new pin.
        _executor.Observation.Bind(this, recurrences, time);
        _source.RetainBackgroundJobsUntil(_sourceLifetime);
        applicationSource?.RetainBackgroundJobsUntil(_sourceLifetime);
    }

    public Task Completion => _completion.Task;

    /// <summary>Binds the two actual API messaging owners before any job runtime startup.</summary>
    public void BindMessagingLifetime(Task outboxCompletion, Task deliveryCompletion)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        ArgumentNullException.ThrowIfNull(outboxCompletion);
        ArgumentNullException.ThrowIfNull(deliveryCompletion);
        lock (_sync)
        {
            if (_messagingBound || _started)
                throw new InvalidOperationException("Background job messaging lifetimes bind once before startup.");
            _messagingBound = true;
            _outboxCompletion = outboxCompletion;
            _deliveryCompletion = deliveryCompletion;
        }
        if (ExecutionContext.IsFlowSuppressed())
            _messagingObservation = ObserveMessagingAsync();
        else
        {
            using (ExecutionContext.SuppressFlow())
                _messagingObservation = ObserveMessagingAsync();
        }
    }

    private async Task ObserveMessagingAsync()
    {
        try
        {
            await Task.WhenAll(_outboxCompletion!, _deliveryCompletion!).ConfigureAwait(false);
            _messagingCompletion.TrySetResult();
        }
        catch (Exception failure) when (Catchable(failure)) { RetainCleanup(); }
    }

    private async Task ObserveSourceLifetimeAsync()
    {
        // Unlike a bare WhenAll promise, this retained async owner explicitly
        // roots the facade and its concrete failed-cleanup fields while waiting.
        await Completion.ConfigureAwait(false);
        await _messagingCompletion.Task.ConfigureAwait(false);
        GC.KeepAlive(this);
    }

    public BackgroundJobRuntimeSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                var pools = _pools.Select(pool => pool.Snapshot).ToArray();
                var running = pools.Count(pool => pool.State == HangfirePoolOwnerState.Running);
                var retained = pools.Count(pool => pool.State is HangfirePoolOwnerState.Retained or HangfirePoolOwnerState.Faulted);
                var retainedInvocations = pools.Sum(pool => pool.Work?.Retained ?? 0);
                var closedRunning = pools.Any(pool => pool.State == HangfirePoolOwnerState.Running && pool.Work?.Closed == true);
                var bridge = _bridge?.Snapshot;
                var bridgeHealthy = !_bridgeFault && _bridge?.HasUnconfirmedCleanup == false
                    && bridge is { Retained: 0, LastProgressTimestamp: { } progress }
                    && _time.GetElapsedTime(progress, _time.GetTimestamp()) <= _bounds.ProgressTimeout;
                var expiration = _shared?.Expiration;
                var heartbeat = _shared?.FetchedJobHeartbeat;
                var expirationHealthy = expiration?.IsHealthy == true;
                var heartbeatHealthy = heartbeat?.IsHealthy == true;
                var state = _state;
                if (_cleanupFault || _waitExpired && !Completion.IsCompleted) state = BackgroundJobRuntimeState.Retained;
                else if (retained > 0 || retainedInvocations > 0 || closedRunning) state = BackgroundJobRuntimeState.Retained;
                else if (!_stopping && state == BackgroundJobRuntimeState.Starting && running == _capacity.Pools
                         && retained == 0 && bridgeHealthy && expirationHealthy && heartbeatHealthy) state = BackgroundJobRuntimeState.Running;
                return new(state, _capacity, running, retained, retainedInvocations, bridge, bridgeHealthy, expirationHealthy, heartbeatHealthy,
                    !_started || _bootstrap.ActualJoined, Completion.IsCompletedSuccessfully,
                    Array.AsReadOnly(pools.Select(pool => new BackgroundJobPoolHealth(pool.Pool, pool.State.ToString(),
                        (pool.Generation?.Reason ?? HangfireGenerationReason.None).ToString(), pool.Work?.Retained ?? 0,
                        pool.Generation?.HeartbeatFault == true)
                    {
                        RunningInvocations = pool.Work?.Running ?? 0,
                        Generations = pool.Generations,
                        MetadataHeartbeatTimestamp = pool.Generation?.MetadataHeartbeatTimestamp,
                        GenerationCaptured = pool.Generation?.Captured == true,
                        NormalChildrenJoined = pool.Generation?.NormalChildrenJoined == true,
                        OuterJoined = pool.Generation?.OuterJoined == true,
                        GenerationCleanupComplete = pool.Generation?.CleanupComplete == true,
                    }).ToArray()),
                    Array.AsReadOnly(new[] { MaintenanceHealth("expiration", expiration), MaintenanceHealth("fetched_job_heartbeat", heartbeat) }),
                    _jobs.Snapshot, _applicationCapacity)
                {
                    InstrumentationHealthy = _executor.Observation.IsHealthy,
                };
            }
        }
    }

    private static BackgroundJobMaintenanceHealth MaintenanceHealth(string component, HangfireSharedComponentSnapshot? snapshot) =>
        new(component, (snapshot?.State ?? HangfireSharedComponentState.NotStarted).ToString(),
            (snapshot?.Outcome ?? HangfireSharedComponentOutcome.None).ToString(), snapshot?.Invocations ?? 0,
            snapshot?.ActualJoined ?? true, snapshot?.OwnsResources == true, snapshot?.StorageFault == true,
            snapshot?.IsHealthy == true);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        if (cancellationToken.IsCancellationRequested)
        {
            RequestStop();
            throw new OperationCanceledException(cancellationToken);
        }
        lock (_sync)
        {
            if (_started || _stopping || !_messagingBound || !_gate.TryAdmit())
                throw new InvalidOperationException("A bound background job runtime starts once while its process gate is open.");
            _started = true;
            _state = BackgroundJobRuntimeState.Bootstrapping;
        }
        try
        {
            Task run;
            if (ExecutionContext.IsFlowSuppressed()) run = RunAsync();
            else { using (ExecutionContext.SuppressFlow()) run = RunAsync(); }
            _runPublished.TrySetResult(run);
            _bootstrap.ScheduleStart();
            var bootstrap = await _bootstrap.Completion.WaitAsync(_bounds.ProbeTimeout, _time, cancellationToken)
                .ConfigureAwait(false);
            if (bootstrap.Failed || bootstrap.Provider is null) throw new HangfireStorageRefusedException();
            if (!await _issued.Task.WaitAsync(_bounds.ProgressTimeout, _time, cancellationToken).ConfigureAwait(false))
                throw new HangfireStorageRefusedException();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ExpireWait();
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception failure) when (Catchable(failure))
        {
            ExpireWait();
            throw new HangfireStorageRefusedException();
        }
    }

    /// <summary>Fixed-memory closure only; the retained orchestration broadcasts actual stop/cancellation independently.</summary>
    public void RequestStop()
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        _gate.StopStarts();
        lock (_sync)
        {
            _stopping = true;
            if (_state != BackgroundJobRuntimeState.Stopped) _state = BackgroundJobRuntimeState.Stopping;
            if (!_started)
            {
                _issued.TrySetResult(false);
                _runPublished.TrySetResult(null);
            }
        }
        _jobs.RequestStop();
        _stop.TrySetResult();
    }

    /// <summary>One configured external deadline covers every owner; actual Completion is never canceled by it.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        RequestStop();
        using var timeout = new CancellationTokenSource(_bounds.ShutdownTimeout, _time);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try { await Completion.WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            lock (_sync) _waitExpired = true;
            throw;
        }
    }

    private async Task RunAsync()
    {
        try
        {
            var bootstrap = await _bootstrap.Completion.ConfigureAwait(false);
            if (bootstrap.Failed)
            {
                // The existing sanitized bootstrap helper supplies no separate
                // disposal-fault witness. Refuse and retain its actual owner.
                RetainCleanup();
                return;
            }
            if (bootstrap.Provider is null || !MayStart()) return;
            PreallocateOwners(bootstrap.Provider);
            if (!MayStart()) return;
            HangfirePoolOwner[] pools;
            lock (_sync) { pools = _pools.ToArray(); _state = BackgroundJobRuntimeState.Starting; }
            _shared!.Start(); // PreallocateOwners published both independent shared owners.
            _jobs.Start();
            foreach (var pool in pools) pool.Start();
            _bridgeLoop = RunBridgeAsync();
            _issued.TrySetResult(true);
            await Task.WhenAny(_stop.Task, _gate.FinalExitSignal).ConfigureAwait(false);
        }
        catch (Exception failure) when (Catchable(failure)) { RetainCleanup(); }
        finally
        {
            _issued.TrySetResult(false);
            RequestStop();
            await JoinOwnersAsync().ConfigureAwait(false);
        }
    }

    private void PreallocateOwners(PostgreSqlStorage provider)
    {
        var inventory = new HangfireComponentInventory(provider);
        var filters = HangfireRuntimeFilters.Create();
        _jobs.BindRecurrenceClient(new HangfireRecurrenceClient(provider, filters));
        var shared = new HangfireSharedComponents(inventory, _gate, _time, _diagnostics);
        var store = new PostgresBackgroundJobBridgeStore(_source, _definitions, _bounds, _ids, _gate);
        var bridge = new BackgroundJobBridge(store, new HangfireWakeupClient(provider, filters), _bounds, _time, _gate);
        lock (_sync) { _shared = shared; _bridge = bridge; }
        foreach (var pool in _definitions.Infrastructure.Pools.Values.OrderBy(pool => pool.Name, StringComparer.Ordinal))
        {
            var activator = HangfireInvocationContract.CreateActivator(pool, _definitions.Infrastructure,
                _routing, _executor, _recurrences, _gate, _jobs);
            var owner = new HangfirePoolOwner(provider, inventory, filters, activator, pool, _gate, _bounds, _time);
            lock (_sync) _pools.Add(owner);
        }
        // All owners exist before the first Start. A late stop closes the same
        // gate checked by every actual owner invocation and physical acquisition.
    }

    private async Task RunBridgeAsync()
    {
        while (!_control.IsCancellationRequested && _gate.IsOpen)
        {
            var didWork = false;
            try
            {
                didWork = await _bridge!.RunQuantumAsync(_control.Token).ConfigureAwait(false); // Preallocated before loop issuance.
                lock (_sync) _bridgeFault = false;
            }
            catch (OperationCanceledException) when (_control.IsCancellationRequested) { break; }
            catch (Exception failure) when (Catchable(failure))
            {
                lock (_sync) _bridgeFault = true;
                if (_bridge!.HasUnconfirmedCleanup) { RetainCleanup(); break; }
            }
            if (!didWork)
            {
                try { await Task.Delay(_bounds.ControlPoll, _time, _control.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_control.IsCancellationRequested) { break; }
            }
        }
    }

    private async Task JoinOwnersAsync()
    {
        HangfirePoolOwner[] pools;
        HangfireSharedComponents? shared;
        BackgroundJobBridge? bridge;
        lock (_sync) { pools = _pools.ToArray(); shared = _shared; bridge = _bridge; }
        _jobs.RequestStop();
        foreach (var pool in pools)
        {
            try { pool.RequestStop(); }
            catch (Exception failure) when (Catchable(failure)) { RetainCleanup(); }
        }
        try { shared?.RequestStop(); }
        catch (Exception failure) when (Catchable(failure)) { RetainCleanup(); }
        var joins = pools.Select(pool => pool.Completion).ToList();
        joins.Add(_jobs.Completion);
        if (shared is not null) joins.Add(shared.Completion);
        if (_bridgeLoop is not null) joins.Add(_bridgeLoop);
        try { _controlCancellation = _control.CancelAsync(); joins.Add(_controlCancellation); }
        catch (Exception failure) when (Catchable(failure)) { RetainCleanup(); }
        // Every other owner has already been signaled before bridge stop can
        // encounter a retained quantum, native startup or callback.
        if (bridge is not null)
        {
            try { _bridgeStop = bridge.StopAsync(); joins.Add(_bridgeStop); }
            catch (Exception failure) when (Catchable(failure)) { RetainCleanup(); }
        }
        try { await Task.WhenAll(joins).ConfigureAwait(false); }
        catch (Exception failure) when (Catchable(failure)) { RetainCleanup(); }
        if (bridge?.HasUnconfirmedCleanup == true) RetainCleanup();
        try { _control.Dispose(); }
        catch (Exception failure) when (Catchable(failure)) { RetainCleanup(); }
    }

    private async Task ObserveRuntimeAsync()
    {
        try
        {
            var actual = await _runPublished.Task.ConfigureAwait(false);
            if (actual is not null) await actual.ConfigureAwait(false);
            else
            {
                await _jobs.Completion.ConfigureAwait(false);
                _control.Dispose(); // No runtime operation was ever issued.
            }
            lock (_sync)
            {
                if (_cleanupFault) return;
                _state = BackgroundJobRuntimeState.Stopped;
                _completion.TrySetResult();
            }
        }
        catch (Exception failure) when (Catchable(failure)) { RetainCleanup(); }
    }

    private bool MayStart()
    {
        lock (_sync) return !_stopping && !_cleanupFault && _gate.TryAdmit();
    }

    private void ExpireWait()
    {
        lock (_sync) _waitExpired = true;
        RequestStop();
    }

    private void RetainCleanup()
    {
        lock (_sync) _cleanupFault = true;
        RequestStop();
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static bool Catchable(Exception failure) => failure.GetType() != typeof(OutOfMemoryException)
        && failure.GetType() != typeof(StackOverflowException);

    private sealed record BootstrapResult(PostgreSqlStorage? Provider, bool Failed);

    /// <summary>One native bootstrap and a dedicated actual join; no supplied factory or callback.</summary>
    private sealed class BootstrapInvocation
    {
        private readonly BackgroundJobRuntime _owner;
        private readonly Thread _native;
        private readonly Thread _join;
        private readonly TaskCompletionSource<Task?> _startupPublished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<BootstrapResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private PostgreSqlStorage? _provider;
        private bool _failed;
        private int _startupFailed, _joined;

        internal BootstrapInvocation(BackgroundJobRuntime owner)
        {
            _owner = owner;
            _native = new Thread(Execute) { IsBackground = true, Name = "LearnStack storage bootstrap" };
            _join = new Thread(JoinAndPublish) { IsBackground = true, Name = "LearnStack bootstrap join" };
        }

        internal Task<BootstrapResult> Completion => _completion.Task;
        internal bool ActualJoined => Volatile.Read(ref _joined) != 0;

        internal void ScheduleStart()
        {
            try
            {
                Task startup;
                if (ExecutionContext.IsFlowSuppressed()) startup = Task.Run(StartThreads);
                else { using (ExecutionContext.SuppressFlow()) startup = Task.Run(StartThreads); }
                _startupPublished.TrySetResult(startup);
            }
            catch (Exception failure) when (Catchable(failure))
            {
                Interlocked.Exchange(ref _startupFailed, 1);
                _owner.RetainCleanup();
                _startupPublished.TrySetResult(null);
            }
        }

        private void StartThreads()
        {
            try { _join.Start(); _native.Start(); }
            catch (Exception failure) when (Catchable(failure))
            {
                Interlocked.Exchange(ref _startupFailed, 1);
                _owner.RetainCleanup();
            }
        }

        private void Execute()
        {
            try
            {
                _owner._gate.EnsureFinalExitRegistered();
                if (_owner._gate.TryAdmit())
                    _provider = HangfireStorageBootstrap.Create(_owner._source,
                        _owner._definitions.Infrastructure, _owner._diagnostics, _owner._gate);
            }
            catch (Exception failure) when (Catchable(failure)) { _failed = true; }
        }

        private void JoinAndPublish()
        {
            try
            {
                var startup = _startupPublished.Task.GetAwaiter().GetResult();
                startup?.GetAwaiter().GetResult();
                if (Volatile.Read(ref _startupFailed) != 0 || startup is null) return;
                _native.Join();
                Interlocked.Exchange(ref _joined, 1);
                _completion.TrySetResult(new(_provider, _failed));
            }
            catch (Exception failure) when (Catchable(failure)) { _owner.RetainCleanup(); }
        }
    }
}
