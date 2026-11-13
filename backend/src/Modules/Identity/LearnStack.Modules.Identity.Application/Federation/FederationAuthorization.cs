using LearnStack.Modules.Identity.Application.Contracts.Federation;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Identity.Application.Federation;

/// <summary>
/// Fresh committed Tenant <c>identity.federation.admin</c> authority for one federation act in an orgless context.
/// The pipeline's admission is necessary, not sufficient: each handler rechecks under its tenant fence, so a
/// permission revoked since Begin refuses Complete and leaves the operation pending for Resume (ADR-0099).
/// </summary>
public sealed class FederationAuthorization(IOperationInvocationContext invocations, IOperationPolicyCatalog catalog,
    IProtectedOperationAuthorizer authorizer, ITenantContext context, IAuthorizationRefusalReporter refusals)
{
    internal static bool IsProtectedFederationRequest(object request) => request is BeginConfigureFederationCommand
        or BeginRotateFederationSecretCommand or BeginRenameFederationCommand or BeginFederationTestStartCommand
        or BeginDisableFederationCommand or BeginDeleteFederationCommand or ResumeFederationOperationCommand
        or CompleteFederationOperationCommand or EnableFederationCommand or ActivateFederationCommand
        or RecordFederationTestResultCommand or ReconcileFederationConnectionCommand or GetFederationConnectionQuery;

    internal async Task<Result<OperationInvocation>> AdmitAsync(object request, CancellationToken cancellationToken)
    {
        if (invocations.CurrentInvocation is not { } invocation || !Matches(request, invocation)) return Fail<OperationInvocation>("lockey_forbidden");
        var check = await RecheckAsync(request, invocation, cancellationToken);
        return check.IsFailure ? Result<OperationInvocation>.Fail(check.Error!) : Result.Ok(invocation);
    }

    internal async Task<Result<None>> RecheckAsync(object request, OperationInvocation invocation, CancellationToken cancellationToken)
    {
        if (!Matches(request, invocation)) return Fail<None>("lockey_forbidden");
        var decision = await authorizer.AuthorizeAsync(invocation, invocation.Policy, invocation.Candidate!, cancellationToken);
        if (!Matches(request, invocation) || !decision.IsActive || !ReferenceEquals(decision.Invocation, invocation))
            return Fail<None>("lockey_dependency_unavailable");
        if (decision.Outcome == ProtectedOperationOutcome.Allowed) { refusals.ReportRecheck(decision); return Result.Ok(None.Value); }
        if (decision.Outcome is ProtectedOperationOutcome.Forbidden or ProtectedOperationOutcome.NotCovered)
            refusals.ReportResourceRefusal(decision, AuthorizationRefusalReason.Capability);
        return Fail<None>(decision.Outcome == ProtectedOperationOutcome.Forbidden ? "lockey_forbidden"
            : decision.Outcome == ProtectedOperationOutcome.NotCovered ? "lockey_not_found" : "lockey_dependency_unavailable");
    }

    private bool Matches(object request, OperationInvocation invocation) =>
        IsProtectedFederationRequest(request)
        && ReferenceEquals(invocations.CurrentInvocation, invocation) && invocation.IsActive
        && invocation.EntryKind == OperationEntryKind.Application && invocation.TrustedSource is null
        && ReferenceEquals(invocation.Operation, request) && ReferenceEquals(invocation.Context, context)
        && catalog.TryGet(request.GetType(), out var policy) && ReferenceEquals(policy, invocation.Policy)
        && policy is { Kind: OperationPolicyKind.Protected, ReadScope: OperationReadScope.Scoped, RequiresOrglessWrite: true }
        && policy.RequiredPermission == FederationPermissions.Definition
        && invocation.Candidate is { IsActive: true, SourceKind: AuthorizationSourceKind.Http } candidate
        && context.IsResolved && context.OrganizationId is null && context.TenantId != TenantId.PlatformSentinel
        && context.UserId is { } actor && StronglyTypedId.IsAssigned(actor) && actor != UserId.SystemActor
        && context.Origin is TenantContextOrigin.HostAndClaim or TenantContextOrigin.ClaimAndMembership or TenantContextOrigin.HostAndNativeSessionScope
        && candidate.Actor == actor && candidate.TenantId == context.TenantId && candidate.OrganizationId is null
        && invocation.Actor == actor && invocation.TenantId == context.TenantId && invocation.OrganizationId is null
        && invocations.CurrentDecision is { IsActive: true, Outcome: ProtectedOperationOutcome.Allowed } decision
        && ReferenceEquals(decision.Invocation, invocation);

    internal static Result<T> Fail<T>(string key) => Result<T>.Fail(new Error(LocalizedMessage.Of(key)));
}
