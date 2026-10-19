using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Infrastructure.Jobs;

internal sealed partial class BackgroundJobExecutionControl
{
    internal bool HasSettledStage(BackgroundJobInvocationStage stage)
    {
        lock (_sync)
        {
            if (!_joined || _cleanupFault) return false;
            return stage switch
            {
                BackgroundJobInvocationStage.Transactional => _business is
                { IsCompletedSuccessfully: true, Result: { CleanupFailed: false, Receipt.Stage: BackgroundJobExecutionStage.Transactional } },
                BackgroundJobInvocationStage.Prepare => _business is
                { IsCompletedSuccessfully: true, Result: { CleanupFailed: false, Receipt.Stage: BackgroundJobExecutionStage.Prepare } },
                BackgroundJobInvocationStage.Finalize => _business is
                { IsCompletedSuccessfully: true, Result: { CleanupFailed: false, Receipt.Stage: BackgroundJobExecutionStage.Finalize } },
                BackgroundJobInvocationStage.ExternalRead => _externalRead is { IsCompletedSuccessfully: true, Result.CleanupFailed: false },
                BackgroundJobInvocationStage.Arm => _externalArm is
                { IsCompletedSuccessfully: true, Result: { CleanupFailed: false, Kind: BackgroundJobCheckpointKind.Arm } },
                BackgroundJobInvocationStage.Outcome => _externalOutcome is
                { IsCompletedSuccessfully: true, Result: { CleanupFailed: false, Kind: BackgroundJobCheckpointKind.Outcome } },
                BackgroundJobInvocationStage.NoStart => _externalOutcome is
                { IsCompletedSuccessfully: true, Result: { CleanupFailed: false, Kind: BackgroundJobCheckpointKind.NoStart } },
                BackgroundJobInvocationStage.Execute => _claim is not null
                    && _externalProvider is { IsCompletedSuccessfully: true, Result.CleanupFailed: false },
                BackgroundJobInvocationStage.Reconcile => _claim is null && _recoveryEntered && _recoveryBinding is not null
                    && _externalProvider is { IsCompletedSuccessfully: true, Result.CleanupFailed: false },
                BackgroundJobInvocationStage.PrepareHistory => _prepareHistory is { IsCompletedSuccessfully: true, Result.CleanupConfirmed: true },
                BackgroundJobInvocationStage.ArmHistory => _armHistory is { IsCompletedSuccessfully: true, Result.CleanupConfirmed: true },
                BackgroundJobInvocationStage.OutcomeHistory => _outcomeHistory is { IsCompletedSuccessfully: true, Result.CleanupConfirmed: true },
                BackgroundJobInvocationStage.FinalizeHistory => _finalizeHistory is { IsCompletedSuccessfully: true, Result.CleanupConfirmed: true },
                BackgroundJobInvocationStage.RecoveryAcquire => _recoveryAcquisition is { IsCompletedSuccessfully: true, Result.CleanupConfirmed: true },
                // An old clean acquisition probe cannot stand in for a later
                // retry/NoPlan probe whose expected task has not been issued.
                BackgroundJobInvocationStage.RecoveryConfirmation => _noPlanTerminalStatus is not null
                    ? _noPlanTerminalProbe is { IsCompletedSuccessfully: true, Result.CleanupConfirmed: true }
                    : _externalRetry is not null
                        ? _externalRetryProbe is { IsCompletedSuccessfully: true, Result.CleanupConfirmed: true }
                        : _recoveryConfirmation is { IsCompletedSuccessfully: true, Result.CleanupConfirmed: true },
                BackgroundJobInvocationStage.RecoveryDisposition =>
                    _externalRetry is { IsCompletedSuccessfully: true, Result.CleanupConfirmed: true }
                    || _externalFinalizeDisposition is { IsCompletedSuccessfully: true, Result: true }
                    || _noPlanTerminalDisposition is { IsCompletedSuccessfully: true, Result: true }
                    || _noPlanTerminalStatus is { IsCompletedSuccessfully: true, Result.CleanupConfirmed: true },
                _ => false,
            };
        }
    }
}
