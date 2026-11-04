using LearnStack.Modules.Identity.Application.Contracts.Authorization;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.Modules.Tenancy.Application.Contracts.Settings;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Settings;

public sealed class InvitationPolicyHandlers(IInvitationPolicyReader reader, ITenantSettingWriteStore store,
    TenancyAdministrationAuthorization writes, IOperationInvocationContext invocations, IOperationPolicyCatalog catalog,
    IProtectedOperationAuthorizer authorizer, IIdentityAuthorizationReader facts, ITenantContext context,
    IAuthorizationRefusalReporter refusals, IAuditSubject subject, IGuidFactory ids, IClock clock)
    : IRequestHandler<GetTenantInvitationPolicyQuery, Result<InvitationPolicyDto>>,
      IRequestHandler<SetTenantInvitationLifetimeCommand, Result<InvitationPolicyMutationResult>>,
      IRequestHandler<ReadInvitationPolicyReceiptQuery, Result<None>>
{
    private async Task<Result<OperationInvocation>> OwnAsync(object request, CancellationToken cancellationToken)
    {
        if (request is not (GetTenantInvitationPolicyQuery or ReadInvitationPolicyReceiptQuery)
            || invocations.CurrentInvocation is not { IsActive: true, EntryKind: OperationEntryKind.Application, TrustedSource: null } invocation
            || !ReferenceEquals(invocation.Operation, request) || !ReferenceEquals(invocation.Context, context)
            || !catalog.TryGet(request.GetType(), out var policy) || !ReferenceEquals(policy, invocation.Policy)
            || policy.Kind != OperationPolicyKind.OwnMembershipRead || policy.ReadScope != OperationReadScope.Scoped
            || invocation.Candidate is not { IsActive: true, SourceKind: AuthorizationSourceKind.Http } candidate
            || !context.IsResolved || context.OrganizationId is not null || context.UserId is not { } actor || actor == UserId.SystemActor
            || context.TenantId == TenantId.PlatformSentinel || candidate.Actor != actor || candidate.TenantId != context.TenantId
            || candidate.OrganizationId is not null || invocation.OrganizationId is not null || invocation.Actor != actor || invocation.TenantId != context.TenantId
            || context.Origin is not (TenantContextOrigin.HostAndClaim or TenantContextOrigin.ClaimAndMembership or TenantContextOrigin.HostAndNativeSessionScope)
            || invocations.CurrentDecision is not { IsActive: true, Outcome: ProtectedOperationOutcome.Allowed } current || !ReferenceEquals(current.Invocation, invocation))
            return Fail<OperationInvocation>("lockey_forbidden");
        var decision = await authorizer.AuthorizeAsync(invocation, policy, candidate, cancellationToken);
        if (!invocation.IsActive || !candidate.IsActive || !decision.IsActive || !ReferenceEquals(decision.Invocation, invocation)) return Fail<OperationInvocation>("lockey_dependency_unavailable");
        if (decision.Outcome == ProtectedOperationOutcome.Allowed) { refusals.ReportRecheck(decision); return Result.Ok(invocation); }
        if (decision.Outcome is ProtectedOperationOutcome.Forbidden or ProtectedOperationOutcome.NotCovered)
            refusals.ReportResourceRefusal(decision, AuthorizationRefusalReason.Capability);
        return Fail<OperationInvocation>(decision.Outcome == ProtectedOperationOutcome.Unavailable ? "lockey_dependency_unavailable" : "lockey_not_found");
    }
    public async Task<Result<InvitationPolicyDto>> Handle(GetTenantInvitationPolicyQuery request, CancellationToken cancellationToken)
    {
        var admission = await OwnAsync(request, cancellationToken);
        if (admission.IsFailure) return Result<InvitationPolicyDto>.Fail(admission.Error);
        var row = await reader.ReadAsync(cancellationToken);
        var parsed = row is null ? Result.Ok(new InvitationLifetimePolicy(14)) : InvitationLifetimePolicyRegistry.Read(row.Value);
        if (parsed.IsFailure) return Result<InvitationPolicyDto>.Fail(parsed.Error);
        var current = await facts.ReadAsync(admission.Value.Candidate!, cancellationToken);
        var pair = BoundedTenancyAdministrationPermissions.SettingWrite;
        var canEdit = current.Covers && current.Actor == context.UserId && current.TenantId == context.TenantId && current.OrganizationId is null
            && catalog.TryGetDefinition(pair.Scope, pair.Key, out var registration) && registration.Assignable && registration.Definition.IsActive
            && current.Memberships.Where(member => member.OrganizationId is null).SelectMany(member => member.Roles)
                .Any(role => role.Scope == PermissionScope.Tenant && role.Grants.Contains(pair));
        var check = await OwnAsync(request, cancellationToken);
        return check.IsFailure ? Result<InvitationPolicyDto>.Fail(check.Error) : Result.Ok(new InvitationPolicyDto(parsed.Value.Days, row is not null, row?.Version, canEdit));
    }
    public async Task<Result<InvitationPolicyMutationResult>> Handle(SetTenantInvitationLifetimeCommand request, CancellationToken cancellationToken)
    {
        if (request.Days is < 1 or > 30 || request.ExpectedPresent != request.ExpectedVersion.HasValue || request.ExpectedVersion is < 0)
            return Fail<InvitationPolicyMutationResult>("lockey_validation_failed");
        try
        {
            var admission = await writes.AdmitWriteAsync(request, cancellationToken);
            if (admission.IsFailure) return Result<InvitationPolicyMutationResult>.Fail(admission.Error);
            var before = await reader.ReadAsync(cancellationToken);
            var row = before is null ? null : await store.FindForUpdateAsync(before.Id, cancellationToken);
            var check = await writes.VerifyWriteAsync(admission.Value, cancellationToken);
            if (check.IsFailure) return Result<InvitationPolicyMutationResult>.Fail(check.Error);
            if (request.ExpectedPresent != (row is not null) || row is not null && (row.Version != request.ExpectedVersion
                || row.Key != TenantSettingKeys.InvitationLifetimeName || row.OrganizationId is not null || row.TenantId != context.TenantId))
                return Fail<InvitationPolicyMutationResult>("lockey_concurrency_conflict");
            var value = InvitationLifetimePolicyRegistry.Canonicalize(request.Days).Value!;
            if (row is null)
            {
                row = TenantSetting.Create(TenantSettingId.From(ids.NewUuidV7()), context.TenantId, null, TenantSettingKeys.InvitationLifetimeName, value, clock, context.UserId!.Value);
                subject.Designate(row); await store.AddAsync(row, cancellationToken);
            }
            else
            {
                subject.Designate(row);
                var previous = InvitationLifetimePolicyRegistry.Read(row.Value);
                if (previous.IsFailure || previous.Value.Days != request.Days)
                {
                    row.SetValue(value, clock, context.UserId!.Value);
                    await store.UpdateAsync(row, cancellationToken);
                }
            }
            return Result.Ok(new InvitationPolicyMutationResult(row.Id.Value, row.Version));
        }
        catch (TenancyAdministrationStorageException failure) { return Fail<InvitationPolicyMutationResult>(failure.Failure == TenancyAdministrationStorageFailure.Conflict ? "lockey_concurrency_conflict" : "lockey_dependency_unavailable"); }
    }
    public async Task<Result<None>> Handle(ReadInvitationPolicyReceiptQuery request, CancellationToken cancellationToken)
    {
        var admission = await OwnAsync(request, cancellationToken);
        if (admission.IsFailure) return Result<None>.Fail(admission.Error);
        if (request.SettingId == Guid.Empty || request.SettingId.Version != 7) return Fail<None>("lockey_validation_failed");
        var row = await reader.ReadReceiptAsync(TenantSettingId.From(request.SettingId), cancellationToken);
        var check = await OwnAsync(request, cancellationToken);
        if (check.IsFailure) return Result<None>.Fail(check.Error);
        if (row is null) return Fail<None>("lockey_not_found");
        subject.Designate(row); return Result.Ok(None.Value);
    }
    private static Result<T> Fail<T>(string key) => Result<T>.Fail(new Error(LocalizedMessage.Of(key)));
}
