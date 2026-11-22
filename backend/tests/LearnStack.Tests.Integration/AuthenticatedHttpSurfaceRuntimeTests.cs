using LearnStack.Api.Customization;
using LearnStack.Modules.Customization.Application.Contracts.CustomFields;
using LearnStack.Modules.Identity.Application.Contracts.CustomFields;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.Modules.Tenancy.Application.Contracts.Settings;
using System.Reflection;
using FluentAssertions;
using LearnStack.Api.Account;
using LearnStack.Api.PublicReads;
using LearnStack.Api.Identity;
using LearnStack.Api.Idempotency;
using LearnStack.Api.Pagination;
using LearnStack.Api.Tenancy;
using LearnStack.Modules.Education.Application.Contracts.PublicReads;
using LearnStack.Modules.Identity.Application.Contracts.Federation;
using LearnStack.Modules.Identity.Application.Federation;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Application.Contracts.Entry;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.Modules.Tenancy.Application.Contracts.Account;
using LearnStack.Modules.Tenancy.Application.Contracts.PublicReads;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tests.Fixtures;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>ADR-0064's real endpoint union and the request each discovered MVC action actually sends.</summary>
[Collection(TenantAuthenticationDiagnosticsTestGroup.Name)]
public sealed partial class AuthenticatedHttpSurfaceRuntimeTests
{
    /// <summary>
    /// <paramref name="Reads"/> are the requests the action sends, in order, before <paramref name="Request"/>; the census
    /// answers exactly those and stops at the action's own request. Only the ADR-0099 runner's Activate and test record
    /// read before their operation; every other action's first send is its request.
    /// </summary>
    private sealed record SurfaceAction(string Path, string Verb, Type Controller, string Action, Type Request,
        Type Response, string? Policy = null, ProtectedAdministrationOperation? Protected = null, Type[]? Reads = null);

