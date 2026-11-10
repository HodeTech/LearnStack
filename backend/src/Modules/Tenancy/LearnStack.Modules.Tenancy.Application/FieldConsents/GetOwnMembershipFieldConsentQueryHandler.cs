using LearnStack.Modules.Customization.Application.Contracts.CustomFields;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.FieldConsents;

public sealed class GetOwnMembershipFieldConsentQueryHandler(IOperationPolicyCatalog catalog,
    IOperationInvocationContext invocations, IProtectedOperationAuthorizer authorizer, IAuthorizationRefusalReporter refusals,
    ITenantContext context, IOwnMembershipFieldConsentHistory history, IExactCustomFieldDefinitionReader definitions)
    : IRequestHandler<GetOwnMembershipFieldConsentQuery, Result<MembershipFieldConsentCurrent>>
{
    public async Task<Result<MembershipFieldConsentCurrent>> Handle(GetOwnMembershipFieldConsentQuery request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MembershipId == Guid.Empty || request.MembershipId.Version != 7 || request.DefinitionId == Guid.Empty || request.DefinitionId.Version != 7 || request.DefinitionVersion < 1)
            return Fail("lockey_validation_failed");
        if (!await AuthorizedAsync(request, cancellationToken)) return Fail("lockey_forbidden");
        var row = await history.ReadExactAsync(context.UserId!.Value, request.MembershipId, request.DefinitionId,
            request.DefinitionVersion, cancellationToken);
        if (row is not null)
        {
            var definition = await definitions.ReadAsync(new(row.DefinitionId, row.DefinitionVersion),
                CustomFieldResolution.ExistingPin, null, cancellationToken);
            if (definition?.Notices is null || !definition.Notices.TryGetValue(row.NoticeLocale, out var text)
                || MembershipFieldConsentNotice.ComputeHash(text) != row.NoticeHash)
                return Fail("lockey_dependency_unavailable");
            row = row with { NoticeText = text };
        }
        if (!await AuthorizedAsync(request, cancellationToken)) return Fail("lockey_forbidden");
        return Result.Ok(new MembershipFieldConsentCurrent(row));
    }

    private async Task<bool> AuthorizedAsync(GetOwnMembershipFieldConsentQuery request, CancellationToken cancellationToken)
    {
        if (invocations.CurrentInvocation is not { IsActive: true, EntryKind: OperationEntryKind.Application, TrustedSource: null } invocation
            || !ReferenceEquals(invocation.Operation, request) || !ReferenceEquals(invocation.Context, context)
            || !catalog.TryGet(request.GetType(), out var policy) || !ReferenceEquals(policy, invocation.Policy)
            || policy.Kind != OperationPolicyKind.OwnMembershipRead || policy.ReadScope != OperationReadScope.Scoped
            || invocation.Candidate is not { IsActive: true, SourceKind: AuthorizationSourceKind.Http } candidate
            || !context.IsResolved || context.UserId is not { } actor || actor == UserId.SystemActor
            || context.TenantId == TenantId.PlatformSentinel || candidate.Actor != actor || candidate.TenantId != context.TenantId
            || candidate.OrganizationId != context.OrganizationId || invocation.Actor != actor || invocation.TenantId != context.TenantId
            || invocation.OrganizationId != context.OrganizationId
            || context.Origin is not (TenantContextOrigin.HostAndClaim or TenantContextOrigin.ClaimAndMembership or TenantContextOrigin.HostAndNativeSessionScope)
            || invocations.CurrentDecision is not { IsActive: true, Outcome: ProtectedOperationOutcome.Allowed } current
            || !ReferenceEquals(current.Invocation, invocation) || !current.MembershipIds.Contains(request.MembershipId)) return false;
        var fresh = await authorizer.AuthorizeAsync(invocation, policy, candidate, cancellationToken);
        if (!invocation.IsActive || !candidate.IsActive || !fresh.IsActive || !ReferenceEquals(fresh.Invocation, invocation)) return false;
        if (fresh.Outcome == ProtectedOperationOutcome.Allowed && fresh.MembershipIds.Contains(request.MembershipId))
        {
            refusals.ReportRecheck(fresh);
            return true;
        }
        if (fresh.Outcome is ProtectedOperationOutcome.Forbidden or ProtectedOperationOutcome.NotCovered)
            refusals.ReportResourceRefusal(fresh, AuthorizationRefusalReason.Capability);
        return false;
    }

    private static Result<MembershipFieldConsentCurrent> Fail(string code) => OwnFieldConsentAuthorization.Failure<MembershipFieldConsentCurrent>(code);
}
