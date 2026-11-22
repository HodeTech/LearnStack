using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Application.Contracts.CustomFields;
using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Application.Contracts.OperatorActs;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Identity.Application.Contracts.Entry;
using LearnStack.Modules.Identity.Application.Contracts.Federation;
using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Application.Invitations;
using LearnStack.Modules.Identity.Application.DataRequests;
using LearnStack.SharedKernel.Permissions;

namespace LearnStack.Modules.Identity.Application;

/// <summary>Finite Identity definitions and exact internal operations; registration grants no stored Role authority.</summary>
public sealed class IdentityOperationPolicySource : IOperationPolicySource
{
    public const string InvitationListOperation = "identity.invitation.list";
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
    public const string MembershipCustomFieldsReadOperation = "identity.membership.custom_fields_read";
    public const string OwnMembershipCustomFieldsReadOperation = "identity.membership.custom_fields_read_own";
    public const string MembershipCustomFieldsWriteOperation = "identity.membership.custom_fields_write";
    public const string OwnMembershipCustomFieldsWriteOperation = "identity.membership.custom_fields_write_own";
    public const string MembershipStatusOperation = "identity.membership.change_status";
    public const string MembershipAssignmentsOperation = "identity.membership.replace_role_assignments";
    public static PermissionDefinition OrganizationMembershipAdministration { get; } = new(PermissionScope.Organization, IdentityFoundationManifest.MembershipAdmin);
    public static PermissionDefinition ProfileWrite { get; } = new(PermissionScope.Tenant, IdentityFoundationManifest.ProfileWrite);
    public static PermissionDefinition OrganizationProfileWrite { get; } = new(PermissionScope.Organization, IdentityFoundationManifest.ProfileWrite);
    public static PermissionDefinition MembershipAdministration { get; } = new(PermissionScope.Tenant, IdentityFoundationManifest.MembershipAdmin);
    public static PermissionDefinition RoleAdministration { get; } = new(PermissionScope.Tenant, IdentityFoundationManifest.RoleAdmin);
    public const string FederationReadOperation = "identity.federation.read";

    // ADR-0106 § 4: the subject's own live-session data-request operations carry no key (OwnDataRequest, whose
    // operation ids share this prefix); the administrator's reuse the identity.membership.admin alternatives.
    public const string OwnDataExportCreateOperation = "identity.data_request.create_own_export";
    public const string OwnDataErasureCreateOperation = "identity.data_request.create_own_erasure";
    public const string OwnAccountClosureCreateOperation = "identity.data_request.create_own_closure";
    public const string OwnDataRequestListOperation = "identity.data_request.list_own";
    public const string OwnDataRequestReadOperation = "identity.data_request.read_own";
    public const string OwnDataRequestCancelOperation = "identity.data_request.cancel_own";
    public const string OwnDataExportRevokeOperation = "identity.data_request.revoke_own_export";
    public const string OwnDataExportTicketOperation = "identity.data_request.mint_export_ticket";
    public const string OwnDataExportChunkOperation = "identity.data_request.read_export_chunk";
    public const string OwnDataRequestReceiptOperation = "identity.data_request.read_own_receipt";
    public const string DataExportCreateOperation = "identity.data_request.create_export";
    public const string DataErasureCreateOperation = "identity.data_request.create_erasure";
    public const string DataRequestListOperation = "identity.data_request.list";
    public const string DataRequestReadOperation = "identity.data_request.read";
    public const string DataRequestCancelOperation = "identity.data_request.cancel";
    public const string DataExportRevokeOperation = "identity.data_request.revoke_export";
    public const string DataRequestReceiptOperation = "identity.data_request.read_receipt";
    // ADR-0106 § 9: the retained own-scope native entry (OwnDataRequestEntry). The same prefix and no key; each is
    // admitted only by the subject's own retained-scope witness, never by a Membership in the announced context.
    public const string RetainedDataRequestScopesOperation = "identity.data_request.list_retained_scopes";
    public const string RetainedDataRequestListOperation = "identity.data_request.list_retained";
    public const string RetainedDataExportCreateOperation = "identity.data_request.create_retained_export";
    public const string RetainedDataErasureCreateOperation = "identity.data_request.create_retained_erasure";
    public const string RetainedAccountClosureCreateOperation = "identity.data_request.create_retained_closure";
    public const string RetainedDataExportRevokeOperation = "identity.data_request.revoke_retained_export";
    public const string RetainedDataRequestCancelOperation = "identity.data_request.cancel_retained";
    public const string RetainedDataExportTicketOperation = "identity.data_request.mint_retained_export_ticket";
    public const string RetainedDataExportChunkOperation = "identity.data_request.read_retained_export_chunk";
    public const string RetainedDataRequestReceiptOperation = "identity.data_request.read_retained_receipt";
    public const string PublicSignInOperation = "identity.public_sign_in.read";
    /// <summary>Virtual Portal Public definition for the anonymous sign-in read; never a stored Role grant.</summary>
    public static PermissionDefinition PublicSignIn { get; } = new(PermissionScope.Tenant, "identity.public_sign_in.read");

