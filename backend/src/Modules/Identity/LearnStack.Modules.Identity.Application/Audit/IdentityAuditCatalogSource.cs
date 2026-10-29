using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Identity.Application.Contracts.Entry;
using LearnStack.SharedKernel.Audit;

namespace LearnStack.Modules.Identity.Application.Audit;

public sealed class IdentityAuditCatalogSource : IAuditCatalogSource
{
    public string ModuleName => "identity";

    public void Describe(IAuditCatalogBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.MustAudit<AdmitNativeSessionScopeCommand>(IdentityOperationPolicySource.NativeSessionAdmissionOperation,
            OperationType.SecurityEvent, typeof(Membership));
        builder.MustAudit<InitializeSeedRoleCommand>("identity.role.initialize", OperationType.SecurityEvent, typeof(Role));
        builder.MustAudit<CreateFirstSeedAdminMembershipCommand>("identity.membership.create_first_seed_admin", OperationType.SecurityEvent, typeof(Membership));
        builder.MustAudit<CreateSeedMembershipCommand>("identity.membership.create_seed", OperationType.SecurityEvent, typeof(Membership));
        builder.MustAudit<SetSeedMembershipProfileCommand>("identity.membership_profile.set_seed_profile", OperationType.Update, typeof(Membership));
        builder.Off<GetRoleSeedStateQuery>();
        builder.Off<GetMembershipSeedStateQuery>();
        builder.MustAudit<ListMembershipsQuery>(IdentityOperationPolicySource.MembershipDirectoryOperation, OperationType.ReadSensitive, typeof(Membership));
        builder.MustAudit<GetMembershipDetailQuery>(IdentityOperationPolicySource.MembershipDetailOperation, OperationType.ReadSensitive, typeof(Membership));
        builder.Off<GetOwnMembershipProfileQuery>();
        builder.MustAudit<SetMembershipProfileCommand>(IdentityOperationPolicySource.MembershipProfileWriteOperation, OperationType.Update, typeof(Membership));
        builder.MustAudit<SetOwnMembershipProfileCommand>(IdentityOperationPolicySource.MembershipProfileWriteOperation, OperationType.Update, typeof(Membership));
        builder.MustAudit<ChangeMembershipStatusCommand>(IdentityOperationPolicySource.MembershipStatusOperation, OperationType.SecurityEvent, typeof(Membership));
        builder.MustAudit<ReplaceMembershipRoleAssignmentsCommand>(IdentityOperationPolicySource.MembershipAssignmentsOperation, OperationType.SecurityEvent, typeof(Membership));
        builder.MustAudit<ChangeRoleGrantsCommand>("identity.role.change_grants", OperationType.SecurityEvent, typeof(Role));
        builder.MustAudit<AdoptRoleGrantManifestCommand>("identity.role.adopt_manifest", OperationType.SecurityEvent, typeof(Role));
        builder.DeclareOffPath("identity.user.create", OperationType.SecurityEvent, OperationClass.Must, typeof(User));
        builder.DeclareOffPath("identity.authorization.deny", OperationType.SecurityEvent, OperationClass.Must);
    }
}