    // This literal wire/caller inventory is independent of production classifiers and descriptors.
    private static readonly SurfaceAction[] ApprovedActions =
    [
        new("public/site", "GET", typeof(PublicSiteController), "Get", typeof(GetPublicSiteQuery), typeof(PublicSite)),
        new("public/site", "HEAD", typeof(PublicSiteController), "Head", typeof(GetPublicSiteQuery), typeof(void)),
        new("public/courses", "GET", typeof(PublicEducationController), "Catalog", typeof(GetPublicCoursesQuery), typeof(PublicCourseCatalog)),
        new("public/courses", "HEAD", typeof(PublicEducationController), "HeadCatalog", typeof(GetPublicCoursesQuery), typeof(void)),
        new("public/courses/{slug}", "GET", typeof(PublicEducationController), "Course", typeof(GetPublicCourseQuery), typeof(PublicCourseDetail)),
        new("public/courses/{slug}", "HEAD", typeof(PublicEducationController), "HeadCourse", typeof(GetPublicCourseQuery), typeof(void)),
        new("public/courses/{slug}/lessons/{lessonSlug}", "GET", typeof(PublicEducationController), "Lesson", typeof(GetPublicLessonQuery), typeof(PublicLessonDetail)),
        new("public/courses/{slug}/lessons/{lessonSlug}", "HEAD", typeof(PublicEducationController), "HeadLesson", typeof(GetPublicLessonQuery), typeof(void)),
        new("account/site", "GET", typeof(AccountController), "GetSite", typeof(GetAccountSiteQuery), typeof(AccountSite), "tenancy.account_site.read"),
        new("identity/memberships", "GET", typeof(MembershipsController), "List", typeof(ListMembershipsQuery), typeof(Page<MembershipDirectoryItem>), "identity.membership.list"),
        new("identity/session-scope/admissions", "POST", typeof(NativeSessionAdmissionController), "Admit", typeof(AdmitNativeSessionScopeCommand), typeof(NativeSessionScopeAdmission), "identity.membership.admit_session_scope"),
        new("identity/memberships/{id}", "GET", typeof(MembershipsController), "Get", typeof(GetMembershipDetailQuery), typeof(MembershipDetailResponse), "identity.membership.read"),
        new("identity/memberships/{id}/profile", "PUT", typeof(MembershipsController), "SetProfile", typeof(SetMembershipProfileCommand), typeof(AdministrationVersionReceipt), "identity.membership_profile.write", ProtectedAdministrationOperation.SetMembershipProfile),
        new("identity/memberships/{id}/role-assignments", "PUT", typeof(MembershipsController), "ReplaceRoles", typeof(ReplaceMembershipRoleAssignmentsCommand), typeof(AdministrationVersionReceipt), "identity.membership.replace_role_assignments", ProtectedAdministrationOperation.ReplaceMembershipRoleAssignments),
        new("identity/memberships/{id}/status", "PUT", typeof(MembershipsController), "ChangeStatus", typeof(ChangeMembershipStatusCommand), typeof(AdministrationVersionReceipt), "identity.membership.change_status", ProtectedAdministrationOperation.ChangeMembershipStatus),
        new("identity/organization-memberships", "GET", typeof(OrganizationMembershipsController), "List", typeof(ListOrganizationMembershipsQuery), typeof(Page<MembershipDirectoryItem>), "identity.membership.list_organization"),
        new("identity/me/profile", "GET", typeof(OwnMembershipController), "GetProfile", typeof(GetOwnMembershipProfileQuery), typeof(OwnMembershipProfileResponse), "identity.membership_profile.read_own"),
        new("identity/me/profile", "PUT", typeof(OwnMembershipController), "SetProfile", typeof(SetOwnMembershipProfileCommand), typeof(AdministrationVersionReceipt), "identity.membership_profile.write_own", ProtectedAdministrationOperation.SetOwnMembershipProfile),
        new("identity/me/contexts", "GET", typeof(OwnMembershipController), "Contexts", typeof(GetOwnMembershipContextsQuery), typeof(OwnMembershipContextPage), "identity.membership.list_own_contexts"),
        new("identity/me/capabilities", "GET", typeof(OwnMembershipController), "Capabilities", typeof(GetOwnCapabilitiesQuery), typeof(OwnCapabilitiesDto), "identity.membership.read_own_capabilities"),
        new("identity/roles", "GET", typeof(RolesController), "List", typeof(ListRolesQuery), typeof(RoleDirectoryResponse), "identity.role.list"),
        new("identity/roles/{id}", "GET", typeof(RolesController), "Get", typeof(GetRoleDetailQuery), typeof(RoleDetailResponse), "identity.role.read"),
        new("identity/roles/{id}/grants", "PUT", typeof(RolesController), "ChangeGrants", typeof(ChangeRoleGrantsCommand), typeof(AdministrationVersionReceipt), "identity.role.change_grants", ProtectedAdministrationOperation.ChangeRoleGrants),
        new("identity/roles/{id}/manifest-adoptions", "POST", typeof(RolesController), "AdoptManifest", typeof(AdoptRoleGrantManifestCommand), typeof(AdministrationVersionReceipt), "identity.role.adopt_manifest", ProtectedAdministrationOperation.AdoptRoleGrantManifest),
        new("identity/permissions", "GET", typeof(PermissionsController), "List", typeof(GetPermissionRegistryQuery), typeof(PermissionRegistryResponse), "identity.role.list_permissions"),
        new("tenancy/organizations", "GET", typeof(OrganizationsController), "List", typeof(ListOrganizationsQuery), typeof(OrganizationDirectoryResponse), "tenancy.organization.list"),
        new("tenancy/organizations/{id}", "GET", typeof(OrganizationsController), "Get", typeof(GetOrganizationDetailQuery), typeof(OrganizationResponse), "tenancy.organization.read"),
        new("tenancy/organizations", "POST", typeof(OrganizationsController), "Create", typeof(CreateManagedOrganizationCommand), typeof(AdministrationVersionReceipt), "tenancy.organization.create", ProtectedAdministrationOperation.CreateOrganization),
        new("tenancy/organizations/{id}", "PUT", typeof(OrganizationsController), "Rename", typeof(RenameOrganizationCommand), typeof(AdministrationVersionReceipt), "tenancy.organization.rename", ProtectedAdministrationOperation.RenameOrganization),
        new("tenancy/organizations/{id}/archive", "POST", typeof(OrganizationsController), "Archive", typeof(ArchiveOrganizationCommand), typeof(AdministrationVersionReceipt), "tenancy.organization.archive", ProtectedAdministrationOperation.ArchiveOrganization),
        new("tenancy/default-organization", "PUT", typeof(DefaultOrganizationController), "Select", typeof(SelectDefaultOrganizationCommand), typeof(AdministrationVersionReceipt), "tenancy.organization.select_default", ProtectedAdministrationOperation.SelectDefaultOrganization),
        new("identity/invitations", "GET", typeof(InvitationsController), "List", typeof(ListInvitationsQuery), typeof(InvitationPage), "identity.invitation.list"),
        new("identity/invitations/{id}", "GET", typeof(InvitationsController), "Get", typeof(GetInvitationQuery), typeof(InvitationResponse), "identity.invitation.read"),
        new("identity/invitations", "POST", typeof(InvitationsController), "Create", typeof(CreateInvitationCommand), typeof(AdministrationVersionReceipt), "identity.invitation.create", ProtectedAdministrationOperation.CreateInvitation),
        new("identity/invitations/{id}/revocations", "POST", typeof(InvitationsController), "Revoke", typeof(RevokeInvitationCommand), typeof(AdministrationVersionReceipt), "identity.invitation.revoke", ProtectedAdministrationOperation.RevokeInvitation),
        new("identity/invitations/{id}/reissues", "POST", typeof(InvitationsController), "Reissue", typeof(ReissueInvitationCommand), typeof(AdministrationVersionReceipt), "identity.invitation.reissue", ProtectedAdministrationOperation.ReissueInvitation),
        new("identity/invitable-roles", "GET", typeof(InvitableRolesController), "List", typeof(ListInvitableRolesQuery), typeof(LearnStack.Api.Identity.InvitableRoleDirectory), "identity.invitation.list_invitable_roles"),
        new("identity/invitation-entry/inspections", "POST", typeof(InvitationEntryController), "Inspect", typeof(InspectInvitationQuery), typeof(InvitationInspectionResponse), "identity.invitation.inspect"),
        new("identity/invitation-entry/acceptances", "POST", typeof(InvitationEntryController), "Accept", typeof(AcceptInvitationCommand), typeof(InvitationAcceptanceResponse), "identity.invitation.accept"),
        new("identity/invitation-entry/results", "POST", typeof(InvitationEntryController), "Result", typeof(GetOwnInvitationResultQuery), typeof(InvitationAcceptanceResponse), "identity.invitation.read_own_result"),
        new("tenancy/settings/invitation-policy", "GET", typeof(InvitationPolicyController), "Get", typeof(GetTenantInvitationPolicyQuery), typeof(InvitationPolicyResponse), "tenancy.setting.read_invitation_policy"),
        new("tenancy/settings/invitation-policy", "PUT", typeof(InvitationPolicyController), "Set", typeof(SetTenantInvitationLifetimeCommand), typeof(AdministrationVersionReceipt), "tenancy.setting.write_invitation_lifetime", ProtectedAdministrationOperation.SetTenantInvitationLifetime),
        new("customization/custom-field-definitions", "GET", typeof(CustomFieldDefinitionsController), "List", typeof(ListCustomFieldDefinitionsQuery), typeof(CustomFieldDefinitionPageResponse), "customization.custom_field.list"),
        new("customization/custom-field-definitions/{id}", "GET", typeof(CustomFieldDefinitionsController), "Get", typeof(GetCustomFieldDefinitionQuery), typeof(CustomFieldDefinitionResponse), "customization.custom_field.read"),
        new("customization/custom-field-definitions/{id}/revisions", "GET", typeof(CustomFieldDefinitionsController), "Revisions", typeof(ListCustomFieldDefinitionRevisionsQuery), typeof(CustomFieldDefinitionPageResponse), "customization.custom_field.list_revisions"),
        new("customization/custom-field-definitions", "POST", typeof(CustomFieldDefinitionsController), "Register", typeof(RegisterCustomFieldDefinitionCommand), typeof(CustomFieldReceiptResponse), "customization.custom_field.register", ProtectedAdministrationOperation.RegisterCustomFieldDefinition),
        new("customization/custom-field-definitions/{id}", "PUT", typeof(CustomFieldDefinitionsController), "Revise", typeof(ReviseCustomFieldDefinitionDraftCommand), typeof(CustomFieldReceiptResponse), "customization.custom_field.revise", ProtectedAdministrationOperation.ReviseCustomFieldDraft),
        new("customization/custom-field-definitions/{id}/publication", "POST", typeof(CustomFieldDefinitionsController), "Publish", typeof(PublishCustomFieldDefinitionCommand), typeof(CustomFieldReceiptResponse), "customization.custom_field.publish", ProtectedAdministrationOperation.PublishCustomFieldRevision),
        new("customization/custom-field-definitions/{id}/retirement", "POST", typeof(CustomFieldDefinitionsController), "Retire", typeof(RetireCustomFieldDefinitionCommand), typeof(CustomFieldReceiptResponse), "customization.custom_field.retire", ProtectedAdministrationOperation.RetireCustomFieldRevision),
        new("customization/custom-field-definitions/{id}/discard", "POST", typeof(CustomFieldDefinitionsController), "Discard", typeof(DiscardCustomFieldDefinitionDraftCommand), typeof(CustomFieldReceiptResponse), "customization.custom_field.discard", ProtectedAdministrationOperation.DiscardCustomFieldDraft),
        new("identity/memberships/{id}/custom-fields", "GET", typeof(MembershipCustomFieldsController), "Get", typeof(GetMembershipCustomFieldsQuery), typeof(MembershipCustomFieldsResponse), "identity.membership.custom_fields_read"),
        new("identity/me/custom-fields", "GET", typeof(MembershipCustomFieldsController), "GetOwn", typeof(GetOwnMembershipCustomFieldsQuery), typeof(MembershipCustomFieldsResponse), "identity.membership.custom_fields_read_own"),
        new("identity/memberships/{id}/custom-fields", "PATCH", typeof(MembershipCustomFieldsController), "Set", typeof(SetMembershipCustomFieldsCommand), typeof(CustomFieldReceiptResponse), "identity.membership.custom_fields_write", ProtectedAdministrationOperation.SetMemberCustomFields),
        new("identity/me/custom-fields", "PATCH", typeof(MembershipCustomFieldsController), "SetOwn", typeof(SetOwnMembershipCustomFieldsCommand), typeof(CustomFieldReceiptResponse), "identity.membership.custom_fields_write_own", ProtectedAdministrationOperation.SetOwnCustomFields),
        new("tenancy/membership-field-consents/own/scopes", "GET", typeof(ConsentSubjectController), "Scopes", typeof(ListOwnFieldConsentScopesQuery), typeof(ConsentScopePageResponse), "tenancy.membership_field_consent.scopes"),
        new("tenancy/membership-field-consents/own/history", "GET", typeof(ConsentSubjectController), "History", typeof(ListOwnMembershipFieldConsentsQuery), typeof(ConsentHistoryPageResponse), "tenancy.membership_field_consent.history"),
        new("tenancy/membership-field-consents/withdrawals", "POST", typeof(ConsentSubjectController), "Withdraw", typeof(WithdrawOwnMembershipFieldConsentCommand), typeof(ConsentWithdrawalResponse), "tenancy.membership_field_consent.withdraw", ProtectedAdministrationOperation.WithdrawOwnFieldConsent),
        new("tenancy/membership-field-consents/own", "GET", typeof(OwnFieldConsentController), "Current", typeof(GetOwnMembershipFieldConsentQuery), typeof(OwnFieldConsentCurrentResponse), "tenancy.membership_field_consent.get_own"),
        new("tenancy/membership-field-consents/grants", "POST", typeof(OwnFieldConsentController), "Grant", typeof(GrantOwnMembershipFieldConsentCommand), typeof(FieldConsentReceiptResponse), "tenancy.membership_field_consent.grant", ProtectedAdministrationOperation.GrantOwnFieldConsent),
        // ADR-0106, ADR-0107 § 4: the subject's own data requests and the administrator's status-only view.
        new("identity/me/data-requests/exports", "POST", typeof(OwnDataRequestsController), "Create", typeof(CreateOwnDataExportCommand), typeof(DataRequestAcceptedResponse), "identity.data_request.create_own_export", ProtectedAdministrationOperation.CreateOwnDataExport),
        new("identity/me/data-requests", "GET", typeof(OwnDataRequestsController), "List", typeof(ListOwnDataRequestsQuery), typeof(OwnDataRequestPage), "identity.data_request.list_own"),
        new("identity/me/data-requests/{id}", "GET", typeof(OwnDataRequestsController), "Get", typeof(GetOwnDataRequestQuery), typeof(OwnDataRequestResponse), "identity.data_request.read_own"),
        new("identity/me/data-requests/{id}/cancellations", "POST", typeof(OwnDataRequestsController), "Cancel", typeof(CancelOwnDataRequestCommand), typeof(AdministrationVersionReceipt), "identity.data_request.cancel_own", ProtectedAdministrationOperation.CancelOwnDataRequest),
        new("identity/me/data-requests/{id}/artifact/revocations", "POST", typeof(OwnDataRequestsController), "Revoke", typeof(RevokeOwnDataExportCommand), typeof(AdministrationVersionReceipt), "identity.data_request.revoke_own_export", ProtectedAdministrationOperation.RevokeOwnDataExport),
        new("identity/me/data-requests/{id}/artifact/tickets", "POST", typeof(OwnDataExportTicketsController), "MintTicket", typeof(MintOwnDataExportTicketCommand), typeof(DataExportTicketResponse), "identity.data_request.mint_export_ticket"),
        new("identity/me/data-requests/{id}/artifact/chunks", "POST", typeof(OwnDataExportChunksController), "ReadChunk", typeof(ReadOwnDataExportChunkCommand), typeof(DataExportChunkResponse), "identity.data_request.read_export_chunk"),
        new("identity/data-requests/exports", "POST", typeof(DataRequestsController), "Create", typeof(CreateDataExportForSubjectCommand), typeof(DataRequestAcceptedResponse), "identity.data_request.create_export", ProtectedAdministrationOperation.CreateDataExportForSubject),
        new("identity/data-requests", "GET", typeof(DataRequestsController), "List", typeof(ListDataRequestsQuery), typeof(DataRequestPage), "identity.data_request.list"),
        new("identity/data-requests/{id}", "GET", typeof(DataRequestsController), "Get", typeof(GetDataRequestQuery), typeof(DataRequestResponse), "identity.data_request.read"),
        new("identity/data-requests/{id}/cancellations", "POST", typeof(DataRequestsController), "Cancel", typeof(CancelDataRequestCommand), typeof(AdministrationVersionReceipt), "identity.data_request.cancel", ProtectedAdministrationOperation.CancelDataRequest),
        new("identity/data-requests/{id}/artifact/revocations", "POST", typeof(DataRequestsController), "Revoke", typeof(RevokeDataExportCommand), typeof(AdministrationVersionReceipt), "identity.data_request.revoke_export", ProtectedAdministrationOperation.RevokeDataExport),
        new("public/sign-in", "GET", typeof(PublicSignInController), "Get", typeof(GetPublicSignInQuery), typeof(PublicSignIn)),
        new("public/sign-in", "HEAD", typeof(PublicSignInController), "Head", typeof(GetPublicSignInQuery), typeof(void)),
        // ADR-0099: the federation controller holds only FederationOperationRunner; the census observes the runner's sends.
        new("identity/federation", "GET", typeof(FederationController), "Get", typeof(GetFederationConnectionQuery), typeof(FederationConnectionResponse), "identity.federation.read"),
        new("identity/federation/configuration", "PUT", typeof(FederationController), "Configure", typeof(BeginConfigureFederationCommand), typeof(FederationOperationResponse), "identity.federation.configure"),
        new("identity/federation/secret-rotations", "POST", typeof(FederationController), "RotateSecret", typeof(BeginRotateFederationSecretCommand), typeof(FederationOperationResponse), "identity.federation.rotate_secret"),
        new("identity/federation/display-name", "PUT", typeof(FederationController), "Rename", typeof(BeginRenameFederationCommand), typeof(FederationOperationResponse), "identity.federation.rename"),
        new("identity/federation/test-starts", "POST", typeof(FederationController), "StartTest", typeof(BeginFederationTestStartCommand), typeof(FederationOperationResponse), "identity.federation.test_start"),
        new("identity/federation/test-results", "POST", typeof(FederationController), "RecordTestResult", typeof(RecordFederationTestResultCommand), typeof(FederationOperationResponse), "identity.federation.test_record", Reads: [typeof(GetFederationConnectionQuery)]),
        new("identity/federation/activation", "POST", typeof(FederationController), "Activate", typeof(ActivateFederationCommand), typeof(FederationOperationResponse), "identity.federation.activate", Reads: [typeof(GetFederationConnectionQuery)]),
        new("identity/federation/disablement", "POST", typeof(FederationController), "Disable", typeof(BeginDisableFederationCommand), typeof(FederationOperationResponse), "identity.federation.disable"),
        new("identity/federation/enablement", "POST", typeof(FederationController), "Enable", typeof(EnableFederationCommand), typeof(FederationOperationResponse), "identity.federation.enable"),
        new("identity/federation/deletion", "POST", typeof(FederationController), "Delete", typeof(BeginDeleteFederationCommand), typeof(FederationOperationResponse), "identity.federation.delete"),
        new("identity/federation/resumption", "POST", typeof(FederationController), "Resume", typeof(ResumeFederationOperationCommand), typeof(FederationOperationResponse), "identity.federation.resume"),
    ];
    private static readonly Type[] ContextControllers =
        [typeof(MembershipsController), typeof(OwnMembershipController), typeof(RolesController), typeof(OrganizationsController), typeof(DefaultOrganizationController), typeof(InvitationsController), typeof(InvitationPolicyController), typeof(MembershipCustomFieldsController), typeof(ConsentSubjectController), typeof(OwnFieldConsentController),
            typeof(OwnDataRequestsController), typeof(DataRequestsController)];
    private const string CensusTarget = "018f4d40-0000-7000-8000-000000000011";