    // Registry v4's federation pair is Identity-owned and assignable; registration grants it
    // to no Role. Its Step 2 operations declare requiresOrglessWrite (ADR-0099).
    public IEnumerable<PermissionRegistration> Definitions => IdentityFoundationManifest.Version1.Definitions
        .Append(FederationPermissions.Definition)
        .Select(definition => new PermissionRegistration(definition, Assignable: true))
        .Append(new PermissionRegistration(PublicSignIn, Assignable: false));

    public IEnumerable<OperationPolicy> Policies =>
    [
        OperationPolicy.ForInvitationEntry<InspectInvitationQuery>("identity.invitation.inspect"),
        OperationPolicy.ForInvitationEntry<AcceptInvitationCommand>("identity.invitation.accept"),
        OperationPolicy.ForInvitationEntry<GetOwnInvitationResultQuery>("identity.invitation.read_own_result"),
        OperationPolicy.ForTrusted<PrepareTenantInvitationEmailCommand>("identity.invitation.prepare_tenant_email", AuthorizationSourceKind.BackgroundJob),
        OperationPolicy.ForTrusted<PrepareOrganizationInvitationEmailCommand>("identity.invitation.prepare_organization_email", AuthorizationSourceKind.BackgroundJob),
        OperationPolicy.ForTrusted<FinalizeTenantInvitationEmailCommand>("identity.invitation.tenant_email_submission", AuthorizationSourceKind.BackgroundJob),
        OperationPolicy.ForTrusted<FinalizeOrganizationInvitationEmailCommand>("identity.invitation.organization_email_submission", AuthorizationSourceKind.BackgroundJob),
        OperationPolicy.ForTrusted<RunTenantDataExportCommand>("identity.data_request.fulfill_tenant_export", AuthorizationSourceKind.BackgroundJob),
        OperationPolicy.ForTrusted<RunOrganizationDataExportCommand>("identity.data_request.fulfill_organization_export", AuthorizationSourceKind.BackgroundJob),
        OperationPolicy.ForTrusted<PurgeTenantDataExportCommand>("identity.data_export.purge_tenant", AuthorizationSourceKind.BackgroundJob),
        OperationPolicy.ForTrusted<PurgeOrganizationDataExportCommand>("identity.data_export.purge_organization", AuthorizationSourceKind.BackgroundJob),
        OperationPolicy.ForTrusted<RunTenantDataErasureCommand>("identity.data_request.fulfill_tenant_erasure", AuthorizationSourceKind.BackgroundJob),
        OperationPolicy.ForTrusted<RunOrganizationDataErasureCommand>("identity.data_request.fulfill_organization_erasure", AuthorizationSourceKind.BackgroundJob),
        OperationPolicy.ForOwnDataRequest<CreateOwnDataExportCommand>(OwnDataExportCreateOperation),
        OperationPolicy.ForOwnDataRequest<CreateOwnDataErasureCommand>(OwnDataErasureCreateOperation),
        OperationPolicy.ForOwnDataRequest<CreateOwnAccountClosureCommand>(OwnAccountClosureCreateOperation),
        OperationPolicy.ForOwnDataRequest<ListOwnDataRequestsQuery>(OwnDataRequestListOperation),
        OperationPolicy.ForOwnDataRequest<GetOwnDataRequestQuery>(OwnDataRequestReadOperation),
        OperationPolicy.ForOwnDataRequest<CancelOwnDataRequestCommand>(OwnDataRequestCancelOperation),
        OperationPolicy.ForOwnDataRequest<RevokeOwnDataExportCommand>(OwnDataExportRevokeOperation),
        OperationPolicy.ForOwnDataRequest<MintOwnDataExportTicketCommand>(OwnDataExportTicketOperation),
        OperationPolicy.ForOwnDataRequest<ReadOwnDataExportChunkCommand>(OwnDataExportChunkOperation),
        OperationPolicy.ForOwnDataRequest<ReadOwnDataRequestReceiptQuery>(OwnDataRequestReceiptOperation),
        OperationPolicy.ForOwnDataRequestEntry<ListRetainedDataRequestScopesQuery>(RetainedDataRequestScopesOperation),
        OperationPolicy.ForOwnDataRequestEntry<ListRetainedDataRequestsQuery>(RetainedDataRequestListOperation),
        OperationPolicy.ForOwnDataRequestEntry<CreateRetainedDataExportCommand>(RetainedDataExportCreateOperation),
        OperationPolicy.ForOwnDataRequestEntry<CreateRetainedDataErasureCommand>(RetainedDataErasureCreateOperation),
        OperationPolicy.ForOwnDataRequestEntry<CreateRetainedAccountClosureCommand>(RetainedAccountClosureCreateOperation),
        OperationPolicy.ForOwnDataRequestEntry<RevokeRetainedDataExportCommand>(RetainedDataExportRevokeOperation),
        OperationPolicy.ForOwnDataRequestEntry<CancelRetainedDataRequestCommand>(RetainedDataRequestCancelOperation),
        OperationPolicy.ForOwnDataRequestEntry<MintRetainedDataExportTicketCommand>(RetainedDataExportTicketOperation),
        OperationPolicy.ForOwnDataRequestEntry<ReadRetainedDataExportChunkCommand>(RetainedDataExportChunkOperation),
        OperationPolicy.ForOwnDataRequestEntry<ReadRetainedDataRequestReceiptQuery>(RetainedDataRequestReceiptOperation),
        OperationPolicy.ForProtected<CreateDataExportForSubjectCommand>(DataExportCreateOperation, [new([MembershipAdministration]), new([OrganizationMembershipAdministration])]),
        OperationPolicy.ForProtected<CreateDataErasureForSubjectCommand>(DataErasureCreateOperation, [new([MembershipAdministration]), new([OrganizationMembershipAdministration])]),
        OperationPolicy.ForProtected<ListDataRequestsQuery>(DataRequestListOperation, [new([MembershipAdministration]), new([OrganizationMembershipAdministration])]),
        OperationPolicy.ForProtected<GetDataRequestQuery>(DataRequestReadOperation, [new([MembershipAdministration]), new([OrganizationMembershipAdministration])]),
        OperationPolicy.ForProtected<CancelDataRequestCommand>(DataRequestCancelOperation, [new([MembershipAdministration]), new([OrganizationMembershipAdministration])]),
        OperationPolicy.ForProtected<RevokeDataExportCommand>(DataExportRevokeOperation, [new([MembershipAdministration]), new([OrganizationMembershipAdministration])]),
        OperationPolicy.ForProtected<ReadDataRequestReceiptQuery>(DataRequestReceiptOperation, [new([MembershipAdministration]), new([OrganizationMembershipAdministration])]),
        OperationPolicy.ForProtected<ListInvitationsQuery>("identity.invitation.list", [new([MembershipAdministration]), new([OrganizationMembershipAdministration])]),
        OperationPolicy.ForProtected<GetInvitationQuery>("identity.invitation.read", [new([MembershipAdministration]), new([OrganizationMembershipAdministration])]),
        OperationPolicy.ForProtected<ListInvitableRolesQuery>("identity.invitation.list_invitable_roles", [new([MembershipAdministration]), new([OrganizationMembershipAdministration])]),
        OperationPolicy.ForProtected<CreateInvitationCommand>("identity.invitation.create", [new([MembershipAdministration]), new([OrganizationMembershipAdministration])]),
        OperationPolicy.ForProtected<RevokeInvitationCommand>("identity.invitation.revoke", [new([MembershipAdministration]), new([OrganizationMembershipAdministration])]),
        OperationPolicy.ForProtected<ReissueInvitationCommand>("identity.invitation.reissue", [new([MembershipAdministration]), new([OrganizationMembershipAdministration])]),
        OperationPolicy.ForProtected<ReadInvitationReceiptQuery>("identity.invitation.read_receipt", [new([MembershipAdministration]), new([OrganizationMembershipAdministration])]),
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
        OperationPolicy.ForProtected<GetMembershipCustomFieldsQuery>(MembershipCustomFieldsReadOperation,
            [new([MembershipAdministration], OperationReadScope.Tenant), new([OrganizationMembershipAdministration])]),
        OperationPolicy.ForOwnMembershipRead<GetOwnMembershipCustomFieldsQuery>(OwnMembershipCustomFieldsReadOperation),
        OperationPolicy.ForProtected<SetMembershipCustomFieldsCommand>(MembershipCustomFieldsWriteOperation,
            [new([MembershipAdministration, ProfileWrite]), new([MembershipAdministration, OrganizationProfileWrite]),
                new([OrganizationMembershipAdministration, ProfileWrite]), new([OrganizationMembershipAdministration, OrganizationProfileWrite])]),
        OperationPolicy.ForProtected<SetOwnMembershipCustomFieldsCommand>(OwnMembershipCustomFieldsWriteOperation,
            [new([ProfileWrite]), new([OrganizationProfileWrite])]),
        OperationPolicy.ForProtected<ChangeMembershipStatusCommand>(MembershipStatusOperation,
            [new([MembershipAdministration]), new([OrganizationMembershipAdministration])]),
        OperationPolicy.ForProtected<ReplaceMembershipRoleAssignmentsCommand>(MembershipAssignmentsOperation,
            [new([MembershipAdministration]), new([OrganizationMembershipAdministration])]),
        OperationPolicy.ForProtected<ChangeRoleGrantsCommand>("identity.role.change_grants", RoleAdministration, requiresOrglessWrite: true),
        OperationPolicy.ForProtected<AdoptRoleGrantManifestCommand>("identity.role.adopt_manifest", RoleAdministration, requiresOrglessWrite: true),
        // ADR-0099: every federation operation is Protected on Tenant identity.federation.admin and orgless.
        Federation<BeginConfigureFederationCommand>("identity.federation.configure"),
        Federation<BeginRotateFederationSecretCommand>("identity.federation.rotate_secret"),
        Federation<BeginRenameFederationCommand>("identity.federation.rename"),
        Federation<BeginFederationTestStartCommand>("identity.federation.test_start"),
        Federation<BeginDisableFederationCommand>("identity.federation.disable"),
        Federation<BeginDeleteFederationCommand>("identity.federation.delete"),
        Federation<ResumeFederationOperationCommand>("identity.federation.resume"),
        Federation<CompleteFederationOperationCommand>("identity.federation.apply"),
        Federation<EnableFederationCommand>("identity.federation.enable"),
        Federation<ActivateFederationCommand>("identity.federation.activate"),
        Federation<RecordFederationTestResultCommand>("identity.federation.test_record"),
        Federation<ReconcileFederationConnectionCommand>("identity.federation.reconcile"),
        Federation<GetFederationConnectionQuery>(FederationReadOperation),
        OperationPolicy.ForPublic<GetPublicSignInQuery>(PublicSignInOperation, PublicSignIn),
        OperationPolicy.ForTrusted<InitializeSeedRoleCommand>("identity.role.initialize", AuthorizationSourceKind.Seed),
        OperationPolicy.ForTrusted<CreateFirstSeedAdminMembershipCommand>("identity.membership.create_first_seed_admin", AuthorizationSourceKind.Seed),
        OperationPolicy.ForTrusted<CreateSeedMembershipCommand>("identity.membership.create_seed", AuthorizationSourceKind.Seed),
        OperationPolicy.ForTrusted<SetSeedMembershipProfileCommand>("identity.membership_profile.set_seed_profile", AuthorizationSourceKind.Seed),
        OperationPolicy.ForTrusted<GetRoleSeedStateQuery>("identity.role.verify_seed", AuthorizationSourceKind.Seed),
        OperationPolicy.ForTrusted<GetMembershipSeedStateQuery>("identity.membership.verify_seed", AuthorizationSourceKind.Seed),
        OperationPolicy.ForTrusted<OperatorInitializeRoleCommand>("identity.operator.initialize_role", AuthorizationSourceKind.PlatformOperator),
        OperationPolicy.ForTrusted<OperatorCreateFirstAdminCommand>("identity.operator.create_first_admin", AuthorizationSourceKind.PlatformOperator),
        OperationPolicy.ForTrusted<OperatorRecoverAdminCommand>("identity.operator.recover_admin", AuthorizationSourceKind.PlatformOperator),
    ];

    private static OperationPolicy Federation<TRequest>(string id) =>
        OperationPolicy.ForProtected<TRequest>(id, FederationPermissions.Definition, requiresOrglessWrite: true);
}
