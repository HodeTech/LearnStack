using System.Diagnostics.CodeAnalysis;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Infrastructure.Jobs;

public sealed record BackgroundJobRecurrenceHealth(int ApplicationOwners, int RetainedApplications,
    int PendingHistory, bool NativeOutstanding, bool NativeRetained, bool SqlHealthy,
    bool NativeHealthy, bool CleanupConfirmed, bool HasQuarantinedControl, bool IsHealthy);

/// <summary>Fair durable quanta remain independent of native timers and actual application cleanup.</summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The parent owns actual completion; a finite wait cannot dispose a live lane or callback.")]
internal sealed class BackgroundJobRecurrenceControl
{
    internal const int ApplicationOwnerLimit = 4;
    private readonly object _sync = new();
    private readonly PostgresBackgroundJobControlStore _store;
    private readonly BackgroundJobDefinitionRegistry _definitions;
    private readonly BackgroundJobRecurrenceRegistry _registry;
    private readonly BackgroundJobLifecycleProjection? _projection;
    private readonly JobExecutor _executor;
    private readonly BackgroundJobRuntimeGate _runtime;
    private readonly BackgroundJobRuntimeOptions _bounds;
    private readonly TimeProvider _time;
    private readonly Dictionary<Guid, PendingRecipient> _recipients = [];
    private readonly Dictionary<string, long> _nativeAfter = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deploymentMismatch = new(StringComparer.Ordinal);
    private readonly HashSet<string> _nativeFailures = new(StringComparer.Ordinal);
    private readonly bool[] _faults = new bool[5];
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _stopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Task?> _loopPublished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completion;
    private readonly Task _stopRelay, _completionObservation;
    private HangfireRecurrenceClient? _client;
    private BackgroundJobNativeRecurrenceOperation? _native;
    private Task? _sqlFlight;
    private object? _sqlOperation;
    private bool _started, _stopping, _cleanupFault, _quarantined;
    private long? _progress;
    private int _turn, _keyCursor, _historyCursor, _tick;

    internal BackgroundJobRecurrenceControl(PrivilegedDataSource source, BackgroundJobDefinitionRegistry definitions,
        BackgroundJobRecurrenceRegistry registry, BackgroundJobLifecycleProjection? projection, JobExecutor executor,
        BackgroundJobRuntimeGate runtime, BackgroundJobRuntimeOptions bounds, IGuidFactory ids, TimeProvider time)
    {
        if (registry.Keys.Count != 0 && projection is null)
            throw new ArgumentException("Recurrence requires its declared lifecycle projection.", nameof(projection));
        _store = new(source, definitions, bounds, ids, runtime);
        _definitions = definitions;
        _registry = registry;
        _projection = projection;
        _executor = executor;
        _runtime = runtime;
        _bounds = bounds;
        _time = time;
        _completion = new(this, TaskCreationOptions.RunContinuationsAsynchronously);
        _stopRelay = RelayStopAsync();
        _completionObservation = ObserveCompletionAsync();
    }

    internal Task Completion => _completion.Task;

    internal BackgroundJobRecurrenceHealth Snapshot
    {
        get
        {
            lock (_sync)
            {
                var retained = _recipients.Values.Count(item => item.Operation.Retained);
                var history = _recipients.Values.Count(item => item.Operation.Completion.IsCompletedSuccessfully
                    && item.Operation.Result is { Command.Gate.CommitAcknowledged: false });
                var nativeOutstanding = _native is { Completion.IsCompleted: false };
                var nativeRetained = nativeOutstanding && _native is { } native
                    && _time.GetElapsedTime(native.StartedAt, _time.GetTimestamp()) >= _bounds.BridgeWaitTimeout;
                var sqlHealthy = !_cleanupFault && !_faults.Any(value => value)
                    && (_registry.Keys.Count == 0 || _progress is { } progress
                        && _time.GetElapsedTime(progress, _time.GetTimestamp()) <= _bounds.ProgressTimeout);
                var nativeHealthy = _nativeFailures.Count == 0 && !nativeRetained;
                return new(_recipients.Count, retained, history, nativeOutstanding, nativeRetained,
                    sqlHealthy, nativeHealthy, Completion.IsCompletedSuccessfully, _quarantined,
                    _started && !_stopping && sqlHealthy && nativeHealthy && retained == 0 && history == 0
                        && _deploymentMismatch.Count == 0 && !_quarantined);
            }
        }
    }