    private static SurfaceAction Approved(ControllerActionDescriptor action) => ApprovedActions.Single(expected =>
        expected.Controller == action.ControllerTypeInfo.AsType() && expected.Action == action.MethodInfo.Name);

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Production_endpoint_union_has_exact_patterns_methods_handlers_and_requests(string environment)
    {
        await using var factory = new SurfaceFactory(environment);
        using var client = factory.CreateClient();
        AssertGlobalFilterControls(factory.Services.GetRequiredService<IOptions<MvcOptions>>().Value);
        AssertFilterProviders(factory.Services.GetServices<IFilterProvider>());
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;
        endpoints.Should().OnlyContain(endpoint => endpoint is RouteEndpoint,
            "non-route endpoints cannot disappear from the closed production census");
        var actual = new List<string>();
        foreach (var endpoint in endpoints.Cast<RouteEndpoint>())
        {
            var action = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>();
            var method = action?.MethodInfo ?? endpoint.Metadata.GetMetadata<MethodInfo>();
            method.Should().NotBeNull("every admitted endpoint has an explicitly enumerated handler");
            Type? request = null;
            if (action is not null)
            {
                AssertActionFilterControls(action);
                AssertEndpointAuthorizationControls(action, endpoint.Metadata);
                request = await DispatchedRequest(action, endpoint, factory.Services);
            }
            actual.Add((endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/') + "|"
                + string.Join(",", endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []) + "|"
                + method!.DeclaringType!.FullName + "." + method.Name + "|" + (request?.FullName ?? "infrastructure"));
        }
        AssertUnion(actual);
        Action missing = () => AssertUnion(actual.Skip(1));
        missing.Should().Throw<Xunit.Sdk.XunitException>("an incomplete data source is not a clean census");
        Action extra = () => AssertUnion(actual.Append("extra|GET|Hidden.Handler|infrastructure"));
        extra.Should().Throw<Xunit.Sdk.XunitException>("an added runtime endpoint must fail the same union assertion");
        Action wrongRequest = () => AssertUnion(actual.Select(entry => entry.Replace(typeof(GetAccountSiteQuery).FullName!,
            typeof(GetPublicSiteQuery).FullName!, StringComparison.Ordinal)));
        wrongRequest.Should().Throw<Xunit.Sdk.XunitException>("an approved action may not dispatch another approved query");
        Action wrongAdmissionRequest = () => AssertUnion(actual.Select(entry => entry.Replace(typeof(AdmitNativeSessionScopeCommand).FullName!,
            typeof(ListMembershipsQuery).FullName!, StringComparison.Ordinal)));
        wrongAdmissionRequest.Should().Throw<Xunit.Sdk.XunitException>("native admission cannot substitute a Membership directory request");
        Action wrongAdmissionMethod = () => AssertUnion(actual.Select(entry => entry.Replace("identity/session-scope/admissions|POST|",
            "identity/session-scope/admissions|GET|", StringComparison.Ordinal)));
        wrongAdmissionMethod.Should().Throw<Xunit.Sdk.XunitException>("native admission remains the exact POST route");
        Action readOnlyActivation = () => AssertUnion(actual.Select(entry => entry.Replace(typeof(ActivateFederationCommand).FullName!,
            typeof(GetFederationConnectionQuery).FullName!, StringComparison.Ordinal)));
        readOnlyActivation.Should().Throw<Xunit.Sdk.XunitException>("an Activate whose runner stops at its detail read never reached its operation");
        Action anonymousSignIn = () => AssertUnion(actual.Where(entry => !entry.StartsWith("api/v1/public/sign-in|", StringComparison.Ordinal)));
        anonymousSignIn.Should().Throw<Xunit.Sdk.XunitException>("the anonymous sign-in GET and HEAD belong to the closed union");
        AssertFederationPolicyControls(endpoints.Cast<RouteEndpoint>().ToArray());

        await using var dirtyFactory = new SurfaceFactory(environment, addFilterProvider: true);
        using var dirtyClient = dirtyFactory.CreateClient();
        AssertGlobalFilters(dirtyFactory.Services.GetRequiredService<IOptions<MvcOptions>>().Value.Filters);
        var dirtyProviders = dirtyFactory.Services.GetServices<IFilterProvider>().ToArray();
        Action providerInventory = () => AssertFilterProviders(dirtyProviders);
        providerInventory.Should().Throw<Xunit.Sdk.XunitException>(
            "a real DI provider registration can replace effective filters while MvcOptions.Filters stays unchanged");
        var filter = new AllowAnonymousFilter();
        var context = new FilterProviderContext(new ActionContext(),
            [new FilterItem(new FilterDescriptor(filter, 0), filter)]);
        dirtyProviders.OfType<UnexpectedFilterProvider>().Single().OnProvidersExecuting(context);
        context.Results[0].Filter.Should().BeOfType<UnexpectedAuthorizationFilter>();
        AssertFilterProviders(factory.Services.GetServices<IFilterProvider>());
    }

    /// <summary>
    /// Dirty controls: every federation endpoint without its policy, or keeping its policy with <c>AllowAnonymous</c> added
    /// (reported by the anonymous-bypass check itself), fails the same assertion the census applies; a planted Activate descriptor built without its <c>[Authorize]</c> is reported by it, and an
    /// inventory that gains or loses a federation action fails.
    /// </summary>
    private static void AssertFederationPolicyControls(RouteEndpoint[] endpoints)
    {
        var actions = endpoints.Select(endpoint => endpoint.Metadata.GetMetadata<ControllerActionDescriptor>())
            .OfType<ControllerActionDescriptor>().ToArray();
        var federation = endpoints.Where(endpoint =>
            endpoint.Metadata.GetMetadata<ControllerActionDescriptor>()?.ControllerTypeInfo.AsType() == typeof(FederationController)).ToArray();
        federation.Should().HaveCount(11, "ADR-0099 Step 2 routes eleven federation actions, each through the runner");
        foreach (var endpoint in federation)
        {
            var action = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>()!;
            var unprotected = endpoint.Metadata.Where(item => item is not IAuthorizeData).ToArray();
            Action missing = () => AssertEndpointAuthorization(action, unprotected);
            missing.Should().Throw<Xunit.Sdk.XunitException>("a federation action without its live operation policy is not protected");
            // The policy stays and AllowAnonymous is added: only the anonymous-bypass check can report this one.
            Action anonymous = () => AssertEndpointAuthorization(action, endpoint.Metadata.Append(new AllowAnonymousAttribute()));
            anonymous.Should().Throw<Xunit.Sdk.XunitException>("a federation action cannot be reclassified as a public read")
                .WithMessage("*authenticated endpoint metadata must never bypass authorization*");
            AssertEndpointAuthorization(action, endpoint.Metadata);
        }
        var activate = actions.Single(action => action.ControllerTypeInfo.AsType() == typeof(FederationController)
            && action.MethodInfo.Name == nameof(FederationController.Activate));
        var planted = new ControllerActionDescriptor
        {
            ControllerTypeInfo = activate.ControllerTypeInfo,
            MethodInfo = activate.MethodInfo,
            ControllerName = activate.ControllerName,
            ActionName = activate.ActionName,
            FilterDescriptors = activate.FilterDescriptors,
            EndpointMetadata = [.. activate.EndpointMetadata.Where(item => item is not IAuthorizeData)],
        };
        Action plantedPolicy = () => AssertEndpointAuthorization(planted, planted.EndpointMetadata);
        plantedPolicy.Should().Throw<Xunit.Sdk.XunitException>("the census reports a federation descriptor that lacks its policy")
            .WithMessage("*authorizations to contain a single item, but the collection is empty*");
        Action extra = () => AssertActionInventory(actions.Append(planted));
        extra.Should().Throw<Xunit.Sdk.XunitException>("a second federation Activate descriptor is not the approved inventory");
        Action dropped = () => AssertActionInventory(actions.Where(action => !ReferenceEquals(action, activate)));
        dropped.Should().Throw<Xunit.Sdk.XunitException>("a missing federation action is not the approved inventory");
        AssertActionInventory(actions);
    }

    private static void AssertEndpointAuthorizationControls(ControllerActionDescriptor action, EndpointMetadataCollection metadata)
    {
        AssertEndpointAuthorization(action, metadata);
        Action extra = () => AssertEndpointAuthorization(action, metadata.Append(new UnexpectedAnonymousAttribute()));
        extra.Should().Throw<Xunit.Sdk.XunitException>("custom IAllowAnonymous metadata cannot augment any approved MVC endpoint");
        if (Approved(action).Policy is null)
        {
            Action substitute = () => AssertEndpointAuthorization(action,
                metadata.Where(item => item is not IAllowAnonymous).Append(new UnexpectedAnonymousAttribute()));
            substitute.Should().Throw<Xunit.Sdk.XunitException>("a public endpoint requires the exact standard AllowAnonymousAttribute");
        }
        else
        {
            var authorization = metadata.OfType<IAuthorizeData>().Single();
            foreach (var policy in new[] { null, "LearnStackOperation:unregistered.read" })
            {
                var changed = new AuthorizeAttribute { AuthenticationSchemes = authorization.AuthenticationSchemes, Policy = policy };
                Action substitute = () => AssertEndpointAuthorization(action,
                    metadata.Where(item => !ReferenceEquals(item, authorization)).Append(changed));
                substitute.Should().Throw<Xunit.Sdk.XunitException>("the exact named live operation policy is required on the built endpoint");
            }
        }
        AssertEndpointAuthorization(action, metadata);
    }

    private static void AssertEndpointAuthorization(ControllerActionDescriptor action, IEnumerable<object> metadata)
    {
        var items = metadata.ToArray();
        var anonymous = items.OfType<IAllowAnonymous>().ToArray();
        var authorizations = items.OfType<IAuthorizeData>().ToArray();
        items.OfType<AuthorizationPolicy>().Should().BeEmpty("explicit policies are a separate framework authorization channel");
        items.OfType<IAuthorizationRequirementData>().Should().BeEmpty("requirement metadata must not augment the approved authorization contract");
        var expected = Approved(action);
        if (expected.Policy is not null)
        {
            anonymous.Should().BeEmpty("authenticated endpoint metadata must never bypass authorization");
            var authorization = authorizations.Should().ContainSingle().Which.Should().BeOfType<AuthorizeAttribute>().Subject;
            authorization.AuthenticationSchemes.Should().Be("TenantBearer");
            authorization.Roles.Should().BeNull();
            authorization.Policy.Should().Be("LearnStackOperation:" + expected.Policy);
        }
        else
        {
            authorizations.Should().BeEmpty("public endpoints have no authorization metadata");
            anonymous.Should().ContainSingle().Which.Should().BeOfType<AllowAnonymousAttribute>();
        }
        var keyed = items.OfType<IdempotentAttribute>().ToArray();
        var declarations = action.EndpointMetadata?.OfType<IdempotentAttribute>().ToArray() ?? [];
        var filters = action.FilterDescriptors.Where(descriptor => descriptor.Filter is IdempotentAttribute).ToArray();
        if (expected.Protected is { } operation)
        {
            var declaration = declarations.Should().ContainSingle().Which;
            declaration.ProtectedOperation.Should().Be(operation);
            filters.Should().ContainSingle().Which.Filter.Should().BeSameAs(declaration);
            keyed.Should().HaveCount(2, "MVC projects one action filter through both metadata sources");
            keyed.Should().OnlyContain(value => ReferenceEquals(value, declaration));
        }
        else
        {
            keyed.Should().BeEmpty();
            declarations.Should().BeEmpty();
            filters.Should().BeEmpty();
        }
    }

    private static void AssertActionInventory(IEnumerable<ControllerActionDescriptor> actions)
    {
        var actual = actions.ToArray();
        actual.Should().HaveCount(84);
        actual.Select(action => action.ControllerTypeInfo.FullName + "." + action.MethodInfo.Name).Order(StringComparer.Ordinal)
            .Should().Equal(ApprovedActions.Select(expected => expected.Controller.FullName + "." + expected.Action).Order(StringComparer.Ordinal),
                "every convention must reach exactly the approved controller/action multiset");
    }

    private static void AssertFilterProviders(IEnumerable<IFilterProvider> providers) =>
        providers.Select(provider => (Assembly: provider.GetType().Assembly.GetName().Name,
            Type: provider.GetType().FullName, provider.Order)).Should().Equal(
            [("Microsoft.AspNetCore.Mvc.Core", "Microsoft.AspNetCore.Mvc.Filters.DefaultFilterProvider", -1000)],
            "Fix: preserve the exact framework MVC filter provider inventory and order");

    private static void AssertGlobalFilterControls(MvcOptions options)
    {
        AssertGlobalFilters(options.Filters);
        var original = options.Filters.ToArray();
        foreach (var mutate in new Action<FilterCollection>[]
        {
            filters => filters.Add(new AllowAnonymousFilter()),
            filters => filters.Add(new UnexpectedAuthorizationFilter()),
            filters => filters.RemoveAt(0),
            filters => filters.RemoveAt(filters.Count - 1),
            filters => filters.Add(original[0]),
            filters => filters.Add(new TypeFilterAttribute(typeof(UnexpectedAuthorizationFilter))),
        })
        {
            try
            {
                mutate(options.Filters);
                Action assert = () => AssertGlobalFilters(options.Filters);
                assert.Should().Throw<Xunit.Sdk.XunitException>("the actual global MVC filters must not change outside the closed surface");
            }
            finally
            {
                options.Filters.Clear();
                foreach (var filter in original) options.Filters.Add(filter);
            }
        }
        var normalizer = options.Filters.OfType<TypeFilterAttribute>().Single();
        var order = normalizer.Order;
        normalizer.Order = 100;
        try
        {
            Action assert = () => AssertGlobalFilters(options.Filters);
            assert.Should().Throw<Xunit.Sdk.XunitException>("filter factory order is part of the real composition");
        }
        finally { normalizer.Order = order; }
        AssertGlobalFilters(options.Filters);
    }

    private static void AssertGlobalFilters(IEnumerable<IFilterMetadata> filters)
    {
        var actual = filters.Select(filter => (Kind: filter.GetType().FullName,
            Implementation: filter is TypeFilterAttribute factory ? factory.ImplementationType.FullName : null,
            Order: filter is IOrderedFilter ordered ? ordered.Order : 0,
            Reusable: filter is IFilterFactory provider && provider.IsReusable,
            Arguments: filter is TypeFilterAttribute typed ? typed.Arguments?.Length ?? 0 : 0)).ToArray();
        actual.Should().Equal(
        [
            ("Microsoft.AspNetCore.Mvc.ModelBinding.UnsupportedContentTypeFilter", null, -3000, false, 0),
            ("Microsoft.AspNetCore.Mvc.TypeFilterAttribute", "LearnStack.Api.Common.ProblemDetailsNormalizationFilter", 0, false, 0),
        ], "Fix: preserve the exact framework and application global MVC filters; authorization cannot change outside the enumerated endpoint contract");
    }

    private sealed class UnexpectedAuthorizationFilter : IAuthorizationFilter
    {
        public void OnAuthorization(AuthorizationFilterContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            context.Result = new UnauthorizedResult();
        }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    private sealed class UnexpectedAnonymousAttribute : Attribute, IAllowAnonymous;

    private sealed class UnexpectedFilterProvider : IFilterProvider
    {
        public int Order => int.MaxValue;

        public void OnProvidersExecuting(FilterProviderContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            context.Results[0].Filter = new UnexpectedAuthorizationFilter();
        }

        public void OnProvidersExecuted(FilterProviderContext context) => ArgumentNullException.ThrowIfNull(context);
    }

    private static async Task<Type> DispatchedRequest(ControllerActionDescriptor action, RouteEndpoint endpoint, IServiceProvider services)
    {
        var expected = Approved(action);
        var sender = DispatchProxy.Create<ISender, RequestObservation>();
        var observation = (RequestObservation)sender;
        var reads = expected.Reads ?? [];
        observation.Answer = request => observation.Requests.Count <= reads.Length
            && request.GetType() == reads[observation.Requests.Count - 1] ? CensusRead(request) : null;
        var controllerType = action.ControllerTypeInfo.AsType();
        // The federation controller holds only the runner (ADR-0099); the real runner sends through the observed sender.
        object[] constructor = controllerType == typeof(FederationController) ? [CensusRunner(sender, services)]
            : controllerType == typeof(OwnMembershipController)
            ? [sender, new CensusTenantContext(), services.GetRequiredService<LearnStack.Api.Authentication.NativeEntryOriginOptions>()]
            : controllerType == typeof(CustomFieldDefinitionsController) ? [sender, new CensusTenantContext(), services.GetRequiredService<IGuidFactory>()]
            : ContextControllers.Contains(controllerType) ? [sender, new CensusTenantContext()] : [sender];
        var controller = (ControllerBase)Activator.CreateInstance(controllerType, constructor)!;
        var context = new DefaultHttpContext();
        context.SetEndpoint(endpoint);
        if (controllerType == typeof(InvitationEntryController))
        {
            object request = expected.Action switch
            {
                "Inspect" => new InspectInvitationQuery(Guid.Parse(CensusTarget), "census-code"),
                "Accept" => new AcceptInvitationCommand(Guid.Parse(CensusTarget), "census-code", 7),
                "Result" => new GetOwnInvitationResultQuery(Guid.Parse(CensusTarget), "census-code"),
                _ => throw new InvalidOperationException("Unclassified invitation entry action"),
            };
            context.Features.Set(new LearnStack.Api.Authentication.InvitationEntryRequest(request));
        }
        if (controllerType == typeof(ConsentSubjectController))
        {
            object request = expected.Action switch
            {
                "Scopes" => new ListOwnFieldConsentScopesQuery(null, 20),
                "History" => new ListOwnMembershipFieldConsentsQuery(Guid.Parse(CensusTarget), null, 20),
                "Withdraw" => new WithdrawOwnMembershipFieldConsentCommand(Guid.Parse(CensusTarget), 1, Guid.Parse(CensusTarget), true),
                _ => throw new InvalidOperationException("Unclassified consent entry action"),
            };
            context.Features.Set(new LearnStack.Api.Authentication.ConsentSubjectRequest(request));
        }
        context.Features.Set(HostClassification.Platform("census.invalid"));
        context.Request.Method = expected.Verb;
        context.Request.Path = "/api/v1/" + expected.Path.Replace("{id}", CensusTarget, StringComparison.Ordinal);
        if (controllerType == typeof(OwnFieldConsentController) && expected.Action == "Current")
            context.Request.QueryString = new QueryString("?membershipId=" + CensusTarget + "&definitionId=" + CensusTarget + "&definitionVersion=1");
        var body = expected.Protected is { } operation ? CensusBody(operation)
            : controllerType == typeof(NativeSessionAdmissionController) ? "{}"
            : controllerType == typeof(FederationController) ? FederationCensusBody(expected.Action)
            : controllerType == typeof(OwnDataExportTicketsController) ? "{}"
            : controllerType == typeof(OwnDataExportChunksController)
                ? "{\"ticket\":\"" + new string('A', 43) + "\",\"index\":0}" : string.Empty;
        using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body));
        context.Request.Body = input;
        context.Request.ContentLength = input.Length;
        if (body.Length != 0) context.Request.ContentType = "application/json";
        if (expected.Protected is not null)
        {
            // This census stops at Send: valid closed input and the actual built endpoint
            // let it observe the typed carrier without claiming a live authorization proof.
            context.Request.Headers["Idempotency-Key"] = "census-command-key";
            if (expected.Protected is not (ProtectedAdministrationOperation.CreateOrganization or ProtectedAdministrationOperation.CreateInvitation
                or ProtectedAdministrationOperation.RegisterCustomFieldDefinition or ProtectedAdministrationOperation.GrantOwnFieldConsent
                or ProtectedAdministrationOperation.WithdrawOwnFieldConsent or ProtectedAdministrationOperation.CreateOwnDataExport
                or ProtectedAdministrationOperation.CreateDataExportForSubject)) context.Request.Headers.IfMatch = "\"7\"";
        }
        // Federation mutations carry no Idempotency-Key; the connection revision converges them. Configure without
        // If-Match is the first configuration, the strictest closed input.
        if (controllerType == typeof(FederationController) && expected.Action is not ("Get" or "Configure"))
            context.Request.Headers.IfMatch = "\"7\"";
        // ADR-0099 Amendment 6 (S-B): the test record admits only a credential carrying the test-record scope.
        if (controllerType == typeof(FederationController) && expected.Action == "RecordTestResult")
            context.Features.Set(new LearnStack.Api.Authentication.TenantCredentialFeature
            {
                VerifiedSubject = new LearnStack.Api.Authentication.VerifiedProviderSubject(
                    "https://issuer.example/realms/learnstack", "census-subject", null, null, null, FederationTestRecord: true),
            });
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        var arguments = action.MethodInfo.GetParameters().Select(parameter => parameter.ParameterType switch
        {
            var type when type == typeof(CancellationToken) => (object)CancellationToken.None,
            var type when type == typeof(CursorPaginationRequest) => new CursorPaginationRequest(),
            var type when type == typeof(string) => parameter.Name == "id" ? CensusTarget : "census-slug",
            _ => throw new InvalidOperationException("Unclassified action parameter"),
        }).ToArray();
        var execution = (Task)action.MethodInfo.Invoke(controller, arguments)!;
        Func<Task> invoke = () => execution;
        await invoke.Should().ThrowAsync<DispatchObservedException>();
        observation.Requests.Should().HaveCount(reads.Length + 1, "each discovered production action sends its exact request once, after its declared reads");
        observation.Requests.Take(reads.Length).Should().Equal(reads, "only the declared reads precede the action's own request");
        return observation.Requests[^1];
    }

