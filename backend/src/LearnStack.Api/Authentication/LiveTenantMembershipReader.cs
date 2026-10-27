using LearnStack.Modules.Identity.Application.Contracts.Authorization;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Api.Authentication;

/// <summary>Confirms only the quota-admitted bound claim through a fresh committed reader.</summary>
internal sealed class LiveTenantMembershipReader(TenantAuthorizationCandidateSource candidates,
    Func<IIdentityAuthorizationReader> reader) : ITenantMembershipReader
{
    public async Task<bool> CoversAsync(UserId userId, TenantId tenantId, OrganizationId? organizationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidate = candidates.ForValidatedScope(tenantId, organizationId);
        if (candidate is not { IsActive: true } || candidate.Actor != userId) return false;
        var result = await reader().ReadAsync(candidate, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Outcome == IdentityAuthorizationOutcome.Unavailable)
            throw new InfrastructureException("Identity authorization is unavailable.");
        return candidate.IsActive && result.Covers && result.Actor == userId
            && result.TenantId == tenantId && result.OrganizationId == organizationId;
    }
}
