using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Immutable control facts from an acknowledged start; never a tenant selector or executable receipt.</summary>
internal sealed class BackgroundJobExecutionIdentity
{
    private BackgroundJobExecutionIdentity(ConfirmedStartReceipt receipt)
    {
        JobId = receipt.Envelope.JobId;
        TenantId = receipt.Envelope.TenantId;
        OrganizationId = receipt.Envelope.OrganizationId;
        DefinitionKey = receipt.Envelope.DefinitionKey;
        Queue = receipt.Envelope.Queue;
        CapacityGroup = receipt.Envelope.CapacityGroup;
        Profile = receipt.Envelope.Profile;
        LedgerStage = receipt.LedgerStage;
        Stage = receipt.Stage;
        FinalizeAttemptOrdinal = receipt.FinalizeAttemptOrdinal;
        SelectedNormalAttemptId = receipt.SelectedNormalAttemptId;
        OriginalDueAt = receipt.Envelope.OriginalDueAt;
        AttemptId = receipt.AttemptId;
        Token = receipt.Claim.Token;
        AdmissionToken = receipt.Claim.Token;
        WakeGeneration = receipt.Claim.WakeGeneration;
        Deadline = receipt.Deadline;
        AdmittedAt = receipt.AdmittedAt;
        OwnershipUntil = receipt.OwnershipUntil;
        NormalAttemptOrdinal = receipt.NormalAttemptOrdinal;
    }

    private BackgroundJobExecutionIdentity(BackgroundJobExternalRecoveryReceipt receipt)
    {
        JobId = receipt.JobId;
        TenantId = receipt.TenantId;
        OrganizationId = receipt.OrganizationId;
        DefinitionKey = receipt.DefinitionKey;
        Queue = receipt.Queue;
        CapacityGroup = receipt.CapacityGroup;
        Profile = "external";
        LedgerStage = BackgroundJobLedgerStage.Normal;
        OriginalDueAt = receipt.OriginalDueAt;
        AttemptId = receipt.AttemptId;
        AdmissionToken = receipt.AdmissionToken;
        Token = receipt.RecoveryToken;
        WakeGeneration = receipt.WakeGeneration;
        Deadline = receipt.Deadline;
        AdmittedAt = receipt.AdmittedAt;
        OwnershipUntil = receipt.OwnershipUntil;
        NormalAttemptOrdinal = receipt.NormalAttemptOrdinal;
        IsRecovery = true;
    }

    internal BackgroundJobId JobId { get; }
    internal TenantId TenantId { get; }
    internal OrganizationId? OrganizationId { get; }
    internal string DefinitionKey { get; }
    internal string Queue { get; }
    internal string? CapacityGroup { get; }
    internal string Profile { get; }
    internal BackgroundJobLedgerStage LedgerStage { get; }
    internal BackgroundJobExecutionStage? Stage { get; }
    internal int FinalizeAttemptOrdinal { get; }
    internal BackgroundJobAttemptId? SelectedNormalAttemptId { get; }
    internal DateTimeOffset OriginalDueAt { get; }
    internal BackgroundJobAttemptId AttemptId { get; }
    internal Guid Token { get; }
    internal Guid AdmissionToken { get; }
    internal bool IsRecovery { get; }
    internal long WakeGeneration { get; }
    internal DateTimeOffset Deadline { get; }
    internal DateTimeOffset AdmittedAt { get; }
    internal DateTimeOffset OwnershipUntil { get; }
    internal int NormalAttemptOrdinal { get; }

    internal static BackgroundJobExecutionIdentity FromRecovery(BackgroundJobExternalRecoveryReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.Purpose != BackgroundJobExternalRecoveryPurpose.Reconcile)
            throw new InvalidOperationException("Terminal no-start recovery grants no ordinary execution or renewal identity.");
        // A passive copy of actual recovery facts, never a source/run/binding or
        // another executable receipt. The SQL source still fences every value.
        return new(receipt);
    }

    internal static BackgroundJobExecutionIdentity FromReceipt(ConfirmedStartReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var normal = receipt.LedgerStage == BackgroundJobLedgerStage.Normal
            && receipt.FinalizeAttemptOrdinal == 0 && receipt.SelectedNormalAttemptId is null;
        var validStage = receipt.Envelope.Profile switch
        {
            "transactional" => normal && receipt.Stage == BackgroundJobExecutionStage.Transactional,
            "external" when receipt.Envelope.CapacityGroup is not null => normal
                ? receipt.Stage is null or BackgroundJobExecutionStage.Prepare
                : receipt.LedgerStage == BackgroundJobLedgerStage.Finalize
                    && receipt.Stage == BackgroundJobExecutionStage.Finalize
                    && receipt.FinalizeAttemptOrdinal is >= 1 and <= BackgroundJobRuntimeOptions.MaxFinalizeAttempts
                    && receipt.SelectedNormalAttemptId is not null && receipt.SelectedNormalAttemptId != receipt.AttemptId,
            _ => false,
        };
        if (!validStage || (receipt.Claim.Profile == BackgroundJobExecutionProfile.External) != (receipt.Envelope.Profile == "external")
            || receipt.NormalAttemptOrdinal is < 1 or > BackgroundJobRuntimeOptions.MaxNormalAttempts
            || receipt.Deadline <= receipt.AdmittedAt || receipt.OwnershipUntil <= receipt.AdmittedAt)
            throw new InvalidOperationException("Job control requires an acknowledged start with its exact profile and stage.");
        return new(receipt);
    }
}