    /// <summary>The real runner with census collaborators: entitled, an empty broker store and admitted example egress.</summary>
    private static FederationOperationRunner CensusRunner(ISender sender, IServiceProvider services) => new(sender,
        new InMemoryFederationProvider(), new InMemoryFederationEgressFetcher(), new CensusFeatureFlags(), new CensusTenantContext(),
        services.GetRequiredService<IGuidFactory>(), services.GetRequiredService<LearnStack.SharedKernel.Time.IClock>(),
        FederationOperationBudgets.Default);

    /// <summary>
    /// The census's answer to a declared read: the connection at revision 7, whose desired digest differs from the empty
    /// broker's, so Activate skips the endpoint re-check and the runner reaches the operation's own send.
    /// </summary>
    private static Task<LearnStack.SharedKernel.Results.Result<FederationConnectionRead>> CensusRead(object request)
    {
        request.Should().BeOfType<GetFederationConnectionQuery>("the only declared read is the federation detail query");
        var connection = Guid.Parse(CensusTarget);
        var view = new FederationConnectionView(connection, LearnStack.SharedKernel.Identity.FederationBrokerProtocol.Oidc,
            LearnStack.SharedKernel.Identity.FederationBrokerOwner.AliasPrefix + connection.ToString("N"), "Census",
            InMemoryFederationEgressFetcher.Issuer, FederationConnectionStatus.TestPassed, FederationSource.DiscoveryUrl, "learnstack", 7,
            "census-configuration", "census-desired", null, null, null, 7);
        return Task.FromResult(LearnStack.SharedKernel.Results.Result.Ok(new FederationConnectionRead(view)));
    }

