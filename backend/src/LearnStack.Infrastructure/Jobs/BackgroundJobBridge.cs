using LearnStack.Application.Jobs;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Infrastructure.Jobs;

public sealed record BackgroundJobBridgeSnapshot(int Outstanding, int Retained, long Acknowledged,
    long Failed, long Deferred, long? LastProgressTimestamp)
{
    public long PriorHintRepairsAcknowledged { get; init; }
    public long ExpiredTokenRepairsAcknowledged { get; init; }
}

/// <summary>Independent, bounded handoff quanta. Host lifecycle/composition supplies its polling and shared deadline.</summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The process-owned semaphore never creates a wait handle and remains live with retained operations.")]
public sealed class BackgroundJobBridge(PostgresBackgroundJobBridgeStore store, HangfireWakeupClient client,
    BackgroundJobRuntimeOptions options, TimeProvider time, BackgroundJobRuntimeGate? runtimeGate = null)
{
    private readonly object _gate = new();
    private readonly List<BridgeOperation> _operations = [];
    private readonly SemaphoreSlim _quantum = new(1, 1);
    private bool _stopping;
    private long _acknowledged;
    private long _failed;
    private long _deferred;
    private long _priorHintRepairs, _expiredTokenRepairs;
    private long? _lastProgress;

    internal bool HasUnconfirmedCleanup => store.HasUnconfirmedCleanup;

    public BackgroundJobBridgeSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return new(_operations.Count(operation => !operation.Completion.IsCompleted),
                    _operations.Count(operation => !operation.Completion.IsCompleted
                        && time.GetElapsedTime(operation.Started, time.GetTimestamp()) >= options.BridgeWaitTimeout),
                    _acknowledged, _failed, _deferred, _lastProgress)
                {
                    PriorHintRepairsAcknowledged = _priorHintRepairs,
                    ExpiredTokenRepairsAcknowledged = _expiredTokenRepairs,
                };
        }
    }

    /// <summary>One just-in-time claim; a held vendor call never blocks another control lane.</summary>
    public async Task<bool> RunQuantumAsync(CancellationToken cancellationToken = default)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        options.Validate();
        await _quantum.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BackgroundJobId[] retained;
            lock (_gate)
            {
                _operations.RemoveAll(operation => operation.Completion.IsCompleted);
                if (_stopping || store.HasUnconfirmedCleanup || runtimeGate is { IsOpen: false }
                    || _operations.Count >= BackgroundJobRuntimeOptions.MaxOutstandingBridgeCalls) return false;
                retained = _operations.Select(operation => operation.Claim.JobId).ToArray();
            }
            var claim = await store.TryClaimAsync(retained, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _lastProgress = time.GetTimestamp();
                if (_stopping || runtimeGate is { IsOpen: false } || claim is null) return false;
            }
            if (!claim.CanHandoff)
            {
                if (await store.DeferAsync(claim, BackgroundJobBridgeDeferral.DeploymentMismatch, cancellationToken).ConfigureAwait(false))
                    lock (_gate) _deferred++;
                return true;
            }
            BridgeOperation operation;
            lock (_gate)
            {
                if (_stopping || runtimeGate is { IsOpen: false }) return false;
                operation = new BridgeOperation(claim, client, time.GetTimestamp(), this);
                _operations.Add(operation);
            }
            // Native startup may itself stall. Its bound owner is visible before
            // scheduling, and no fixed-state lock is held across Task.Run/Start.
            operation.ScheduleStart();
            return true;
        }
        finally { _quantum.Release(); }
    }

    /// <summary>Close starts before joining. Caller supplies the one host-wide finite shutdown deadline.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        lock (_gate) _stopping = true;
        store.StopAcquisitions();
        // Include an already-started quantum's actual SQL/cleanup in this join.
        await _quantum.WaitAsync(cancellationToken).ConfigureAwait(false);
        Task[] pending;
        try { lock (_gate) pending = _operations.Select(operation => operation.Completion).ToArray(); }
        finally { _quantum.Release(); }
        await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ObserveAsync(BridgeOperation operation)
    {
        try
        {
            // A native result can arrive before Thread.Start returns. Even a faulted
            // startup task supplies no substitute for the thread/result witnesses.
            await operation.Startup.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            var result = await operation.Result.Task.ConfigureAwait(false);
            // Result signals the finished client call; wait for the actual thread to
            // return before forgetting its owner. No thread-pool thread performs a join.
            while (operation.Thread.IsAlive)
                await Task.Delay(TimeSpan.FromMilliseconds(1), time, CancellationToken.None).ConfigureAwait(false);
            try
            {
                bool stopping;
                lock (_gate) stopping = _stopping;
                if (!stopping)
                {
                    var changed = result is not null
                        ? await store.AcknowledgeAsync(operation.Claim, result).ConfigureAwait(false)
                        : await store.DeferAsync(operation.Claim, BackgroundJobBridgeDeferral.CreationFailed).ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (result is not null && changed)
                        {
                            _acknowledged++;
                            switch (operation.Claim.Kind)
                            {
                                case BackgroundJobBridgeClaimKind.PriorAcknowledgedHint: _priorHintRepairs++; break;
                                case BackgroundJobBridgeClaimKind.ExpiredPriorBridgeToken: _expiredTokenRepairs++; break;
                            }
                        }
                        else _failed++;
                        _lastProgress = time.GetTimestamp();
                    }
                }
            }
            catch (Exception exception) when (Catchable(exception))
            {
                // A confirmed physical return permits durable same-generation
                // repair. Failed connection cleanup retains this concrete owner.
                lock (_gate) _failed++;
            }
            if (!store.HasUnconfirmedCleanup) operation.Complete();
        }
        catch (Exception exception) when (Catchable(exception))
        {
            // A failed observer is not native-end/cleanup evidence. Retain its
            // independent positive completion signal and close no ownership.
            lock (_gate) _failed++;
        }
    }

    private static bool Catchable(Exception exception) => exception.GetType() != typeof(OutOfMemoryException)
        && exception.GetType() != typeof(StackOverflowException);

    private bool TryAdmitStart()
    {
        lock (_gate)
            return !_stopping && !store.HasUnconfirmedCleanup && (runtimeGate is null || runtimeGate.TryAdmit());
    }

    private sealed class BridgeOperation
    {
        private readonly BackgroundJobBridge _owner;
        private readonly TaskCompletionSource<Task> _startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal BridgeOperation(BackgroundJobBridgeClaim claim, HangfireWakeupClient client, long started,
            BackgroundJobBridge owner)
        {
            _owner = owner;
            Claim = claim;
            Started = started;
            Startup = _startup.Task.Unwrap();
            Thread = new Thread(() =>
            {
                string? result = null;
                var culture = CultureInfo.CurrentCulture;
                var uiCulture = CultureInfo.CurrentUICulture;
                try
                {
                    CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                    CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
                    // Scheduling and native startup are not Create admission.
                    if (owner.TryAdmitStart()) result = client.Create(claim);
                }
                catch (Exception exception) when (Catchable(exception))
                { }
                finally
                {
                    CultureInfo.CurrentCulture = culture;
                    CultureInfo.CurrentUICulture = uiCulture;
                    Result.TrySetResult(result);
                }
            })
            { IsBackground = true, Name = "learnstack-job-bridge" };
            // Bind observation before any start attempt. Even a catchable Start
            // failure cannot leave a default-completed owner to be pruned.
            Observer = owner.ObserveAsync(this);
        }

        internal BackgroundJobBridgeClaim Claim { get; }
        internal long Started { get; }
        internal Thread Thread { get; }
        internal TaskCompletionSource<string?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Startup { get; }
        internal Task Observer { get; }
        internal Task Completion => _completion.Task;
        internal void Complete() => _completion.TrySetResult();

        internal void ScheduleStart()
        {
            try
            {
                Task startup;
                if (ExecutionContext.IsFlowSuppressed()) startup = Task.Run(Start);
                else
                {
                    using var suppressed = ExecutionContext.SuppressFlow();
                    startup = Task.Run(Start);
                }
                _startup.TrySetResult(startup);
            }
            catch (Exception exception) when (Catchable(exception))
            {
                // Without the actual scheduled task, no witness excludes a late
                // startup callback. Leave the prebound observer retained.
            }
        }

        internal void Start()
        {
            if (!_owner.TryAdmitStart())
            {
                Result.TrySetResult(null);
                return;
            }
            try
            {
                if (ExecutionContext.IsFlowSuppressed()) Thread.Start();
                else
                {
                    using var suppressed = ExecutionContext.SuppressFlow();
                    Thread.Start();
                }
            }
            catch (Exception exception) when (Catchable(exception))
            {
                // Only a positively unstarted/non-live thread proves no call
                // began. Any live or uncertain thread keeps its bound observer
                // and result until the actual thread's finally completes it.
                if (!Thread.IsAlive && (Thread.ThreadState & ThreadState.Unstarted) != 0)
                    Result.TrySetResult(null);
            }
        }
    }
}
