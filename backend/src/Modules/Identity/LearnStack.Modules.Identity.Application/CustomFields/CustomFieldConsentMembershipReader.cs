using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Contracts.CustomFields;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Identity.Application.CustomFields;

public sealed class CustomFieldConsentMembershipReader(IMembershipAdministrationReader memberships,
    IIdentityScopeLifecycleReader lifecycle, ITenantContext context, IIdentityMutationLocks locks)
    : ICustomFieldConsentMembershipReader
{
    public async Task<CustomFieldConsentMembership?> ReadOwnActiveAsync(Guid membershipId, Guid definitionId, int schemaVersion,
        IdentityMutationLockPlan fence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fence);
        if (fence.TenantId != context.TenantId || fence.TenantMode != IdentityTenantLockMode.Exclusive)
            throw new InvalidOperationException("Consent admission requires its exclusive Tenant fence.");
        locks.RequireCurrent(fence);
        if (membershipId == Guid.Empty || membershipId.Version != 7 || definitionId == Guid.Empty
            || definitionId.Version != 7 || schemaVersion < 1) return null;
        var member = await memberships.ReadOwnAsync(cancellationToken);
        var state = await lifecycle.ReadAsync(context.TenantId, context.OrganizationId, cancellationToken);
        locks.RequireCurrent(fence);
        if (member is not { Status: MembershipStatus.Active, IsDeleted: false, DeletedBy: null }
            || member.Id.Value != membershipId || member.UserId != context.UserId || member.TenantId != context.TenantId
            || member.OrganizationId != context.OrganizationId || !state.TenantExistsAndNotDeleted || !state.TenantAllowsOrdinaryWork
            || context.OrganizationId is not null && (!state.OrganizationBelongsToTenant || !state.OrganizationAllowsOrdinaryWork)) return null;
        return new(member.TenantId, member.OrganizationId, member.UserId, member.Id.Value,
            MembershipCustomFieldValues.Read(member.Profile.CustomFieldsJson)
                .Any(value => value.DefinitionId == definitionId && value.SchemaVersion == schemaVersion));
    }
}