    private static string FederationCensusBody(string action) => action switch
    {
        "Get" => string.Empty,
        "Configure" => System.Text.Json.JsonSerializer.Serialize(new
        {
            protocol = "oidc",
            displayName = "Census",
            source = "discovery_url",
            documentUrl = InMemoryFederationEgressFetcher.Discovery.AbsoluteUri,
            issuerOrEntityId = (string?)null,
            authorizationEndpoint = (string?)null,
            tokenEndpoint = (string?)null,
            jwksEndpoint = (string?)null,
            userInfoEndpoint = (string?)null,
            singleSignOnEndpoint = (string?)null,
            signingCertificates = Array.Empty<string>(),
            clientId = "learnstack",
            clientAuthentication = "client_secret_basic",
            clientSecret = "census-s3cret-value",
        }),
        "RotateSecret" => "{\"clientSecret\":\"census-s3cret-value\"}",
        "Rename" => "{\"displayName\":\"Census\"}",
        "RecordTestResult" => "{\"testAttemptId\":\"" + CensusTarget + "\",\"passed\":true}",
        "StartTest" or "Activate" or "Disable" or "Enable" or "Delete" or "Resume" => "{}",
        _ => throw new InvalidOperationException("Unclassified federation census input"),
    };

    private sealed class CensusFeatureFlags : LearnStack.SharedKernel.Entitlements.IFeatureFlags
    {
        public Task<bool> IsEnabledAsync(LearnStack.SharedKernel.Entitlements.FeatureKey key, CancellationToken ct = default) => Task.FromResult(true);
        public Task<long> GetLimitAsync(LearnStack.SharedKernel.Entitlements.LimitKey key, CancellationToken ct = default) =>
            Task.FromResult(LearnStack.SharedKernel.Entitlements.LimitKeys.Unlimited);
    }

