using System.Diagnostics.CodeAnalysis;
using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>
/// The terminal lane's one application owner. SQL acquisition and historical
/// observation run sequentially on that lane; actual app work is polled there.
/// </summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Only actual app/callback/source cleanup releases this retained owner.")]
internal sealed partial class BackgroundJobTerminalNoStartOperation
{
    private readonly JobExecutor _executor;
    private readonly BackgroundJobRuntimeOptions _bounds;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _private = new();
    private readonly TaskCompletionSource _stopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Task> _published;
    private readonly Task<bool> _stopRelay;
    private Task? _actual, _cancellation;
    private Task<bool>? _timelyReturn;
    private Task<JobExternalReadResult>? _read;
    private Task<JobExternalCheckpointResult>? _checkpoint;
    private IDisposable? _initialSourceBinding;
    private BackgroundJobExternalRecoveryBinding? _binding;
    private BackgroundJobCommitGate? _noStart;
    private bool _appStarted, _settled, _fault;
    private volatile bool _appCleanupFault;

    internal BackgroundJobTerminalNoStartOperation(JobExecutor executor,
        BackgroundJobRunGate.RuntimeStop processStop, BackgroundJobRuntimeOptions bounds, TimeProvider time)
    {
        _executor = executor;
        _bounds = bounds;
        _time = time;
        Run = new(processStop);
        Source = JobApplicationSourceOwner.Create();
        _published = new(this, TaskCreationOptions.RunContinuationsAsynchronously);
        _stopRelay = RelayStopAsync();
        Completion = ObserveActualAsync();
    }

    internal BackgroundJobRunGate Run { get; }
    internal JobApplicationSourceOwner Source { get; }
    // This is actual task, cancellation and application-source completion.
    // Durable history is tracked separately and cannot be inferred from it.
    internal Task<bool> Completion { get; }
    internal bool Healthy => !_fault && !PrivilegedCleanupFailed && !_appCleanupFault
        && !(_timelyReturn is { IsCompletedSuccessfully: true, Result: false })
        && !NeedsAcquisitionHistory && !NeedsCheckpointHistory;
    internal bool CanRelease => _settled && Completion is { IsCompletedSuccessfully: true, Result: true }
        && (_timelyReturn is null || _timelyReturn.IsCompletedSuccessfully) && PrivilegedOperationsCleaned;

