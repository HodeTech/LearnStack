using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Identity.Application.Authorization;

/// <summary>Receipt disclosure reuses the live evaluator without minting authority or applying mutation predicates.</summary>
public sealed class IdentityReceiptAuthorization(IOperationPolicyCatalog catalog, IOperationInvocationContext invocations,
    ITenantContext context, IProtectedOperationAuthorizer authorizer, IAuthorizationRefusalReporter refusals)
{
    internal async Task<Result<OperationInvocation>> AdmitAsync(object request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (invocations.CurrentInvocation is not { } invocation || !Matches(request, invocation))
            return Fail<OperationInvocation>("lockey_forbidden");
        var target = request switch
        {
            ReadMembershipReceiptQuery query => query.MembershipId,
            ReadMemberProfileReceiptQuery query => query.MembershipId,
            ReadOwnMembershipProfileReceiptQuery query => query.MembershipId,
            ReadRoleReceiptQuery query => query.RoleId,
            _ => Guid.Empty,
        };
        if (target == Guid.Empty || target.Version != 7) return Fail<OperationInvocation>("lockey_validation_failed");
        var check = await RecheckAsync(request, invocation, cancellationToken);
        return check.IsFailure ? Result<OperationInvocation>.Fail(check.Error) : Result.Ok(invocation);
    }

    internal async Task<Result<None>> RecheckAsync(object request, OperationInvocation invocation, CancellationToken cancellationToken)
    {
        if (!Matches(request, invocation)) return Fail<None>("lockey_forbidden");
        var decision = await authorizer.AuthorizeAsync(invocation, invocation.Policy, invocation.Candidate!, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Matches(request, invocation) || !ReferenceEquals(decision.Invocation, invocation) || !decision.IsActive)
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

    private bool Matches(object request, OperationInvocation invocation) =>
        request is ReadMembershipReceiptQuery or ReadMemberProfileReceiptQuery or ReadOwnMembershipProfileReceiptQuery or ReadRoleReceiptQuery
        && ReferenceEquals(invocations.CurrentInvocation, invocation)
        && invocation is { IsActive: true, EntryKind: OperationEntryKind.Application, TrustedSource: null }
        && ReferenceEquals(invocation.Operation, request) && ReferenceEquals(invocation.Context, context)
        && invocation.Candidate is { IsActive: true, SourceKind: AuthorizationSourceKind.Http } candidate
        && catalog.TryGet(request.GetType(), out var policy) && ReferenceEquals(invocation.Policy, policy)
        && policy.RequestType == request.GetType() && !policy.RequiresOrganization && !policy.RequiresOrglessWrite
        && policy.ReadScope == OperationReadScope.Scoped && policy.Alternatives.All(clause => clause.ReadScope == OperationReadScope.Scoped)
        && (request is ReadOwnMembershipProfileReceiptQuery ? policy.Kind == OperationPolicyKind.OwnMembershipRead
            : policy.Kind == OperationPolicyKind.Protected)
        && context.IsResolved && StronglyTypedId.IsAssigned(context.TenantId) && context.TenantId != TenantId.PlatformSentinel
        && context.UserId is { } actor && StronglyTypedId.IsAssigned(actor) && actor != UserId.SystemActor
        && context.Origin is TenantContextOrigin.HostAndClaim or TenantContextOrigin.ClaimAndMembership
            or TenantContextOrigin.HostAndNativeSessionScope
        && candidate.Actor == actor && candidate.TenantId == context.TenantId && candidate.OrganizationId == context.OrganizationId
        && invocation.Actor == actor && invocation.TenantId == context.TenantId && invocation.OrganizationId == context.OrganizationId
        && invocations.CurrentDecision is { Outcome: ProtectedOperationOutcome.Allowed, IsActive: true } admitted
        && ReferenceEquals(admitted.Invocation, invocation);

    internal static Result<T> Fail<T>(string key) => Result<T>.Fail(new Error(LocalizedMessage.Of(key)));
}