    private static string CensusBody(ProtectedAdministrationOperation operation) => operation switch
    {
        ProtectedAdministrationOperation.SetMembershipProfile or ProtectedAdministrationOperation.SetOwnMembershipProfile => "{\"displayName\":null,\"preferredLocale\":null}",
        ProtectedAdministrationOperation.ChangeMembershipStatus => "{\"status\":\"Suspended\"}",
        ProtectedAdministrationOperation.ReplaceMembershipRoleAssignments => "{\"roleIds\":[]}",
        ProtectedAdministrationOperation.ChangeRoleGrants => "{\"grants\":[]}",
        ProtectedAdministrationOperation.AdoptRoleGrantManifest => "{\"manifestVersion\":2,\"manifestFingerprint\":\"" + new string('a', 64) + "\"}",
        ProtectedAdministrationOperation.CreateOrganization => "{\"slug\":\"census-branch\",\"displayName\":\"Census\"}",
        ProtectedAdministrationOperation.RenameOrganization => "{\"displayName\":\"Census\"}",
        ProtectedAdministrationOperation.ArchiveOrganization => "{}",
        ProtectedAdministrationOperation.SelectDefaultOrganization => "{\"organizationId\":\"" + CensusTarget + "\"}",
        ProtectedAdministrationOperation.CreateInvitation => "{\"email\":\"census@example.invalid\",\"roleId\":\"" + CensusTarget + "\",\"roleVersion\":\"7\"}",
        ProtectedAdministrationOperation.RevokeInvitation or ProtectedAdministrationOperation.ReissueInvitation => "{}",
        ProtectedAdministrationOperation.SetTenantInvitationLifetime => "{\"days\":14}",
        ProtectedAdministrationOperation.RegisterCustomFieldDefinition or ProtectedAdministrationOperation.ReviseCustomFieldDraft => """
            {"targetEntity":"Membership","key":"census-field","schemaVersion":1,"schema":{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{"value":{"type":"boolean"}},"required":["value"],"additionalProperties":false},"labels":{"en":"Census"},"piiCategory":"None","isRequired":false,"sort":0,"purposeKey":null,"notices":null,"formLabels":null}
            """,
        ProtectedAdministrationOperation.PublishCustomFieldRevision => "{\"expectedIncumbentId\":null,\"expectedIncumbentVersion\":null}",
        ProtectedAdministrationOperation.RetireCustomFieldRevision or ProtectedAdministrationOperation.DiscardCustomFieldDraft => "{}",
        ProtectedAdministrationOperation.SetMemberCustomFields or ProtectedAdministrationOperation.SetOwnCustomFields => "{\"set\":[],\"remove\":[]}",
        ProtectedAdministrationOperation.GrantOwnFieldConsent => "{\"definitionId\":\"" + CensusTarget + "\",\"definitionVersion\":1,\"noticeLocale\":\"en\",\"expectedNoticeHash\":\"" + new string('a', 64) + "\",\"expectedSequence\":0,\"expectedEvidenceId\":null,\"confirmed\":true}",
        ProtectedAdministrationOperation.WithdrawOwnFieldConsent => "{}",
        ProtectedAdministrationOperation.CreateOwnDataExport or ProtectedAdministrationOperation.CancelOwnDataRequest
            or ProtectedAdministrationOperation.RevokeOwnDataExport or ProtectedAdministrationOperation.CancelDataRequest
            or ProtectedAdministrationOperation.RevokeDataExport => "{}",
        ProtectedAdministrationOperation.CreateDataExportForSubject => "{\"membershipId\":\"" + CensusTarget + "\"}",
        _ => throw new InvalidOperationException("Unclassified protected census input"),
    };

