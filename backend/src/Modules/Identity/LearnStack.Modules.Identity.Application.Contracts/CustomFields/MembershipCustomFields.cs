using System.Collections.Immutable;
using System.Text.Json;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Identity.Application.Contracts.CustomFields;

public sealed record MembershipCustomFieldSet(string ConceptKey, Guid DefinitionId, int SchemaVersion,
    [property: PiiSensitive] JsonElement Envelope, Guid? ConsentEvidenceId)
{ public override string ToString() => nameof(MembershipCustomFieldSet); }

/// <summary>Omission preserves a slot. Removal names its exact retained pin, so a stale form cannot remove a replacement.</summary>
public sealed record MembershipCustomFieldRemove(string ConceptKey, Guid DefinitionId, int SchemaVersion)
{ public override string ToString() => nameof(MembershipCustomFieldRemove); }

public sealed record MembershipCustomFieldPatch([property: PiiSensitive] ImmutableArray<MembershipCustomFieldSet> Set,
    ImmutableArray<MembershipCustomFieldRemove> Remove)
{ public override string ToString() => nameof(MembershipCustomFieldPatch); }

public sealed record GetMembershipCustomFieldsQuery(Guid MembershipId) : IRequest<Result<MembershipCustomFieldsDto>>;
public sealed record GetOwnMembershipCustomFieldsQuery() : IRequest<Result<MembershipCustomFieldsDto>>;
public sealed record SetMembershipCustomFieldsCommand(Guid MembershipId, long ExpectedVersion,
    [property: PiiSensitive] MembershipCustomFieldPatch Patch) : IRequest<Result<MembershipMutationResult>>
{ public override string ToString() => nameof(SetMembershipCustomFieldsCommand); }
public sealed record SetOwnMembershipCustomFieldsCommand(long ExpectedVersion,
    [property: PiiSensitive] MembershipCustomFieldPatch Patch) : IRequest<Result<MembershipMutationResult>>
{ public override string ToString() => nameof(SetOwnMembershipCustomFieldsCommand); }

/// <summary>Current/Missing/Retired/Withheld; withheld slots never carry an envelope. The metadata is an ephemeral Identity disclosure projection of the exact owning revision.</summary>
public sealed record MembershipCustomFieldDto(string ConceptKey, Guid DefinitionId, int SchemaVersion, string State,
    bool IsRequired, [property: PiiSensitive] JsonElement? Envelope,
    [property: PiiSensitive] MembershipCustomFieldMetadata Definition)
{ public override string ToString() => nameof(MembershipCustomFieldDto); }
/// <summary>Read-model projection only; definition authority and interpretation stay with Customization.</summary>
public sealed record MembershipCustomFieldMetadata([property: PiiSensitive] string JsonSchema,
    [property: PiiSensitive] IReadOnlyDictionary<string, string> Labels, PersonalDataCategory PiiCategory,
    int DisplayOrder, [property: PiiSensitive] string? PurposeKey,
    [property: PiiSensitive] IReadOnlyDictionary<string, string>? Notices,
    [property: PiiSensitive] IReadOnlyList<MembershipCustomFieldControlLabels>? FormLabels)
{ public override string ToString() => nameof(MembershipCustomFieldMetadata); }

public sealed record MembershipCustomFieldControlLabels(string Location,
    [property: PiiSensitive] IReadOnlyDictionary<string, string>? Labels,
    [property: PiiSensitive] IReadOnlyList<IReadOnlyDictionary<string, string>>? Choices)
{ public override string ToString() => nameof(MembershipCustomFieldControlLabels); }

public sealed record MembershipCustomFieldsDto(Guid MembershipId, long Version,
    [property: PiiSensitive] IReadOnlyList<MembershipCustomFieldDto> Fields)
{ public override string ToString() => nameof(MembershipCustomFieldsDto); }

public sealed record CustomFieldConsentMembership(TenantId TenantId, OrganizationId? OrganizationId,
    UserId SubjectUserId, Guid MembershipId, bool HasExactRetainedValuePin)
{ public override string ToString() => nameof(CustomFieldConsentMembership); }

/// <summary>Fresh exact own Active Membership and lifecycle on the actual announced owner, after the supplied exclusive fence.</summary>
public interface ICustomFieldConsentMembershipReader
{
    Task<CustomFieldConsentMembership?> ReadOwnActiveAsync(Guid membershipId, Guid definitionId, int schemaVersion,
        IdentityMutationLockPlan fence, CancellationToken cancellationToken = default);
}
