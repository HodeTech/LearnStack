namespace LearnStack.SharedKernel.Jobs;

/// <summary>Identifier-only prepared inputs declared in the module's Application.Contracts.</summary>
public interface IBackgroundJobPlan { }

/// <summary>Identifier-only known result declared in the module's Application.Contracts.</summary>
public interface IBackgroundJobOutcome { }

/// <summary>Immutable external-operation identity; none of these values authorizes a database write.</summary>
/// <param name="OperationId">Stable logical-operation identity, preserved across attempts.</param>
public sealed record BackgroundJobExternalContext(
    BackgroundJobId JobId, BackgroundJobAttemptId AttemptId, Guid OperationId, DateTimeOffset Deadline);

public enum BackgroundJobExternalState
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
    NeverStarted,
    Unknown,
}

public enum BackgroundJobQuiescenceKind
{
    ProviderFenced,
    ExecutionTerminated,
}

/// <summary>
/// An adapter's reference to positive provider/supervisor evidence. This report is not
/// a reservation-release capability; the runtime must bind and persist verified evidence.
/// </summary>
public sealed class BackgroundJobQuiescenceEvidence
{
    public BackgroundJobQuiescenceEvidence(Guid evidenceId, BackgroundJobQuiescenceKind kind)
    {
        if (evidenceId == Guid.Empty || !Enum.IsDefined(kind))
            throw new ArgumentException("Quiescence requires a known evidence identity and kind.");
        EvidenceId = evidenceId;
        Kind = kind;
    }

    public Guid EvidenceId { get; }
    public BackgroundJobQuiescenceKind Kind { get; }
}

/// <summary>
/// Closed adapter report. NeverStarted still requires the runtime's locked no-ExecuteIntent
/// proof; NotFound after ExecuteIntent is Unknown, not permission to start again.
/// </summary>
public sealed class BackgroundJobExternalResult<TOutcome> where TOutcome : IBackgroundJobOutcome
{
    internal BackgroundJobExternalResult(BackgroundJobExternalState state, TOutcome? outcome,
        BackgroundJobQuiescenceEvidence? quiescence)
    {
        State = state;
        Outcome = outcome;
        Quiescence = quiescence;
    }

    public BackgroundJobExternalState State { get; }
    public TOutcome? Outcome { get; }
    public BackgroundJobQuiescenceEvidence? Quiescence { get; }
}

/// <summary>Creates only the closed external report shapes.</summary>
public static class BackgroundJobExternalResult
{
    public static BackgroundJobExternalResult<TOutcome> Running<TOutcome>() where TOutcome : IBackgroundJobOutcome =>
        new(BackgroundJobExternalState.Running, default, null);
    public static BackgroundJobExternalResult<TOutcome> Unknown<TOutcome>() where TOutcome : IBackgroundJobOutcome =>
        new(BackgroundJobExternalState.Unknown, default, null);
    public static BackgroundJobExternalResult<TOutcome> NeverStarted<TOutcome>() where TOutcome : IBackgroundJobOutcome =>
        new(BackgroundJobExternalState.NeverStarted, default, null);

    public static BackgroundJobExternalResult<TOutcome> Completed<TOutcome>(BackgroundJobExternalState state,
        TOutcome outcome, BackgroundJobQuiescenceEvidence quiescence)
        where TOutcome : IBackgroundJobOutcome
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(quiescence);
        if (state is not (BackgroundJobExternalState.Succeeded or BackgroundJobExternalState.Failed or BackgroundJobExternalState.Cancelled))
            throw new ArgumentException("A known external outcome must be succeeded, failed or cancelled.", nameof(state));
        return new(state, outcome, quiescence);
    }
}

/// <summary>
/// Module Contracts ports inherit this shape; their Infrastructure adapters perform
/// external work without a business transaction, sender, service provider or callback.
/// </summary>
public interface IBackgroundJobExternalOperation<TPlan, TOutcome>
    where TPlan : IBackgroundJobPlan
    where TOutcome : IBackgroundJobOutcome
{
    Task<BackgroundJobExternalResult<TOutcome>> ExecuteAsync(
        BackgroundJobExternalContext context, TPlan plan, CancellationToken cancellationToken = default);

    Task<BackgroundJobExternalResult<TOutcome>> ReconcileAsync(
        BackgroundJobExternalContext context, TPlan plan, CancellationToken cancellationToken = default);
}

public enum BackgroundJobFinalizationStatus
{
    Succeeded,
    Failed,
}

/// <summary>Finite domain-finalization reasons; no exception text or unbounded provider message is carried.</summary>
public enum BackgroundJobFinalizationReason
{
    None,
    ExternalFailed,
    ExternalCancelled,
    NormalAttemptsExhausted,
    PermanentFailure,
}

/// <summary>
/// Known finalization facts, separate from identifier-only plan/outcome DTOs. A confirmed
/// prepare followed by failure before an external effect needs no fabricated provider outcome.
/// Applying known Failed state successfully returns Result.Ok from the module finalizer.
/// </summary>
public sealed class BackgroundJobFinalization<TPlan, TOutcome>
    where TPlan : IBackgroundJobPlan
    where TOutcome : IBackgroundJobOutcome
{
    public BackgroundJobFinalization(TPlan plan, TOutcome? outcome,
        BackgroundJobFinalizationStatus status, BackgroundJobFinalizationReason reason)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!Enum.IsDefined(status) || !Enum.IsDefined(reason)
            || (status == BackgroundJobFinalizationStatus.Succeeded
                ? outcome is null || reason != BackgroundJobFinalizationReason.None
                : reason == BackgroundJobFinalizationReason.None)
            || reason is BackgroundJobFinalizationReason.ExternalFailed or BackgroundJobFinalizationReason.ExternalCancelled
                && outcome is null)
            throw new ArgumentException("Finalization requires known success with an outcome or a closed final-failure reason.");
        Plan = plan;
        Outcome = outcome;
        Status = status;
        Reason = reason;
    }

    public TPlan Plan { get; }
    public TOutcome? Outcome { get; }
    public BackgroundJobFinalizationStatus Status { get; }
    public BackgroundJobFinalizationReason Reason { get; }
}
