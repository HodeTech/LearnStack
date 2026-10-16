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
        AttemptId = receipt.AttemptId;
        Token = receipt.Claim.Token;
        WakeGeneration = receipt.Claim.WakeGeneration;
        Deadline = receipt.Deadline;
        AdmittedAt = receipt.AdmittedAt;
        OwnershipUntil = receipt.OwnershipUntil;
        NormalAttemptOrdinal = receipt.NormalAttemptOrdinal;
    }

    internal BackgroundJobId JobId { get; }
    internal TenantId TenantId { get; }
    internal OrganizationId? OrganizationId { get; }
    internal string DefinitionKey { get; }
    internal string Queue { get; }
    internal BackgroundJobAttemptId AttemptId { get; }
    internal Guid Token { get; }
    internal long WakeGeneration { get; }
    internal DateTimeOffset Deadline { get; }
    internal DateTimeOffset AdmittedAt { get; }
    internal DateTimeOffset OwnershipUntil { get; }
    internal int NormalAttemptOrdinal { get; }

    internal static BackgroundJobExecutionIdentity FromReceipt(ConfirmedStartReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.Stage != BackgroundJobExecutionStage.Transactional
            || receipt.NormalAttemptOrdinal is < 1 or > BackgroundJobRuntimeOptions.MaxNormalAttempts
            || receipt.Deadline <= receipt.AdmittedAt || receipt.OwnershipUntil <= receipt.AdmittedAt)
            throw new InvalidOperationException("Job control requires an acknowledged transactional start.");
        return new(receipt);
    }
}
