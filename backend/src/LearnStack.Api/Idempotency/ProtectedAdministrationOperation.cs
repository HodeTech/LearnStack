using LearnStack.Api.Common;
using LearnStack.Modules.Customization.Application.Contracts.CustomFields;
using LearnStack.Modules.Identity.Application.Contracts.CustomFields;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using Microsoft.AspNetCore.Http;
using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.Modules.Tenancy.Application.Contracts.Settings;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Idempotency;

namespace LearnStack.Api.Idempotency;

/// <summary>The finite ADR-0079 keyed mutation set; no endpoint is activated by this declaration.</summary>
public enum ProtectedAdministrationOperation
{
    SetMembershipProfile = 1,
    SetOwnMembershipProfile = 2,
    ChangeMembershipStatus = 3,
    ReplaceMembershipRoleAssignments = 4,
    ChangeRoleGrants = 5,
    AdoptRoleGrantManifest = 6,
    CreateOrganization = 7,
    RenameOrganization = 8,
    ArchiveOrganization = 9,
    SelectDefaultOrganization = 10,
    CreateInvitation = 11,
    RevokeInvitation = 12,
    ReissueInvitation = 13,
    SetTenantInvitationLifetime = 14,
    RegisterCustomFieldDefinition = 15,
    ReviseCustomFieldDraft = 16,
    PublishCustomFieldRevision = 17,
    RetireCustomFieldRevision = 18,
    DiscardCustomFieldDraft = 19,
    SetMemberCustomFields = 20,
    SetOwnCustomFields = 21,
    GrantOwnFieldConsent = 22,
    WithdrawOwnFieldConsent = 23,
    CreateOwnDataExport = 24,
    CreateDataExportForSubject = 25,
    CancelOwnDataRequest = 26,
    CancelDataRequest = 27,
    RevokeOwnDataExport = 28,
    RevokeDataExport = 29,
    CreateRetainedDataExport = 30,
    CancelRetainedDataRequest = 31,
    CreateOwnDataErasure = 32,
    CreateDataErasureForSubject = 33,
    CreateRetainedDataErasure = 34,
    RevokeRetainedDataExport = 35,
}

