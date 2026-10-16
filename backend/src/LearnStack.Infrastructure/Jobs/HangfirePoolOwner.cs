using Hangfire;
using Hangfire.PostgreSql;

namespace LearnStack.Infrastructure.Jobs;

internal enum HangfirePoolOwnerState
{
    NotStarted, Starting, Running, Draining, Retained, Backoff, Stopping, Stopped, Faulted,
}

internal sealed record HangfirePoolOwnerSnapshot(string Pool, HangfirePoolOwnerState State,
    HangfireGenerationSnapshot? Generation, long Generations);

/// <summary>One closed replacement owner retains the actual generation and all its cleanup.</summary>
internal sealed class HangfirePoolOwner
{
    private readonly object _sync = new();
    private readonly PostgreSqlStorage _provider;
    private readonly HangfireComponentInventory _inventory;
    private readonly HangfireRuntimeFilters _filters;
    private readonly JobActivator _activator;
    private readonly BackgroundJobWorkerPoolRegistration _pool;
    private readonly BackgroundJobRuntimeGate _gate;
    private readonly BackgroundJobRuntimeOptions _bounds;
    private readonly TimeProvider _time;
    private readonly TaskCompletionSource _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Task?> _supervisionPublished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _completionObservation;
    private HangfireGeneration? _generation;
    private Task? _loop;
    private bool _started, _stopping;
    private long _generations;
    private HangfirePoolOwnerState _state;

    internal HangfirePoolOwner(PostgreSqlStorage provider, HangfireComponentInventory inventory,
        HangfireRuntimeFilters filters, JobActivator activator,
        BackgroundJobWorkerPoolRegistration pool, BackgroundJobRuntimeGate gate,
        BackgroundJobRuntimeOptions bounds, TimeProvider timeProvider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _filters = filters ?? throw new ArgumentNullException(nameof(filters));
        _activator = activator ?? throw new ArgumentNullException(nameof(activator));
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _bounds = bounds ?? throw new ArgumentNullException(nameof(bounds));
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        // Bind the actual-task observer before Start can publish ownership.
        // Its publication signal is not itself a supervisor-completion witness.
        if (ExecutionContext.IsFlowSuppressed()) _completionObservation = ObserveSupervisionAsync();
        else { using (ExecutionContext.SuppressFlow()) _completionObservation = ObserveSupervisionAsync(); }
    }

    internal Task Completion => _completion.Task;

