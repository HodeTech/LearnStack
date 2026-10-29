using System.Collections.Immutable;
using System.Text.Json.Serialization;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Identity.Application.Contracts.Memberships;

[JsonConverter(typeof(JsonStringEnumConverter<ManagedMembershipStatus>))]
public enum ManagedMembershipStatus { Active = 1, Suspended = 2, Revoked = 3 }

public sealed record MembershipDetailDto(Guid MembershipId, UserId UserId, OrganizationId? OrganizationId,
    ManagedMembershipStatus Status, [property: PiiSensitive] string? DisplayName,
    [property: PiiSensitive] string? PreferredLocale, ImmutableArray<Guid> RoleIds, long Version)
{
    public override string ToString() => nameof(MembershipDetailDto);
}

public sealed record OwnMembershipProfileDto([property: PiiSensitive] string? DisplayName,
    [property: PiiSensitive] string? PreferredLocale, long Version)
{
    public override string ToString() => nameof(OwnMembershipProfileDto);
}

/// <summary>Module result retaining the exact changed target for protected receipt disclosure.</summary>
public sealed record MembershipMutationResult(Guid MembershipId, long Version);

public sealed record GetMembershipDetailQuery(Guid MembershipId) : IRequest<Result<MembershipDetailDto>>;

public sealed record GetOwnMembershipProfileQuery() : IRequest<Result<OwnMembershipProfileDto>>;

public sealed record SetMembershipProfileCommand(Guid MembershipId, long ExpectedVersion,
    [property: PiiSensitive] string? DisplayName, [property: PiiSensitive] string? PreferredLocale)
    : IRequest<Result<MembershipMutationResult>>
{
    public override string ToString() => nameof(SetMembershipProfileCommand);
}

public sealed record SetOwnMembershipProfileCommand(long ExpectedVersion,
    [property: PiiSensitive] string? DisplayName, [property: PiiSensitive] string? PreferredLocale)
    : IRequest<Result<MembershipMutationResult>>
{
    public override string ToString() => nameof(SetOwnMembershipProfileCommand);
}

public sealed record ChangeMembershipStatusCommand(Guid MembershipId, long ExpectedVersion, ManagedMembershipStatus Status)
    : IRequest<Result<MembershipMutationResult>>;

public sealed record ReplaceMembershipRoleAssignmentsCommand(Guid MembershipId, long ExpectedVersion, ImmutableArray<Guid> RoleIds)
    : IRequest<Result<MembershipMutationResult>>;
