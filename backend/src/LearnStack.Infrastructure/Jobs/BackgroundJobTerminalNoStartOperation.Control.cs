using LearnStack.Application.Jobs;

namespace LearnStack.Infrastructure.Jobs;

internal sealed partial class BackgroundJobTerminalNoStartOperation
{
    private Task<OwnedBackgroundJobExternalRecovery>? _acquisitionTask;
    private OwnedBackgroundJobExternalRecovery? _acquisition;
    private BackgroundJobCommitProbe? _descriptor;
    // One sequential privileged probe slot. It is never replaced until the
    // previous actual task and physical cleanup have positively finished.
    private Task? _probeTask;
    private object? _probe;
    private bool _probeClean = true, _sqlCleanupFault;
    private BackgroundJobCheckpointHistoryOrigin? _historyOrigin;
    private long _probeDue;
    private int _reportedUnknown;

    internal IEnumerable<Guid> RetainedIds => _descriptor is { } descriptor ? [descriptor.JobId]
        : _acquisition?.Probes.Select(probe => probe.JobId) ?? [];
    // An empty selection still owns its actual resources, but is not a pending
    // job outcome. Count only a real candidate's original immutable identity.
    internal int PendingOutcomes => _descriptor is not null || _acquisition?.Value is not null ? 1 : 0;
    internal bool PrivilegedCleanupFailed => _sqlCleanupFault;
    internal int TakeNewUnknownOutcomes()
    {
        var count = (_acquisition?.CommitUncertain == true ? 1 : 0)
            + (_checkpoint is { IsCompletedSuccessfully: true, Result.CommitUncertain: true } ? 1 : 0);
        var added = count - _reportedUnknown;
        _reportedUnknown = count;
        return added;
    }
    private bool PrivilegedOperationsCleaned => !_sqlCleanupFault && _probeClean
        && (_acquisitionTask is null
            || _acquisitionTask is { IsCompletedSuccessfully: true, Result.CleanupConfirmed: true })
        && (_probeTask is null || _probeTask.IsCompletedSuccessfully);
    private bool NeedsAcquisitionHistory => _acquisition is not null && _descriptor is not null
        && !_appStarted && !_settled;
    private bool NeedsCheckpointHistory => !_settled
        && Completion is { IsCompletedSuccessfully: true, Result: true }
        && _checkpoint is { IsCompletedSuccessfully: true, Result.CommitUncertain: true };

    /// <summary>Called only by RunTerminalAsync; issues at most one app task.</summary>
    internal async Task AdvanceAsync(PostgresBackgroundJobControlStore store,
        IReadOnlyCollection<Guid> blocked, CancellationToken terminalStop)
    {
        if (_sqlCleanupFault || _settled) return;
        try
        {
            if (_acquisitionTask is null)
            {
                // This owner is already published on BackgroundJobControl.
                // The actual async state machine owns resources through every
                // synchronous prefix, await and failed cleanup.
                _acquisitionTask = store.AcquireExternalTerminalNoStartAsync(blocked, terminalStop);
                _acquisition = await _acquisitionTask.ConfigureAwait(false);
                if (_acquisition.Probes.Count > 1) { _sqlCleanupFault = true; return; }
                _descriptor = _acquisition.Probes.SingleOrDefault();
                if (!_acquisition.CleanupConfirmed) { _sqlCleanupFault = true; return; }
                if (_acquisition.Value is { } receipt)
                {
                    StartApplication(receipt);
                    return;
                }
                if (_descriptor is null && !_acquisition.CommitUncertain && !_acquisition.CommitAcknowledged)
                {
                    _fault = _acquisition.Failed;
                    _settled = true;
                    FinishWithoutApplication();
                    return;
                }
                if (_descriptor is null) { _sqlCleanupFault = true; return; }
                // Preserve the exact original object and its descriptor before
                // either confirmation call. No reconstructed receipt/identity.
            }

            if (!_appStarted)
            {
                if (_time.GetTimestamp() < _probeDue || terminalStop.IsCancellationRequested) return;
                await ObserveAcquisitionAsync(store, terminalStop).ConfigureAwait(false);
                return;
            }

            // Never await a still-running app task from this SQL lane: another
            // job's ordinary terminal claim/audit must remain able to progress.
            if (!Completion.IsCompleted) return;
            if (!await Completion.ConfigureAwait(false)) { _fault = true; return; }
            var checkpoint = _checkpoint?.Result;
            if (checkpoint is null || !checkpoint.CommitStarted || checkpoint.KnownRollback || checkpoint.CommitAcknowledged)
            {
                _settled = true;
                return;
            }
            if (!checkpoint.CommitUncertain) { _fault = true; return; }
            if (_time.GetTimestamp() < _probeDue || terminalStop.IsCancellationRequested) return;
            _historyOrigin ??= BackgroundJobCheckpointHistoryOrigin.ForNoStart(checkpoint);
            _probeClean = false;
            var actual = store.ProbeExternalCheckpointAsync(_historyOrigin, terminalStop);
            _probeTask = actual;
            var history = await actual.ConfigureAwait(false);
            _probe = history;
            _probeClean = history.CleanupConfirmed;
            if (!_probeClean) { _sqlCleanupFault = true; return; }
            if (!history.Failed && history.Value is { } value)
            {
                if (value.Disposition == BackgroundJobCheckpointHistoryDisposition.Absent)
                    _settled = true;
                else if (value.Disposition == BackgroundJobCheckpointHistoryDisposition.Committed
                    && checkpoint.Outcome is { } command && value.Confirmation?.Matches(command) == true)
                    _settled = true;
            }
            _fault = !_settled;
            DeferProbe();
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure))
        {
            // A fault before a returned owned handle cannot certify that the
            // store's physical resources have cleaned up. Seal this SQL lane.
            _sqlCleanupFault = true;
            RequestStop();
        }
    }

    private async Task ObserveAcquisitionAsync(PostgresBackgroundJobControlStore store, CancellationToken token)
    {
        _probeClean = false;
        var liveTask = store.ProbeExternalRecoveryAsync(_acquisition!, _descriptor!, token);
        _probeTask = liveTask;
        var live = await liveTask.ConfigureAwait(false);
        _probe = live;
        _probeClean = live.CleanupConfirmed;
        if (!_probeClean) { _sqlCleanupFault = true; return; }
        if (!live.Failed && live.Value is { } receipt)
        {
            StartApplication(receipt);
            return;
        }
        if (token.IsCancellationRequested) { DeferProbe(); return; }
        // The original app has never started or bound. A clean, locked status
        // observation may retire this local wrapper, never activate its Run.
        _probeClean = false;
        var historyTask = store.ProbeExternalTerminalNoStartAcquisitionAsync(_acquisition!, _descriptor!, token);
        _probeTask = historyTask;
        var history = await historyTask.ConfigureAwait(false);
        _probe = history;
        _probeClean = history.CleanupConfirmed;
        if (!_probeClean) { _sqlCleanupFault = true; return; }
        if (!history.Failed && history.Value?.Disposition is BackgroundJobProbeDisposition.ControlCommitted
            or BackgroundJobProbeDisposition.ChangedOwner or BackgroundJobProbeDisposition.TerminalCompleted)
        {
            _settled = true;
            FinishWithoutApplication();
        }
        _fault = !_settled;
        DeferProbe();
    }

    private void DeferProbe() => _probeDue = checked(_time.GetTimestamp()
        + (long)(_bounds.RecoveryProbeInterval.TotalSeconds * _time.TimestampFrequency));
}
