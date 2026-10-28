using LearnStack.Modules.Identity.Application.Contracts.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Identity.Application.Authorization;

/// <summary>Fresh committed authoring authority for one current grant act; no ambient facts or cached ceiling.</summary>
public sealed class RoleGrantAuthorization(IIdentityAuthorizationReader reader, IOperationPolicyCatalog catalog,
    IOperationInvocationContext invocations, ITenantContext context, IAuthorizationRefusalReporter refusals)
    : ProtectedOperationDecision.MintingSource
{
    internal bool Matches(object request, OperationInvocation invocation) =>
        ReferenceEquals(invocations.CurrentInvocation, invocation) && invocation.IsActive
        && invocation.EntryKind == OperationEntryKind.Application && ReferenceEquals(invocation.Operation, request)
        && ReferenceEquals(invocation.Context, context) && invocation.Candidate is { IsActive: true } candidate
        && request is ChangeRoleGrantsCommand or AdoptRoleGrantManifestCommand
        && catalog.TryGet(request.GetType(), out var policy) && ReferenceEquals(invocation.Policy, policy)
        && policy is { Kind: OperationPolicyKind.Protected, ReadScope: OperationReadScope.Scoped, RequiresOrglessWrite: true }
        && policy.RequiredPermission == IdentityOperationPolicySource.RoleAdministration
        && context.IsResolved && context.OrganizationId is null && context.TenantId != TenantId.PlatformSentinel
        && context.UserId is { } actor && StronglyTypedId.IsAssigned(actor) && actor != UserId.SystemActor
        && context.Origin is TenantContextOrigin.HostAndClaim or TenantContextOrigin.ClaimAndMembership
            or TenantContextOrigin.HostAndNativeSessionScope
        && candidate.Actor == actor && candidate.TenantId == context.TenantId && candidate.OrganizationId is null
        && invocation.Actor == actor && invocation.TenantId == context.TenantId && invocation.OrganizationId is null
        && invocations.CurrentDecision is { Outcome: ProtectedOperationOutcome.Allowed, IsActive: true } admitted
        && ReferenceEquals(admitted.Invocation, invocation);

    internal async Task<RoleGrantAssessment> RecheckAsync(object request, OperationInvocation invocation, Role target,
        IReadOnlyCollection<PermissionDefinition> desired, CancellationToken cancellationToken)
    {
        var candidate = invocation.Candidate ?? throw new ArgumentException("Role grant invocation has no human source.", nameof(invocation));
        if (!Matches(request, invocation)) return Refused(ProtectedOperationOutcome.Unavailable);
        var facts = await reader.ReadAsync(candidate, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Matches(request, invocation)) return Refused(ProtectedOperationOutcome.Unavailable);
        if (facts.Outcome == IdentityAuthorizationOutcome.Unavailable) return Refused(ProtectedOperationOutcome.Unavailable);
        if (!facts.Covers || facts.Actor != candidate.Actor || facts.TenantId != candidate.TenantId
            || facts.OrganizationId is not null || facts.Memberships.IsDefaultOrEmpty)
            return Refused(ProtectedOperationOutcome.NotCovered);
        if (facts.Memberships.Any(member => member.OrganizationId is not null)) return Refused(ProtectedOperationOutcome.Unavailable);
        var members = facts.Memberships.Select(member => member.MembershipId).Distinct().ToArray();
        var roles = facts.Memberships.SelectMany(member => member.Roles).ToArray();
        var roleIds = roles.Select(role => role.RoleId).Distinct().ToArray();
        var tenantGrants = roles.Where(role => role.Scope == PermissionScope.Tenant).SelectMany(role => role.Grants)
            .Where(Registered).Distinct().ToArray();
        var eligible = roles.Any(role => role.BuiltInKey == BuiltInRoleKey.TenantAdmin && role.Scope == PermissionScope.Tenant
            && IdentityFoundationManifest.Version1.TenantAdminKernel.All(definition => role.Grants.Contains(definition) && Registered(definition)));
        if (!eligible || !tenantGrants.Contains(IdentityOperationPolicySource.RoleAdministration))
            return Refused(ProtectedOperationOutcome.Forbidden, AuthorizationRefusalReason.Capability, members, roleIds);
        if (!target.CanReplaceGrants(desired) || desired.Any(definition => !Registered(definition))
            || desired.Where(definition => !target.Grants.Any(grant => grant.PermissionScope == definition.Scope && grant.PermissionKey == definition.Key))
                .Any(definition => !tenantGrants.Any(held => held.Scope == PermissionScope.Tenant && held.Key == definition.Key)))
            return Refused(ProtectedOperationOutcome.Forbidden, AuthorizationRefusalReason.Delegation, members, roleIds);
        return new(ForDecision(invocation, candidate, ProtectedOperationOutcome.Allowed, members, roleIds), AuthorizationRefusalReason.Capability);

        RoleGrantAssessment Refused(ProtectedOperationOutcome outcome, AuthorizationRefusalReason reason = AuthorizationRefusalReason.Capability,
            IEnumerable<Guid>? membershipIds = null, IEnumerable<Guid>? roleIdentifiers = null) =>
            new(ForDecision(invocation, candidate, outcome, membershipIds, roleIdentifiers), reason);
    }

    internal void ReportRecheck(RoleGrantAssessment assessment) => refusals.ReportRecheck(assessment.Decision);
    internal void ReportRefusal(RoleGrantAssessment assessment) => refusals.ReportResourceRefusal(assessment.Decision, assessment.Reason);

    private bool Registered(PermissionDefinition definition) => definition is { IsActive: true }
        && IdentityFoundationManifest.Version1.IsActive(definition.Scope, definition.Key)
        && catalog.TryGetDefinition(definition.Scope, definition.Key, out var registered)
        && registered.Assignable && registered.Definition == definition && registered.Definition.IsActive;
}

internal sealed record RoleGrantAssessment(ProtectedOperationDecision Decision, AuthorizationRefusalReason Reason);
