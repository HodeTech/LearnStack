using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Infrastructure.Jobs;

internal sealed partial class BackgroundJobExecutionControl
{
    private Task<OwnedBackgroundJobControl<BackgroundJobControlChange>>? _noPlanTerminalStatus;
    private Task<OwnedBackgroundJobControl<BackgroundJobProbeResult>>? _noPlanTerminalProbe;
    private Task<bool>? _noPlanTerminalDisposition;
    private int _noPlanTerminalIssued, _noPlanTerminalSettled;

    private bool NoPlanTerminalOperationsCleaned =>
        (_noPlanTerminalDisposition is null || _noPlanTerminalDisposition is { IsCompletedSuccessfully: true, Result: true })
        && (_noPlanTerminalStatus is null || _noPlanTerminalStatus is { IsCompletedSuccessfully: true, Result.CleanupConfirmed: true })
        && (_noPlanTerminalProbe is null || _noPlanTerminalProbe is { IsCompletedSuccessfully: true, Result.CleanupConfirmed: true });

    private async Task<bool> RequestNoPlanTerminalAfterPrepareAsync(JobBusinessExecutionResult prepared)
    {
        if (_business is not { IsCompletedSuccessfully: true } held || !ReferenceEquals(held.Result, prepared)
            || _admission is not { IsCompletedSuccessfully: true, Result.Receipt: { } receipt }
            || !ReferenceEquals(prepared.Receipt, receipt) || !ReferenceEquals(prepared.Gate.Run, RunGate)
            || prepared.Receipt.Stage != BackgroundJobExecutionStage.Prepare)
            throw new InvalidOperationException("NoPlan preparation status requires the exact held ordinary result.");
        if (prepared.CleanupFailed || !prepared.SourceOwner.CleanupConfirmed) return false;
        // Cancellation, uncertain COMMIT and an unissued root grant no permanent
        // classification. A known rollback still uses only the closed classifier.
        if (!prepared.ExecutionIssued || prepared.CommitAcknowledged || prepared.DurableOutcomeUncertain
            || !prepared.KnownRollback || prepared.Decision.Disposition != BackgroundJobFailureDisposition.Terminal
            || prepared.Decision.TerminalReason != BackgroundJobTerminalReason.PermanentFailure
            || prepared.Decision.ErrorCode != "permanent_failure" || Identity is not { IsRecovery: false } identity)
            return true;
        if (!ReferenceEquals(identity, Identity) || Interlocked.Exchange(ref _noPlanTerminalIssued, 1) != 0)
            throw new InvalidOperationException("One actual native invocation owns one immutable NoPlan terminal request.");
        // Classification followed positive business cleanup. A later local
        // deadline/lease closure cannot replace that already-fixed policy.
        // The controller token grants only status SQL, never another app root.
        if (!_owner.TryBeginExternalStatus(this, out _)) return true;
        if (!RunGate.IsClosed && !Token.IsCancellationRequested)
            _ = _work.BeginStageWait(BackgroundJobInvocationStage.RecoveryDisposition);
        if (!await JoinBusinessObserversAsync().ConfigureAwait(false)) return false;
        if (!_owner.TryBeginExternalStatus(this, out _)) return true;
        RunGate.Close();
        _noPlanTerminalDisposition = _owner.FinishNoPlanPrepareAsync(this, prepared);
        return await _noPlanTerminalDisposition.ConfigureAwait(false);
    }

    private Task<bool> RequestNoPlanTerminalAfterRecoveryAsync(PostgresBackgroundJobControlStore store,
        JobExternalReadResult read)
    {
        if (_externalRead is not { IsCompletedSuccessfully: true } held || !ReferenceEquals(held.Result, read)
            || _recoveryBinding is not { } binding || Identity is not { IsRecovery: true } identity
            || binding.Receipt.Purpose != BackgroundJobExternalRecoveryPurpose.Reconcile)
            throw new InvalidOperationException("NoPlan exhaustion requires the exact held genuine recovery read.");
        if (read.CleanupFailed || !read.SourceOwner.CleanupConfirmed || _recoverySource is not { CleanupConfirmed: true })
            return Task.FromResult(false);
        if (RunGate.IsClosed || Token.IsCancellationRequested || read.Handle is not { } handle) return Task.FromResult(true);
        var snapshot = handle.Require(binding);
        if (identity.NormalAttemptOrdinal != BackgroundJobRuntimeOptions.MaxNormalAttempts
            || snapshot.PlanJson is not null || snapshot.ExecuteIntentRecorded || snapshot.OutcomeJson is not null
            || snapshot.OutcomeState is not null || snapshot.SelectedNormalAttemptId is not null)
            throw new InvalidOperationException("Only a sixth positive NoPlan recovery may request exhaustion.");
        return RequestNoPlanTerminalAsync(store, identity, BackgroundJobTerminalReason.RetryExhausted);
    }

