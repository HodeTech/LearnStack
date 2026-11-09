using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Customization.Application.CustomFields;

/// <summary>Exact human invocation, retained Tenant fence, then fresh authority.</summary>
public sealed class CustomFieldDefinitionAdmission(IOperationInvocationContext invocations,
    IOperationPolicyCatalog catalog, ITenantContext context, IProtectedOperationAuthorizer authorizer,
    IAuthorizationRefusalReporter refusals, IIdentityMutationLocks locks, IUnitOfWork unit)
{
    internal async Task<Result<IdentityMutationLockPlan>> PrepareAsync(object request, CancellationToken token)
    {
        if (invocations.CurrentInvocation is not { } invocation || !Matches(request, invocation, true)
            || unit.Mode != TransactionMode.ReadWrite || !unit.HasActiveTransaction || unit.IsRollbackOnly)
            return Fail<IdentityMutationLockPlan>("lockey_forbidden");
        var plan = new IdentityMutationLockPlan([], context.TenantId, IdentityTenantLockMode.Exclusive);
        await locks.AcquireAsync(plan, token);
        locks.RequireCurrent(plan);
        var check = await RecheckAsync(request, invocation, true, token);
        return check.IsFailure ? Result<IdentityMutationLockPlan>.Fail(check.Error) : Result.Ok(plan);
    }

    internal async Task<Result<None>> ReadAsync(object request, CancellationToken token)
    {
        if (invocations.CurrentInvocation is not { } invocation || !Matches(request, invocation, false)) return Fail<None>("lockey_forbidden");
        return await RecheckAsync(request, invocation, false, token);
    }

    internal bool RequireCurrent(object request, IdentityMutationLockPlan plan)
    {
        if (invocations.CurrentInvocation is not { } invocation || !Matches(request, invocation, true))
        { if (unit.HasActiveTransaction) unit.MarkRollbackOnly(); return false; }
        locks.RequireCurrent(plan);
        return true;
    }

    private bool Matches(object request, OperationInvocation invocation, bool write) =>
        ReferenceEquals(invocations.CurrentInvocation, invocation) && invocation is
        { IsActive: true, EntryKind: OperationEntryKind.Application, TrustedSource: null }
        && ReferenceEquals(invocation.Operation, request) && ReferenceEquals(invocation.Context, context)
        && invocation.Candidate is { IsActive: true, SourceKind: AuthorizationSourceKind.Http } candidate
        && catalog.TryGet(request.GetType(), out var policy) && ReferenceEquals(policy, invocation.Policy)
        && policy.Kind == OperationPolicyKind.Protected && policy.RequiresOrglessWrite == write && !policy.RequiresOrganization
        && policy.ReadScope == OperationReadScope.Scoped
        && policy.Alternatives.Length == 1 && policy.Alternatives[0].RequiredPermissions.Length == 1
        && policy.RequiredPermission == BoundedCustomFieldPermissions.Definition
        && context.IsResolved && context.TenantId != TenantId.PlatformSentinel && (!write || context.OrganizationId is null)
        && context.UserId is { } actor && StronglyTypedId.IsAssigned(actor) && actor != UserId.SystemActor
        && context.Origin is TenantContextOrigin.HostAndClaim or TenantContextOrigin.ClaimAndMembership or TenantContextOrigin.HostAndNativeSessionScope
        && candidate.Actor == actor && candidate.TenantId == context.TenantId && candidate.OrganizationId == context.OrganizationId
        && invocation.Actor == actor && invocation.TenantId == context.TenantId && invocation.OrganizationId == context.OrganizationId
        && invocations.CurrentDecision is { Outcome: ProtectedOperationOutcome.Allowed, IsActive: true } admitted
        && ReferenceEquals(admitted.Invocation, invocation);

    private async Task<Result<None>> RecheckAsync(object request, OperationInvocation invocation, bool write, CancellationToken token)
    {
        if (!Matches(request, invocation, write)) return Fail<None>("lockey_forbidden");
        var decision = await authorizer.AuthorizeAsync(invocation, invocation.Policy, invocation.Candidate!, token);
        token.ThrowIfCancellationRequested();
        if (!Matches(request, invocation, write) || !ReferenceEquals(decision.Invocation, invocation) || !decision.IsActive)
            return Fail<None>("lockey_dependency_unavailable");
        if (decision.Outcome == ProtectedOperationOutcome.Allowed) { refusals.ReportRecheck(decision); return Result.Ok(None.Value); }
        if (decision.Outcome is ProtectedOperationOutcome.Forbidden or ProtectedOperationOutcome.NotCovered)
            refusals.ReportResourceRefusal(decision, AuthorizationRefusalReason.Capability);
        return Fail<None>(decision.Outcome switch
        {
            ProtectedOperationOutcome.Forbidden => "lockey_forbidden",
            ProtectedOperationOutcome.NotCovered => "lockey_not_found",
            _ => "lockey_dependency_unavailable"
        });
    }

    internal static Result<T> Fail<T>(string code) => Result<T>.Fail(new Error(new LocalizedMessage(code)));
}
