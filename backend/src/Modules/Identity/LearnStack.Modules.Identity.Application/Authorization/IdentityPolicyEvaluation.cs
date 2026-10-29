using System.Collections.Immutable;
using LearnStack.Modules.Identity.Application.Contracts.Authorization;
using LearnStack.SharedKernel.Permissions;

namespace LearnStack.Modules.Identity.Application.Authorization;

/// <summary>Pure scoped-pair assessment; only live, separately confined sources mint authority from its result.</summary>
internal static class IdentityPolicyEvaluation
{
    internal static IdentityPolicyAssessment Evaluate(OperationPolicy policy, AuthorizationCandidate candidate,
        IdentityAuthorizationResult facts, IOperationPolicyCatalog catalog)
    {
        if (facts.Outcome == IdentityAuthorizationOutcome.Unavailable)
            return Refused(ProtectedOperationOutcome.Unavailable);
        if (!facts.Covers || facts.Actor != candidate.Actor || facts.TenantId != candidate.TenantId
            || facts.OrganizationId != candidate.OrganizationId || facts.Memberships.IsDefaultOrEmpty)
            return Refused(ProtectedOperationOutcome.NotCovered);
        var members = facts.Memberships.Where(member => member.OrganizationId is null || member.OrganizationId == candidate.OrganizationId).ToArray();
        if (members.Length != facts.Memberships.Length)
            return Refused(ProtectedOperationOutcome.Unavailable);
        if (policy.Kind is OperationPolicyKind.AccountRead or OperationPolicyKind.NativeSessionAdmission or OperationPolicyKind.OwnMembershipRead)
            return new(ProtectedOperationOutcome.Allowed, [.. members.Select(member => member.MembershipId)],
                [.. members.SelectMany(member => member.Roles).Select(role => role.RoleId).Distinct()], null);
        if (policy.Kind != OperationPolicyKind.Protected)
            return Refused(ProtectedOperationOutcome.Unavailable);
        if (policy.RequiresOrglessWrite && candidate.OrganizationId is not null
            || policy.RequiresOrganization && candidate.OrganizationId is null)
            return Refused(ProtectedOperationOutcome.Forbidden);
        foreach (var clause in policy.Alternatives)
        {
            var contributors = new List<(Guid Member, Guid Role)>();
            var satisfied = true;
            foreach (var permission in clause.RequiredPermissions)
            {
                if (!IsRegistered(permission, catalog))
                {
                    satisfied = false;
                    break;
                }
                var contributing = members.SelectMany(member => member.Roles
                    .Where(role => role.Scope == permission.Scope
                        && (permission.Scope == PermissionScope.Tenant ? member.OrganizationId is null
                            : candidate.OrganizationId is not null && member.OrganizationId == candidate.OrganizationId)
                        && role.Grants.Any(grant => grant.IsActive && grant == permission))
                    .Select(role => (Member: member.MembershipId, Role: role.RoleId))).ToArray();
                if (contributing.Length == 0)
                {
                    satisfied = false;
                    break;
                }
                contributors.AddRange(contributing);
            }
            if (satisfied)
                return new(ProtectedOperationOutcome.Allowed, [.. contributors.Select(value => value.Member).Distinct()],
                    [.. contributors.Select(value => value.Role).Distinct()], clause);
        }
        return Refused(ProtectedOperationOutcome.Forbidden);
    }

    internal static bool IsRegistered(PermissionDefinition definition, IOperationPolicyCatalog catalog) =>
        definition is { IsActive: true } && catalog.TryGetDefinition(definition.Scope, definition.Key, out var registration)
        && registration.Assignable && registration.Definition.IsActive && registration.Definition == definition;

    private static IdentityPolicyAssessment Refused(ProtectedOperationOutcome outcome) => new(outcome, [], [], null);
}

internal sealed record IdentityPolicyAssessment(ProtectedOperationOutcome Outcome, ImmutableArray<Guid> MembershipIds,
    ImmutableArray<Guid> RoleIds, OperationPolicyClause? MatchedClause)
{
    public override string ToString() => nameof(IdentityPolicyAssessment);
}