    private sealed class CensusTenantContext : ITenantContext
    {
        public bool IsResolved => true;
        public TenantId TenantId { get; } = LearnStack.SharedKernel.Identifiers.TenantId.From(Guid.Parse("018f4d40-0000-7000-8000-000000000022"));
        public OrganizationId? OrganizationId => null;
        public UserId? UserId { get; } = LearnStack.SharedKernel.Identifiers.UserId.From(Guid.Parse("018f4d40-0000-7000-8000-000000000033"));
        public string? CorrelationId => null;
        public string? ModuleName => null;
    }

    private static void AssertUnion(IEnumerable<string> actual)
    {
        ApprovedActions.Should().HaveCount(84);
        ApprovedActions.Count(expected => expected.Protected is not null).Should().Be(29);
        ApprovedActions.Count(expected => expected.Controller == typeof(FederationController)).Should().Be(11);
        ApprovedActions.Count(expected => expected.Policy is null).Should().Be(10, "eight public site and course reads plus the sign-in GET and HEAD");
        ApprovedActions.Where(expected => expected.Reads is not null).Select(expected => expected.Action)
            .Should().BeEquivalentTo(["RecordTestResult", "Activate"], "only the runner's observe-first operations read before they send");
        var mvc = ApprovedActions.Select(expected => Mvc(expected.Path, expected.Verb, expected.Controller, expected.Action, expected.Request));
        var infrastructure = new[]
        {
            "healthz|GET|Program+<>c.<<Main>$>b__0_1|infrastructure",
            "openapi/{documentName:regex(^(v1)$)}.json|GET|Microsoft.AspNetCore.Builder.OpenApiEndpointRouteBuilderExtensions+<>c__DisplayClass0_0.<MapOpenApi>b__0|infrastructure",
            "docs/{documentName?}|GET|Scalar.AspNetCore.ScalarEndpointRouteBuilderExtensions+<>c__DisplayClass9_0.<MapScalarApiReference>b__0|infrastructure",
            "docs/scalar.js|GET|Scalar.AspNetCore.ScalarEndpointRouteBuilderExtensions+<>c.<MapStaticAssetsEndpoints>b__10_0|infrastructure",
            "docs/scalar.aspnetcore.js|GET|Scalar.AspNetCore.ScalarEndpointRouteBuilderExtensions+<>c.<MapStaticAssetsEndpoints>b__10_1|infrastructure",
            "docs/favicon.svg|GET|Scalar.AspNetCore.ScalarEndpointRouteBuilderExtensions+<>c.<MapStaticAssetsEndpoints>b__10_2|infrastructure",
        };
        actual.Order(StringComparer.Ordinal).Should().Equal(mvc.Concat(infrastructure).Order(StringComparer.Ordinal),
            "the entire production EndpointDataSource equals the approved MVC plus infrastructure multiset");
    }

