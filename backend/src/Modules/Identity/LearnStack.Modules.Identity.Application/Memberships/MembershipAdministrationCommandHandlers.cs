using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;

namespace LearnStack.Modules.Identity.Application.Memberships;

public sealed class SetMembershipProfileCommandHandler(IMembershipWriteStore store, IRoleReadStore roles,
    IMembershipAdministrationReader reader, ITenantContext context, IIdentityMutationLocks locks,
    IIdentityScopeLifecycleReader lifecycle, ITenantLocaleEligibilityReader locales, IOperationInvocationContext invocations,
    Func<MembershipAdministrationAuthorization> authorization, IAuditSubject subject, IClock clock)
    : MembershipAdministrationCommandHandlerBase(store, roles, reader, context, locks, lifecycle, locales, invocations, authorization, subject, clock),
        IRequestHandler<SetMembershipProfileCommand, Result<MembershipMutationResult>>
{
    public Task<Result<MembershipMutationResult>> Handle(SetMembershipProfileCommand request, CancellationToken cancellationToken) =>
        ExecuteAsync(request, cancellationToken);
}

public sealed class SetOwnMembershipProfileCommandHandler(IMembershipWriteStore store, IRoleReadStore roles,
    IMembershipAdministrationReader reader, ITenantContext context, IIdentityMutationLocks locks,
    IIdentityScopeLifecycleReader lifecycle, ITenantLocaleEligibilityReader locales, IOperationInvocationContext invocations,
    Func<MembershipAdministrationAuthorization> authorization, IAuditSubject subject, IClock clock)
    : MembershipAdministrationCommandHandlerBase(store, roles, reader, context, locks, lifecycle, locales, invocations, authorization, subject, clock),
        IRequestHandler<SetOwnMembershipProfileCommand, Result<MembershipMutationResult>>
{
    public Task<Result<MembershipMutationResult>> Handle(SetOwnMembershipProfileCommand request, CancellationToken cancellationToken) =>
        ExecuteAsync(request, cancellationToken);
}

public sealed class ChangeMembershipStatusCommandHandler(IMembershipWriteStore store, IRoleReadStore roles,
    IMembershipAdministrationReader reader, ITenantContext context, IIdentityMutationLocks locks,
    IIdentityScopeLifecycleReader lifecycle, ITenantLocaleEligibilityReader locales, IOperationInvocationContext invocations,
    Func<MembershipAdministrationAuthorization> authorization, IAuditSubject subject, IClock clock)
    : MembershipAdministrationCommandHandlerBase(store, roles, reader, context, locks, lifecycle, locales, invocations, authorization, subject, clock),
        IRequestHandler<ChangeMembershipStatusCommand, Result<MembershipMutationResult>>
{
    public Task<Result<MembershipMutationResult>> Handle(ChangeMembershipStatusCommand request, CancellationToken cancellationToken) =>
        ExecuteAsync(request, cancellationToken);
}

public sealed class ReplaceMembershipRoleAssignmentsCommandHandler(IMembershipWriteStore store, IRoleReadStore roles,
    IMembershipAdministrationReader reader, ITenantContext context, IIdentityMutationLocks locks,
    IIdentityScopeLifecycleReader lifecycle, ITenantLocaleEligibilityReader locales, IOperationInvocationContext invocations,
    Func<MembershipAdministrationAuthorization> authorization, IAuditSubject subject, IClock clock)
    : MembershipAdministrationCommandHandlerBase(store, roles, reader, context, locks, lifecycle, locales, invocations, authorization, subject, clock),
        IRequestHandler<ReplaceMembershipRoleAssignmentsCommand, Result<MembershipMutationResult>>
{
    public Task<Result<MembershipMutationResult>> Handle(ReplaceMembershipRoleAssignmentsCommand request, CancellationToken cancellationToken) =>
        ExecuteAsync(request, cancellationToken);
}

