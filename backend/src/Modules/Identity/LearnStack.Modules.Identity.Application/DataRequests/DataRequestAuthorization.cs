using LearnStack.Modules.Identity.Application.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Identity.Application.DataRequests;

/// <summary>
/// The live-session data-request operations' own admission and fresh recheck
/// (<see href="../../../../../docs/decisions/0106-subject-data-requests-authority-and-status.md">ADR-0106 § 3–§ 4</see>):
/// the subject's key-less <see cref="OperationPolicyKind.OwnDataRequest"/> operations and the administrator's
/// <see cref="OperationPolicyKind.Protected"/> ones, each bound to the exact current invocation and re-evaluated
/// after the handler's fences, so authority lost while waiting refuses the act.
/// </summary>
/// <remarks>
/// It confers nothing on its own: the policy decided coverage before the handler ran, and every handler still
/// confines its rows to the announced group and to the subject or requester it serves.
/// </remarks>
public sealed class DataRequestAuthorization(IOperationInvocationContext invocations, IOperationPolicyCatalog catalog,
    IProtectedOperationAuthorizer authorizer, ITenantContext context, IAuthorizationRefusalReporter refusals)
{
    internal async Task<Result<OperationInvocation>> AdmitAsync(object request, CancellationToken cancellationToken)
    {
        if (invocations.CurrentInvocation is not { } invocation || !Matches(request, invocation)) return DataRequestRules.Fail<OperationInvocation>("lockey_forbidden");
        var check = await RecheckAsync(request, invocation, cancellationToken);
        return check.IsFailure ? Result<OperationInvocation>.Fail(check.Error) : Result.Ok(invocation);
    }

    internal async Task<Result<None>> RecheckAsync(object request, OperationInvocation invocation, CancellationToken cancellationToken)
    {
        if (!Matches(request, invocation)) return DataRequestRules.Fail<None>("lockey_forbidden");
        var decision = await authorizer.AuthorizeAsync(invocation, invocation.Policy, invocation.Candidate!, cancellationToken);
        if (!Matches(request, invocation) || !decision.IsActive || !ReferenceEquals(decision.Invocation, invocation)) return DataRequestRules.Fail<None>("lockey_dependency_unavailable");
        if (decision.Outcome == ProtectedOperationOutcome.Allowed) { refusals.ReportRecheck(decision); return Result.Ok(None.Value); }
        if (decision.Outcome is ProtectedOperationOutcome.Forbidden or ProtectedOperationOutcome.NotCovered)
            refusals.ReportResourceRefusal(decision, AuthorizationRefusalReason.Capability);
        return DataRequestRules.Fail<None>(decision.Outcome == ProtectedOperationOutcome.Forbidden ? "lockey_forbidden"
            : decision.Outcome == ProtectedOperationOutcome.NotCovered ? "lockey_not_found" : "lockey_dependency_unavailable");
    }

    /// <summary>The administrator's request types; every other admitted type is the subject's own.</summary>
    internal static bool IsAdministration(object request) => request is CreateDataExportForSubjectCommand
        or CreateDataErasureForSubjectCommand or ListDataRequestsQuery or GetDataRequestQuery or CancelDataRequestCommand
        or RevokeDataExportCommand or ReadDataRequestReceiptQuery;

    /// <summary>The subject's request types: exactly the closed set an <see cref="OperationPolicyKind.OwnDataRequest"/> policy may carry.</summary>
    private static bool IsOwn(object request) =>
        request is not null && IdentityPolicyEvaluation.OwnDataRequestOperations.ContainsKey(request.GetType());

    private bool Matches(object request, OperationInvocation invocation) =>
        (IsAdministration(request) || IsOwn(request))
        && ReferenceEquals(invocations.CurrentInvocation, invocation) && invocation.IsActive
        && invocation.EntryKind == OperationEntryKind.Application && invocation.TrustedSource is null
        && ReferenceEquals(invocation.Operation, request) && ReferenceEquals(invocation.Context, context)
        && catalog.TryGet(request.GetType(), out var policy) && ReferenceEquals(policy, invocation.Policy)
        && (IsAdministration(request)
            ? policy.Kind == OperationPolicyKind.Protected && policy.ReadScope == OperationReadScope.Scoped
                && policy.Alternatives.All(clause => clause.ReadScope == OperationReadScope.Scoped)
            : policy.Kind == OperationPolicyKind.OwnDataRequest)
        && invocation.Candidate is { IsActive: true, SourceKind: AuthorizationSourceKind.Http } candidate
        && context.IsResolved && context.TenantId != TenantId.PlatformSentinel && context.UserId is { } actor && actor != UserId.SystemActor
        && context.Origin is TenantContextOrigin.HostAndClaim or TenantContextOrigin.ClaimAndMembership or TenantContextOrigin.HostAndNativeSessionScope
        && candidate.Actor == actor && candidate.TenantId == context.TenantId && candidate.OrganizationId == context.OrganizationId
        && invocation.Actor == actor && invocation.TenantId == context.TenantId && invocation.OrganizationId == context.OrganizationId
        && invocations.CurrentDecision is { IsActive: true, Outcome: ProtectedOperationOutcome.Allowed } decision && ReferenceEquals(decision.Invocation, invocation);
}
