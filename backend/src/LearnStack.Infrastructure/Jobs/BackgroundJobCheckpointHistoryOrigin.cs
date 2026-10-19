using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>
/// Retains one actual issued checkpoint operation, including its original cleanup
/// owners. Historical observation is never a new execution or recovery authority.
/// </summary>
internal sealed class BackgroundJobCheckpointHistoryOrigin
{
    private BackgroundJobCheckpointHistoryOrigin(JobBusinessExecutionResult business)
    {
        Business = business;
        Gate = business.Gate;
        Receipt = business.Receipt;
    }

    private BackgroundJobCheckpointHistoryOrigin(JobExternalCheckpointResult checkpoint)
    {
        Checkpoint = checkpoint;
        Gate = checkpoint.Gate;
        Authority = checkpoint.Arm?.Authority ?? checkpoint.Outcome!.Authority;
    }

    internal JobBusinessExecutionResult? Business { get; }
    internal JobExternalCheckpointResult? Checkpoint { get; }
    internal ConfirmedStartReceipt? Receipt { get; }
    internal BackgroundJobExternalCheckpointAuthority? Authority { get; }
    internal BackgroundJobCommitGate Gate { get; }
    internal BackgroundJobCheckpointKind Kind => Gate.Kind;
    internal BackgroundJobId JobId => Receipt?.Envelope.JobId ?? Authority!.JobId;
    internal BackgroundJobAttemptId AttemptId => Receipt?.AttemptId ?? Authority!.AttemptId;

    internal bool Matches(BackgroundJobCheckpointHistoryFacts facts)
    {
        var receipt = Receipt;
        return facts.JobId == JobId.Value && facts.AttemptId == AttemptId.Value && facts.Profile == "external"
            && facts.TenantId == (receipt?.Envelope.TenantId.Value ?? Authority!.TenantId.Value)
            && facts.OrganizationId == (receipt is not null ? receipt.Envelope.OrganizationId?.Value : Authority!.OrganizationId?.Value)
            && facts.DefinitionKey == (receipt?.Envelope.DefinitionKey ?? Authority!.DefinitionKey)
            && facts.Queue == (receipt?.Envelope.Queue ?? Authority!.Queue)
            && facts.CapacityGroup == (receipt?.Envelope.CapacityGroup ?? Authority!.CapacityGroup)
            && facts.OriginalDueAt == (receipt?.Envelope.OriginalDueAt ?? Authority!.OriginalDueAt)
            && facts.Stage == (receipt?.LedgerStage == BackgroundJobLedgerStage.Finalize ? "finalize" : "normal")
            && facts.Ordinal == (receipt?.LedgerStage == BackgroundJobLedgerStage.Finalize
                ? receipt.FinalizeAttemptOrdinal : receipt?.NormalAttemptOrdinal ?? Authority!.NormalAttemptOrdinal)
            && facts.AdmissionToken == (receipt?.Claim.Token ?? Authority!.AdmissionToken)
            && facts.AdmittedAt == (receipt?.AdmittedAt ?? Authority!.AdmittedAt)
            && facts.Deadline == (receipt?.Deadline ?? Authority!.Deadline)
            && facts.SelectedNormalAttemptId == receipt?.SelectedNormalAttemptId?.Value;
    }

    internal static BackgroundJobCheckpointHistoryOrigin ForPrepare(JobBusinessExecutionResult execution)
    {
        RequireBusiness(execution, BackgroundJobCheckpointKind.Prepare, BackgroundJobExecutionStage.Prepare,
            BackgroundJobLedgerStage.Normal);
        return new(execution);
    }

    internal static BackgroundJobCheckpointHistoryOrigin ForFinalize(JobBusinessExecutionResult execution)
    {
        RequireBusiness(execution, BackgroundJobCheckpointKind.Finalize, BackgroundJobExecutionStage.Finalize,
            BackgroundJobLedgerStage.Finalize);
        if (execution.Receipt.SelectedNormalAttemptId is null) throw Refused();
        return new(execution);
    }

    internal static BackgroundJobCheckpointHistoryOrigin ForArm(JobExternalCheckpointResult execution)
    {
        RequireCheckpoint(execution, BackgroundJobCheckpointKind.Arm);
        if (execution.Arm is null || execution.Outcome is not null || execution.Arm.Authority.IsRecovery)
            throw Refused();
        return new(execution);
    }

    internal static BackgroundJobCheckpointHistoryOrigin ForOutcome(JobExternalCheckpointResult execution)
    {
        RequireCheckpoint(execution, BackgroundJobCheckpointKind.Outcome);
        if (execution.Arm is not null || execution.Outcome is not { IsNoStart: false }) throw Refused();
        return new(execution);
    }

    internal static BackgroundJobCheckpointHistoryOrigin ForNoStart(JobExternalCheckpointResult execution)
    {
        RequireCheckpoint(execution, BackgroundJobCheckpointKind.NoStart);
        if (execution.Arm is not null || execution.Outcome is not { IsNoStart: true, Authority.IsRecovery: true })
            throw Refused();
        return new(execution);
    }

    private static void RequireBusiness(JobBusinessExecutionResult execution, BackgroundJobCheckpointKind kind,
        BackgroundJobExecutionStage stage, BackgroundJobLedgerStage ledger)
    {
        ArgumentNullException.ThrowIfNull(execution);
        var receipt = execution.Receipt;
        if (!execution.ExecutionIssued || !execution.CommitStarted || execution.KnownRollback
            || receipt.Claim.Profile != BackgroundJobExecutionProfile.External || receipt.Stage != stage
            || receipt.LedgerStage != ledger || execution.Gate.Kind != kind
            || !ReferenceEquals(receipt.Run, execution.Gate.Run)
            || !ReferenceEquals(receipt.SourceOwner, execution.SourceOwner)) throw Refused();
    }

    private static void RequireCheckpoint(JobExternalCheckpointResult execution, BackgroundJobCheckpointKind kind)
    {
        ArgumentNullException.ThrowIfNull(execution);
        var authority = execution.Arm?.Authority ?? execution.Outcome!.Authority;
        if (!execution.Issued || !execution.CommitStarted || execution.KnownRollback || execution.Kind != kind
            || authority.LedgerStage != BackgroundJobLedgerStage.Normal
            || !ReferenceEquals(authority.Run, execution.Gate.Run)
            || !ReferenceEquals(authority.SourceOwner, execution.SourceOwner)) throw Refused();
    }

    private static InvalidOperationException Refused() =>
        new("Checkpoint history requires the exact issued operation and its COMMIT admission.");
}
