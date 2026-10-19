using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Application.Jobs;

/// <summary>
/// The closed original/recovery alternatives used by the three framework requests.
/// Original ledger facts remain distinct from current recovery ownership.
/// </summary>
public sealed class BackgroundJobExternalCheckpointAuthority
{
    private readonly ConfirmedStartReceipt? _original;
    private readonly BackgroundJobExternalRecoveryBinding? _recovery;

    internal BackgroundJobExternalCheckpointAuthority(ConfirmedStartReceipt original)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (original.Claim.Profile != BackgroundJobExecutionProfile.External)
            throw new InvalidOperationException("An external checkpoint requires its acknowledged external origin.");
        _original = original;
    }

    internal BackgroundJobExternalCheckpointAuthority(BackgroundJobExternalRecoveryBinding recovery)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        if (!recovery.Receipt.IsBoundTo(recovery))
            throw new InvalidOperationException("An external checkpoint requires its receipt's exact recovery binding.");
        _recovery = recovery;
    }

    internal ConfirmedStartReceipt Original => _original
        ?? throw new InvalidOperationException("Recovery is not a counted admission receipt.");
    public bool IsRecovery => _recovery is not null;
    public bool IsTerminalNoStart => _recovery?.Receipt.Purpose == BackgroundJobExternalRecoveryPurpose.TerminalNoStart;
    public string? TerminalReason => _recovery?.Receipt.TerminalReason;
    public BackgroundJobId JobId => _original?.Envelope.JobId ?? _recovery!.Receipt.JobId;
    public TenantId TenantId => _original?.Envelope.TenantId ?? _recovery!.Receipt.TenantId;
    public OrganizationId? OrganizationId => _original is not null ? _original.Envelope.OrganizationId : _recovery!.Receipt.OrganizationId;
    public string DefinitionKey => _original?.Envelope.DefinitionKey ?? _recovery!.Receipt.DefinitionKey;
    public string Queue => _original?.Envelope.Queue ?? _recovery!.Receipt.Queue;
    public string CapacityGroup => _original?.Envelope.CapacityGroup ?? _recovery?.Receipt.CapacityGroup
        ?? throw new InvalidOperationException("External work requires its declared capacity group.");
    public DateTimeOffset OriginalDueAt => _original?.Envelope.OriginalDueAt ?? _recovery!.Receipt.OriginalDueAt;
    public BackgroundJobAttemptId AttemptId => _original?.AttemptId ?? _recovery!.Receipt.AttemptId;
    public BackgroundJobLedgerStage LedgerStage => _original?.LedgerStage ?? BackgroundJobLedgerStage.Normal;
    public int NormalAttemptOrdinal => _original?.NormalAttemptOrdinal ?? _recovery!.Receipt.NormalAttemptOrdinal;
    public int FinalizeAttemptOrdinal => _original?.FinalizeAttemptOrdinal ?? 0;
    public BackgroundJobAttemptId? SelectedNormalAttemptId => _original?.SelectedNormalAttemptId;
    public DateTimeOffset AdmittedAt => _original?.AdmittedAt ?? _recovery!.Receipt.AdmittedAt;
    public DateTimeOffset Deadline => _original?.Deadline ?? _recovery!.Receipt.Deadline;
    public Guid AdmissionToken => _original?.Claim.Token ?? _recovery!.Receipt.AdmissionToken;
    public Guid OwnerToken => _original?.Claim.Token ?? _recovery!.Receipt.RecoveryToken;
    public long WakeGeneration => _original?.Claim.WakeGeneration ?? _recovery!.Receipt.WakeGeneration;
    public JobApplicationSourceOwner SourceOwner => _original?.SourceOwner ?? _recovery!.SourceOwner;
    public BackgroundJobRunGate Run => _original?.Run ?? _recovery!.Run;

    public bool MatchesEnvelope(BackgroundJobEnvelope envelope) =>
        envelope.JobId == JobId && envelope.TenantId == TenantId && envelope.OrganizationId == OrganizationId
        && envelope.DefinitionKey == DefinitionKey && envelope.Profile == "external" && envelope.Queue == Queue
        && envelope.CapacityGroup == CapacityGroup && envelope.OriginalDueAt == OriginalDueAt
        && (_original is null || envelope == _original.Envelope);
}
