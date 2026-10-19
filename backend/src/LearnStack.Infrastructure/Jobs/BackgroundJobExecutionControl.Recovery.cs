using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Infrastructure.Jobs;

internal sealed partial class BackgroundJobExecutionControl
{
    private readonly long _recoveryGeneration;
    private readonly string? _recoveryQueue;
    private Task<OwnedBackgroundJobExternalRecovery>? _recoveryAcquisition;
    private Task<OwnedBackgroundJobControl<BackgroundJobExternalRecoveryReceipt>>? _recoveryConfirmation;
    private JobApplicationSourceOwner? _recoverySource;
    private BackgroundJobExternalRecoveryBinding? _recoveryBinding;
    private bool _recoveryEntered;

    private bool RecoveryOperationsCleaned => _recoveryEntered
        && (_recoveryAcquisition is null || _recoveryAcquisition is { IsCompletedSuccessfully: true, Result.CleanupConfirmed: true })
        && (_recoveryConfirmation is null || _recoveryConfirmation is { IsCompletedSuccessfully: true, Result.CleanupConfirmed: true });

    internal async Task<bool> ExecuteExternalRecoveryAsync(JobExecutor executor, PostgresBackgroundJobControlStore store)
    {
        if (_claim is not null || _recoveryGeneration < 1 || _recoveryQueue is null
            || Interlocked.Exchange(ref _externalIssued, 1) != 0)
            throw new InvalidOperationException("External recovery requires its exact held native invocation.");
        _recoveryEntered = true;
        try
        {
            if (RunGate.IsClosed || Token.IsCancellationRequested
                || !_work.BeginStageWait(BackgroundJobInvocationStage.RecoveryAcquire)) return true;
            // The dictionary and native work already own this control before
            // the actual acquisition or any synchronous provider prefix.
            var observedAt = _time.GetTimestamp();
            _recoveryAcquisition = store.AcquireExternalRecoveryAsync(JobId, _recoveryGeneration, _recoveryQueue, Token);
            var acquired = await _recoveryAcquisition.ConfigureAwait(false);
            if (!acquired.CleanupConfirmed) return false;
            var receipt = acquired.Value;
            if (receipt is null && acquired.CommitUncertain)
            {
                if (RunGate.IsClosed || Token.IsCancellationRequested
                    || !_work.BeginStageWait(BackgroundJobInvocationStage.RecoveryConfirmation)) return true;
                observedAt = _time.GetTimestamp();
                _recoveryConfirmation = store.ProbeExternalRecoveryAsync(acquired, acquired.Probes.Single(), Token);
                var confirmed = await _recoveryConfirmation.ConfigureAwait(false);
                if (!confirmed.CleanupConfirmed) return false;
                if (confirmed.Failed) return true;
                receipt = confirmed.Value;
            }
            if (receipt is null || RunGate.IsClosed || Token.IsCancellationRequested) return true;

            lock (_sync) _leaseObservationEligible = true;
            if (receipt.OwnershipUntil - receipt.DbNow - _time.GetElapsedTime(observedAt, _time.GetTimestamp()) <= TimeSpan.Zero)
                ObserveLeaseLoss(BackgroundJobLeaseLossCause.Elapsed);
            _recoverySource = JobApplicationSourceOwner.Create();
            try
            {
                using (_recoverySource.Enter()) _recoveryBinding = receipt.Bind(RunGate);
            }
            catch (InvalidOperationException) when (RunGate.IsClosed && _recoverySource.CleanupConfirmed)
            {
                return true; // A clean, elapsed first binding grants no entry.
            }
            if (!StartRecoveryLease(receipt, observedAt)) return true;
            if (!_work.BeginStageWait(BackgroundJobInvocationStage.ExternalRead)) return true;
            _externalRead = executor.ReadExternalStateAsync(_recoveryBinding, Token);
            var read = await _externalRead.ConfigureAwait(false);
            if (read.CleanupFailed) return false;
            if (read.Handle is not { } handle || RunGate.IsClosed || Token.IsCancellationRequested) return true;
            var snapshot = handle.Require(_recoveryBinding);
            var retryEligible = false;
            // Committed facts precede a new provider observation. Final-selecting
            // retained facts use the app owner without inventing another report.
            if (snapshot.OutcomeJson is not null || snapshot.OutcomeState is not null)
            {
                if (snapshot.OutcomeState is not { } state || snapshot.OutcomeJson is null
                    || snapshot.SelectedNormalAttemptId is not null) return true;
                if (BackgroundJobExternalPolicy.Select(state, snapshot.FailurePolicy, receipt.NormalAttemptOrdinal)
                        .Disposition == BackgroundJobKnownOutcomeDisposition.Retry)
                    return await RetryExternalKnownAsync(store, BackgroundJobExternalRetryMode.RecoveryKnown).ConfigureAwait(false);
                var selected = RunGate.TryOpenBoundary(BackgroundJobCheckpointKind.Outcome);
                if (selected is null || !_work.BeginStageWait(BackgroundJobInvocationStage.Outcome)) return true;
                Volatile.Write(ref _externalOutcomeGate, selected);
                _externalOutcome = executor.SelectStoredExternalOutcomeAsync(_recoveryBinding, handle, selected, Token);
            }
            else if (!snapshot.ExecuteIntentRecorded)
            {
                // A sixth NoPlan receipt cannot retry or manufacture a plan.
                // Request terminal cleanup before ordinary NoStart or provider entry,
                // including when an earlier cleanup already recorded NoStart.
                if (snapshot.PlanJson is null && receipt.NormalAttemptOrdinal == BackgroundJobRuntimeOptions.MaxNormalAttempts)
                    return await RequestNoPlanTerminalAfterRecoveryAsync(store, read).ConfigureAwait(false);
                if (snapshot.NoStartRecorded && receipt.NormalAttemptOrdinal < 6)
                    return await RetryExternalKnownAsync(store, BackgroundJobExternalRetryMode.RecoveryKnown).ConfigureAwait(false);
                retryEligible = receipt.NormalAttemptOrdinal < 6;
                var noStart = RunGate.TryOpenBoundary(BackgroundJobCheckpointKind.NoStart);
                if (noStart is null || !_work.BeginStageWait(BackgroundJobInvocationStage.NoStart)) return true;
                Volatile.Write(ref _externalOutcomeGate, noStart);
                _externalOutcome = executor.RecordExternalNoStartAsync(_recoveryBinding, handle, noStart, Token);
            }
            else
            {
                if (snapshot.PlanJson is null || !_work.BeginStageWait(BackgroundJobInvocationStage.Reconcile)) return true;
                // The original immutable deadline remains provider identity.
                // Recovery uses a live probe token, never the old deadline timer.
                _externalProvider = executor.ReconcileExternalAsync(_recoveryBinding, handle, Token);
                var report = await _externalProvider.ConfigureAwait(false);
                if (report.CleanupFailed) return false;
                if (report.Status != JobExternalExecutionStatus.Reported || report.Observation is not { IsCompleted: true } observation)
                    return await FinishUnresolvedExternalAsync(store, report, recovered: true).ConfigureAwait(false);
                if (RunGate.IsClosed || Token.IsCancellationRequested) return true;
                retryEligible = BackgroundJobExternalPolicy.Select(observation.State, observation.FailurePolicy,
                    receipt.NormalAttemptOrdinal).Disposition == BackgroundJobKnownOutcomeDisposition.Retry;
                var outcomeGate = RunGate.TryOpenBoundary(BackgroundJobCheckpointKind.Outcome);
                if (outcomeGate is null || !_work.BeginStageWait(BackgroundJobInvocationStage.Outcome)) return true;
                Volatile.Write(ref _externalOutcomeGate, outcomeGate);
                _externalOutcome = executor.RecordExternalOutcomeAsync(_recoveryBinding, handle, outcomeGate, observation, Token);
            }
            var outcome = await _externalOutcome.ConfigureAwait(false);
            if (outcome.CleanupFailed) return false;
            if (!outcome.CommitAcknowledged && outcome.CommitUncertain)
            {
                if (!_work.BeginStageWait(BackgroundJobInvocationStage.OutcomeHistory)) return true;
                _outcomeHistory = ObserveCheckpointHistoryAsync(store, outcome.Kind == BackgroundJobCheckpointKind.NoStart
                    ? BackgroundJobCheckpointHistoryOrigin.ForNoStart(outcome)
                    : BackgroundJobCheckpointHistoryOrigin.ForOutcome(outcome));
                if (!(await _outcomeHistory.ConfigureAwait(false)).CleanupConfirmed) return false;
            }
            return !retryEligible || !ExternalOutcomeCommitted(outcome)
                || await RetryExternalKnownAsync(store, BackgroundJobExternalRetryMode.RecoveryKnown).ConfigureAwait(false);
        }
        catch (Exception failure) when (Catchable(failure))
        {
            // Unexpected initialization may have allocated an owner without
            // returning its handle. Null task/timer slots prove no cleanup.
            RetainCleanup();
            return false;
        }
        finally
        {
            RunGate.Close();
            _recoverySource?.CloseIssuance();
        }
    }

