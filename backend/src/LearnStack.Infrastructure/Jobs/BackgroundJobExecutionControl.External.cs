using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Infrastructure.Jobs;

internal sealed partial class BackgroundJobExecutionControl
{
    // Finite, once-issued operations on the existing generation-owned control.
    // Completed failed results retain their actual scopes and cleanup resources.
    private Task<JobExternalReadResult>? _externalRead;
    private Task<JobExternalCheckpointResult>? _externalArm, _externalOutcome;
    private Task<JobExternalExecutionResult>? _externalProvider;
    private Task<OwnedBackgroundJobControl<BackgroundJobControlChange>>? _externalRetry;
    private Task<OwnedBackgroundJobControl<BackgroundJobProbeResult>>? _externalRetryProbe;
    private Task<OwnedBackgroundJobControl<BackgroundJobControlChange>>? _externalUnresolved;
    private Task<OwnedBackgroundJobControl<BackgroundJobProbeResult>>? _externalUnresolvedProbe;
    private Task<bool>? _externalFinalizeDisposition;
    private Task<bool>? _externalProbeCancellationJoin;
    private Task<OwnedBackgroundJobControl<BackgroundJobCheckpointHistoryObservation>>? _prepareHistory,
        _armHistory, _outcomeHistory, _finalizeHistory;
    private BackgroundJobCommitGate? _externalOutcomeGate;
    private int _externalIssued;

    private bool ExternalLeaseWorkSettled => Volatile.Read(ref _noPlanTerminalSettled) != 0
        || Volatile.Read(ref _externalOutcomeGate) is
        { Kind: BackgroundJobCheckpointKind.Outcome or BackgroundJobCheckpointKind.NoStart, CommitAcknowledged: true }
        || _externalOutcome is { IsCompletedSuccessfully: true } outcome && outcome.Result.Outcome is { } command
            && _outcomeHistory is { IsCompletedSuccessfully: true } history && history.Result.CleanupConfirmed
            && !history.Result.Failed && history.Result.Value is
            { Disposition: BackgroundJobCheckpointHistoryDisposition.Committed, Confirmation: { } confirmation }
            && confirmation.Matches(command)
        || _business is { IsCompletedSuccessfully: true } business
            && business.Result.Receipt.Stage == BackgroundJobExecutionStage.Finalize
            && _finalizeHistory is { IsCompletedSuccessfully: true } final && final.Result.CleanupConfirmed
            && !final.Result.Failed && final.Result.Value is
            { Disposition: BackgroundJobCheckpointHistoryDisposition.Committed, Confirmation: { } finalConfirmation }
            && finalConfirmation.Matches(business.Result.Receipt, business.Result.Gate);

    private bool ExternalOperationsCleaned =>
        (_externalRead is null || _externalRead is { IsCompletedSuccessfully: true, Result.CleanupFailed: false })
        && (_externalArm is null || _externalArm is { IsCompletedSuccessfully: true, Result.CleanupFailed: false })
        && (_externalProvider is null || _externalProvider is { IsCompletedSuccessfully: true, Result.CleanupFailed: false })
        && (_externalOutcome is null || _externalOutcome is { IsCompletedSuccessfully: true, Result.CleanupFailed: false })
        && (_externalRetry is null || _externalRetry is { IsCompletedSuccessfully: true, Result.CleanupConfirmed: true })
        && (_externalRetryProbe is null || _externalRetryProbe is { IsCompletedSuccessfully: true, Result.CleanupConfirmed: true })
        && (_externalUnresolved is null || _externalUnresolved is { IsCompletedSuccessfully: true, Result.CleanupConfirmed: true })
        && (_externalUnresolvedProbe is null || _externalUnresolvedProbe is { IsCompletedSuccessfully: true, Result.CleanupConfirmed: true })
        && (_externalFinalizeDisposition is null || _externalFinalizeDisposition is { IsCompletedSuccessfully: true, Result: true })
        && NoPlanTerminalOperationsCleaned
        && (_externalProbeCancellationJoin is null || _externalProbeCancellationJoin is { IsCompletedSuccessfully: true, Result: true })
        && HistoryCleaned(_prepareHistory) && HistoryCleaned(_armHistory)
        && HistoryCleaned(_outcomeHistory) && HistoryCleaned(_finalizeHistory);

