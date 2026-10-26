using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;

namespace LearnStack.Modules.Identity.Application.Contracts.Seeding;

public enum SeedIdentityFoundationOperation
{
    InitializeRole = 1, CreateFirstAdmin = 2, CreateMembership = 3, SetProfile = 4,
    VerifyRole = 5, VerifyMembership = 6,
}

/// <summary>Bounded declaration. A separate confined owner must also possess actual provider-adoption witnesses.</summary>
public sealed record SeedIdentityFoundationAct(SeedIdentityFoundationOperation Operation,
    TenantId TenantId, OrganizationId? OrganizationId, UserId ActorUserId, Guid RootId,
    UserId? TargetUserId, Guid? RoleId, BuiltInRoleKey? BuiltInKey, PermissionScope Scope,
    int ManifestVersion, string ExpectedFingerprint);
