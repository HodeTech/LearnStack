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
    public const string OrganizationMembershipDirectoryOperation = "identity.membership.list_organization";
    public const string RoleDirectoryOperation = "identity.role.list";
    public const string RoleDetailOperation = "identity.role.read";
    public const string PermissionRegistryOperation = "identity.role.list_permissions";
    public const string OwnCapabilitiesOperation = "identity.membership.read_own_capabilities";
    public const string OwnMembershipContextsOperation = LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle.OwnTenantNavigationContract.Operation;
    public const string NativeSessionAdmissionOperation = "identity.membership.admit_session_scope";
    public const string MembershipDetailOperation = "identity.membership.read";
    public const string OwnMembershipProfileReadOperation = "identity.membership_profile.read_own";
    public const string MembershipProfileWriteOperation = "identity.membership_profile.write";
    public const string OwnMembershipProfileWriteOperation = "identity.membership_profile.write_own";
    public const string MembershipStatusOperation = "identity.membership.change_status";
    public const string MembershipAssignmentsOperation = "identity.membership.replace_role_assignments";
    public static PermissionDefinition OrganizationMembershipAdministration { get; } = new(PermissionScope.Organization, IdentityFoundationManifest.MembershipAdmin);
    public static PermissionDefinition ProfileWrite { get; } = new(PermissionScope.Tenant, IdentityFoundationManifest.ProfileWrite);
    public static PermissionDefinition OrganizationProfileWrite { get; } = new(PermissionScope.Organization, IdentityFoundationManifest.ProfileWrite);
    public static PermissionDefinition MembershipAdministration { get; } = new(PermissionScope.Tenant, IdentityFoundationManifest.MembershipAdmin);
    public static PermissionDefinition RoleAdministration { get; } = new(PermissionScope.Tenant, IdentityFoundationManifest.RoleAdmin);

    public IEnumerable<PermissionRegistration> Definitions => IdentityFoundationManifest.Version1.Definitions
        .Select(definition => new PermissionRegistration(definition, Assignable: true));

    public IEnumerable<OperationPolicy> Policies =>
    [
        OperationPolicy.ForNativeSessionAdmission<AdmitNativeSessionScopeCommand>(NativeSessionAdmissionOperation),
        OperationPolicy.ForProtected<ListMembershipsQuery>(MembershipDirectoryOperation, MembershipAdministration, OperationReadScope.Tenant),
        OperationPolicy.ForProtected<ListOrganizationMembershipsQuery>(OrganizationMembershipDirectoryOperation,
            [new([MembershipAdministration]), new([OrganizationMembershipAdministration])], requiresOrganization: true),
        OperationPolicy.ForProtected<ListRolesQuery>(RoleDirectoryOperation, RoleAdministration),
        OperationPolicy.ForProtected<GetRoleDetailQuery>(RoleDetailOperation, RoleAdministration),
        OperationPolicy.ForProtected<GetPermissionRegistryQuery>(PermissionRegistryOperation, RoleAdministration),
        OperationPolicy.ForOwnMembershipRead<GetOwnMembershipContextsQuery>(OwnMembershipContextsOperation),
        OperationPolicy.ForOwnMembershipRead<GetOwnCapabilitiesQuery>(OwnCapabilitiesOperation),
        OperationPolicy.ForProtected<GetMembershipDetailQuery>(MembershipDetailOperation,
            [new([MembershipAdministration], OperationReadScope.Tenant), new([OrganizationMembershipAdministration])]),
        OperationPolicy.ForOwnMembershipRead<GetOwnMembershipProfileQuery>(OwnMembershipProfileReadOperation),
        OperationPolicy.ForProtected<ReadMembershipReceiptQuery>("identity.membership.read_receipt",
            [new([MembershipAdministration]), new([OrganizationMembershipAdministration])]),
        OperationPolicy.ForProtected<ReadMemberProfileReceiptQuery>("identity.membership_profile.read_receipt",
            [new([MembershipAdministration]), new([OrganizationMembershipAdministration])]),
        OperationPolicy.ForOwnMembershipRead<ReadOwnMembershipProfileReceiptQuery>("identity.membership_profile.read_own_receipt"),
        OperationPolicy.ForProtected<ReadRoleReceiptQuery>("identity.role.read_receipt", RoleAdministration),
        OperationPolicy.ForProtected<SetMembershipProfileCommand>(MembershipProfileWriteOperation,
            [new([MembershipAdministration, ProfileWrite]), new([MembershipAdministration, OrganizationProfileWrite]),
                new([OrganizationMembershipAdministration, ProfileWrite]), new([OrganizationMembershipAdministration, OrganizationProfileWrite])]),
        OperationPolicy.ForProtected<SetOwnMembershipProfileCommand>(OwnMembershipProfileWriteOperation,
            [new([ProfileWrite]), new([OrganizationProfileWrite])]),
        OperationPolicy.ForProtected<ChangeMembershipStatusCommand>(MembershipStatusOperation,
            [new([MembershipAdministration]), new([OrganizationMembershipAdministration])]),
        OperationPolicy.ForProtected<ReplaceMembershipRoleAssignmentsCommand>(MembershipAssignmentsOperation,
            [new([MembershipAdministration]), new([OrganizationMembershipAdministration])]),
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
