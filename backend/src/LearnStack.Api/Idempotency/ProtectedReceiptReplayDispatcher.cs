using LearnStack.Api.Authentication;
using LearnStack.Modules.Customization.Application.Contracts.CustomFields;
using LearnStack.Modules.Identity.Application.Contracts.CustomFields;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.Modules.Tenancy.Application.Contracts.Settings;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Idempotency;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Api.Idempotency;

/// <summary>Fresh ordinary MUST reads of the persisted original target; never a mutation or current-target lookup.</summary>
internal sealed class ProtectedReceiptReplayDispatcher(ISender sender, HttpContext? http = null)
{
    internal Task<Result<None>> ReadAsync(ProtectedAdministrationDescriptor descriptor, IdempotencyReplayTarget target,
        CancellationToken cancellationToken)
    {
        if (target.Kind != descriptor.TargetKind || target.Id == Guid.Empty || target.Id.Version != 7)
            return Task.FromResult(Result<None>.Fail(new Error(LocalizedMessage.Of("lockey_dependency_unavailable"))));
        return descriptor.Operation switch
        {
            ProtectedAdministrationOperation.SetMembershipProfile or ProtectedAdministrationOperation.SetMemberCustomFields => sender.Send(new ReadMemberProfileReceiptQuery(target.Id), cancellationToken),
            ProtectedAdministrationOperation.SetOwnMembershipProfile or ProtectedAdministrationOperation.SetOwnCustomFields => sender.Send(new ReadOwnMembershipProfileReceiptQuery(target.Id), cancellationToken),
            ProtectedAdministrationOperation.ChangeMembershipStatus or ProtectedAdministrationOperation.ReplaceMembershipRoleAssignments =>
                sender.Send(new ReadMembershipReceiptQuery(target.Id), cancellationToken),
            ProtectedAdministrationOperation.ChangeRoleGrants or ProtectedAdministrationOperation.AdoptRoleGrantManifest =>
                sender.Send(new ReadRoleReceiptQuery(target.Id), cancellationToken),
            ProtectedAdministrationOperation.CreateOrganization or ProtectedAdministrationOperation.RenameOrganization
                or ProtectedAdministrationOperation.ArchiveOrganization =>
                sender.Send(new ReadOrganizationReceiptQuery(OrganizationId.From(target.Id)), cancellationToken),
            ProtectedAdministrationOperation.SelectDefaultOrganization =>
                sender.Send(new ReadDefaultOrganizationReceiptQuery(TenantId.From(target.Id)), cancellationToken),
            ProtectedAdministrationOperation.CreateInvitation or ProtectedAdministrationOperation.RevokeInvitation or ProtectedAdministrationOperation.ReissueInvitation =>
                sender.Send(new ReadInvitationReceiptQuery(target.Id), cancellationToken),
            ProtectedAdministrationOperation.SetTenantInvitationLifetime => sender.Send(new ReadInvitationPolicyReceiptQuery(target.Id), cancellationToken),
            ProtectedAdministrationOperation.RegisterCustomFieldDefinition or ProtectedAdministrationOperation.ReviseCustomFieldDraft
                or ProtectedAdministrationOperation.PublishCustomFieldRevision or ProtectedAdministrationOperation.RetireCustomFieldRevision
                or ProtectedAdministrationOperation.DiscardCustomFieldDraft => sender.Send(new ReadCustomFieldDefinitionReceiptQuery(target.Id), cancellationToken),
            ProtectedAdministrationOperation.GrantOwnFieldConsent => sender.Send(new ReadOwnGrantedFieldConsentReceiptQuery(target.Id), cancellationToken),
            ProtectedAdministrationOperation.WithdrawOwnFieldConsent when http?.Features.Get<ConsentSubjectSource>()?.Request is ReadOwnWithdrawnFieldConsentReceiptQuery request
                && request.EvidenceId == target.Id => sender.Send(request, cancellationToken),
            ProtectedAdministrationOperation.WithdrawOwnFieldConsent => Task.FromResult(Result<None>.Fail(new Error(LocalizedMessage.Of("lockey_forbidden")))),
            _ => throw new ArgumentOutOfRangeException(nameof(descriptor)),
        };
    }
}
