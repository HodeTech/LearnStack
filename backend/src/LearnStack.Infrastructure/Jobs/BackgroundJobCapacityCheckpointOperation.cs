using System.Diagnostics.CodeAnalysis;
using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Infrastructure.Jobs;

internal sealed record JobCapacityCleanupResult(bool CommitAcknowledged, bool CommitStarted,
    bool KnownRollback, bool CleanupFailed)
{
    internal bool DurableOutcomeUncertain => CommitStarted && !CommitAcknowledged && !KnownRollback;
    internal IReadOnlyList<JobStartCleanupOwnership> RetainedCleanup { get; init; } = [];
    internal required JobApplicationSourceOwner SourceOwner { get; init; }
}

/// <summary>One sequential app checkpoint, retained before issuance and through actual cancellation cleanup.</summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Only actual task and callback cleanup may dispose this retained private source.")]
internal sealed class BackgroundJobCapacityCheckpointOperation
{
    private readonly JobExecutor _executor;
    private readonly CancellationTokenSource _private = new();
    private readonly TaskCompletionSource _stopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Task<JobCapacityCleanupResult>> _published;
    private readonly Task<bool> _stopRelay;
    private Task<JobCapacityCleanupResult>? _actual;
    private Task? _cancellation;
    private int _started;

    internal BackgroundJobCapacityCheckpointOperation(JobExecutor executor, BackgroundJobCapacityRecoveryReceipt recovery,
        BackgroundJobActualCleanup cleanup, BackgroundJobRuntimeOptions bounds, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(cleanup);
        cleanup.RequireRecovery(recovery);
        _executor = executor;
        Recovery = recovery;
        Cleanup = cleanup;
        _published = new(this, TaskCreationOptions.RunContinuationsAsynchronously);
        _stopRelay = RelayStopAsync();
        Completion = ObserveActualAsync();
        TimelyReturn = ObserveWaitAsync(bounds.SqlTimeout, time);
    }

    internal BackgroundJobCapacityRecoveryReceipt Recovery { get; }
    internal BackgroundJobActualCleanup Cleanup { get; }
    internal Task<JobCapacityCleanupResult> Completion { get; }
    internal Task<bool> TimelyReturn { get; }

    internal void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("A capacity checkpoint starts once.");
        try
        {
            if (ExecutionContext.IsFlowSuppressed()) Schedule();
            else { using (ExecutionContext.SuppressFlow()) Schedule(); }
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure))
        {
            // A missing returned task does not prove that no synchronous work
            // began. The original published owner stays unresolved and retained.
            RequestStop();
        }
    }

    private void Schedule()
    {
        _actual = Task.Run(() => _executor.ExecuteCapacityCleanupAsync(Recovery, Cleanup, _private.Token));
        _published.TrySetResult(_actual);
    }

    internal void RequestStop() => _stopRequested.TrySetResult();

    private async Task<bool> RelayStopAsync()
    {
        await _stopRequested.Task.ConfigureAwait(false);
        try
        {
            _cancellation = _private.CancelAsync();
            await _cancellation.ConfigureAwait(false);
            return true;
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { return false; }
    }

    private async Task<JobCapacityCleanupResult> ObserveActualAsync()
    {
        JobCapacityCleanupResult result;
        try { result = await (await _published.Task.ConfigureAwait(false)).ConfigureAwait(false); }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure))
        {
            result = new(false, false, false, true) { SourceOwner = Cleanup.SourceOwner };
        }
        RequestStop();
        if (!await _stopRelay.ConfigureAwait(false)) return result with { CleanupFailed = true };
        try { _private.Dispose(); }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure))
        {
            return result with { CleanupFailed = true };
        }
        return result;
    }

    private async Task<bool> ObserveWaitAsync(TimeSpan budget, TimeProvider time)
    {
        try { await Completion.WaitAsync(budget, time).ConfigureAwait(false); return true; }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure))
        {
            RequestStop();
            return false;
        }
    }
}
