using LearnStack.Modules.Identity.Application.Contracts.Authorization;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;

namespace LearnStack.Modules.Identity.Application.Authorization;

/// <summary>One fresh committed-state evaluation per act; no fact or permission decision cache.</summary>
public sealed class IdentityOperationAuthorizer(Func<IIdentityAuthorizationReader> reader, IOperationPolicyCatalog catalog, IEnumerable<IInvitationEntryScopeSource>? invitationSources = null)
    : ProtectedOperationDecision.MintingSource, IProtectedOperationAuthorizer
{
    public async Task<ProtectedOperationDecision> AuthorizeAsync(OperationInvocation invocation, OperationPolicy policy,
        AuthorizationCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        if (invocation.Candidate is not { } original || invocation.Policy.Kind is not
            (OperationPolicyKind.AccountRead or OperationPolicyKind.Protected or OperationPolicyKind.NativeSessionAdmission or OperationPolicyKind.OwnMembershipRead or OperationPolicyKind.InvitationEntry))
            throw new ArgumentException("Protected operation invocation provenance is invalid.", nameof(invocation));
        if (!ReferenceEquals(original, candidate))
            return ForDecision(invocation, original, ProtectedOperationOutcome.NotCovered);
        if (!invocation.IsActive || !candidate.IsActive)
            return ForDecision(invocation, candidate, ProtectedOperationOutcome.Unavailable);
        if (!Matches(invocation, policy, candidate))
            return ForDecision(invocation, candidate, ProtectedOperationOutcome.NotCovered);
        if (policy.Kind == OperationPolicyKind.InvitationEntry)
        {
            var witnesses = (invitationSources ?? []).Select(source => source.ForInvocation(invocation))
                .Where(witness => witness is not null).Cast<InvitationEntryScopeWitness>().Take(2).ToArray();
            return witnesses.Length == 1 && witnesses[0].IsActive
                ? ForDecision(invocation, candidate, ProtectedOperationOutcome.Allowed, invitationWitness: witnesses[0])
                : ForDecision(invocation, candidate, ProtectedOperationOutcome.NotCovered);
        }
        var facts = await reader().ReadAsync(candidate, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!invocation.IsActive || !candidate.IsActive)
            return ForDecision(invocation, candidate, ProtectedOperationOutcome.Unavailable);
        if (!Matches(invocation, policy, candidate) || facts.Actor != candidate.Actor || facts.TenantId != candidate.TenantId
            || facts.OrganizationId != candidate.OrganizationId)
            return ForDecision(invocation, candidate, ProtectedOperationOutcome.NotCovered);
        var assessment = IdentityPolicyEvaluation.Evaluate(policy, candidate, facts, catalog);
        return ForDecision(invocation, candidate, assessment.Outcome, assessment.MembershipIds, assessment.RoleIds,
            assessment.MatchedClause);
    }

    private bool Matches(OperationInvocation invocation, OperationPolicy policy, AuthorizationCandidate candidate) =>
        ReferenceEquals(invocation.Policy, policy)
        && ReferenceEquals(invocation.Candidate, candidate)
        && (invocation.EntryKind == OperationEntryKind.Http
            || invocation.EntryKind == OperationEntryKind.Application && invocation.Operation.GetType() == policy.RequestType)
        && catalog.TryGet(policy.RequestType, out var registered) && ReferenceEquals(registered, policy)
        && policy.Kind is OperationPolicyKind.AccountRead or OperationPolicyKind.Protected or OperationPolicyKind.NativeSessionAdmission or OperationPolicyKind.OwnMembershipRead or OperationPolicyKind.InvitationEntry
        && invocation.Context.IsResolved && invocation.Context.UserId == candidate.Actor
        && candidate.Actor != UserId.SystemActor && candidate.TenantId != TenantId.PlatformSentinel
        && invocation.Actor == candidate.Actor && invocation.TenantId == candidate.TenantId
        && invocation.OrganizationId == candidate.OrganizationId && invocation.Context.TenantId == candidate.TenantId
        && invocation.Context.OrganizationId == candidate.OrganizationId;
}
