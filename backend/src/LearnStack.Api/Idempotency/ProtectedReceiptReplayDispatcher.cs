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
internal sealed class ProtectedReceiptReplayDispatcher(ISender sender)
{
    internal Task<Result<None>> ReadAsync(ProtectedAdministrationDescriptor descriptor, IdempotencyReplayTarget target,
        CancellationToken cancellationToken)
    {
        if (target.Kind != descriptor.TargetKind || target.Id == Guid.Empty || target.Id.Version != 7)
            return Task.FromResult(Result<None>.Fail(new Error(LocalizedMessage.Of("lockey_dependency_unavailable"))));
        return descriptor.Operation switch
        {
            ProtectedAdministrationOperation.SetMembershipProfile => sender.Send(new ReadMemberProfileReceiptQuery(target.Id), cancellationToken),
            ProtectedAdministrationOperation.SetOwnMembershipProfile => sender.Send(new ReadOwnMembershipProfileReceiptQuery(target.Id), cancellationToken),
            ProtectedAdministrationOperation.ChangeMembershipStatus or ProtectedAdministrationOperation.ReplaceMembershipRoleAssignments =>
                sender.Send(new ReadMembershipReceiptQuery(target.Id), cancellationToken),
            ProtectedAdministrationOperation.ChangeRoleGrants or ProtectedAdministrationOperation.AdoptRoleGrantManifest =>
                sender.Send(new ReadRoleReceiptQuery(target.Id), cancellationToken),
            ProtectedAdministrationOperation.CreateOrganization or ProtectedAdministrationOperation.RenameOrganization
                or ProtectedAdministrationOperation.ArchiveOrganization =>
                sender.Send(new ReadOrganizationReceiptQuery(OrganizationId.From(target.Id)), cancellationToken),
            ProtectedAdministrationOperation.SelectDefaultOrganization =>
                sender.Send(new ReadDefaultOrganizationReceiptQuery(TenantId.From(target.Id)), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(descriptor)),
        };
    }
}
