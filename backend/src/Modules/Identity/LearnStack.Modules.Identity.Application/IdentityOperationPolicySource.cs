using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Identity.Application.Contracts.Entry;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Permissions;

namespace LearnStack.Modules.Identity.Application;

/// <summary>Finite Identity definitions and exact internal operations; registration grants no stored Role authority.</summary>
public sealed class IdentityOperationPolicySource : IOperationPolicySource
{
    public const string MembershipDirectoryOperation = "identity.membership.list";
    public const string NativeSessionAdmissionOperation = "identity.membership.admit_session_scope";
    public static PermissionDefinition MembershipAdministration { get; } = new(PermissionScope.Tenant, IdentityFoundationManifest.MembershipAdmin);
    public static PermissionDefinition RoleAdministration { get; } = new(PermissionScope.Tenant, IdentityFoundationManifest.RoleAdmin);

    public IEnumerable<PermissionRegistration> Definitions => IdentityFoundationManifest.Version1.Definitions
        .Select(definition => new PermissionRegistration(definition, Assignable: true));

    public IEnumerable<OperationPolicy> Policies =>
    [
        OperationPolicy.ForNativeSessionAdmission<AdmitNativeSessionScopeCommand>(NativeSessionAdmissionOperation),
        OperationPolicy.ForProtected<ListMembershipsQuery>(MembershipDirectoryOperation, MembershipAdministration, OperationReadScope.Tenant),
        OperationPolicy.ForProtected<ChangeRoleGrantsCommand>("identity.role.change_grants", RoleAdministration, requiresOrglessWrite: true),
        OperationPolicy.ForProtected<AdoptRoleGrantManifestCommand>("identity.role.adopt_manifest", RoleAdministration, requiresOrglessWrite: true),
        OperationPolicy.ForTrusted<InitializeSeedRoleCommand>("identity.role.initialize", AuthorizationSourceKind.Seed),
        OperationPolicy.ForTrusted<CreateFirstSeedAdminMembershipCommand>("identity.membership.create_first_seed_admin", AuthorizationSourceKind.Seed),
        OperationPolicy.ForTrusted<CreateSeedMembershipCommand>("identity.membership.create_seed", AuthorizationSourceKind.Seed),
        OperationPolicy.ForTrusted<SetSeedMembershipProfileCommand>("identity.membership_profile.set_seed_profile", AuthorizationSourceKind.Seed),
        OperationPolicy.ForTrusted<GetRoleSeedStateQuery>("identity.role.verify_seed", AuthorizationSourceKind.Seed),
        OperationPolicy.ForTrusted<GetMembershipSeedStateQuery>("identity.membership.verify_seed", AuthorizationSourceKind.Seed),
    ];
}
