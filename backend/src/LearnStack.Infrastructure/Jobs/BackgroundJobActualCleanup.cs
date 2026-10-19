using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Positive local cleanup, distinct from the database outcome and recovery authority.</summary>
internal sealed class BackgroundJobActualCleanup
{
    private BackgroundJobActualCleanup(BackgroundJobExecutionControl execution, JobStartExecutionResult admission,
        BackgroundJobEnvelope envelope, JobBusinessExecutionResult? business)
    {
        Execution = execution;
        Claim = execution.Claim;
        Envelope = envelope;
        SourceOwner = admission.SourceOwner;
        Receipt = admission.Receipt;
        BusinessResult = business;
        Evidence = business is null ? BackgroundJobCapacityCleanupEvidence.NeverStarted
            : BackgroundJobCapacityCleanupEvidence.TransactionalRollback;
    }

    internal BackgroundJobCapacityCleanupEvidence Evidence { get; }
    internal BackgroundJobExecutionControl Execution { get; }
    internal BackgroundJobRoutingClaim Claim { get; }
    internal BackgroundJobEnvelope Envelope { get; }
    internal JobApplicationSourceOwner SourceOwner { get; }
    internal ConfirmedStartReceipt? Receipt { get; }
    internal JobBusinessExecutionResult? BusinessResult { get; }

    internal void RequireRecovery(BackgroundJobCapacityRecoveryReceipt recovery)
    {
        if (recovery.JobId != Envelope.JobId || recovery.TenantId != Envelope.TenantId
            || recovery.OrganizationId != Envelope.OrganizationId || recovery.DefinitionKey != Envelope.DefinitionKey
            || recovery.Profile != Envelope.Profile || recovery.Queue != Envelope.Queue
            || recovery.CapacityGroup != Envelope.CapacityGroup || recovery.OriginalDueAt != Envelope.OriginalDueAt
            || recovery.AdmissionToken != Claim.Token || recovery.WakeGeneration != Claim.WakeGeneration
            || Receipt is { } receipt && (recovery.AttemptId != receipt.AttemptId
                || recovery.NormalAttemptOrdinal != receipt.NormalAttemptOrdinal
                || recovery.AdmittedAt != receipt.AdmittedAt || recovery.Deadline != receipt.Deadline))
            throw new InvalidOperationException("Capacity recovery does not match the actual cleaned invocation.");
    }

    internal abstract class MintingSource
    {
        protected static BackgroundJobActualCleanup MintCleanup(BackgroundJobExecutionControl execution,
            JobStartExecutionResult admission, BackgroundJobEnvelope envelope, JobBusinessExecutionResult? business) =>
            new(execution, admission, envelope, business);
    }
}
