using LearnStack.Api.Common;
using Microsoft.AspNetCore.Http;
using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.Modules.Tenancy.Application.Contracts.Settings;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
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
}

internal sealed record ProtectedAdministrationDescriptor(
    ProtectedAdministrationOperation Operation, Type RequestType, IdempotencyReplayTargetKind TargetKind, bool Creates = false)
{
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
        _ => throw new ArgumentException("The protected operation is not registered.", nameof(request)),
    };
    internal bool TryReadPrecondition(HttpRequest request, out long? expected)
    {
        expected = null;
        if (Operation == ProtectedAdministrationOperation.SetTenantInvitationLifetime
            && request.Headers.IfMatch.Count == 0 && request.Headers.IfNoneMatch.Count == 1
            && request.Headers.IfNoneMatch[0] == "*") return true;
        if (request.Headers.IfNoneMatch.Count != 0) return false;
        if (Creates) return request.Headers.IfMatch.Count == 0;
        if (!ProtectedEntityTag.TryRead(request, out var version)) return false;
        expected = version;
        return true;
    }
    internal string LocationFor(Guid id) => Operation == ProtectedAdministrationOperation.CreateInvitation
        ? "/api/v1/identity/invitations/" + id.ToString("D") : OrganizationLocationFor(id);
    internal static string OrganizationLocationFor(Guid id) => "/api/v1/tenancy/organizations/" + id.ToString("D");

}
