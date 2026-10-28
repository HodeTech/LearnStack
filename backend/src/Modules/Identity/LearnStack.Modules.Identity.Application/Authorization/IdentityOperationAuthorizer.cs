using LearnStack.Modules.Identity.Application.Contracts.Authorization;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;

namespace LearnStack.Modules.Identity.Application.Authorization;

/// <summary>One fresh committed-state evaluation per act; no fact or permission decision cache.</summary>
public sealed class IdentityOperationAuthorizer(Func<IIdentityAuthorizationReader> reader, IOperationPolicyCatalog catalog)
    : ProtectedOperationDecision.MintingSource, IProtectedOperationAuthorizer
{
    public async Task<ProtectedOperationDecision> AuthorizeAsync(OperationInvocation invocation, OperationPolicy policy,
        AuthorizationCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        if (invocation.Candidate is not { } original || invocation.Policy.Kind is not (OperationPolicyKind.AccountRead or OperationPolicyKind.Protected))
            throw new ArgumentException("Protected operation invocation provenance is invalid.", nameof(invocation));
        if (!ReferenceEquals(original, candidate))
            return ForDecision(invocation, original, ProtectedOperationOutcome.NotCovered);
        if (!invocation.IsActive || !candidate.IsActive)
            return ForDecision(invocation, candidate, ProtectedOperationOutcome.Unavailable);
        if (!Matches(invocation, policy, candidate))
            return ForDecision(invocation, candidate, ProtectedOperationOutcome.NotCovered);
        var facts = await reader().ReadAsync(candidate, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!invocation.IsActive || !candidate.IsActive)
            return ForDecision(invocation, candidate, ProtectedOperationOutcome.Unavailable);
        if (!Matches(invocation, policy, candidate) || facts.Actor != candidate.Actor || facts.TenantId != candidate.TenantId
            || facts.OrganizationId != candidate.OrganizationId)
            return ForDecision(invocation, candidate, ProtectedOperationOutcome.NotCovered);
        if (facts.Outcome == IdentityAuthorizationOutcome.Unavailable)
            return ForDecision(invocation, candidate, ProtectedOperationOutcome.Unavailable);
        if (!facts.Covers || facts.Memberships.IsDefaultOrEmpty)
            return ForDecision(invocation, candidate, ProtectedOperationOutcome.NotCovered);
        var members = facts.Memberships.Where(member => member.OrganizationId is null || member.OrganizationId == candidate.OrganizationId).ToArray();
        if (members.Length != facts.Memberships.Length)
            return ForDecision(invocation, candidate, ProtectedOperationOutcome.Unavailable);
        if (policy.Kind == OperationPolicyKind.AccountRead)
            return ForDecision(invocation, candidate, ProtectedOperationOutcome.Allowed,
                members.Select(member => member.MembershipId), members.SelectMany(member => member.Roles).Select(role => role.RoleId).Distinct());
        var permission = policy.RequiredPermission!;
        if (!permission.IsActive || !catalog.TryGetDefinition(permission.Scope, permission.Key, out var registration)
            || !registration.Assignable || !registration.Definition.IsActive || registration.Definition != permission
            || policy.RequiresOrglessWrite && candidate.OrganizationId is not null)
            return ForDecision(invocation, candidate, ProtectedOperationOutcome.Forbidden);
        var contributing = members.SelectMany(member => member.Roles
            .Where(role => role.Scope == permission.Scope
                && (permission.Scope == PermissionScope.Tenant ? member.OrganizationId is null
                    : candidate.OrganizationId is not null && member.OrganizationId == candidate.OrganizationId)
                && role.Grants.Any(grant => grant.IsActive && grant == permission))
            .Select(role => (Member: member.MembershipId, Role: role.RoleId))).ToArray();
        return contributing.Length == 0
            ? ForDecision(invocation, candidate, ProtectedOperationOutcome.Forbidden)
            : ForDecision(invocation, candidate, ProtectedOperationOutcome.Allowed,
                contributing.Select(value => value.Member).Distinct(), contributing.Select(value => value.Role).Distinct());
    }

    private bool Matches(OperationInvocation invocation, OperationPolicy policy, AuthorizationCandidate candidate) =>
        ReferenceEquals(invocation.Policy, policy)
        && ReferenceEquals(invocation.Candidate, candidate)
        && (invocation.EntryKind == OperationEntryKind.Http
            || invocation.EntryKind == OperationEntryKind.Application && invocation.Operation.GetType() == policy.RequestType)
        && catalog.TryGet(policy.RequestType, out var registered) && ReferenceEquals(registered, policy)
        && policy.Kind is OperationPolicyKind.AccountRead or OperationPolicyKind.Protected
        && invocation.Context.IsResolved && invocation.Context.UserId == candidate.Actor
        && candidate.Actor != UserId.SystemActor && candidate.TenantId != TenantId.PlatformSentinel
        && invocation.Actor == candidate.Actor && invocation.TenantId == candidate.TenantId
        && invocation.OrganizationId == candidate.OrganizationId && invocation.Context.TenantId == candidate.TenantId
        && invocation.Context.OrganizationId == candidate.OrganizationId;
}