    internal HangfirePoolOwnerSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                var generation = _generation?.Snapshot;
                var state = _state;
                if (state is HangfirePoolOwnerState.Starting or HangfirePoolOwnerState.Running)
                {
                    if (generation?.Reason is HangfireGenerationReason.StartupStalled or HangfireGenerationReason.CleanupFault)
                        state = HangfirePoolOwnerState.Retained;
                    else if (generation?.Draining == true) state = HangfirePoolOwnerState.Draining;
                    else if (_generation?.Ready is { IsCompletedSuccessfully: true } ready && ready.Result)
                        state = HangfirePoolOwnerState.Running;
                }
                return new(_pool.Name, state, generation, _generations);
            }
        }
    }

    internal void Start()
    {
        lock (_sync)
        {
            if (_started || _stopping) return;
            _started = true;
            _state = HangfirePoolOwnerState.Starting;
        }
        try
        {
            // Scheduling/native worker startup can stall. RequestStop retains
            // its fixed-memory lock independently of that actual invocation.
            if (ExecutionContext.IsFlowSuppressed()) _loop = Task.Run(SuperviseAsync);
            else { using (ExecutionContext.SuppressFlow()) _loop = Task.Run(SuperviseAsync); }
            _supervisionPublished.TrySetResult(_loop);
        }
        catch (Exception failure) when (Catchable(failure))
        {
            // No returned task means an unknown scheduling outcome. Keep its
            // prebound observer pending rather than publishing a guessed join.
            RetainFault();
        }
    }

    internal void RequestStop()
    {
        HangfireGeneration? current;
        lock (_sync)
        {
            _stopping = true;
            current = _generation;
            if (!_started)
            {
                // No supervision or generation construction was ever issued.
                _state = HangfirePoolOwnerState.Stopped;
                _supervisionPublished.TrySetResult(null);
            }
            else if (_state != HangfirePoolOwnerState.Faulted && _state != HangfirePoolOwnerState.Stopped)
                _state = HangfirePoolOwnerState.Stopping;
        }
        _stop.TrySetResult();
        // Independently of the loop, request this same generation's private
        // cancellation relays. No native callback or join runs on this caller.
        try { current?.RequestHostStop(); }
        catch (Exception failure) when (Catchable(failure)) { RetainFault(); }
    }

    internal async Task StopAsync(CancellationToken callerBudget)
    {
        RequestStop();
        await Completion.WaitAsync(callerBudget).ConfigureAwait(false);
    }

    private async Task SuperviseAsync()
    {
        try
        {
            while (MayStart())
            {
                // Prevalidation/construction failures remain owned faults. This
                // owner never infers cleanup from an exception or elapsed time.
                var generation = new HangfireGeneration(_provider, _inventory, _filters,
                    _activator, _pool, _gate, _bounds, _time);
                bool start;
                lock (_sync)
                {
                    _generation = generation;
                    _generations = checked(_generations + 1);
                    start = !_stopping && _state != HangfirePoolOwnerState.Faulted && _gate.TryAdmit();
                }
                if (start)
                {
                    try { generation.Start(); }
                    catch (Exception failure) when (Catchable(failure) && IsStopping())
                    {
                        // Stop can consume the generation's one start permit
                        // before Start reaches it. Only actual Completion below
                        // can establish the resulting positive no-Core cleanup.
                        generation.RequestHostStop();
                    }
                }
                else generation.RequestHostStop();

                // Caller cancellation never cancels, substitutes for or abandons
                // this actual stock-and-LearnStack completion witness.
                await generation.Completion.ConfigureAwait(false);
                if (IsStopping()) break;

                if (generation.Snapshot.Reason == HangfireGenerationReason.StartupStalled)
                {
                    // Late positive cleanup does not reopen a startup-stalled
                    // pool. Its retained owner can still join on host stop.
                    SetState(HangfirePoolOwnerState.Retained);
                    await Task.WhenAny(_stop.Task, _gate.FinalExitSignal).ConfigureAwait(false);
                    break;
                }

                SetState(HangfirePoolOwnerState.Backoff);
                await BackoffAsync().ConfigureAwait(false);
            }
        }
        catch (Exception failure) when (Catchable(failure))
        {
            RetainFault();
            // The current generation and actual loop stay retained. Closing
            // admission grants neither replacement nor a guessed cleanup join.
            _generation?.RequestDrain(HangfireGenerationReason.BuilderFailure);
        }
    }

    private async Task ObserveSupervisionAsync()
    {
        try
        {
            var actual = await _supervisionPublished.Task.ConfigureAwait(false);
            if (actual is not null) await actual.ConfigureAwait(false);
            lock (_sync)
            {
                if (_state == HangfirePoolOwnerState.Faulted) return;
                _state = HangfirePoolOwnerState.Stopped;
                _completion.TrySetResult();
            }
        }
        catch (Exception failure) when (Catchable(failure)) { RetainFault(); }
    }

    private bool MayStart()
    {
        lock (_sync)
        {
            if (_stopping || _state == HangfirePoolOwnerState.Faulted || !_gate.TryAdmit()) return false;
            _state = HangfirePoolOwnerState.Starting;
            return true;
        }
    }

    private bool IsStopping()
    {
        lock (_sync) return _stopping || !_gate.IsOpen;
    }

    private void SetState(HangfirePoolOwnerState state)
    {
        lock (_sync)
            if (_state != HangfirePoolOwnerState.Faulted)
                _state = _stopping ? HangfirePoolOwnerState.Stopping : state;
    }

    private void RetainFault()
    {
        lock (_sync) _state = HangfirePoolOwnerState.Faulted;
    }

    private async Task BackoffAsync()
    {
        // This source owns only Task.Delay's closed callback. Its cancellation
        // and disposal are part of the retained loop, never arbitrary job work.
        using var timer = new CancellationTokenSource();
        var delay = Task.Delay(HangfireGeneration.ReplacementBackoff, _time, timer.Token);
        var winner = await Task.WhenAny(delay, _stop.Task, _gate.FinalExitSignal).ConfigureAwait(false);
        if (winner != delay) await timer.CancelAsync().ConfigureAwait(false);
        try { await delay.ConfigureAwait(false); }
        catch (OperationCanceledException) when (timer.IsCancellationRequested) { }
    }

    private static bool Catchable(Exception failure) => failure.GetType() != typeof(OutOfMemoryException)
        && failure.GetType() != typeof(StackOverflowException);
}