internal sealed record ProtectedAdministrationDescriptor(
    ProtectedAdministrationOperation Operation, Type RequestType, IdempotencyReplayTargetKind TargetKind, bool Creates = false)
{
    internal bool UsesCustomFieldFingerprint => Operation is ProtectedAdministrationOperation.RegisterCustomFieldDefinition
        or ProtectedAdministrationOperation.ReviseCustomFieldDraft or ProtectedAdministrationOperation.PublishCustomFieldRevision
        or ProtectedAdministrationOperation.RetireCustomFieldRevision or ProtectedAdministrationOperation.DiscardCustomFieldDraft
        or ProtectedAdministrationOperation.SetMemberCustomFields or ProtectedAdministrationOperation.SetOwnCustomFields
        or ProtectedAdministrationOperation.GrantOwnFieldConsent or ProtectedAdministrationOperation.WithdrawOwnFieldConsent;
    internal bool IsConsent => Operation is ProtectedAdministrationOperation.GrantOwnFieldConsent or ProtectedAdministrationOperation.WithdrawOwnFieldConsent;

    /// <summary>
    /// ADR-0106 § 10: a data-request creation is accepted, not finished — 202 with the request's initial resource, its
    /// <c>Location</c> and its <c>ETag</c>. Every other keyed creation answers 201 and every other mutation 200.
    /// </summary>
    internal bool IsDataRequestCreation => IsDataExportCreation || IsDataErasureCreation;

    internal bool IsDataExportCreation => Operation is ProtectedAdministrationOperation.CreateOwnDataExport
        or ProtectedAdministrationOperation.CreateDataExportForSubject or ProtectedAdministrationOperation.CreateRetainedDataExport;

    /// <summary>ADR-0106 § 11: an erasure is accepted <c>scheduled</c>, executed once its cancellable window ends.</summary>
    internal bool IsDataErasureCreation => Operation is ProtectedAdministrationOperation.CreateOwnDataErasure
        or ProtectedAdministrationOperation.CreateDataErasureForSubject or ProtectedAdministrationOperation.CreateRetainedDataErasure;

    /// <summary>
    /// ADR-0106 § 9: the retained own-scope entry has no per-request route a Location could name; its receipt is read
    /// through the group's request list, so its creation answers 202 with the resource and ETag and no Location.
    /// </summary>
    internal bool HasLocation => Creates && Operation is not (ProtectedAdministrationOperation.CreateRetainedDataExport
        or ProtectedAdministrationOperation.CreateRetainedDataErasure);

    /// <summary>The retained own-scope entry's keyed operations, whose replay reads only through their own witness.</summary>
    internal bool IsRetainedDataRequest => Operation is ProtectedAdministrationOperation.CreateRetainedDataExport
        or ProtectedAdministrationOperation.CreateRetainedDataErasure or ProtectedAdministrationOperation.CancelRetainedDataRequest
        or ProtectedAdministrationOperation.RevokeRetainedDataExport;

    internal int SuccessStatus => IsDataRequestCreation ? StatusCodes.Status202Accepted
        : Creates ? StatusCodes.Status201Created : StatusCodes.Status200OK;
    private static readonly ProtectedAdministrationDescriptor[] Descriptors =
        Enum.GetValues<ProtectedAdministrationOperation>().Select(For).ToArray();

    internal static ProtectedAdministrationDescriptor? ForRequest(Type requestType)
    {
        foreach (var descriptor in Descriptors)
            if (descriptor.RequestType == requestType) return descriptor;
        return null;
    }

    internal static ProtectedAdministrationDescriptor For(ProtectedAdministrationOperation operation) => operation switch
    {
        ProtectedAdministrationOperation.SetMembershipProfile => new(operation, typeof(SetMembershipProfileCommand), IdempotencyReplayTargetKind.Membership),
        ProtectedAdministrationOperation.SetOwnMembershipProfile => new(operation, typeof(SetOwnMembershipProfileCommand), IdempotencyReplayTargetKind.Membership),
        ProtectedAdministrationOperation.ChangeMembershipStatus => new(operation, typeof(ChangeMembershipStatusCommand), IdempotencyReplayTargetKind.Membership),
        ProtectedAdministrationOperation.ReplaceMembershipRoleAssignments => new(operation, typeof(ReplaceMembershipRoleAssignmentsCommand), IdempotencyReplayTargetKind.Membership),
        ProtectedAdministrationOperation.ChangeRoleGrants => new(operation, typeof(ChangeRoleGrantsCommand), IdempotencyReplayTargetKind.Role),
        ProtectedAdministrationOperation.AdoptRoleGrantManifest => new(operation, typeof(AdoptRoleGrantManifestCommand), IdempotencyReplayTargetKind.Role),
        ProtectedAdministrationOperation.CreateOrganization => new(operation, typeof(CreateManagedOrganizationCommand), IdempotencyReplayTargetKind.Organization, true),
        ProtectedAdministrationOperation.RenameOrganization => new(operation, typeof(RenameOrganizationCommand), IdempotencyReplayTargetKind.Organization),
        ProtectedAdministrationOperation.ArchiveOrganization => new(operation, typeof(ArchiveOrganizationCommand), IdempotencyReplayTargetKind.Organization),
        ProtectedAdministrationOperation.SelectDefaultOrganization => new(operation, typeof(SelectDefaultOrganizationCommand), IdempotencyReplayTargetKind.Tenant),
        ProtectedAdministrationOperation.CreateInvitation => new(operation, typeof(CreateInvitationCommand), IdempotencyReplayTargetKind.Invitation, true),
        ProtectedAdministrationOperation.RevokeInvitation => new(operation, typeof(RevokeInvitationCommand), IdempotencyReplayTargetKind.Invitation),
        ProtectedAdministrationOperation.ReissueInvitation => new(operation, typeof(ReissueInvitationCommand), IdempotencyReplayTargetKind.Invitation),
        ProtectedAdministrationOperation.SetTenantInvitationLifetime => new(operation, typeof(SetTenantInvitationLifetimeCommand), IdempotencyReplayTargetKind.TenantSetting),
        ProtectedAdministrationOperation.RegisterCustomFieldDefinition => new(operation, typeof(RegisterCustomFieldDefinitionCommand), IdempotencyReplayTargetKind.CustomFieldDefinition, true),
        ProtectedAdministrationOperation.ReviseCustomFieldDraft => new(operation, typeof(ReviseCustomFieldDefinitionDraftCommand), IdempotencyReplayTargetKind.CustomFieldDefinition),
        ProtectedAdministrationOperation.PublishCustomFieldRevision => new(operation, typeof(PublishCustomFieldDefinitionCommand), IdempotencyReplayTargetKind.CustomFieldDefinition),
        ProtectedAdministrationOperation.RetireCustomFieldRevision => new(operation, typeof(RetireCustomFieldDefinitionCommand), IdempotencyReplayTargetKind.CustomFieldDefinition),
        ProtectedAdministrationOperation.DiscardCustomFieldDraft => new(operation, typeof(DiscardCustomFieldDefinitionDraftCommand), IdempotencyReplayTargetKind.CustomFieldDefinition),
        ProtectedAdministrationOperation.SetMemberCustomFields => new(operation, typeof(SetMembershipCustomFieldsCommand), IdempotencyReplayTargetKind.Membership),
        ProtectedAdministrationOperation.SetOwnCustomFields => new(operation, typeof(SetOwnMembershipCustomFieldsCommand), IdempotencyReplayTargetKind.Membership),
        ProtectedAdministrationOperation.GrantOwnFieldConsent => new(operation, typeof(GrantOwnMembershipFieldConsentCommand), IdempotencyReplayTargetKind.MembershipFieldConsent),
        ProtectedAdministrationOperation.WithdrawOwnFieldConsent => new(operation, typeof(WithdrawOwnMembershipFieldConsentCommand), IdempotencyReplayTargetKind.MembershipFieldConsent),
        ProtectedAdministrationOperation.CreateOwnDataExport => new(operation, typeof(CreateOwnDataExportCommand), IdempotencyReplayTargetKind.DataSubjectRequest, true),
        ProtectedAdministrationOperation.CreateDataExportForSubject => new(operation, typeof(CreateDataExportForSubjectCommand), IdempotencyReplayTargetKind.DataSubjectRequest, true),
        ProtectedAdministrationOperation.CancelOwnDataRequest => new(operation, typeof(CancelOwnDataRequestCommand), IdempotencyReplayTargetKind.DataSubjectRequest),
        ProtectedAdministrationOperation.CancelDataRequest => new(operation, typeof(CancelDataRequestCommand), IdempotencyReplayTargetKind.DataSubjectRequest),
        ProtectedAdministrationOperation.RevokeOwnDataExport => new(operation, typeof(RevokeOwnDataExportCommand), IdempotencyReplayTargetKind.DataSubjectRequest),
        ProtectedAdministrationOperation.RevokeDataExport => new(operation, typeof(RevokeDataExportCommand), IdempotencyReplayTargetKind.DataSubjectRequest),
        ProtectedAdministrationOperation.CreateRetainedDataExport => new(operation, typeof(CreateRetainedDataExportCommand), IdempotencyReplayTargetKind.DataSubjectRequest, true),
        ProtectedAdministrationOperation.CancelRetainedDataRequest => new(operation, typeof(CancelRetainedDataRequestCommand), IdempotencyReplayTargetKind.DataSubjectRequest),
        ProtectedAdministrationOperation.CreateOwnDataErasure => new(operation, typeof(CreateOwnDataErasureCommand), IdempotencyReplayTargetKind.DataSubjectRequest, true),
        ProtectedAdministrationOperation.CreateDataErasureForSubject => new(operation, typeof(CreateDataErasureForSubjectCommand), IdempotencyReplayTargetKind.DataSubjectRequest, true),
        ProtectedAdministrationOperation.CreateRetainedDataErasure => new(operation, typeof(CreateRetainedDataErasureCommand), IdempotencyReplayTargetKind.DataSubjectRequest, true),
        ProtectedAdministrationOperation.RevokeRetainedDataExport => new(operation, typeof(RevokeRetainedDataExportCommand), IdempotencyReplayTargetKind.DataSubjectRequest),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    internal static ProtectedAdministrationOperation? OperationFor(object request) => request switch
    {
        SetMembershipProfileCommand => ProtectedAdministrationOperation.SetMembershipProfile,
        SetOwnMembershipProfileCommand => ProtectedAdministrationOperation.SetOwnMembershipProfile,
        ChangeMembershipStatusCommand => ProtectedAdministrationOperation.ChangeMembershipStatus,
        ReplaceMembershipRoleAssignmentsCommand => ProtectedAdministrationOperation.ReplaceMembershipRoleAssignments,
        ChangeRoleGrantsCommand => ProtectedAdministrationOperation.ChangeRoleGrants,
        AdoptRoleGrantManifestCommand => ProtectedAdministrationOperation.AdoptRoleGrantManifest,
        CreateManagedOrganizationCommand => ProtectedAdministrationOperation.CreateOrganization,
        RenameOrganizationCommand => ProtectedAdministrationOperation.RenameOrganization,
        ArchiveOrganizationCommand => ProtectedAdministrationOperation.ArchiveOrganization,
        SelectDefaultOrganizationCommand => ProtectedAdministrationOperation.SelectDefaultOrganization,
        CreateInvitationCommand => ProtectedAdministrationOperation.CreateInvitation,
        RevokeInvitationCommand => ProtectedAdministrationOperation.RevokeInvitation,
        ReissueInvitationCommand => ProtectedAdministrationOperation.ReissueInvitation,
        SetTenantInvitationLifetimeCommand => ProtectedAdministrationOperation.SetTenantInvitationLifetime,
        RegisterCustomFieldDefinitionCommand => ProtectedAdministrationOperation.RegisterCustomFieldDefinition,
        ReviseCustomFieldDefinitionDraftCommand => ProtectedAdministrationOperation.ReviseCustomFieldDraft,
        PublishCustomFieldDefinitionCommand => ProtectedAdministrationOperation.PublishCustomFieldRevision,
        RetireCustomFieldDefinitionCommand => ProtectedAdministrationOperation.RetireCustomFieldRevision,
        DiscardCustomFieldDefinitionDraftCommand => ProtectedAdministrationOperation.DiscardCustomFieldDraft,
        SetMembershipCustomFieldsCommand => ProtectedAdministrationOperation.SetMemberCustomFields,
        SetOwnMembershipCustomFieldsCommand => ProtectedAdministrationOperation.SetOwnCustomFields,
        GrantOwnMembershipFieldConsentCommand => ProtectedAdministrationOperation.GrantOwnFieldConsent,
        WithdrawOwnMembershipFieldConsentCommand => ProtectedAdministrationOperation.WithdrawOwnFieldConsent,
        CreateOwnDataExportCommand => ProtectedAdministrationOperation.CreateOwnDataExport,
        CreateDataExportForSubjectCommand => ProtectedAdministrationOperation.CreateDataExportForSubject,
        CancelOwnDataRequestCommand => ProtectedAdministrationOperation.CancelOwnDataRequest,
        CancelDataRequestCommand => ProtectedAdministrationOperation.CancelDataRequest,
        RevokeOwnDataExportCommand => ProtectedAdministrationOperation.RevokeOwnDataExport,
        RevokeDataExportCommand => ProtectedAdministrationOperation.RevokeDataExport,
        CreateRetainedDataExportCommand => ProtectedAdministrationOperation.CreateRetainedDataExport,
        CancelRetainedDataRequestCommand => ProtectedAdministrationOperation.CancelRetainedDataRequest,
        CreateOwnDataErasureCommand => ProtectedAdministrationOperation.CreateOwnDataErasure,
        CreateDataErasureForSubjectCommand => ProtectedAdministrationOperation.CreateDataErasureForSubject,
        CreateRetainedDataErasureCommand => ProtectedAdministrationOperation.CreateRetainedDataErasure,
        RevokeRetainedDataExportCommand => ProtectedAdministrationOperation.RevokeRetainedDataExport,
        _ => null,
    };

    internal static long? ExpectedVersion(object request) => request switch
    {
        SetMembershipProfileCommand command => command.ExpectedVersion,
        SetOwnMembershipProfileCommand command => command.ExpectedVersion,
        ChangeMembershipStatusCommand command => command.ExpectedVersion,
        ReplaceMembershipRoleAssignmentsCommand command => command.ExpectedVersion,
        ChangeRoleGrantsCommand command => command.ExpectedRoleVersion,
        AdoptRoleGrantManifestCommand command => command.ExpectedRoleVersion,
        RenameOrganizationCommand command => command.ExpectedVersion,
        ArchiveOrganizationCommand command => command.ExpectedVersion,
        SelectDefaultOrganizationCommand command => command.ExpectedVersion,
        CreateManagedOrganizationCommand or CreateInvitationCommand => null,
        RevokeInvitationCommand command => command.ExpectedVersion,
        ReissueInvitationCommand command => command.ExpectedVersion,
        SetTenantInvitationLifetimeCommand command => command.ExpectedVersion,
        RegisterCustomFieldDefinitionCommand or GrantOwnMembershipFieldConsentCommand or WithdrawOwnMembershipFieldConsentCommand => null,
        ReviseCustomFieldDefinitionDraftCommand command => command.ExpectedVersion,
        PublishCustomFieldDefinitionCommand command => command.ExpectedVersion,
        RetireCustomFieldDefinitionCommand command => command.ExpectedVersion,
        DiscardCustomFieldDefinitionDraftCommand command => command.ExpectedVersion,
        SetMembershipCustomFieldsCommand command => command.ExpectedVersion,
        SetOwnMembershipCustomFieldsCommand command => command.ExpectedVersion,
        CreateOwnDataExportCommand or CreateDataExportForSubjectCommand => null,
        CancelOwnDataRequestCommand command => command.ExpectedVersion,
        CancelDataRequestCommand command => command.ExpectedVersion,
        RevokeOwnDataExportCommand command => command.ExpectedArtifactVersion,
        RevokeDataExportCommand command => command.ExpectedArtifactVersion,
        CreateRetainedDataExportCommand => null,
        CancelRetainedDataRequestCommand command => command.ExpectedVersion,
        CreateOwnDataErasureCommand => null,
        CreateDataErasureForSubjectCommand => null,
        CreateRetainedDataErasureCommand => null,
        RevokeRetainedDataExportCommand command => command.ExpectedArtifactVersion,
        _ => throw new ArgumentException("The protected operation is not registered.", nameof(request)),
    };
    internal bool TryReadPrecondition(HttpRequest request, out long? expected)
    {
        expected = null;
        if (Operation == ProtectedAdministrationOperation.SetTenantInvitationLifetime
            && request.Headers.IfMatch.Count == 0 && request.Headers.IfNoneMatch.Count == 1
            && request.Headers.IfNoneMatch[0] == "*") return true;
        if (request.Headers.IfNoneMatch.Count != 0) return false;
        if (Creates || IsConsent) return request.Headers.IfMatch.Count == 0;
        if (!ProtectedEntityTag.TryRead(request, out var version)) return false;
        expected = version;
        return true;
    }
    internal string LocationFor(Guid id) => Operation switch
    {
        ProtectedAdministrationOperation.CreateInvitation => "/api/v1/identity/invitations/" + id.ToString("D"),
        ProtectedAdministrationOperation.RegisterCustomFieldDefinition => "/api/v1/customization/custom-field-definitions/" + id.ToString("D"),
        ProtectedAdministrationOperation.CreateOwnDataExport => "/api/v1/identity/me/data-requests/" + id.ToString("D"),
        ProtectedAdministrationOperation.CreateDataExportForSubject => "/api/v1/identity/data-requests/" + id.ToString("D"),
        ProtectedAdministrationOperation.CreateOwnDataErasure => "/api/v1/identity/me/data-requests/" + id.ToString("D"),
        ProtectedAdministrationOperation.CreateDataErasureForSubject => "/api/v1/identity/data-requests/" + id.ToString("D"),
        ProtectedAdministrationOperation.CreateRetainedDataExport or ProtectedAdministrationOperation.CreateRetainedDataErasure =>
            throw new InvalidOperationException("The retained entry has no request location."),
        _ => OrganizationLocationFor(id),
    };
    internal static string OrganizationLocationFor(Guid id) => "/api/v1/tenancy/organizations/" + id.ToString("D");

}