    private static bool HistoryCleaned(Task<OwnedBackgroundJobControl<BackgroundJobCheckpointHistoryObservation>>? history) =>
        history is null || history is { IsCompletedSuccessfully: true, Result.CleanupConfirmed: true };

    internal async Task<bool> ExecuteExternalNormalAsync(JobExecutor executor, PostgresBackgroundJobControlStore store)
    {
        if (Interlocked.Exchange(ref _externalIssued, 1) != 0 || _admission is not { IsCompletedSuccessfully: true }
            || _admission.Result is not { CleanupFailed: false, Outcome: JobStartExecutionOutcome.Started, Receipt: { } receipt }
            || receipt.Envelope.Profile != "external" || receipt.LedgerStage != BackgroundJobLedgerStage.Normal
            || !ReferenceEquals(receipt.Run, RunGate))
            throw new InvalidOperationException("External execution requires its exact held original normal admission.");
        try
        {
            // An admitted run survives generation-local drain. Only its sticky
            // run/private cancellation and actual cleanup govern later stages.
            if (RunGate.IsClosed || Token.IsCancellationRequested) return true;
            if (receipt.Stage == BackgroundJobExecutionStage.Prepare)
            {
                if (!_work.BeginStageWait(BackgroundJobInvocationStage.Prepare)) return true;
                var business = executor.ExecuteBusinessAsync(receipt, Gate, Token);
                HoldBusiness(business);
                _work.HoldBusiness(business);
                var prepared = await business.ConfigureAwait(false);
                if (prepared.CleanupFailed) return false;
                if (!prepared.CommitAcknowledged)
                {
                    if (!prepared.DurableOutcomeUncertain)
                        return await RequestNoPlanTerminalAfterPrepareAsync(prepared).ConfigureAwait(false);
                    if (!_work.BeginStageWait(BackgroundJobInvocationStage.PrepareHistory)) return true;
                    _prepareHistory = ObserveCheckpointHistoryAsync(store, BackgroundJobCheckpointHistoryOrigin.ForPrepare(prepared));
                    var history = await _prepareHistory.ConfigureAwait(false);
                    if (!history.CleanupConfirmed) return false;
                    if (history.Failed || history.Value is not
                        { Disposition: BackgroundJobCheckpointHistoryDisposition.Committed, Confirmation: { } confirmed }
                        || !confirmed.Matches(receipt, prepared.Gate)) return true;
                }
            }
            else if (receipt.Stage is not null)
                throw new InvalidOperationException("An existing external plan has no new Prepare root.");

            if (RunGate.IsClosed || Token.IsCancellationRequested) return true;
            if (!_work.BeginStageWait(BackgroundJobInvocationStage.ExternalRead)) return true;
            _externalRead = executor.ReadExternalStateAsync(receipt, Token);
            var read = await _externalRead.ConfigureAwait(false);
            if (read.CleanupFailed) return false;
            if (read.Handle is not { } handle) return true;
            var snapshot = handle.Require(receipt);
            if (snapshot.PlanJson is null || snapshot.ExecuteIntentRecorded || snapshot.NoStartRecorded
                || snapshot.OutcomeJson is not null || snapshot.OutcomeState is not null) return true;
            var armGate = RunGate.TryOpenBoundary(BackgroundJobCheckpointKind.Arm);
            if (armGate is null) return true;
            if (!_work.BeginStageWait(BackgroundJobInvocationStage.Arm)) return true;
            _externalArm = executor.RecordExternalArmAsync(receipt, handle, armGate, Token);
            var arm = await _externalArm.ConfigureAwait(false);
            if (arm.CleanupFailed) return false;
            if (!arm.CommitAcknowledged)
            {
                if (!arm.CommitUncertain) return true;
                if (!_work.BeginStageWait(BackgroundJobInvocationStage.ArmHistory)) return true;
                _armHistory = ObserveCheckpointHistoryAsync(store, BackgroundJobCheckpointHistoryOrigin.ForArm(arm));
                var history = await _armHistory.ConfigureAwait(false);
                if (!history.CleanupConfirmed) return false;
                if (history.Failed || history.Value is not
                    { Disposition: BackgroundJobCheckpointHistoryDisposition.Committed, Confirmation: { } confirmed }
                    || arm.Arm is null || !confirmed.AllowsOriginalExecute(arm.Arm)) return true;
            }
            if (RunGate.IsClosed || Token.IsCancellationRequested) return true;
            if (!_work.BeginStageWait(BackgroundJobInvocationStage.Execute)) return true;
            _externalProvider = executor.ExecuteExternalAsync(handle, arm, Token);
            var report = await _externalProvider.ConfigureAwait(false);
            if (report.CleanupFailed) return false;
            if (report.Status != JobExternalExecutionStatus.Reported || report.Observation is not { IsCompleted: true } observation)
                return await FinishUnresolvedExternalAsync(store, report, recovered: false).ConfigureAwait(false);
            if (RunGate.IsClosed || Token.IsCancellationRequested) return true;
            var outcomeGate = RunGate.TryOpenBoundary(BackgroundJobCheckpointKind.Outcome);
            if (outcomeGate is null) return true;
            if (!_work.BeginStageWait(BackgroundJobInvocationStage.Outcome)) return true;
            // The actual writer's physical ACK settles durable lease work even
            // while its scope cleanup is still pending. That never releases work.
            Volatile.Write(ref _externalOutcomeGate, outcomeGate);
            _externalOutcome = executor.RecordExternalOutcomeAsync(receipt, handle, outcomeGate, observation, Token);
            var outcome = await _externalOutcome.ConfigureAwait(false);
            if (outcome.CleanupFailed) return false;
            if (!outcome.CommitAcknowledged && outcome.CommitUncertain)
            {
                if (!_work.BeginStageWait(BackgroundJobInvocationStage.OutcomeHistory)) return true;
                _outcomeHistory = ObserveCheckpointHistoryAsync(store, BackgroundJobCheckpointHistoryOrigin.ForOutcome(outcome));
                var history = await _outcomeHistory.ConfigureAwait(false);
                if (!history.CleanupConfirmed) return false;
                // Historical confirmation stays bound to the exact issued write;
                // absence/unknown cannot invent rollback, retry or quiescence.
            }
            return !ExternalOutcomeCommitted(outcome) || BackgroundJobExternalPolicy.Select(observation.State,
                    observation.FailurePolicy, receipt.NormalAttemptOrdinal).Disposition != BackgroundJobKnownOutcomeDisposition.Retry
                || await RetryExternalKnownAsync(store, BackgroundJobExternalRetryMode.OriginalCompleted).ConfigureAwait(false);
        }
        finally
        {
            // The normal invocation cannot reopen Execute after any return.
            // Unresolved durable work retains occupancy for fresh recovery.
            RunGate.Close();
        }
    }

