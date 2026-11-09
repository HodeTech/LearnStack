using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;

/// <summary>The exact, scope-bound tuple used to check the current consent for a value pin.</summary>
public sealed record MembershipFieldConsentTuple(Guid MembershipId, UserId SubjectUserId,
    Guid DefinitionId, int DefinitionVersion, string PurposeKey)
{
    public override string ToString() => nameof(MembershipFieldConsentTuple);
}

public sealed record MembershipFieldConsentSnapshot(Guid EvidenceId, long Sequence, bool IsGranted,
    string NoticeLocale, string NoticeHash)
{
    public override string ToString() => nameof(MembershipFieldConsentSnapshot);
}

/// <summary>
/// Uncached current-evidence reads on the ambient tenant owner. The caller owns target
/// authorization and the required fence; this contract does not grant either.
/// </summary>
public interface IMembershipFieldConsentReader
{
    Task<MembershipFieldConsentSnapshot?> ReadCurrentAsync(MembershipFieldConsentTuple tuple,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MembershipFieldConsentSnapshot?>> ReadCurrentBatchAsync(
        IReadOnlyList<MembershipFieldConsentTuple> tuples, CancellationToken cancellationToken = default);
}