    private async Task<bool> RequestNoPlanTerminalAsync(PostgresBackgroundJobControlStore store,
        BackgroundJobExecutionIdentity identity, BackgroundJobTerminalReason reason)
    {
        if (!ReferenceEquals(identity, Identity) || Interlocked.Exchange(ref _noPlanTerminalIssued, 1) != 0)
            throw new InvalidOperationException("One actual native invocation owns one immutable NoPlan terminal request.");
        if (RunGate.IsClosed || Token.IsCancellationRequested
            || !_work.BeginStageWait(BackgroundJobInvocationStage.RecoveryDisposition)) return true;

        BackgroundJobCommitProbe? pending = null;
        var confirmationWaitStarted = false;
        while (true)
        {
            // Keep the existing lease/deadline observers while status remains
            // unsettled. Neither a retry nor a historical read reopens Run.
            if (RunGate.IsClosed || Token.IsCancellationRequested) return true;
            if (pending is null)
            {
                // The generation already retains this execution. The concrete
                // task remains in its slot through every actual cleanup failure.
                _noPlanTerminalStatus = store.RequestNoPlanExternalTerminalAsync(identity, reason, Token);
                var status = await _noPlanTerminalStatus.ConfigureAwait(false);
                if (!status.CleanupConfirmed) return false;
                if (status.CommitAcknowledged)
                {
                    Volatile.Write(ref _noPlanTerminalSettled, 1);
                    return true;
                }
                if (status.CommitUncertain)
                {
                    if (status.Probes.Count != 1) return false;
                    pending = status.Probes[0];
                    if (pending.Boundary != BackgroundJobCommitBoundary.ExternalNoPlanTerminalRequest
                        || pending.ExpectedTerminalReason != BackgroundJobFailurePolicy.TerminalCode(reason)) return false;
                    if (!confirmationWaitStarted)
                    {
                        if (!_work.BeginStageWait(BackgroundJobInvocationStage.RecoveryConfirmation)) return true;
                        confirmationWaitStarted = true;
                    }
                }
                // A clean no-op (including SKIP LOCKED) or known rollback may
                // retry only within this original bounded continuation window.
            }
            if (pending is not null)
            {
                if (RunGate.IsClosed || Token.IsCancellationRequested) return true;
                _noPlanTerminalProbe = store.ProbeCommitAsync(pending, Token);
                var observed = await _noPlanTerminalProbe.ConfigureAwait(false);
                if (!observed.CleanupConfirmed) return false;
                if (!observed.Failed && observed.Value is { } proof)
                {
                    if (proof.Disposition == BackgroundJobProbeDisposition.ControlCommitted)
                    {
                        // Durable confirmation never rewrites the old task's ACK.
                        Volatile.Write(ref _noPlanTerminalSettled, 1);
                        return true;
                    }
                    if (proof.Disposition == BackgroundJobProbeDisposition.ChangedOwner) return true;
                    if (proof.Disposition == BackgroundJobProbeDisposition.RolledBack) pending = null;
                }
                // Failed, absent and unresolved probes retain the same pending
                // descriptor. They can issue another read, never another write.
            }
            if (RunGate.IsClosed || Token.IsCancellationRequested) return true;
            // No per-iteration stage rearm: SQL retries share the first stage
            // budget, with at most one additional historical-confirmation window.
            try { await Task.Delay(TimeSpan.FromMilliseconds(50), _time, Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (Token.IsCancellationRequested) { return true; }
        }
    }
}