    private void StartApplication(BackgroundJobExternalRecoveryReceipt receipt)
    {
        if (_appStarted || _settled || receipt.Purpose != BackgroundJobExternalRecoveryPurpose.TerminalNoStart
            || _acquisition is null || !ReferenceEquals(receipt, _acquisition.Value))
            throw new InvalidOperationException("Terminal cleanup starts once from its original purpose-bound acquisition.");
        _appStarted = true;
        try
        {
            if (ExecutionContext.IsFlowSuppressed()) ScheduleApplication(receipt);
            else { using (ExecutionContext.SuppressFlow()) ScheduleApplication(receipt); }
            _timelyReturn = ObserveTimelyReturnAsync();
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure))
        {
            // A missing actual task is not proof that a synchronous prefix did
            // nothing. Leave publication unresolved and retain this owner.
            _appCleanupFault = true;
            RequestStop();
        }
    }

    private void ScheduleApplication(BackgroundJobExternalRecoveryReceipt receipt)
    {
        _actual = Task.Run(() => ExecuteApplicationAsync(receipt));
        _published.TrySetResult(_actual);
    }

    private async Task ExecuteApplicationAsync(BackgroundJobExternalRecoveryReceipt receipt)
    {
        try
        {
            if (Run.IsClosed || _private.IsCancellationRequested) return;
            try
            {
                // Retain the actual ambient binding even if restoration throws.
                var initialSourceBinding = Source.Enter();
                _initialSourceBinding = initialSourceBinding;
                try { _binding = receipt.Bind(Run); }
                finally
                {
                    initialSourceBinding.Dispose();
                    _initialSourceBinding = null;
                }
            }
            catch (InvalidOperationException) when (Run.IsClosed && _binding is null
                && _initialSourceBinding is null && _read is null && _checkpoint is null && Source.CleanupConfirmed)
            { return; } // Stop won before any app task or resource was issued.
            if (Run.IsClosed || _private.IsCancellationRequested) return;
            var binding = _binding ?? throw new InvalidOperationException("Terminal cleanup retains its actual binding.");
            _read = _executor.ReadExternalStateAsync(binding, _private.Token);
            var read = await _read.ConfigureAwait(false);
            if (read.CleanupFailed) { _appCleanupFault = true; return; }
            if (read.Handle is not { } handle || Run.IsClosed || _private.IsCancellationRequested) return;
            // The real read handle carries the exact immutable app snapshot.
            // The named writer repeats all purpose/NoPlan/history fences.
            _noStart = Run.TryOpenBoundary(BackgroundJobCheckpointKind.NoStart);
            if (_noStart is null) return;
            try { _checkpoint = _executor.RecordExternalTerminalNoStartAsync(binding, handle, _noStart, _private.Token); }
            catch (InvalidOperationException) when (Run.IsClosed && _checkpoint is null && _initialSourceBinding is null
                && _read is { IsCompletedSuccessfully: true, Result.CleanupFailed: false, Result.CommitAcknowledged: true }
                && Source.CleanupConfirmed)
            { return; } // The stopped handle refused synchronously before SQL.
            var checkpoint = await _checkpoint.ConfigureAwait(false);
            if (checkpoint.CleanupFailed) _appCleanupFault = true;
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure))
        {
            // Executor results retain known cleanup. An unexpected thrown
            // prefix/result supplies no substitute positive cleanup witness.
            _appCleanupFault = true;
        }
        finally
        {
            Run.Close();
            Source.CloseIssuance();
        }
    }

    internal void RequestStop()
    {
        Run.Close();
        Source.CloseIssuance();
        _stopRequested.TrySetResult();
    }

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

    private async Task<bool> ObserveActualAsync()
    {
        try { await (await _published.Task.ConfigureAwait(false)).ConfigureAwait(false); }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { _appCleanupFault = true; }
        RequestStop();
        var callbacksClean = await _stopRelay.ConfigureAwait(false);
        var auxiliaryClean = false;
        try { auxiliaryClean = await Source.JoinAuxiliaryCleanupAsync().ConfigureAwait(false); }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { _appCleanupFault = true; }
        var clean = callbacksClean && auxiliaryClean && !_appCleanupFault && _initialSourceBinding is null
            && (_read is null || _read is { IsCompletedSuccessfully: true, Result.CleanupFailed: false })
            && (_checkpoint is null || _checkpoint is { IsCompletedSuccessfully: true, Result.CleanupFailed: false })
            && Source.CleanupConfirmed;
        try { _private.Dispose(); }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { clean = false; }
        return clean;
    }

    private async Task<bool> ObserveTimelyReturnAsync()
    {
        try { return await Completion.WaitAsync(_bounds.SqlTimeout, _time).ConfigureAwait(false); }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure))
        {
            RequestStop();
            return false;
        }
    }

    private void FinishWithoutApplication()
    {
        if (_appStarted) throw new InvalidOperationException("An actual application task must be joined.");
        RequestStop();
        _published.TrySetResult(Task.CompletedTask);
    }

    // Called only after the terminal SQL loop has actually returned. No new
    // acquisition, probe or app task can race this last publication.
    internal async Task<bool> JoinAfterLaneStoppedAsync()
    {
        RequestStop();
        if (!_appStarted) FinishWithoutApplication();
        var appClean = await Completion.ConfigureAwait(false);
        if (_timelyReturn is not null) await _timelyReturn.ConfigureAwait(false);
        GC.KeepAlive(_probe);
        return appClean && PrivilegedOperationsCleaned;
    }
}
