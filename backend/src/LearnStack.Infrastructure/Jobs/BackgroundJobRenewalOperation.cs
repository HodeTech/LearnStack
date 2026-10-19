namespace LearnStack.Infrastructure.Jobs;

/// <summary>One batch's actual SQL/cleanup task and independently bounded observation, rooted before issuance.</summary>
internal sealed class BackgroundJobRenewalOperation
{
    private readonly PostgresBackgroundJobControlStore _store;
    private readonly BackgroundJobExecutionIdentity[] _identities;
    private readonly CancellationToken _stop;
    private readonly TaskCompletionSource<Task<OwnedBackgroundJobControl<IReadOnlyList<BackgroundJobRenewal>>>> _published;
    private Task<OwnedBackgroundJobControl<IReadOnlyList<BackgroundJobRenewal>>>? _actual;
    private int _started;

    internal BackgroundJobRenewalOperation(PostgresBackgroundJobControlStore store,
        BackgroundJobExecutionControl[] executions, BackgroundJobRuntimeOptions bounds,
        TimeProvider time, CancellationToken stop)
    {
        _store = store;
        _identities = executions.Select(execution => execution.Identity!).ToArray();
        _stop = stop;
        IssuedAt = time.GetTimestamp();
        _published = new(this, TaskCreationOptions.RunContinuationsAsynchronously);
        Completion = _published.Task.Unwrap();
        // Observation exists before scheduling a possibly synchronous prefix.
        TimelyReturn = ObserveWaitAsync(bounds.SqlTimeout, time);
    }

    internal long IssuedAt { get; }
    internal Task<OwnedBackgroundJobControl<IReadOnlyList<BackgroundJobRenewal>>> Completion { get; }
    internal Task<bool> TimelyReturn { get; }

    internal void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("A renewal batch starts once.");
        try
        {
            if (ExecutionContext.IsFlowSuppressed()) Schedule();
            else { using (ExecutionContext.SuppressFlow()) Schedule(); }
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure))
        {
            // No returned task is not proof that issuance/cleanup never began.
            // TimelyReturn closes permission; the actual owner stays retained.
        }
    }

    private void Schedule()
    {
        _actual = Task.Run(() => _store.RenewBatchAsync(_identities, _stop));
        _published.TrySetResult(_actual);
    }

    private async Task<bool> ObserveWaitAsync(TimeSpan timeout, TimeProvider time)
    {
        try { await Completion.WaitAsync(timeout, time, _stop).ConfigureAwait(false); return true; }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { return false; }
    }
}
