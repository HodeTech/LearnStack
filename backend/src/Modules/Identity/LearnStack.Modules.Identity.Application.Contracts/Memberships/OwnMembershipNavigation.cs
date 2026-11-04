using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Identity.Application.Contracts.Memberships;

public sealed record ListOrganizationMembershipsQuery(string? Cursor = null, int Limit = CursorPagination.DefaultLimit)
    : IRequest<Result<Page<MembershipDirectoryItem>>>;

public sealed record GetOwnMembershipContextsQuery(string? Cursor = null, int Limit = CursorPagination.DefaultLimit)
    : IRequest<Result<Page<OwnMembershipContextDto>>>;

/// <summary>Own navigation only. The API independently converts the verified host to an admitted origin.</summary>
public sealed record OwnMembershipContextDto(TenantId TenantId, OrganizationId? OrganizationId,
    [property: PiiSensitive] string TenantName, [property: PiiSensitive] string? OrganizationName, string EntryHost)
{
    public override string ToString() => nameof(OwnMembershipContextDto);
}

public sealed record GetOwnCapabilitiesQuery() : IRequest<Result<OwnCapabilitiesDto>>;

/// <summary>Advisory current-self flags, never reusable authorization or an aggregate representation.</summary>
public sealed record OwnCapabilitiesDto(bool CanReadTenantMembers, bool CanReadOrganizationMembers,
    bool CanWriteMemberProfiles, bool CanManageMemberships, bool CanReadRoles, bool CanChangeRoleGrants,
    bool CanAdoptRoleRelease, bool CanWriteOwnProfile, bool CanManageOrganizations);
