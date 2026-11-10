using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.CustomFields;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Application.Memberships;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;

namespace LearnStack.Modules.Identity.Application.CustomFields;

public sealed class SetMembershipCustomFieldsCommandHandler(MembershipCustomFieldCommandExecutor executor)
    : IRequestHandler<SetMembershipCustomFieldsCommand, Result<MembershipMutationResult>>
{
    public Task<Result<MembershipMutationResult>> Handle(SetMembershipCustomFieldsCommand request, CancellationToken cancellationToken) =>
        executor.ExecuteAsync(request, cancellationToken);
}
public sealed class SetOwnMembershipCustomFieldsCommandHandler(MembershipCustomFieldCommandExecutor executor)
    : IRequestHandler<SetOwnMembershipCustomFieldsCommand, Result<MembershipMutationResult>>
{
    public Task<Result<MembershipMutationResult>> Handle(SetOwnMembershipCustomFieldsCommand request, CancellationToken cancellationToken) =>
        executor.ExecuteAsync(request, cancellationToken);
}

public sealed class MembershipCustomFieldCommandExecutor(IMembershipCustomFieldStore store, IMembershipAdministrationReader reader,
    ITenantContext context, IIdentityMutationLocks locks, IIdentityScopeLifecycleReader lifecycle,
    IOperationInvocationContext invocations, Func<MembershipAdministrationAuthorization> authorization,
    MembershipCustomFieldService fields, IAuditSubject subject, IClock clock)
{
    public async Task<Result<MembershipMutationResult>> ExecuteAsync(object request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (invocations.CurrentInvocation is not { IsActive: true, EntryKind: OperationEntryKind.Application } invocation
            || !ReferenceEquals(invocation.Operation, request)
            || invocations.CurrentDecision is not { Outcome: ProtectedOperationOutcome.Allowed, IsActive: true } admitted
            || !ReferenceEquals(admitted.Invocation, invocation)) return Fail("lockey_forbidden");
        var gate = authorization();
        if (!gate.Matches(request, invocation)) return Fail("lockey_forbidden");
        if (!MembershipCustomFieldInput.Valid(request)) return Fail("lockey_validation_failed");
        var (version, patch) = request switch
        {
            SetMembershipCustomFieldsCommand command => (command.ExpectedVersion, command.Patch),
            SetOwnMembershipCustomFieldsCommand command => (command.ExpectedVersion, command.Patch),
            _ => throw new ArgumentException("Custom-field request is invalid.", nameof(request)),
        };
        try
        {
            var before = request is SetMembershipCustomFieldsCommand target
                ? await reader.ReadDetailAsync(MembershipId.From(target.MembershipId), cancellationToken)
                : await reader.ReadOwnAsync(cancellationToken);
            var check = await gate.RecheckAsync(request, invocation, before, [], cancellationToken);
            if (check.Decision.Outcome != ProtectedOperationOutcome.Allowed)
                return MembershipAdministrationResult.Refused<MembershipMutationResult>(gate, check);
            if (before!.Version != version) return Fail("lockey_concurrency_conflict");
            var fence = new IdentityMutationLockPlan([], context.TenantId, IdentityTenantLockMode.Shared);
            await locks.AcquireAsync(fence, cancellationToken);
            await store.LockAsync(before.Id, cancellationToken);
            var member = await store.ReadForWriteAsync(before.Id, cancellationToken);
            check = await gate.RecheckAsync(request, invocation, member, [], cancellationToken);
            if (check.Decision.Outcome != ProtectedOperationOutcome.Allowed)
                return MembershipAdministrationResult.Refused<MembershipMutationResult>(gate, check);
            if (member!.Version != version || member.UserId != before.UserId || member.OrganizationId != before.OrganizationId)
                return Fail("lockey_concurrency_conflict");
            var state = await lifecycle.ReadAsync(context.TenantId, context.OrganizationId, cancellationToken);
            if (!state.TenantExistsAndNotDeleted || !state.TenantAllowsOrdinaryWork
                || context.OrganizationId is not null && (!state.OrganizationBelongsToTenant || !state.OrganizationAllowsOrdinaryWork))
                return Fail("lockey_not_found");
            var changed = await fields.ApplyAsync(member, patch, fence, cancellationToken);
            if (changed.IsFailure) return Result<MembershipMutationResult>.Fail(changed.Error);
            // The authority observation is fresh even when field/consent reads waited.
            check = await gate.RecheckAsync(request, invocation, member, [], cancellationToken);
            if (check.Decision.Outcome != ProtectedOperationOutcome.Allowed)
                return MembershipAdministrationResult.Refused<MembershipMutationResult>(gate, check);
            locks.RequireCurrent(fence);
            gate.ReportRecheck(check);
            subject.Designate(member);
            var result = member.SetCustomFields(changed.Value, clock.UtcNow, context.UserId!.Value);
            if (result.IsFailure) return Result<MembershipMutationResult>.Fail(result.Error);
            if (member.Version != version) await store.SaveAsync(cancellationToken);
            return Result<MembershipMutationResult>.Ok(new(member.Id.Value, member.Version));
        }
        catch (IdentityFoundationStorageException failure)
        {
            return Fail(failure.Failure == IdentityFoundationStorageFailure.Conflict ? "lockey_concurrency_conflict"
                : failure.Failure == IdentityFoundationStorageFailure.Plan ? "lockey_dependency_unavailable" : "lockey_forbidden");
        }
        catch (Exception failure) when (failure is IdentityMutationLockException or IdentityScopeLifecycleReadException)
        {
            return Fail("lockey_dependency_unavailable");
        }
    }

    private static Result<MembershipMutationResult> Fail(string key) => MembershipAdministrationResult.Fail<MembershipMutationResult>(key);
}