    private static string Mvc(string path, string verb, Type controller, string action, Type request) =>
        "api/v1/" + path + "|" + verb + "|" + controller.FullName + "." + action + "|" + request.FullName;

    public class RequestObservation : DispatchProxy
    {
        internal List<Type> Requests { get; } = [];
        /// <summary>Answers a declared read; null stops the dispatch at the observed request.</summary>
        internal Func<object, object?>? Answer { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            targetMethod!.Name.Should().Be("Send");
            var request = args![0]!;
            Requests.Add(request.GetType());
            return Answer?.Invoke(request) ?? throw new DispatchObservedException();
        }
    }

    private sealed class DispatchObservedException : Exception;

    private sealed class SurfaceFactory(string environment, bool addFilterProvider = false,
        Action<MvcOptions>? configureMvc = null) : ConstructionOnlyApiFactory
    {
        protected override void ConfigureTestHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("Deployment:Mode", "SelfHostedOnline");
            builder.UseSetting("Tenancy:PlatformHosts:0", "localhost");
            builder.UseSetting("Authentication:Tenant:Issuer", "https://identity.example/realms/learnstack");
            if (addFilterProvider)
                builder.ConfigureTestServices(services => services.AddSingleton<IFilterProvider, UnexpectedFilterProvider>());
            if (configureMvc is not null)
                builder.ConfigureTestServices(services => services.Configure(configureMvc));
        }
    }
}