    internal async Task<bool> FinishExternalFinalizeAsync(JobBusinessExecutionResult result,
        PostgresBackgroundJobControlStore store)
    {
        if (_business is not { IsCompletedSuccessfully: true } held || !ReferenceEquals(held.Result, result)
            || result.Receipt.Stage != BackgroundJobExecutionStage.Finalize || !ReferenceEquals(result.Gate.Run, RunGate)
            || Interlocked.Exchange(ref _externalIssued, 1) != 0)
            throw new InvalidOperationException("Finalization control requires its exact held ordinary root once.");
        try
        {
            if (result.CleanupFailed || !result.SourceOwner.CleanupConfirmed) return false;
            if (result.CommitAcknowledged) return true;
            if (result.DurableOutcomeUncertain)
            {
                if (!_work.BeginStageWait(BackgroundJobInvocationStage.FinalizeHistory)) return true;
                _finalizeHistory = ObserveCheckpointHistoryAsync(store, BackgroundJobCheckpointHistoryOrigin.ForFinalize(result));
                var history = await _finalizeHistory.ConfigureAwait(false);
                if (!history.CleanupConfirmed) return false;
                // Only exact locked absence may proceed. Committed, another
                // durable branch, skipped locks and unavailable reads grant no
                // status mutation or second root to this old invocation.
                if (history.Failed || history.Value is not { Disposition: BackgroundJobCheckpointHistoryDisposition.Absent })
                    return true;
            }
            else if (!result.KnownRollback || result.Decision.Disposition is not
                (BackgroundJobFailureDisposition.Retry or BackgroundJobFailureDisposition.Terminal)) return true;

            // This classification was captured after actual business cleanup.
            // A later deadline/lease callback cannot turn it into a new retry
            // policy. Actual runtime-cancelled results remain RetainOwnership
            // and returned above. This branch grants only existing control SQL.
            var knownDecision = !result.DurableOutcomeUncertain && result.KnownRollback;
            if (Identity is null) return true;
            if (knownDecision)
            {
                if (!_owner.TryBeginExternalStatus(this, out _)) return true;
                // Rearm only the finite wait while it remains open. An already
                // closed wait returns its caller but still owns this actual task.
                if (!RunGate.IsClosed && !Token.IsCancellationRequested)
                    _ = _work.BeginStageWait(BackgroundJobInvocationStage.RecoveryDisposition);
            }
            else if (RunGate.IsClosed || Token.IsCancellationRequested
                || !_work.BeginStageWait(BackgroundJobInvocationStage.RecoveryDisposition)) return true;
            // Join the actual deadline/renewal observers before privately
            // revoking this owner. No new capacity or business owner is created.
            if (!await JoinBusinessObserversAsync().ConfigureAwait(false)) return false;
            if (knownDecision ? !_owner.TryBeginExternalStatus(this, out _)
                : RunGate.IsClosed || Token.IsCancellationRequested) return true;
            RunGate.Close();
            // The existing bounded control queue retains the actual status
            // task and its original descriptor through failed/uncertain SQL.
            _externalFinalizeDisposition = _owner.FinishFinalizerAsync(this, result, _finalizeHistory?.Result);
            return await _externalFinalizeDisposition.ConfigureAwait(false);
        }
        finally { RunGate.Close(); }
    }

