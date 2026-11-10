using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Tenancy.Application.FieldConsents;

/// <summary>Matches one live grant invocation and freshly reevaluates it under the exclusive tenant fence.</summary>
public sealed class OwnFieldConsentAuthorization(IOperationPolicyCatalog catalog, IOperationInvocationContext invocations,
    ITenantContext context, IProtectedOperationAuthorizer authorizer, IAuthorizationRefusalReporter refusals,
    IIdentityMutationLocks locks)
{
    internal async Task<Result<OwnFieldConsentGrantAdmission>> AdmitGrantAsync(GrantOwnMembershipFieldConsentCommand request,
        CancellationToken cancellationToken)
    {
        if (invocations.CurrentInvocation is not { } invocation || !Matches(request, invocation)
            || invocations.CurrentDecision is not { MembershipIds.Length: 1 } original)
            return Failure<OwnFieldConsentGrantAdmission>("lockey_forbidden");
        var plan = new IdentityMutationLockPlan([], context.TenantId, IdentityTenantLockMode.Exclusive);
        await locks.AcquireAsync(plan, cancellationToken);
        if (!Matches(request, invocation)) return Failure<OwnFieldConsentGrantAdmission>("lockey_forbidden");
        locks.RequireCurrent(plan);
        var fresh = await authorizer.AuthorizeAsync(invocation, invocation.Policy, invocation.Candidate!, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Matches(request, invocation) || !ReferenceEquals(fresh.Invocation, invocation) || !fresh.IsActive)
            return Failure<OwnFieldConsentGrantAdmission>("lockey_dependency_unavailable");
        if (fresh.Outcome != ProtectedOperationOutcome.Allowed)
        {
            if (fresh.Outcome is ProtectedOperationOutcome.NotCovered or ProtectedOperationOutcome.Forbidden)
                refusals.ReportResourceRefusal(fresh, AuthorizationRefusalReason.Capability);
            return Failure<OwnFieldConsentGrantAdmission>(fresh.Outcome == ProtectedOperationOutcome.Unavailable
                ? "lockey_dependency_unavailable" : "lockey_forbidden");
        }
        refusals.ReportRecheck(fresh);
        if (fresh.MembershipIds.Length != 1 || fresh.MembershipIds[0] != original.MembershipIds[0])
            return Failure<OwnFieldConsentGrantAdmission>("lockey_forbidden");
        locks.RequireCurrent(plan);
        return Result.Ok(new OwnFieldConsentGrantAdmission(invocation, plan, fresh.MembershipIds[0], context.UserId!.Value));
    }

    internal bool IsCurrent(GrantOwnMembershipFieldConsentCommand request, OwnFieldConsentGrantAdmission admission)
    {
        if (!Matches(request, admission.Invocation) || context.UserId != admission.Actor) return false;
        locks.RequireCurrent(admission.Plan);
        return true;
    }

    private bool Matches(GrantOwnMembershipFieldConsentCommand request, OperationInvocation invocation) =>
        ReferenceEquals(invocations.CurrentInvocation, invocation)
        && invocation is { IsActive: true, EntryKind: OperationEntryKind.Application, TrustedSource: null }
        && ReferenceEquals(invocation.Operation, request) && ReferenceEquals(invocation.Context, context)
        && invocation.Candidate is { IsActive: true, SourceKind: AuthorizationSourceKind.Http } candidate
        && catalog.TryGet(typeof(GrantOwnMembershipFieldConsentCommand), out var policy)
        && ReferenceEquals(policy, invocation.Policy) && policy.Kind == OperationPolicyKind.OwnFieldConsentGrant
        && context.IsResolved && context.UserId is { } actor && StronglyTypedId.IsAssigned(actor) && actor != UserId.SystemActor
        && context.TenantId != TenantId.PlatformSentinel
        && context.Origin is TenantContextOrigin.HostAndClaim or TenantContextOrigin.ClaimAndMembership or TenantContextOrigin.HostAndNativeSessionScope
        && candidate.Actor == actor && candidate.TenantId == context.TenantId && candidate.OrganizationId == context.OrganizationId
        && invocation.Actor == actor && invocation.TenantId == context.TenantId && invocation.OrganizationId == context.OrganizationId
        && invocations.CurrentDecision is { Outcome: ProtectedOperationOutcome.Allowed, IsActive: true } decision
        && ReferenceEquals(decision.Invocation, invocation);

    internal static Result<T> Failure<T>(string code) => Result<T>.Fail(new Error(LocalizedMessage.Of(code)));
}

internal sealed record OwnFieldConsentGrantAdmission(OperationInvocation Invocation, IdentityMutationLockPlan Plan,
    Guid MembershipId, UserId Actor);
