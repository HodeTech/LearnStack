using LearnStack.Modules.Identity.Application.Contracts.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Identity.Application.Authorization;

/// <summary>Fresh, invocation-bound authority for one actual Membership administration target.</summary>
public sealed class MembershipAdministrationAuthorization(IIdentityAuthorizationReader reader, IOperationPolicyCatalog catalog,
    IOperationInvocationContext invocations, ITenantContext context, IAuthorizationRefusalReporter refusals)
    : ProtectedOperationDecision.MintingSource
{
    internal bool Matches(object request, OperationInvocation invocation) =>
        ReferenceEquals(invocations.CurrentInvocation, invocation) && invocation.IsActive
        && invocation.EntryKind == OperationEntryKind.Application && ReferenceEquals(invocation.Operation, request)
        && ReferenceEquals(invocation.Context, context) && invocation.TrustedSource is null
        && invocation.Candidate is { IsActive: true, SourceKind: AuthorizationSourceKind.Http } candidate
        && request is GetMembershipDetailQuery or GetOwnMembershipProfileQuery or SetMembershipProfileCommand
            or SetOwnMembershipProfileCommand or ChangeMembershipStatusCommand or ReplaceMembershipRoleAssignmentsCommand
        && catalog.TryGet(request.GetType(), out var policy) && ReferenceEquals(invocation.Policy, policy)
        && policy.RequestType == request.GetType()
        && (request is GetOwnMembershipProfileQuery ? policy.Kind == OperationPolicyKind.OwnMembershipRead
            : policy.Kind == OperationPolicyKind.Protected)
        && (request is GetMembershipDetailQuery || policy.ReadScope == OperationReadScope.Scoped
            && policy.Alternatives.All(clause => clause.ReadScope == OperationReadScope.Scoped))
        && context.IsResolved && context.TenantId != TenantId.PlatformSentinel
        && context.UserId is { } actor && StronglyTypedId.IsAssigned(actor) && actor != UserId.SystemActor
        && context.Origin is TenantContextOrigin.HostAndClaim or TenantContextOrigin.ClaimAndMembership
            or TenantContextOrigin.HostAndNativeSessionScope
        && candidate.Actor == actor && candidate.TenantId == context.TenantId && candidate.OrganizationId == context.OrganizationId
        && invocation.Actor == actor && invocation.TenantId == context.TenantId && invocation.OrganizationId == context.OrganizationId
        && invocations.CurrentDecision is { Outcome: ProtectedOperationOutcome.Allowed, IsActive: true } admitted
        && ReferenceEquals(admitted.Invocation, invocation);

    internal async Task<MembershipAdministrationAssessment> RecheckAsync(object request, OperationInvocation invocation,
        Membership? target, IReadOnlyCollection<Role> addedRoles, CancellationToken cancellationToken)
    {
        var candidate = invocation.Candidate
            ?? throw new ArgumentException("Membership invocation has no human source.", nameof(invocation));
        if (!Matches(request, invocation)) return Refused(ProtectedOperationOutcome.Unavailable);
        var facts = await reader.ReadAsync(candidate, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Matches(request, invocation)) return Refused(ProtectedOperationOutcome.Unavailable);
        var policy = IdentityPolicyEvaluation.Evaluate(invocation.Policy, candidate, facts, catalog);
        if (policy.Outcome != ProtectedOperationOutcome.Allowed) return Refused(policy.Outcome);
        if (target is null || target.TenantId != context.TenantId || target.IsDeleted
            || target.DeletedBy is not null || target.Status is not (MembershipStatus.Active or MembershipStatus.Suspended)
            || ExplicitTarget(request) is { } id && target.Id.Value != id)
            return Refused(ProtectedOperationOutcome.NotCovered, AuthorizationRefusalReason.ResourceScope, policy);
        var tenantRead = request is GetMembershipDetailQuery && policy.MatchedClause?.ReadScope == OperationReadScope.Tenant;
        if (!tenantRead && target.OrganizationId != context.OrganizationId)
            return Refused(ProtectedOperationOutcome.NotCovered, AuthorizationRefusalReason.ResourceScope, policy);
        if (request is GetOwnMembershipProfileQuery or SetOwnMembershipProfileCommand
            && (target.UserId != candidate.Actor || target.Status != MembershipStatus.Active
                || !facts.Memberships.Any(member => member.MembershipId == target.Id.Value
                    && member.OrganizationId == context.OrganizationId)))
            return Refused(ProtectedOperationOutcome.NotCovered, AuthorizationRefusalReason.Ownership, policy);
        if (request is SetMembershipProfileCommand && target.Status != MembershipStatus.Active)
            return Refused(ProtectedOperationOutcome.Forbidden, AuthorizationRefusalReason.ResourceScope, policy);
        if (!CanDelegate(request, target, addedRoles, facts, candidate))
            return Refused(ProtectedOperationOutcome.Forbidden, AuthorizationRefusalReason.Delegation, policy);
        return new(ForDecision(invocation, candidate, ProtectedOperationOutcome.Allowed,
            policy.MembershipIds, policy.RoleIds, policy.MatchedClause), AuthorizationRefusalReason.Capability);

        MembershipAdministrationAssessment Refused(ProtectedOperationOutcome outcome,
            AuthorizationRefusalReason reason = AuthorizationRefusalReason.Capability, IdentityPolicyAssessment? assessment = null) =>
            new(ForDecision(invocation, candidate, outcome, assessment?.MembershipIds, assessment?.RoleIds), reason);
    }

    internal void ReportRecheck(MembershipAdministrationAssessment assessment) => refusals.ReportRecheck(assessment.Decision);
    internal void ReportRefusal(MembershipAdministrationAssessment assessment) =>
        refusals.ReportResourceRefusal(assessment.Decision, assessment.Reason);

    private bool CanDelegate(object request, Membership target, IReadOnlyCollection<Role> addedRoles,
        IdentityAuthorizationResult facts, AuthorizationCandidate candidate)
    {
        if (request is not ReplaceMembershipRoleAssignmentsCommand assignments) return addedRoles.Count == 0;
        if (assignments.RoleIds.IsDefault || assignments.RoleIds.Length > Membership.MaximumRoleAssignments
            || assignments.RoleIds.Any(id => id == Guid.Empty || id.Version != 7)
            || assignments.RoleIds.Distinct().Count() != assignments.RoleIds.Length) return false;
        var expected = assignments.RoleIds.Where(id => target.RoleAssignments.All(assignment => assignment.RoleId.Value != id)).ToHashSet();
        if (addedRoles.Count != expected.Count || !expected.SetEquals(addedRoles.Select(role => role.Id.Value))) return false;
        var ceiling = facts.Memberships.SelectMany(member => member.Roles.Where(role =>
                role.Scope == PermissionScope.Tenant && member.OrganizationId is null
                || role.Scope == PermissionScope.Organization && candidate.OrganizationId is not null
                    && member.OrganizationId == candidate.OrganizationId))
            .SelectMany(role => role.Grants).Where(definition => IdentityPolicyEvaluation.IsRegistered(definition, catalog)).ToHashSet();
        var targetScope = target.OrganizationId is null ? PermissionScope.Tenant : PermissionScope.Organization;
        return addedRoles.All(role => role.TenantId == target.TenantId && !role.IsDeleted && role.DeletedBy is null
            && role.PermissionScope == targetScope
            && role.Grants.All(grant =>
            {
                var definition = new PermissionDefinition(grant.PermissionScope, grant.PermissionKey);
                return definition.Scope == targetScope && IdentityPolicyEvaluation.IsRegistered(definition, catalog)
                    && ceiling.Any(held => held.Key == definition.Key && (held.Scope == definition.Scope
                        || targetScope == PermissionScope.Organization && held.Scope == PermissionScope.Tenant));
            }));
    }

    private static Guid? ExplicitTarget(object request) => request switch
    {
        GetMembershipDetailQuery detail => detail.MembershipId,
        SetMembershipProfileCommand profile => profile.MembershipId,
        ChangeMembershipStatusCommand status => status.MembershipId,
        ReplaceMembershipRoleAssignmentsCommand assignments => assignments.MembershipId,
        _ => null,
    };
}

internal sealed record MembershipAdministrationAssessment(ProtectedOperationDecision Decision, AuthorizationRefusalReason Reason)
{
    public override string ToString() => nameof(MembershipAdministrationAssessment);
}
