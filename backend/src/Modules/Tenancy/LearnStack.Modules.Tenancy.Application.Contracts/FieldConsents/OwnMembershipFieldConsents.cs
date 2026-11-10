using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;

/// <summary>Confirmation of one displayed immutable notice and exact predecessor.</summary>
public sealed record GrantOwnMembershipFieldConsentCommand(
    [property: PiiSensitive] Guid DefinitionId, int DefinitionVersion,
    [property: PiiSensitive] string NoticeLocale, [property: PiiSensitive] string ExpectedNoticeHash, long ExpectedSequence,
    [property: PiiSensitive] Guid? ExpectedEvidenceId, bool Confirmed)
    : IRequest<Result<MembershipFieldConsentReceipt>>
{
    public override string ToString() => nameof(GrantOwnMembershipFieldConsentCommand);
}

/// <summary>Revokes a retained exact tuple; its scope and pins are resolved from evidence.</summary>
public sealed record WithdrawOwnMembershipFieldConsentCommand(
    [property: PiiSensitive] Guid EvidenceId, long ExpectedSequence,
    [property: PiiSensitive] Guid ExpectedEvidenceId, bool Confirmed)
    : IRequest<Result<MembershipFieldConsentReceipt>>
{
    public override string ToString() => nameof(WithdrawOwnMembershipFieldConsentCommand);
}

public sealed record GetOwnMembershipFieldConsentQuery(
    [property: PiiSensitive] Guid MembershipId, [property: PiiSensitive] Guid DefinitionId, int DefinitionVersion)
    : IRequest<Result<MembershipFieldConsentCurrent>>
{
    public override string ToString() => nameof(GetOwnMembershipFieldConsentQuery);
}

public sealed record ListOwnFieldConsentScopesQuery([property: PiiSensitive] string? Cursor = null, int Limit = 20)
    : IRequest<Result<MembershipFieldConsentScopePage>>
{
    public override string ToString() => nameof(ListOwnFieldConsentScopesQuery);
}

public sealed record ListOwnMembershipFieldConsentsQuery(
    [property: PiiSensitive] Guid MembershipId, [property: PiiSensitive] string? Cursor = null, int Limit = 20)
    : IRequest<Result<MembershipFieldConsentPage>>
{
    public override string ToString() => nameof(ListOwnMembershipFieldConsentsQuery);
}

public sealed record MembershipFieldConsentCurrent([property: PiiSensitive] MembershipFieldConsentDetails? Consent)
{
    public override string ToString() => nameof(MembershipFieldConsentCurrent);
}

public sealed record MembershipFieldConsentReceipt([property: PiiSensitive] Guid EvidenceId, long Sequence, bool IsGranted)
{
    public override string ToString() => nameof(MembershipFieldConsentReceipt);
}

public sealed record MembershipFieldConsentDetails([property: PiiSensitive] Guid EvidenceId, long Sequence, bool IsGranted,
    [property: PiiSensitive] Guid MembershipId, [property: PiiSensitive] Guid DefinitionId, int DefinitionVersion,
    [property: PiiSensitive] string PurposeKey, [property: PiiSensitive] string NoticeLocale,
    [property: PiiSensitive] string NoticeHash, [property: PiiSensitive] string? NoticeText = null,
    [property: PiiSensitive] IReadOnlyDictionary<string, string>? Labels = null)
{
    public override string ToString() => nameof(MembershipFieldConsentDetails);
}

public sealed record MembershipFieldConsentScope([property: PiiSensitive] Guid MembershipId,
    TenantId TenantId, OrganizationId? OrganizationId)
{
    public override string ToString() => nameof(MembershipFieldConsentScope);
}

public sealed record MembershipFieldConsentScopePage(IReadOnlyList<MembershipFieldConsentScope> Items, [property: PiiSensitive] string? NextCursor)
{
    public override string ToString() => nameof(MembershipFieldConsentScopePage);
}

public sealed record MembershipFieldConsentPage(IReadOnlyList<MembershipFieldConsentDetails> Items, [property: PiiSensitive] string? NextCursor)
{
    public override string ToString() => nameof(MembershipFieldConsentPage);
}
