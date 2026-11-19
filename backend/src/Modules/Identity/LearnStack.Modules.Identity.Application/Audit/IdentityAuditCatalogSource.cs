using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Application.Contracts.CustomFields;
using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.Modules.Identity.Application.Invitations;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Application.Contracts.OperatorActs;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Identity.Application.Contracts.Entry;
using LearnStack.Modules.Identity.Application.Contracts.Federation;
using LearnStack.SharedKernel.Audit;

namespace LearnStack.Modules.Identity.Application.Audit;

public sealed class IdentityAuditCatalogSource : IAuditCatalogSource
{
    public string ModuleName => "identity";

    public void Describe(IAuditCatalogBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Off<InspectInvitationQuery>();
        builder.Off<GetOwnInvitationResultQuery>();
        builder.MustAudit<AcceptInvitationCommand>("identity.invitation.accept", OperationType.SecurityEvent, typeof(Invitation));
        builder.MustAudit<AcceptInvitationCommand>("identity.membership.admit_invitation", OperationType.SecurityEvent, typeof(Membership));
        builder.MustAudit<ListInvitationsQuery>("identity.invitation.list", OperationType.ReadSensitive, typeof(Invitation));
        builder.MustAudit<GetInvitationQuery>("identity.invitation.read", OperationType.ReadSensitive, typeof(Invitation));
        builder.MustAudit<ListInvitableRolesQuery>("identity.invitation.list_invitable_roles", OperationType.ReadSensitive, typeof(Invitation));
        builder.MustAudit<CreateInvitationCommand>("identity.invitation.create", OperationType.SecurityEvent, typeof(Invitation));
        builder.MustAudit<RevokeInvitationCommand>("identity.invitation.revoke", OperationType.SecurityEvent, typeof(Invitation));
        builder.MustAudit<ReissueInvitationCommand>("identity.invitation.reissue", OperationType.SecurityEvent, typeof(Invitation));
        builder.MustAudit<ReadInvitationReceiptQuery>("identity.invitation.read_receipt", OperationType.ReadSensitive, typeof(Invitation));

        builder.MustAudit<PrepareTenantInvitationEmailCommand>("identity.invitation.prepare_email", OperationType.SecurityEvent, typeof(Invitation));
        builder.MustAudit<PrepareOrganizationInvitationEmailCommand>("identity.invitation.prepare_email", OperationType.SecurityEvent, typeof(Invitation));
        builder.MustAudit<FinalizeTenantInvitationEmailCommand>("identity.invitation.email_submission", OperationType.SecurityEvent, typeof(Invitation));
        builder.MustAudit<FinalizeOrganizationInvitationEmailCommand>("identity.invitation.email_submission", OperationType.SecurityEvent, typeof(Invitation));
        builder.MustAudit<AdmitNativeSessionScopeCommand>(IdentityOperationPolicySource.NativeSessionAdmissionOperation,
            OperationType.SecurityEvent, typeof(Membership));
        builder.MustAudit<InitializeSeedRoleCommand>("identity.role.initialize", OperationType.SecurityEvent, typeof(Role));
        builder.MustAudit<CreateFirstSeedAdminMembershipCommand>("identity.membership.create_first_seed_admin", OperationType.SecurityEvent, typeof(Membership));
        builder.MustAudit<CreateSeedMembershipCommand>("identity.membership.create_seed", OperationType.SecurityEvent, typeof(Membership));
        builder.MustAudit<SetSeedMembershipProfileCommand>("identity.membership_profile.set_seed_profile", OperationType.Update, typeof(Membership));
        builder.MustAudit<OperatorInitializeRoleCommand>("identity.role.initialize", OperationType.SecurityEvent, typeof(Role));
        builder.MustAudit<OperatorCreateFirstAdminCommand>("identity.membership.create_first_admin", OperationType.SecurityEvent, typeof(Membership));
        builder.MustAudit<OperatorRecoverAdminCommand>("identity.membership.recover_admin", OperationType.SecurityEvent, typeof(Membership));
        builder.Off<GetRoleSeedStateQuery>();
        builder.Off<GetMembershipSeedStateQuery>();
        builder.MustAudit<ListMembershipsQuery>(IdentityOperationPolicySource.MembershipDirectoryOperation, OperationType.ReadSensitive, typeof(Membership));
        builder.MustAudit<ListOrganizationMembershipsQuery>(IdentityOperationPolicySource.OrganizationMembershipDirectoryOperation, OperationType.ReadSensitive, typeof(Membership));
        builder.MustAudit<ListRolesQuery>(IdentityOperationPolicySource.RoleDirectoryOperation, OperationType.ReadSensitive, typeof(Role));
        builder.MustAudit<GetRoleDetailQuery>(IdentityOperationPolicySource.RoleDetailOperation, OperationType.ReadSensitive, typeof(Role));
        builder.MustAudit<GetPermissionRegistryQuery>(IdentityOperationPolicySource.PermissionRegistryOperation, OperationType.ReadSensitive, typeof(Role));
        builder.Off<GetOwnMembershipContextsQuery>();
        builder.Off<GetOwnCapabilitiesQuery>();
        builder.MustAudit<GetMembershipDetailQuery>(IdentityOperationPolicySource.MembershipDetailOperation, OperationType.ReadSensitive, typeof(Membership));
        builder.Off<GetOwnMembershipProfileQuery>();
        builder.MustAudit<ReadMembershipReceiptQuery>("identity.membership.read_receipt", OperationType.ReadSensitive, typeof(Membership));
        builder.MustAudit<ReadMemberProfileReceiptQuery>("identity.membership_profile.read_receipt", OperationType.ReadSensitive, typeof(Membership));
        builder.MustAudit<ReadOwnMembershipProfileReceiptQuery>("identity.membership_profile.read_receipt", OperationType.ReadSensitive, typeof(Membership));
        builder.MustAudit<ReadRoleReceiptQuery>("identity.role.read_receipt", OperationType.ReadSensitive, typeof(Role));
        builder.MustAudit<SetMembershipProfileCommand>(IdentityOperationPolicySource.MembershipProfileWriteOperation, OperationType.Update, typeof(Membership));
        builder.MustAudit<SetOwnMembershipProfileCommand>(IdentityOperationPolicySource.MembershipProfileWriteOperation, OperationType.Update, typeof(Membership));
        builder.Off<GetOwnMembershipCustomFieldsQuery>();
        builder.MustAudit<GetMembershipCustomFieldsQuery>(IdentityOperationPolicySource.MembershipCustomFieldsReadOperation, OperationType.ReadSensitive, typeof(Membership));
        builder.MustAudit<SetMembershipCustomFieldsCommand>(IdentityOperationPolicySource.MembershipCustomFieldsWriteOperation, OperationType.Update, typeof(Membership));
        builder.MustAudit<SetOwnMembershipCustomFieldsCommand>(IdentityOperationPolicySource.MembershipCustomFieldsWriteOperation, OperationType.Update, typeof(Membership));
        builder.MustAudit<ChangeMembershipStatusCommand>(IdentityOperationPolicySource.MembershipStatusOperation, OperationType.SecurityEvent, typeof(Membership));
        builder.MustAudit<ReplaceMembershipRoleAssignmentsCommand>(IdentityOperationPolicySource.MembershipAssignmentsOperation, OperationType.SecurityEvent, typeof(Membership));
        builder.MustAudit<ChangeRoleGrantsCommand>("identity.role.change_grants", OperationType.SecurityEvent, typeof(Role));
        builder.MustAudit<AdoptRoleGrantManifestCommand>("identity.role.adopt_manifest", OperationType.SecurityEvent, typeof(Role));
        // ADR-0099: each Begin, Resume and Complete is its own request type; Reconcile records a detail load's
        // readback drift or expiry as an apply outcome. No row carries a secret, assertion or token.
        builder.MustAudit<BeginConfigureFederationCommand>("identity.federation.configure", OperationType.SecurityEvent, typeof(FederationConnection));
        builder.MustAudit<BeginRotateFederationSecretCommand>("identity.federation.rotate_secret", OperationType.SecurityEvent, typeof(FederationConnection));
        builder.MustAudit<BeginRenameFederationCommand>("identity.federation.rename", OperationType.SecurityEvent, typeof(FederationConnection));
        builder.MustAudit<BeginFederationTestStartCommand>("identity.federation.test_start", OperationType.SecurityEvent, typeof(FederationConnection));
        builder.MustAudit<BeginDisableFederationCommand>("identity.federation.disable", OperationType.SecurityEvent, typeof(FederationConnection));
        builder.MustAudit<BeginDeleteFederationCommand>("identity.federation.delete", OperationType.SecurityEvent, typeof(FederationConnection));
        builder.MustAudit<ResumeFederationOperationCommand>("identity.federation.resume", OperationType.SecurityEvent, typeof(FederationConnection));
        builder.MustAudit<CompleteFederationOperationCommand>("identity.federation.apply", OperationType.SecurityEvent, typeof(FederationConnection));
        builder.MustAudit<ReconcileFederationConnectionCommand>("identity.federation.apply", OperationType.SecurityEvent, typeof(FederationConnection));
        builder.MustAudit<EnableFederationCommand>("identity.federation.enable", OperationType.SecurityEvent, typeof(FederationConnection));
        builder.MustAudit<ActivateFederationCommand>("identity.federation.activate", OperationType.SecurityEvent, typeof(FederationConnection));
        builder.MustAudit<RecordFederationTestResultCommand>("identity.federation.test_record", OperationType.SecurityEvent, typeof(FederationConnection));
        // Off: the connection detail holds no personal data, and the anonymous sign-in read is a public read.
        builder.Off<GetFederationConnectionQuery>();
        builder.Off<GetPublicSignInQuery>();
        builder.DeclareOffPath("identity.user.create", OperationType.SecurityEvent, OperationClass.Must, typeof(User));
        builder.DeclareOffPath("identity.authorization.deny", OperationType.SecurityEvent, OperationClass.Must);
    }
}