    internal void BindNativeClient(HangfireRecurrenceClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        lock (_sync)
        {
            if (_client is not null || _started || _stopping)
                throw new InvalidOperationException("The recurrence client binds once before startup.");
            _client = client;
        }
    }

    internal void Start()
    {
        lock (_sync)
        {
            if (_started || _stopping || _registry.Keys.Count != 0 && _client is null)
                throw new InvalidOperationException("The recurrence owner starts once with its bound native client.");
            _started = true;
            _progress = _time.GetTimestamp();
        }
        try
        {
            Task loop;
            if (_registry.Keys.Count == 0) loop = Task.CompletedTask;
            else if (ExecutionContext.IsFlowSuppressed()) loop = Task.Run(RunAsync);
            else { using (ExecutionContext.SuppressFlow()) loop = Task.Run(RunAsync); }
            _loopPublished.TrySetResult(loop);
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure))
        { lock (_sync) _cleanupFault = true; }
    }

    internal void SignalTick(string key, long revision)
    {
        if (!_registry.TryGet(key, out var manifest) || manifest.Facts.Revision != revision)
            throw new InvalidOperationException("Recurrence hints require an exact registered revision.");
        lock (_sync)
        {
            if (!_started || _stopping || !_runtime.TryAdmit()) return;
            Interlocked.Exchange(ref _tick, 1);
        }
    }

    internal void RequestStop()
    {
        BackgroundJobRecipientOperation[] recipients;
        BackgroundJobNativeRecurrenceOperation? native;
        lock (_sync)
        {
            _stopping = true;
            if (!_started) _loopPublished.TrySetResult(null);
            recipients = _recipients.Values.Select(item => item.Operation).ToArray();
            native = _native;
        }
        native?.RequestStop();
        foreach (var recipient in recipients) recipient.RequestStop();
        _stopRequested.TrySetResult();
    }

    private async Task RunAsync()
    {
        while (MayStart())
        {
            var turn = _turn;
            _turn = (_turn + 1) % _faults.Length;
            try
            {
                ObserveNative();
                switch (turn)
                {
                    case 0: await RegisterAsync().ConfigureAwait(false); break;
                    case 1: await ObserveSqlAsync(_store.MaterializeRecurrenceAsync(_registry, _stop.Token), turn).ConfigureAwait(false); break;
                    case 2: await ObserveSqlAsync(_store.MaterializeRecipientPageAsync(_registry, _projection!, _stop.Token), turn).ConfigureAwait(false); break;
                    case 3: await ClaimAsync().ConfigureAwait(false); break;
                    case 4: await ResolveOneAsync().ConfigureAwait(false); break;
                }
            }
            catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure))
            { lock (_sync) _faults[turn] = true; }
            lock (_sync) { if (_cleanupFault) break; }
            if (Interlocked.Exchange(ref _tick, 0) != 0) continue;
            try { await Task.Delay(_bounds.ControlPoll, _time, _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
        }
    }

    private async Task RegisterAsync()
    {
        var key = _registry.Keys[_keyCursor];
        _keyCursor = (_keyCursor + 1) % _registry.Keys.Count;
        _registry.TryGet(key, out var manifest);
        var result = await ObserveSqlAsync(_store.RegisterRecurrenceAsync(manifest!, _stop.Token), 0).ConfigureAwait(false);
        if (result is null) return;
        var compatible = result.CommitAcknowledged
            && result.Value is BackgroundJobRecurrenceRegistrationResult.Registered or BackgroundJobRecurrenceRegistrationResult.Existing
            && _definitions.TryGet(manifest!.Facts.Target, out var target)
            && target.Readiness == BackgroundJobDefinitionReadiness.Active;
        BackgroundJobNativeRecurrenceOperation? native = null;
        lock (_sync)
        {
            if (!compatible) { _deploymentMismatch.Add(key); return; }
            _deploymentMismatch.Remove(key);
            if (_stopping || !_runtime.TryAdmit() || _native is not null || _client is null
                || _nativeAfter.GetValueOrDefault(key) > _time.GetTimestamp()) return;
            native = new(manifest!, _client, _runtime, _time);
            _native = native;
            _nativeAfter[key] = Later(_bounds.HandoffRepairGrace);
        }
        native.Start();
    }

    private async Task ClaimAsync()
    {
        Guid[] retained;
        lock (_sync)
        {
            if (_recipients.Count >= ApplicationOwnerLimit) return;
            retained = _recipients.Keys.ToArray();
        }
        var result = await ObserveSqlAsync(_store.TryClaimRecipientAsync(_registry, retained, _stop.Token), 3).ConfigureAwait(false);
        if (result?.Value is not { } claim) return;
        BackgroundJobRecipientOperation operation;
        lock (_sync)
        {
            if (_stopping || !_runtime.TryAdmit() || _recipients.Count >= ApplicationOwnerLimit) return;
            operation = new(claim, _executor, _projection!, _runtime, _bounds, _time);
            _recipients.Add(claim.RecipientId.Value, new(operation));
        }
        operation.Start();
    }

    private async Task ResolveOneAsync()
    {
        PendingRecipient? pending;
        lock (_sync)
        {
            var ready = _recipients.Values.Where(item => item.Operation.Completion.IsCompletedSuccessfully
                && item.After <= _time.GetTimestamp()).ToArray();
            pending = ready.Length == 0 ? null : ready[_historyCursor % ready.Length];
            _historyCursor = ready.Length == 0 ? 0 : (_historyCursor % ready.Length + 1) % ready.Length;
            if (_recipients.Count == 0) _faults[4] = false;
        }
        if (pending?.Operation.Result is not { } original) return;
        var id = pending.Operation.Claim.RecipientId.Value;
        if (original.Command is null || original.Command.Gate.CommitAcknowledged)
        {
            lock (_sync) { _recipients.Remove(id); _faults[4] = false; }
            return;
        }
        var resolution = await ObserveSqlAsync(_store.ResolveRecipientAsync(original, _projection!, _stop.Token), 4).ConfigureAwait(false);
        lock (_sync)
        {
            if (resolution?.Value is not { } value || value.Status == BackgroundJobRecipientResolutionStatus.Unavailable)
            { pending.After = Later(_bounds.RecoveryProbeInterval); return; }
            if (value.Status == BackgroundJobRecipientResolutionStatus.Quarantined) _quarantined = true;
            _recipients.Remove(id);
        }
    }

    private async Task<OwnedBackgroundJobControl<T>?> ObserveSqlAsync<T>(Task<OwnedBackgroundJobControl<T>> flight, int turn)
    {
        lock (_sync) _sqlFlight = flight;
        var operation = await flight.ConfigureAwait(false);
        lock (_sync)
        {
            _sqlOperation = operation;
            if (!operation.CleanupConfirmed) { _cleanupFault = true; return null; }
            _sqlOperation = null;
            _sqlFlight = null;
            _faults[turn] = operation.Failed || operation.CommitUncertain;
            if (!_faults[turn]) _progress = _time.GetTimestamp();
            return _faults[turn] ? null : operation;
        }
    }

    private void ObserveNative()
    {
        lock (_sync)
        {
            if (_native is not { Completion.IsCompletedSuccessfully: true } native) return;
            if (native.Result == false) _nativeFailures.Add(native.Manifest.Facts.Key);
            else if (native.Result == true) _nativeFailures.Remove(native.Manifest.Facts.Key);
            _native = null;
        }
    }

    private bool MayStart() { lock (_sync) return !_stopping && !_cleanupFault && _runtime.TryAdmit(); }
    private long Later(TimeSpan delay) => checked(_time.GetTimestamp() + (long)(delay.TotalSeconds * _time.TimestampFrequency));

    private async Task RelayStopAsync()
    {
        await _stopRequested.Task.ConfigureAwait(false);
        try { await _stop.CancelAsync().ConfigureAwait(false); }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure))
        { lock (_sync) _cleanupFault = true; }
    }

    private async Task ObserveCompletionAsync()
    {
        try
        {
            var loop = await _loopPublished.Task.ConfigureAwait(false);
            if (loop is not null) await loop.ConfigureAwait(false);
            await _stopRelay.ConfigureAwait(false);
            Task[] actual;
            lock (_sync)
                actual = _recipients.Values.Select(item => item.Operation.Completion)
                    .Concat(_native is { } native ? [native.Completion] : Array.Empty<Task>()).ToArray();
            await Task.WhenAll(actual).ConfigureAwait(false);
            _stop.Dispose();
            lock (_sync) { if (!_cleanupFault) _completion.TrySetResult(); }
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure))
        { lock (_sync) _cleanupFault = true; }
    }

    private sealed class PendingRecipient(BackgroundJobRecipientOperation operation)
    {
        internal BackgroundJobRecipientOperation Operation { get; } = operation;
        internal long After { get; set; }
    }
}