    private bool StartRecoveryLease(BackgroundJobExternalRecoveryReceipt receipt, long observedAt)
    {
        var identity = BackgroundJobExecutionIdentity.FromRecovery(receipt);
        TimeSpan remaining;
        lock (_sync)
        {
            if (_joined || _cleanupFault || RunGate.IsClosed || Volatile.Read(ref _renewalClosed) != 0) return false;
            var now = _time.GetTimestamp();
            remaining = receipt.OwnershipUntil - receipt.DbNow - _time.GetElapsedTime(observedAt, now);
            if (remaining <= TimeSpan.Zero)
            {
                ObserveLeaseLoss(BackgroundJobLeaseLossCause.Elapsed);
                Interlocked.Exchange(ref _renewalClosed, 1);
                RunGate.Close();
            }
            else
            {
                _leaseTimerInitializing = true;
                _leaseExpiry = checked(now + (long)(remaining.TotalSeconds * _time.TimestampFrequency));
            }
        }
        if (remaining <= TimeSpan.Zero) { _work.ExpireWait(); return false; }
        try
        {
            // A synchronous factory prefix must not hold the fixed-state lock
            // needed by host stop. Its returned handle remains owned after stop.
            var actual = _time.CreateTimer(static state => ((BackgroundJobExecutionControl)state!).LeaseElapsed(),
                this, remaining, Timeout.InfiniteTimeSpan);
            lock (_sync)
            {
                _leaseTimer = actual;
                if (_joined || _cleanupFault || RunGate.IsClosed || Volatile.Read(ref _renewalClosed) != 0)
                { _leaseTimerInitializing = false; return false; }
                remaining = receipt.OwnershipUntil - receipt.DbNow
                    - _time.GetElapsedTime(observedAt, _time.GetTimestamp());
            }
            var changed = remaining > TimeSpan.Zero && actual.Change(remaining, Timeout.InfiniteTimeSpan);
            lock (_sync)
            {
                _leaseTimerInitializing = false;
                if (!changed && remaining > TimeSpan.Zero) _cleanupFault = true;
                if (changed && !_joined && !_cleanupFault && !RunGate.IsClosed && Volatile.Read(ref _renewalClosed) == 0
                    && _time.GetTimestamp() < _leaseExpiry)
                {
                    Volatile.Write(ref _identity, identity);
                    return true;
                }
                if (remaining <= TimeSpan.Zero || _time.GetTimestamp() >= _leaseExpiry)
                    ObserveLeaseLoss(BackgroundJobLeaseLossCause.Elapsed);
                Interlocked.Exchange(ref _renewalClosed, 1);
                RunGate.Close();
            }
            _work.ExpireWait();
            return false;
        }
        catch (Exception failure) when (Catchable(failure))
        {
            lock (_sync)
            {
                _leaseTimerInitializing = false;
                _cleanupFault = true;
            }
            CloseExecution();
            throw;
        }
    }
}
