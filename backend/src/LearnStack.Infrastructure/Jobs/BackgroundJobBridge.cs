using LearnStack.Application.Jobs;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Infrastructure.Jobs;

public sealed record BackgroundJobBridgeSnapshot(int Outstanding, int Retained, long Acknowledged,
    long Failed, long Deferred, long? LastProgressTimestamp);

/// <summary>Independent, bounded handoff quanta. Host lifecycle/composition supplies its polling and shared deadline.</summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The process-owned semaphore never creates a wait handle and remains live with retained operations.")]
public sealed class BackgroundJobBridge(PostgresBackgroundJobBridgeStore store, HangfireWakeupClient client,
    BackgroundJobRuntimeOptions options, TimeProvider time)
{
    private readonly object _gate = new();
    private readonly List<BridgeOperation> _operations = [];
    private readonly SemaphoreSlim _quantum = new(1, 1);
    private bool _stopping;
    private long _acknowledged;
    private long _failed;
    private long _deferred;
    private long? _lastProgress;

    public BackgroundJobBridgeSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return new(_operations.Count(operation => !operation.Completion.IsCompleted),
                    _operations.Count(operation => !operation.Completion.IsCompleted
                        && time.GetElapsedTime(operation.Started, time.GetTimestamp()) >= options.BridgeWaitTimeout),
                    _acknowledged, _failed, _deferred, _lastProgress);
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
                if (_stopping || _operations.Count >= BackgroundJobRuntimeOptions.MaxOutstandingBridgeCalls) return false;
                retained = _operations.Select(operation => operation.Claim.JobId).ToArray();
            }
            var claim = await store.TryClaimAsync(retained, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _lastProgress = time.GetTimestamp();
                if (_stopping || claim is null) return false;
            }
            if (!claim.CanHandoff)
            {
                await store.DeferAsync(claim, BackgroundJobBridgeDeferral.DeploymentMismatch, cancellationToken).ConfigureAwait(false);
                lock (_gate) _deferred++;
                return true;
            }
            lock (_gate)
            {
                if (_stopping) return false;
                var operation = new BridgeOperation(claim, client, time.GetTimestamp(), this);
                _operations.Add(operation);
                operation.Start();
            }
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
        var result = await operation.Result.Task.ConfigureAwait(false);
        // Result signals the finished client call; wait for the actual thread to
        // return before forgetting its owner. No thread-pool thread performs a join.
        while (operation.Thread.IsAlive)
            await Task.Delay(TimeSpan.FromMilliseconds(1), time, CancellationToken.None).ConfigureAwait(false);
        try
        {
            lock (_gate) { if (_stopping) return; }
            var changed = result is not null
                ? await store.AcknowledgeAsync(operation.Claim, result).ConfigureAwait(false)
                : await store.DeferAsync(operation.Claim, BackgroundJobBridgeDeferral.CreationFailed).ConfigureAwait(false);
            lock (_gate)
            {
                if (result is not null && changed) _acknowledged++;
                else _failed++;
                _lastProgress = time.GetTimestamp();
            }
        }
        catch
        {
            // Failed/uncertain hints leave a durable same-generation repair obligation.
            lock (_gate) _failed++;
        }
    }

    private sealed class BridgeOperation
    {
        internal BridgeOperation(BackgroundJobBridgeClaim claim, HangfireWakeupClient client, long started,
            BackgroundJobBridge owner)
        {
            Claim = claim;
            Started = started;
            Thread = new Thread(() =>
            {
                string? result = null;
                var culture = CultureInfo.CurrentCulture;
                var uiCulture = CultureInfo.CurrentUICulture;
                try
                {
                    CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                    CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
                    result = client.Create(claim);
                }
                catch (Exception exception) when (exception.GetType() != typeof(OutOfMemoryException)
                    && exception.GetType() != typeof(StackOverflowException))
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
            Completion = owner.ObserveAsync(this);
        }

        internal BackgroundJobBridgeClaim Claim { get; }
        internal long Started { get; }
        internal Thread Thread { get; }
        internal TaskCompletionSource<string?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Completion { get; }

        internal void Start()
        {
            try
            {
                if (ExecutionContext.IsFlowSuppressed()) Thread.Start();
                else
                {
                    using var suppressed = ExecutionContext.SuppressFlow();
                    Thread.Start();
                }
            }
            catch (Exception exception) when (exception.GetType() != typeof(OutOfMemoryException)
                && exception.GetType() != typeof(StackOverflowException))
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
