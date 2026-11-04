using LearnStack.Modules.Identity.Application.Contracts.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Identity.Application.Authorization;

/// <summary>Canonical live read admission and bounded self facts; this helper never mints authority.</summary>
public sealed class IdentityAdministrationReadAuthorization(IOperationPolicyCatalog catalog,
    IOperationInvocationContext invocations, ITenantContext context, IProtectedOperationAuthorizer authorizer,
    IIdentityAuthorizationReader factsReader, IAuthorizationRefusalReporter refusals)
{
    internal OperationInvocation? Current(object request) => invocations.CurrentInvocation is { } invocation
        && Matches(request, invocation) ? invocation : null;

    internal async Task<Result<OperationInvocation>> AdmitAsync(object request, CancellationToken cancellationToken)
    {
        var invocation = Current(request);
        if (invocation is null) return Fail<OperationInvocation>("lockey_forbidden");
        var result = await RecheckAsync(request, invocation, cancellationToken);
        return result.IsSuccess ? Result.Ok(invocation) : Result<OperationInvocation>.Fail(result.Error);
    }

    internal async Task<Result<None>> RecheckAsync(object request, OperationInvocation invocation, CancellationToken cancellationToken)
    {
        if (!Matches(request, invocation)) return Fail<None>("lockey_forbidden");
        var decision = await authorizer.AuthorizeAsync(invocation, invocation.Policy, invocation.Candidate!, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Matches(request, invocation) || !decision.IsActive || !ReferenceEquals(decision.Invocation, invocation))
            return Fail<None>("lockey_dependency_unavailable");
        if (decision.Outcome == ProtectedOperationOutcome.Allowed)
        {
            refusals.ReportRecheck(decision);
            return Result.Ok(None.Value);
        }
        if (decision.Outcome is ProtectedOperationOutcome.NotCovered or ProtectedOperationOutcome.Forbidden)
            refusals.ReportResourceRefusal(decision, AuthorizationRefusalReason.Capability);
        return Fail<None>(decision.Outcome switch
        {
            ProtectedOperationOutcome.NotCovered => "lockey_not_found",
            ProtectedOperationOutcome.Forbidden => "lockey_forbidden",
            _ => "lockey_dependency_unavailable",
        });
    }

    internal async Task<Result<IdentityAuthorizationResult>> ReadFactsAsync(object request, OperationInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (!Matches(request, invocation)) return Fail<IdentityAuthorizationResult>("lockey_forbidden");
        var facts = await factsReader.ReadAsync(invocation.Candidate!, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Matches(request, invocation) || facts.Outcome == IdentityAuthorizationOutcome.Unavailable)
            return Fail<IdentityAuthorizationResult>("lockey_dependency_unavailable");
        if (!facts.Covers || facts.Actor != context.UserId || facts.TenantId != context.TenantId
            || facts.OrganizationId != context.OrganizationId || facts.Memberships.IsDefaultOrEmpty)
            return Fail<IdentityAuthorizationResult>("lockey_not_found");
        if (facts.Memberships.Any(member => member.OrganizationId is not null && member.OrganizationId != context.OrganizationId)
            || facts.Memberships.GroupBy(member => member.OrganizationId).Any(group => group.Count() != 1))
            return Fail<IdentityAuthorizationResult>("lockey_dependency_unavailable");
        return Result.Ok(facts);
    }

    private bool Matches(object request, OperationInvocation invocation)
    {
        var own = request is GetOwnCapabilitiesQuery or GetOwnMembershipContextsQuery;
        return (own || request is ListOrganizationMembershipsQuery or ListRolesQuery or GetRoleDetailQuery or GetPermissionRegistryQuery)
            && ReferenceEquals(invocations.CurrentInvocation, invocation)
            && invocation is { IsActive: true, EntryKind: OperationEntryKind.Application, TrustedSource: null }
            && ReferenceEquals(invocation.Operation, request) && ReferenceEquals(invocation.Context, context)
            && invocation.Candidate is { IsActive: true, SourceKind: AuthorizationSourceKind.Http } candidate
            && catalog.TryGet(request.GetType(), out var policy) && ReferenceEquals(invocation.Policy, policy)
            && policy.Kind == (own ? OperationPolicyKind.OwnMembershipRead : OperationPolicyKind.Protected)
            && !policy.RequiresOrglessWrite && policy.RequiresOrganization == (request is ListOrganizationMembershipsQuery)
            && policy.ReadScope == OperationReadScope.Scoped
            && policy.Alternatives.All(clause => clause.ReadScope == OperationReadScope.Scoped)
            && context.IsResolved && StronglyTypedId.IsAssigned(context.TenantId) && context.TenantId != TenantId.PlatformSentinel
            && context.UserId is { } actor && StronglyTypedId.IsAssigned(actor) && actor != UserId.SystemActor
            && context.Origin is TenantContextOrigin.HostAndClaim or TenantContextOrigin.ClaimAndMembership
                or TenantContextOrigin.HostAndNativeSessionScope
            && candidate.Actor == actor && candidate.TenantId == context.TenantId && candidate.OrganizationId == context.OrganizationId
            && invocation.Actor == actor && invocation.TenantId == context.TenantId && invocation.OrganizationId == context.OrganizationId
            && invocations.CurrentDecision is { IsActive: true, Outcome: ProtectedOperationOutcome.Allowed } admitted
            && ReferenceEquals(admitted.Invocation, invocation);
    }

    internal static Result<T> Fail<T>(string key) => Result<T>.Fail(new Error(LocalizedMessage.Of(key)));
}