public abstract class MembershipAdministrationCommandHandlerBase(IMembershipWriteStore store, IRoleReadStore roles,
    IMembershipAdministrationReader reader, ITenantContext context, IIdentityMutationLocks locks,
    IIdentityScopeLifecycleReader lifecycle, ITenantLocaleEligibilityReader locales, IOperationInvocationContext invocations,
    Func<MembershipAdministrationAuthorization> authorization, IAuditSubject subject, IClock clock)
{
    protected async Task<Result<MembershipMutationResult>> ExecuteAsync(object request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (invocations.CurrentInvocation is not { IsActive: true, EntryKind: OperationEntryKind.Application } invocation
            || !ReferenceEquals(invocation.Operation, request)
            || invocations.CurrentDecision is not { Outcome: ProtectedOperationOutcome.Allowed, IsActive: true } admitted
            || !ReferenceEquals(admitted.Invocation, invocation)) return Fail("lockey_forbidden");
        var gate = authorization();
        if (!gate.Matches(request, invocation)) return Fail("lockey_forbidden");
        if (!MembershipAdministrationInput.Valid(request)) return Fail("lockey_validation_failed");
        try
        {
            var id = await TargetIdAsync(request, cancellationToken);
            var before = id is { } membershipId ? await store.FindAsync(membershipId, cancellationToken) : null;
            if (!Visible(before, request))
                return MembershipAdministrationResult.Refused<MembershipMutationResult>(gate,
                    await gate.RecheckAsync(request, invocation, before, [], cancellationToken));
            var version = ExpectedVersion(request);
            if (before!.Version != version) return Fail("lockey_concurrency_conflict");
            var roleIds = RoleIds(request, before);
            if (roleIds.Length > Membership.MaximumRoleAssignments) return Fail("lockey_forbidden");
            IReadOnlyList<Role> initialRoles = roleIds.Length == 0 ? [] : await roles.ReadManyAsync(roleIds, cancellationToken);
            var check = await gate.RecheckAsync(request, invocation, before, AddedRoles(request, before, initialRoles), cancellationToken);
            if (check.Decision.Outcome != ProtectedOperationOutcome.Allowed)
                return MembershipAdministrationResult.Refused<MembershipMutationResult>(gate, check);
            if (!CompleteRoleGraph(initialRoles, roleIds, before)) return Fail("lockey_forbidden");
            if (!await AllowsLifecycleAsync(cancellationToken)) return Fail("lockey_not_found");
            var plan = Plan(request, before, initialRoles);
            await locks.AcquireAsync(plan, cancellationToken);
            // Profiles do not affect administrator eligibility and retain independent
            // Member concurrency under the shared Tenant fence, without Role locks.
            foreach (var roleId in roleIds) await roles.LockAsync(roleId, cancellationToken);
            IReadOnlyList<Role> freshRoles = roleIds.Length == 0 ? [] : await roles.ReadManyAsync(roleIds, cancellationToken);
            await store.LockAsync(before.Id, cancellationToken);
            // This must be the final tracked graph read: the write store clears the
            // Identity tracker so no pre-wait Role or Member instance can be saved.
            var target = await store.FindAsync(before.Id, cancellationToken);
            if (!Visible(target, request))
                return MembershipAdministrationResult.Refused<MembershipMutationResult>(gate,
                    await gate.RecheckAsync(request, invocation, target, [], cancellationToken));
            if (target!.Version != version || target.UserId != before.UserId || target.OrganizationId != before.OrganizationId
                || !RoleIds(request, target).SequenceEqual(roleIds)) return Fail("lockey_concurrency_conflict");
            if (!CompleteRoleGraph(freshRoles, roleIds, target) || !SamePlan(plan, Plan(request, target, freshRoles)))
                return Fail("lockey_forbidden");
            if (!await AllowsLifecycleAsync(cancellationToken)) return Fail("lockey_not_found");
            var (displayName, preferredLocale) = Profile(request);
            if (preferredLocale is not null)
            {
                var eligible = await locales.ReadEligibleAsync(preferredLocale, cancellationToken);
                if (eligible.IsFailure) return Result<MembershipMutationResult>.Fail(eligible.Error);
                preferredLocale = eligible.Value;
            }
            check = await gate.RecheckAsync(request, invocation, target, AddedRoles(request, target, freshRoles), cancellationToken);
            if (check.Decision.Outcome != ProtectedOperationOutcome.Allowed)
                return MembershipAdministrationResult.Refused<MembershipMutationResult>(gate, check);
            locks.RequireCurrent(plan);
            gate.ReportRecheck(check);
            subject.Designate(target);
            var changed = Mutate(request, target, freshRoles, displayName, preferredLocale);
            if (changed.IsFailure) return Result<MembershipMutationResult>.Fail(changed.Error);
            if (target.Version != version)
            {
                await store.UpdateAsync(target, cancellationToken);
                await store.ValidateGraphAsync(cancellationToken);
            }
            return Result<MembershipMutationResult>.Ok(new(target.Id.Value, target.Version));
        }
        catch (IdentityFoundationStorageException failure)
        {
            return Fail(failure.Failure == IdentityFoundationStorageFailure.Conflict ? "lockey_concurrency_conflict"
                : failure.Failure == IdentityFoundationStorageFailure.Plan ? "lockey_dependency_unavailable" : "lockey_forbidden");
        }
        catch (Exception failure) when (failure is IdentityMutationLockException or IdentityScopeLifecycleReadException
            or TenantLocaleEligibilityReadException)
        {
            return Fail("lockey_dependency_unavailable");
        }
    }

    private async Task<MembershipId?> TargetIdAsync(object request, CancellationToken cancellationToken) => request switch
    {
        SetMembershipProfileCommand profile => MembershipId.From(profile.MembershipId),
        ChangeMembershipStatusCommand status => MembershipId.From(status.MembershipId),
        ReplaceMembershipRoleAssignmentsCommand assignments => MembershipId.From(assignments.MembershipId),
        SetOwnMembershipProfileCommand => (await reader.ReadOwnAsync(cancellationToken))?.Id,
        _ => throw new ArgumentException("Membership mutation request is invalid.", nameof(request)),
    };

    private bool Visible(Membership? member, object request) => member is
    { Status: MembershipStatus.Active or MembershipStatus.Suspended, DeletedAt: null, DeletedBy: null }
        && member.TenantId == context.TenantId && member.OrganizationId == context.OrganizationId
        && (request is not SetOwnMembershipProfileCommand || member.UserId == context.UserId && member.Status == MembershipStatus.Active);

    private async Task<bool> AllowsLifecycleAsync(CancellationToken cancellationToken)
    {
        var current = await lifecycle.ReadAsync(context.TenantId, context.OrganizationId, cancellationToken);
        return current.TenantExistsAndNotDeleted && current.TenantAllowsOrdinaryWork
            && (context.OrganizationId is null || current.OrganizationBelongsToTenant && current.OrganizationAllowsOrdinaryWork);
    }

    private IdentityMutationLockPlan Plan(object request, Membership target, IReadOnlyCollection<Role> graph)
    {
        var additions = AddedRoles(request, target, graph);
        var admission = additions.Length != 0 || request is ChangeMembershipStatusCommand { Status: ManagedMembershipStatus.Active }
            && target.Status == MembershipStatus.Suspended;
        var exclusive = request switch
        {
            ChangeMembershipStatusCommand status => (MembershipStatus)status.Status != target.Status
                && graph.Any(AdministratorRole),
            ReplaceMembershipRoleAssignmentsCommand assignments => graph.Any(role => AdministratorRole(role)
                && target.RoleAssignments.Any(assignment => assignment.RoleId == role.Id) != assignments.RoleIds.Contains(role.Id.Value)),
            _ => false,
        };
        return new(admission ? [target.UserId] : [], context.TenantId,
            exclusive ? IdentityTenantLockMode.Exclusive : IdentityTenantLockMode.Shared);
    }

    private static bool AdministratorRole(Role role) => role.PermissionScope == PermissionScope.Tenant
        && role.BuiltInKey == BuiltInRoleKey.TenantAdmin;

    private bool CompleteRoleGraph(IReadOnlyCollection<Role> graph, RoleId[] ids, Membership target) =>
        graph.Count == ids.Length && graph.Select(role => role.Id).ToHashSet().SetEquals(ids)
        && graph.All(role => role.TenantId == context.TenantId && role.PermissionScope ==
            (target.OrganizationId is null ? PermissionScope.Tenant : PermissionScope.Organization));

    private static RoleId[] RoleIds(object request, Membership member) => request switch
    {
        SetMembershipProfileCommand or SetOwnMembershipProfileCommand => [],
        ReplaceMembershipRoleAssignmentsCommand assignments => [.. member.RoleAssignments.Select(assignment => assignment.RoleId)
            .Union(assignments.RoleIds.Select(RoleId.From)).OrderBy(id => id.Value)],
        _ => [.. member.RoleAssignments.Select(assignment => assignment.RoleId).Distinct().OrderBy(id => id.Value)],
    };

    private static Role[] AddedRoles(object request, Membership target, IReadOnlyCollection<Role> graph) =>
        request is ReplaceMembershipRoleAssignmentsCommand assignments
            ? [.. graph.Where(role => assignments.RoleIds.Contains(role.Id.Value)
                && target.RoleAssignments.All(assignment => assignment.RoleId != role.Id))] : [];

    private static bool SamePlan(IdentityMutationLockPlan held, IdentityMutationLockPlan current) =>
        held.TenantId == current.TenantId && held.TenantMode == current.TenantMode && held.UserIds.SequenceEqual(current.UserIds);

    private Result<None> Mutate(object request, Membership target, IReadOnlyCollection<Role> graph,
        string? displayName, string? preferredLocale) => request switch
        {
            SetMembershipProfileCommand or SetOwnMembershipProfileCommand => target.SetProfile(displayName, preferredLocale, clock.UtcNow, context.UserId!.Value),
            ChangeMembershipStatusCommand status when (MembershipStatus)status.Status == target.Status => Result<None>.Ok(None.Value),
            ChangeMembershipStatusCommand { Status: ManagedMembershipStatus.Active } => target.Reactivate(clock.UtcNow, context.UserId!.Value),
            ChangeMembershipStatusCommand { Status: ManagedMembershipStatus.Suspended } => target.Suspend(clock.UtcNow, context.UserId!.Value),
            ChangeMembershipStatusCommand { Status: ManagedMembershipStatus.Revoked } => target.Revoke(clock.UtcNow, context.UserId!.Value),
            ReplaceMembershipRoleAssignmentsCommand assignments => target.ReplaceAssignments(
                graph.Where(role => assignments.RoleIds.Contains(role.Id.Value)).ToArray(), clock.UtcNow, context.UserId!.Value),
            _ => throw new ArgumentException("Membership mutation request is invalid.", nameof(request)),
        };

    private static (string? DisplayName, string? PreferredLocale) Profile(object request) => request switch
    {
        SetMembershipProfileCommand profile => (profile.DisplayName, profile.PreferredLocale),
        SetOwnMembershipProfileCommand profile => (profile.DisplayName, profile.PreferredLocale),
        _ => (null, null),
    };

    private static long ExpectedVersion(object request) => request switch
    {
        SetMembershipProfileCommand profile => profile.ExpectedVersion,
        SetOwnMembershipProfileCommand profile => profile.ExpectedVersion,
        ChangeMembershipStatusCommand status => status.ExpectedVersion,
        ReplaceMembershipRoleAssignmentsCommand assignments => assignments.ExpectedVersion,
        _ => throw new ArgumentException("Membership mutation request is invalid.", nameof(request)),
    };

    private static Result<MembershipMutationResult> Fail(string key) => MembershipAdministrationResult.Fail<MembershipMutationResult>(key);
}
