using LearnStack.Modules.Identity.Application.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Identity.Application.Invitations;

public sealed class InvitationAdministrationAuthorization(IOperationInvocationContext invocations, IOperationPolicyCatalog catalog,
    IProtectedOperationAuthorizer authorizer, IIdentityAuthorizationReader facts, ITenantContext context, IAuthorizationRefusalReporter refusals)
{
    internal async Task<Result<OperationInvocation>> AdmitAsync(object request, CancellationToken cancellationToken)
    {
        if (invocations.CurrentInvocation is not { } invocation || !Matches(request, invocation)) return Fail<OperationInvocation>("lockey_forbidden");
        var check = await RecheckAsync(request, invocation, cancellationToken);
        return check.IsFailure ? Result<OperationInvocation>.Fail(check.Error) : Result.Ok(invocation);
    }
    internal async Task<Result<None>> RecheckAsync(object request, OperationInvocation invocation, CancellationToken cancellationToken)
    {
        if (!Matches(request, invocation)) return Fail<None>("lockey_forbidden");
        var decision = await authorizer.AuthorizeAsync(invocation, invocation.Policy, invocation.Candidate!, cancellationToken);
        if (!Matches(request, invocation) || !decision.IsActive || !ReferenceEquals(decision.Invocation, invocation)) return Fail<None>("lockey_dependency_unavailable");
        if (decision.Outcome == ProtectedOperationOutcome.Allowed) { refusals.ReportRecheck(decision); return Result.Ok(None.Value); }
        if (decision.Outcome is ProtectedOperationOutcome.Forbidden or ProtectedOperationOutcome.NotCovered)
            refusals.ReportResourceRefusal(decision, AuthorizationRefusalReason.Capability);
        return Fail<None>(decision.Outcome == ProtectedOperationOutcome.Forbidden ? "lockey_forbidden"
            : decision.Outcome == ProtectedOperationOutcome.NotCovered ? "lockey_not_found" : "lockey_dependency_unavailable");
    }
    internal async Task<Result<bool>> CanDelegateAsync(object request, OperationInvocation invocation, Role role, CancellationToken cancellationToken)
    {
        if (!Matches(request, invocation)) return Fail<bool>("lockey_dependency_unavailable");
        var current = await facts.ReadAsync(invocation.Candidate!, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Matches(request, invocation) || current.Outcome == IdentityAuthorizationOutcome.Unavailable)
            return Fail<bool>("lockey_dependency_unavailable");
        if (!current.Covers || current.Actor != context.UserId || current.TenantId != context.TenantId
            || current.OrganizationId != context.OrganizationId || role.TenantId != context.TenantId || role.IsDeleted || role.DeletedBy is not null
            || role.PermissionScope != (context.OrganizationId is null ? PermissionScope.Tenant : PermissionScope.Organization)) return Result.Ok(false);
        var ceiling = current.Memberships.Where(member => member.OrganizationId is null || member.OrganizationId == context.OrganizationId)
            .SelectMany(member => member.Roles.Where(held => held.Scope == PermissionScope.Tenant && member.OrganizationId is null
                || held.Scope == PermissionScope.Organization && context.OrganizationId is not null && member.OrganizationId == context.OrganizationId))
            .SelectMany(held => held.Grants).Where(pair => IdentityPolicyEvaluation.IsRegistered(pair, catalog)).ToHashSet();
        return Result.Ok(role.Grants.All(grant =>
        {
            var pair = new PermissionDefinition(grant.PermissionScope, grant.PermissionKey);
            return pair.Scope == role.PermissionScope && IdentityPolicyEvaluation.IsRegistered(pair, catalog)
                && ceiling.Any(held => held.Key == pair.Key && (held.Scope == pair.Scope
                    || pair.Scope == PermissionScope.Organization && held.Scope == PermissionScope.Tenant));
        }));
    }
    private bool Matches(object request, OperationInvocation invocation) =>
        request is ListInvitationsQuery or GetInvitationQuery or ListInvitableRolesQuery or CreateInvitationCommand
            or RevokeInvitationCommand or ReissueInvitationCommand or ReadInvitationReceiptQuery
        && ReferenceEquals(invocations.CurrentInvocation, invocation) && invocation.IsActive
        && invocation.EntryKind == OperationEntryKind.Application && invocation.TrustedSource is null
        && ReferenceEquals(invocation.Operation, request) && ReferenceEquals(invocation.Context, context)
        && catalog.TryGet(request.GetType(), out var policy) && ReferenceEquals(policy, invocation.Policy)
        && policy.Kind == OperationPolicyKind.Protected && policy.ReadScope == OperationReadScope.Scoped
        && policy.Alternatives.All(clause => clause.ReadScope == OperationReadScope.Scoped)
        && invocation.Candidate is { IsActive: true, SourceKind: AuthorizationSourceKind.Http } candidate
        && context.IsResolved && context.TenantId != TenantId.PlatformSentinel && context.UserId is { } actor && actor != UserId.SystemActor
        && context.Origin is TenantContextOrigin.HostAndClaim or TenantContextOrigin.ClaimAndMembership or TenantContextOrigin.HostAndNativeSessionScope
        && candidate.Actor == actor && candidate.TenantId == context.TenantId && candidate.OrganizationId == context.OrganizationId
        && invocation.Actor == actor && invocation.TenantId == context.TenantId && invocation.OrganizationId == context.OrganizationId
        && invocations.CurrentDecision is { IsActive: true, Outcome: ProtectedOperationOutcome.Allowed } decision && ReferenceEquals(decision.Invocation, invocation);
    internal static Result<T> Fail<T>(string key) => Result<T>.Fail(new Error(LocalizedMessage.Of(key)));
}