    private async Task<OwnedBackgroundJobControl<BackgroundJobCheckpointHistoryObservation>> ObserveCheckpointHistoryAsync(
        PostgresBackgroundJobControlStore store, BackgroundJobCheckpointHistoryOrigin origin)
    {
        // The caller armed its one history-stage budget before issuing this
        // owned task. A skipped row is unavailable, never a history disposition.
        while (true)
        {
            var actual = await store.ProbeExternalCheckpointAsync(origin, Token).ConfigureAwait(false);
            if (!actual.CleanupConfirmed || actual.Failed || actual.Value is not null || Token.IsCancellationRequested)
                return actual;
            // The previous actual operation has positively cleaned up before
            // another connection is issued. Closed execution permission does
            // not invalidate a purely historical read of this exact origin.
            try { await Task.Delay(TimeSpan.FromMilliseconds(50), _time, Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (Token.IsCancellationRequested) { return actual; }
        }
    }

    private bool ExternalOutcomeCommitted(JobExternalCheckpointResult outcome) => outcome.CommitAcknowledged
        || outcome.Outcome is { } command && _outcomeHistory is { IsCompletedSuccessfully: true } held
            && held.Result.CleanupConfirmed && !held.Result.Failed && held.Result.Value is
            { Disposition: BackgroundJobCheckpointHistoryDisposition.Committed, Confirmation: { } confirmation }
            && confirmation.Matches(command);

    private async Task<bool> RetryExternalKnownAsync(PostgresBackgroundJobControlStore store, BackgroundJobExternalRetryMode mode)
    {
        if (RunGate.IsClosed || Token.IsCancellationRequested || Identity is not { } identity
            || !_work.BeginStageWait(BackgroundJobInvocationStage.RecoveryDisposition)) return true;
        if (_externalRetry is not null) throw new InvalidOperationException("One native invocation owns one known retry disposition.");
        _externalRetry = store.RetryKnownExternalAsync(identity, mode, Token);
        var result = await _externalRetry.ConfigureAwait(false);
        if (result.CommitAcknowledged && result.Value is { Phase: "pending" }) _owner.ObserveExternalRetry();
        if (!result.CleanupConfirmed) return false;
        if (!result.CommitUncertain || result.Probes.Count != 1 || Token.IsCancellationRequested
            || !_work.BeginStageWait(BackgroundJobInvocationStage.RecoveryConfirmation)) return true;
        // This is read-only historical confirmation, never another scheduling
        // attempt or a new recovery/external permission. Keep both actual tasks.
        var origin = result.Probes.Single();
        _externalRetryProbe = store.ProbeCommitAsync(origin, Token);
        var history = await _externalRetryProbe.ConfigureAwait(false);
        if (history.CleanupConfirmed && !history.Failed && origin.Boundary == BackgroundJobCommitBoundary.ExternalKnownRetry
            && origin.ExpectedPhase == "pending" && history.Value is
            { Disposition: BackgroundJobProbeDisposition.ControlCommitted, Phase: "pending" } confirmed
            && confirmed.WakeGeneration == origin.ExpectedGeneration) _owner.ObserveExternalRetry();
        return history.CleanupConfirmed;
    }

    private async Task<bool> FinishUnresolvedExternalAsync(PostgresBackgroundJobControlStore store,
        JobExternalExecutionResult report, bool recovered)
    {
        if (_externalProvider is not { IsCompletedSuccessfully: true } provider || !ReferenceEquals(provider.Result, report)
            || report.CleanupFailed || Identity is not { } identity || identity.IsRecovery != recovered
            || (_recoverySource ?? _admission?.Result.SourceOwner) is not { CleanupConfirmed: true })
            return false;
        // A real late completed report takes precedence over a wrapper timeout.
        // A closed Run leaves it for fresh recovery; no failed probe is invented.
        if (report.Observation is { IsCompleted: true } || report.Status == JobExternalExecutionStatus.EntryRefused) return true;
        var timedOut = recovered && _work.ReconcileWaitExpired;
        if (timedOut)
        {
            _externalProbeCancellationJoin = _work.JoinReconcileTimeoutCancellationAsync(this);
            if (!await _externalProbeCancellationJoin.ConfigureAwait(false)) return false;
        }
        else if (RunGate.IsClosed || Token.IsCancellationRequested) return true;
        var disposition = report.Status == JobExternalExecutionStatus.Reported && report.Observation is { } observation
            ? observation.State == BackgroundJobExternalState.Running ? BackgroundJobExternalUnresolvedDisposition.Running
                : BackgroundJobExternalUnresolvedDisposition.Unknown
            : recovered ? timedOut ? BackgroundJobExternalUnresolvedDisposition.ProbeTimeout
                    : BackgroundJobExternalUnresolvedDisposition.ProbeFault
                : BackgroundJobExternalUnresolvedDisposition.ReconcileRequired;
        if (_externalUnresolved is not null) throw new InvalidOperationException("One native invocation owns one unresolved disposition.");
        if (!_owner.TryBeginExternalStatus(this, out var controlToken)) return true;
        _externalUnresolved = store.FinishExternalUnresolvedAsync(identity, disposition, controlToken);
        var result = await _externalUnresolved.ConfigureAwait(false);
        if (!result.CleanupConfirmed) return false;
        if (!result.CommitUncertain || result.Probes.Count != 1
            || !_owner.TryBeginExternalStatus(this, out controlToken)) return true;
        _externalUnresolvedProbe = store.ProbeCommitAsync(result.Probes.Single(), controlToken);
        return (await _externalUnresolvedProbe.ConfigureAwait(false)).CleanupConfirmed;
    }
}
