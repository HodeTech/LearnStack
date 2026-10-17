using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Infrastructure.Jobs;

internal enum BackgroundJobKnownOutcomeDisposition { Retry, Finalize }

/// <summary>Disposition of stored completed facts; this value grants no attempt or terminal-write authority.</summary>
internal sealed record BackgroundJobKnownOutcomeDecision(BackgroundJobKnownOutcomeDisposition Disposition,
    BackgroundJobFinalizationStatus? FinalizationStatus, BackgroundJobFinalizationReason? FinalizationReason,
    string? TerminalCode);

/// <summary>ADR-0061's precedence table, shared by live completion and restart reconciliation.</summary>
internal static class BackgroundJobExternalPolicy
{
    internal static BackgroundJobKnownOutcomeDecision Select(BackgroundJobExternalState state,
        BackgroundJobExternalFailurePolicy? policy, int normalAttempts)
    {
        if (normalAttempts is < 1 or > BackgroundJobRuntimeOptions.MaxNormalAttempts)
            throw new ArgumentOutOfRangeException(nameof(normalAttempts));
        if (state == BackgroundJobExternalState.Succeeded && policy is null)
            return new(BackgroundJobKnownOutcomeDisposition.Finalize, BackgroundJobFinalizationStatus.Succeeded,
                BackgroundJobFinalizationReason.None, null);
        if (state is not (BackgroundJobExternalState.Failed or BackgroundJobExternalState.Cancelled)
            || policy is not { } declared || !Enum.IsDefined(declared)
            || state == BackgroundJobExternalState.Cancelled && declared == BackgroundJobExternalFailurePolicy.Permanent)
            throw new ArgumentException("Only explicit completed provider facts select retry or finalization.");

        var reason = state == BackgroundJobExternalState.Failed
            ? BackgroundJobFinalizationReason.ExternalFailed : BackgroundJobFinalizationReason.ExternalCancelled;
        // Provider repeatability is considered before the independent six-start
        // ceiling: a forbidden operation remains forbidden even on attempt six.
        if (declared == BackgroundJobExternalFailurePolicy.Permanent)
            return Failure(reason, "permanent_failure");
        if (declared == BackgroundJobExternalFailurePolicy.TransientNotRepeatable)
            return Failure(reason, "external_retry_forbidden");
        return normalAttempts == BackgroundJobRuntimeOptions.MaxNormalAttempts
            ? Failure(BackgroundJobFinalizationReason.NormalAttemptsExhausted, "retry_exhausted")
            : new(BackgroundJobKnownOutcomeDisposition.Retry, null, null, null);
    }

    private static BackgroundJobKnownOutcomeDecision Failure(BackgroundJobFinalizationReason reason, string terminalCode) =>
        new(BackgroundJobKnownOutcomeDisposition.Finalize, BackgroundJobFinalizationStatus.Failed, reason, terminalCode);
}
