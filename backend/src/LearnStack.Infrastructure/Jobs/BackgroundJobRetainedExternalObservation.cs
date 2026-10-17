using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>
/// Canonical in-memory facts captured at provider return, before scope cleanup.
/// This is neither a durable checkpoint nor permission to release or repeat work.
/// </summary>
internal sealed class BackgroundJobRetainedExternalObservation
{
    private BackgroundJobRetainedExternalObservation(BackgroundJobExternalContext context, string planFingerprint,
        BackgroundJobExternalState state, string? outcome, BackgroundJobExternalFailurePolicy? policy,
        Guid? quiescenceId, BackgroundJobQuiescenceKind? quiescenceKind)
    {
        JobId = context.JobId;
        AttemptId = context.AttemptId;
        OperationId = context.OperationId;
        PlanFingerprint = planFingerprint;
        State = state;
        OutcomeJson = outcome;
        FailurePolicy = policy;
        QuiescenceId = quiescenceId;
        QuiescenceKind = quiescenceKind;
    }

    internal BackgroundJobId JobId { get; }
    internal BackgroundJobAttemptId AttemptId { get; }
    internal Guid OperationId { get; }
    internal string PlanFingerprint { get; }
    internal BackgroundJobExternalState State { get; }
    internal string? OutcomeJson { get; }
    internal BackgroundJobExternalFailurePolicy? FailurePolicy { get; }
    internal Guid? QuiescenceId { get; }
    internal BackgroundJobQuiescenceKind? QuiescenceKind { get; }
    internal bool IsCompleted => State is BackgroundJobExternalState.Succeeded
        or BackgroundJobExternalState.Failed or BackgroundJobExternalState.Cancelled;

    internal static BackgroundJobRetainedExternalObservation Capture<TOutcome>(BackgroundJobExternalContext context,
        string planFingerprint, BackgroundJobExternalResult<TOutcome> report) where TOutcome : IBackgroundJobOutcome
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(report);
        if (!JobApplicationSourceOwner.IsExternal)
            throw new InvalidOperationException("Provider observations are captured within their denial-only external boundary.");
        if (!context.AttemptId.IsInitialized() || context.AttemptId.Value.Version != 7
            || context.OperationId != BackgroundJobExternalIdentity.OperationId(context.JobId, planFingerprint))
            throw new ArgumentException("The provider observation requires its exact attempt and stable logical-operation identity.");

        string? json = null;
        if (report.State is BackgroundJobExternalState.Succeeded or BackgroundJobExternalState.Failed or BackgroundJobExternalState.Cancelled)
        {
            json = BackgroundJobPayloadContract.Serialize(report.Outcome
                ?? throw new ArgumentException("Completed provider facts require their concrete outcome."),
                typeof(TOutcome), BackgroundJobPayloadKind.Outcome);
        }
        return new(context, planFingerprint, report.State, json, report.FailurePolicy,
            report.Quiescence?.EvidenceId, report.Quiescence?.Kind);
    }
}
