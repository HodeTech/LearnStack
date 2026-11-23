using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Application.Contracts.CustomFields;
using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.Modules.Identity.Application.Invitations;
using LearnStack.Modules.Identity.Application.DataRequests;
using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Application.Contracts.OperatorActs;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Identity.Application.Contracts.Entry;
using LearnStack.Modules.Identity.Application.Contracts.Federation;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Platform;

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
        // ADR-0106 § 13, ADR-0107 § 2: an export's fulfilment is one row about the request and one about the artifact
        // it stored (the revoked earlier artifact travels in that row's changes); the purge is one row about the
        // artifact. A recorded no-op writes the rows with no subject.
        builder.MustAudit<RunTenantDataExportCommand>("identity.data_request.fulfill_export", OperationType.SecurityEvent, typeof(DataSubjectRequest));
        builder.MustAudit<RunTenantDataExportCommand>("identity.data_export.store", OperationType.SecurityEvent, typeof(DataExportArtifact));
        builder.MustAudit<RunOrganizationDataExportCommand>("identity.data_request.fulfill_export", OperationType.SecurityEvent, typeof(DataSubjectRequest));
        builder.MustAudit<RunOrganizationDataExportCommand>("identity.data_export.store", OperationType.SecurityEvent, typeof(DataExportArtifact));
        builder.MustAudit<PurgeTenantDataExportCommand>("identity.data_export.purge", OperationType.Delete, typeof(DataExportArtifact));
        builder.MustAudit<PurgeOrganizationDataExportCommand>("identity.data_export.purge", OperationType.Delete, typeof(DataExportArtifact));
        // ADR-0107 § 5: a tenant erasure is one row about the erasure request (a cancelled export request travels in its
        // changes) and one per kind of change it makes: the Memberships of the snapshot erased, the pending invitations the
        // subject issued revoked, and the subject's artifact withdrawn. The erasure designates the first instance of each;
        // a blocked or no-op run writes the rows with no subject.
        builder.MustAudit<RunTenantDataErasureCommand>("identity.data_request.fulfill_erasure", OperationType.SecurityEvent, typeof(DataSubjectRequest));
        builder.MustAudit<RunTenantDataErasureCommand>("identity.membership.erase", OperationType.SecurityEvent, typeof(Membership));
        builder.MustAudit<RunTenantDataErasureCommand>("identity.invitation.revoke", OperationType.SecurityEvent, typeof(Invitation));
        builder.MustAudit<RunTenantDataErasureCommand>("identity.data_export.revoke", OperationType.Delete, typeof(DataExportArtifact));
        builder.MustAudit<RunOrganizationDataErasureCommand>("identity.data_request.fulfill_erasure", OperationType.SecurityEvent, typeof(DataSubjectRequest));
        builder.MustAudit<RunOrganizationDataErasureCommand>("identity.membership.erase", OperationType.SecurityEvent, typeof(Membership));
        builder.MustAudit<RunOrganizationDataErasureCommand>("identity.invitation.revoke", OperationType.SecurityEvent, typeof(Invitation));
        builder.MustAudit<RunOrganizationDataErasureCommand>("identity.data_export.revoke", OperationType.Delete, typeof(DataExportArtifact));

        // ADR-0106 § 13, ADR-0107 § 4: every creation, cancellation and revocation is a MUST row about its aggregate; an
        // administrator's read of the group's requests and every ticket mint and chunk read are MUST read-sensitive rows
        // that refuse the disclosure when they cannot be written. The ticket rows name the ticket, whose digests the
        // capture redacts; no row carries a ticket, plaintext or ciphertext. The subject's own reads are Off.
        builder.MustAudit<CreateOwnDataExportCommand>("identity.data_request.create_export", OperationType.Create, typeof(DataSubjectRequest));
        builder.MustAudit<CreateDataExportForSubjectCommand>("identity.data_request.create_export", OperationType.Create, typeof(DataSubjectRequest));
        builder.MustAudit<CreateOwnDataErasureCommand>("identity.data_request.create_erasure", OperationType.Create, typeof(DataSubjectRequest));
        builder.MustAudit<CreateDataErasureForSubjectCommand>("identity.data_request.create_erasure", OperationType.Create, typeof(DataSubjectRequest));
        // ADR-0107 § 7: the person alone files a closure; no administrator operation names the slug.
        builder.MustAudit<CreateOwnAccountClosureCommand>("identity.data_request.create_closure", OperationType.Create, typeof(DataSubjectRequest));
        builder.MustAudit<CancelOwnDataRequestCommand>("identity.data_request.cancel", OperationType.Update, typeof(DataSubjectRequest));
        builder.MustAudit<CancelDataRequestCommand>("identity.data_request.cancel", OperationType.Update, typeof(DataSubjectRequest));
        builder.MustAudit<RevokeOwnDataExportCommand>("identity.data_export.revoke", OperationType.Delete, typeof(DataExportArtifact));
        builder.MustAudit<RevokeDataExportCommand>("identity.data_export.revoke", OperationType.Delete, typeof(DataExportArtifact));
        builder.MustAudit<MintOwnDataExportTicketCommand>("identity.data_export.mint_ticket", OperationType.ReadSensitive, typeof(DataExportTicket));
        builder.MustAudit<ReadOwnDataExportChunkCommand>("identity.data_export.read_chunk", OperationType.ReadSensitive, typeof(DataExportTicket));
        builder.MustAudit<ListDataRequestsQuery>("identity.data_request.list", OperationType.ReadSensitive, typeof(DataSubjectRequest));
        builder.MustAudit<GetDataRequestQuery>("identity.data_request.read", OperationType.ReadSensitive, typeof(DataSubjectRequest));
        builder.MustAudit<ReadDataRequestReceiptQuery>("identity.data_request.read_receipt", OperationType.ReadSensitive, typeof(DataSubjectRequest));
        builder.Off<ListOwnDataRequestsQuery>();
        builder.Off<GetOwnDataRequestQuery>();
        builder.Off<ReadOwnDataRequestReceiptQuery>();
        // ADR-0106 § 9, § 13: the retained own-scope entry writes exactly the live path's rows under the same names.
        builder.MustAudit<CreateRetainedDataExportCommand>("identity.data_request.create_export", OperationType.Create, typeof(DataSubjectRequest));
        builder.MustAudit<CreateRetainedDataErasureCommand>("identity.data_request.create_erasure", OperationType.Create, typeof(DataSubjectRequest));
        builder.MustAudit<CreateRetainedAccountClosureCommand>("identity.data_request.create_closure", OperationType.Create, typeof(DataSubjectRequest));
        builder.MustAudit<CancelRetainedDataRequestCommand>("identity.data_request.cancel", OperationType.Update, typeof(DataSubjectRequest));
        builder.MustAudit<RevokeRetainedDataExportCommand>("identity.data_export.revoke", OperationType.Delete, typeof(DataExportArtifact));
        builder.MustAudit<MintRetainedDataExportTicketCommand>("identity.data_export.mint_ticket", OperationType.ReadSensitive, typeof(DataExportTicket));
        builder.MustAudit<ReadRetainedDataExportChunkCommand>("identity.data_export.read_chunk", OperationType.ReadSensitive, typeof(DataExportTicket));
        builder.Off<ListRetainedDataRequestScopesQuery>();
        builder.Off<ListRetainedDataRequestsQuery>();
        builder.Off<ReadRetainedDataRequestReceiptQuery>();
        builder.MustAudit<AdmitNativeSessionScopeCommand>(IdentityOperationPolicySource.NativeSessionAdmissionOperation,
            OperationType.SecurityEvent, typeof(Membership));
        builder.MustAudit<InitializeSeedRoleCommand>("identity.role.initialize", OperationType.SecurityEvent, typeof(Role));
        builder.MustAudit<CreateFirstSeedAdminMembershipCommand>("identity.membership.create_first_seed_admin", OperationType.SecurityEvent, typeof(Membership));
        builder.MustAudit<CreateSeedMembershipCommand>("identity.membership.create_seed", OperationType.SecurityEvent, typeof(Membership));
        builder.MustAudit<SetSeedMembershipProfileCommand>("identity.membership_profile.set_seed_profile", OperationType.Update, typeof(Membership));
        builder.MustAudit<OperatorInitializeRoleCommand>("identity.role.initialize", OperationType.SecurityEvent, typeof(Role));
        builder.MustAudit<OperatorCreateFirstAdminCommand>("identity.membership.create_first_admin", OperationType.SecurityEvent, typeof(Membership));
        builder.MustAudit<OperatorRecoverAdminCommand>("identity.membership.recover_admin", OperationType.SecurityEvent, typeof(Membership));
        // ADR-0107 §§ 7, 8: a closure's fan-out writes one row in the participant tenant, about the group's fan-out erasure it
        // created or the erasure it adopted or waits for; a person's or administrator's erasure it cancelled and replaced
        // travels in that row's changes. A refused group writes the row with its refusal and no subject.
        builder.MustAudit<FanOutClosureCommand>(PlatformOperations.ClosureFanOut.AuditSlug, OperationType.SecurityEvent, typeof(DataSubjectRequest));
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
        // The controlled issuer rebind (ADR-0104 § 8): an intent row on its own short transaction and a result
        // row with the change, both from the offline tool's role and neither through the pipeline.
        builder.DeclareOffPath("identity.authority.rebind_attempt", OperationType.SecurityEvent, OperationClass.Must, typeof(IdentityAuthority));
        builder.DeclareOffPath("identity.authority.rebind", OperationType.SecurityEvent, OperationClass.Must, typeof(IdentityAuthority));
    }
}
