using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Identity.Application.Contracts.Seeding;

public sealed record InitializeSeedRoleCommand(Guid RoleId, BuiltInRoleKey BuiltInKey,
    PermissionScope CapabilityScope, int ManifestVersion, string ExpectedFingerprint)
    : IRequest<Result<Guid>>, IIdentityPermissionRequest
{
    public PermissionDefinition RequiredPermission => new(PermissionScope.Tenant, "identity.role.admin");
}

public sealed record CreateFirstSeedAdminMembershipCommand(Guid MembershipId, UserId UserId, Guid RoleId,
    int ManifestVersion, string ExpectedFingerprint) : IRequest<Result<Guid>>, IIdentityPermissionRequest
{
    public PermissionDefinition RequiredPermission => new(PermissionScope.Tenant, "identity.membership.admin");
}

public sealed record CreateSeedMembershipCommand(Guid MembershipId, UserId UserId, Guid RoleId,
    PermissionScope MembershipScope, int ManifestVersion, string ExpectedFingerprint)
    : IRequest<Result<Guid>>, IIdentityPermissionRequest
{
    public PermissionDefinition RequiredPermission => new(MembershipScope, "identity.membership.admin");
}

public sealed record SetSeedMembershipProfileCommand(Guid MembershipId, UserId ExpectedUserId, Guid ExpectedRoleId, PermissionScope MembershipScope,
    [property: PiiSensitive] string? DisplayName, [property: PiiSensitive] string? PreferredLocale,
    int ManifestVersion, string ExpectedFingerprint) : IRequest<Result<None>>, IIdentityPermissionRequest
{
    public PermissionDefinition RequiredPermission => new(MembershipScope, "identity.membership_profile.write");
}
