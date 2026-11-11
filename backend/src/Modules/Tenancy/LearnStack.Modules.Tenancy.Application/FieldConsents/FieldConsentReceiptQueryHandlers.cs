using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.FieldConsents;

/// <summary>A receipt reads the original act only after fresh ordinary own-membership authorization.</summary>
public sealed class ReadOwnGrantedFieldConsentReceiptQueryHandler(IOwnFieldConsentReceiptReader reader,
    IOperationPolicyCatalog catalog, IOperationInvocationContext invocations, IProtectedOperationAuthorizer authorizer,
    IAuthorizationRefusalReporter refusals, ITenantContext context, IAuditSubject audit)
    : IRequestHandler<ReadOwnGrantedFieldConsentReceiptQuery, Result<None>>
{
    public async Task<Result<None>> Handle(ReadOwnGrantedFieldConsentReceiptQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.EvidenceId == Guid.Empty || request.EvidenceId.Version != 7) return Fail("lockey_validation_failed");
        if (invocations.CurrentInvocation is not { } invocation || !Matches(request, invocation)) return Fail("lockey_forbidden");
        var original = invocations.CurrentDecision!.MembershipIds;
        var before = await RecheckAsync(request, invocation, cancellationToken);
        if (before is null) return Fail("lockey_forbidden");
        var admitted = original.Intersect(before).ToArray();
        if (admitted.Length == 0) return Fail("lockey_forbidden");
        try
        {
            var target = await reader.ReadAsync(context.UserId!.Value, admitted, request.EvidenceId, cancellationToken);
            var after = await RecheckAsync(request, invocation, cancellationToken);
            if (after is null) return Fail("lockey_forbidden");
            if (target is null || target.Id.Value != request.EvidenceId || target.State != MembershipFieldConsentState.Granted
                || target.TenantId != context.TenantId || target.OrganizationId != context.OrganizationId
                || target.SubjectUserId != context.UserId || target.ActorUserId != context.UserId || !admitted.Contains(target.MembershipId)
                || !after.Contains(target.MembershipId)) return Fail("lockey_not_found");
            audit.Designate(target);
            return Result.Ok(None.Value);
        }
        catch (TenancyAdministrationStorageException) { return Fail("lockey_dependency_unavailable"); }
    }

    private async Task<IReadOnlyCollection<Guid>?> RecheckAsync(ReadOwnGrantedFieldConsentReceiptQuery request,
        OperationInvocation invocation, CancellationToken cancellationToken)
    {
        if (!Matches(request, invocation)) return null;
        var fresh = await authorizer.AuthorizeAsync(invocation, invocation.Policy, invocation.Candidate!, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Matches(request, invocation) || !fresh.IsActive || !ReferenceEquals(fresh.Invocation, invocation)) return null;
        if (fresh.Outcome == ProtectedOperationOutcome.Allowed)
        {
            refusals.ReportRecheck(fresh);
            return fresh.MembershipIds;
        }
        if (fresh.Outcome is ProtectedOperationOutcome.Forbidden or ProtectedOperationOutcome.NotCovered)
            refusals.ReportResourceRefusal(fresh, AuthorizationRefusalReason.Capability);
        return null;
    }

    private bool Matches(ReadOwnGrantedFieldConsentReceiptQuery request, OperationInvocation invocation) =>
        ReferenceEquals(invocations.CurrentInvocation, invocation)
        && invocation is { IsActive: true, EntryKind: OperationEntryKind.Application, TrustedSource: null }
        && ReferenceEquals(invocation.Operation, request) && ReferenceEquals(invocation.Context, context)
        && catalog.TryGet(request.GetType(), out var policy) && ReferenceEquals(policy, invocation.Policy)
        && policy.Kind == OperationPolicyKind.OwnMembershipRead && policy.ReadScope == OperationReadScope.Scoped
        && invocation.Candidate is { IsActive: true, SourceKind: AuthorizationSourceKind.Http } candidate
        && context.IsResolved && context.UserId is { } actor && StronglyTypedId.IsAssigned(actor) && actor != UserId.SystemActor
        && StronglyTypedId.IsAssigned(context.TenantId) && context.TenantId != TenantId.PlatformSentinel
        && context.Origin is TenantContextOrigin.HostAndClaim or TenantContextOrigin.ClaimAndMembership or TenantContextOrigin.HostAndNativeSessionScope
        && candidate.Actor == actor && candidate.TenantId == context.TenantId && candidate.OrganizationId == context.OrganizationId
        && invocation.Actor == actor && invocation.TenantId == context.TenantId && invocation.OrganizationId == context.OrganizationId
        && invocations.CurrentDecision is { IsActive: true, Outcome: ProtectedOperationOutcome.Allowed } decision
        && ReferenceEquals(decision.Invocation, invocation);
    private static Result<None> Fail(string key) => OwnFieldConsentAuthorization.Failure<None>(key);
}

/// <summary>Withdrawal receipts retain the original writable consent-subject witness.</summary>
public sealed class ReadOwnWithdrawnFieldConsentReceiptQueryHandler(IOwnFieldConsentReceiptReader reader,
    OwnConsentEntryAuthorization authorization, IAuditSubject audit)
    : IRequestHandler<ReadOwnWithdrawnFieldConsentReceiptQuery, Result<None>>
{
    public async Task<Result<None>> Handle(ReadOwnWithdrawnFieldConsentReceiptQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.EvidenceId == Guid.Empty || request.EvidenceId.Version != 7) return Fail("lockey_validation_failed");
        var witness = authorization.For(request, ConsentSubjectPurpose.Withdraw);
        if (witness?.MembershipId is not { } membership) return Fail("lockey_forbidden");
        try
        {
            var target = await reader.ReadAsync(witness.Actor, [membership], request.EvidenceId, cancellationToken);
            if (!ReferenceEquals(authorization.For(request, ConsentSubjectPurpose.Withdraw), witness)) return Fail("lockey_forbidden");
            if (target is null || target.Id.Value != request.EvidenceId || target.State != MembershipFieldConsentState.Withdrawn
                || target.TenantId != witness.TenantId || target.OrganizationId != witness.OrganizationId
                || target.SubjectUserId != witness.Actor || target.ActorUserId != witness.Actor || target.MembershipId != membership) return Fail("lockey_not_found");
            audit.Designate(target);
            return Result.Ok(None.Value);
        }
        catch (TenancyAdministrationStorageException) { return Fail("lockey_dependency_unavailable"); }
    }
    private static Result<None> Fail(string key) => OwnFieldConsentAuthorization.Failure<None>(key);
}
