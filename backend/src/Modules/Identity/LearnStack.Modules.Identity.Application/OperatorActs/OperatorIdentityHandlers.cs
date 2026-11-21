using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Contracts.OperatorActs;
using LearnStack.Modules.Identity.Application.Seeding;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;

namespace LearnStack.Modules.Identity.Application.OperatorActs;

/// <summary>The operator acts' answers: a refusal an operator can act on, in the shapes the API already uses.</summary>
internal static class OperatorIdentityFailures
{
    internal static Result<Guid> NotFound() => Result<Guid>.Fail(new Error(new LocalizedMessage("lockey_not_found")));

    internal static Result<Guid> Unavailable() => Result<Guid>.Fail(new Error(new LocalizedMessage("lockey_dependency_unavailable")));

    /// <summary>
    /// What a refused storage write answers, in the vocabulary the sibling Identity handlers already use: a
    /// lost race or a lock that timed out is retryable, a missing plan is unavailable, and the rest is the
    /// foundation refusing the graph.
    /// </summary>
    internal static Result<Guid> Storage(IdentityFoundationStorageException failure) => failure.Failure switch
    {
        IdentityFoundationStorageFailure.Conflict => Result<Guid>.Fail(new Error(new LocalizedMessage("lockey_concurrency_conflict"))),
        IdentityFoundationStorageFailure.Plan => Unavailable(),
        _ => Rule("$", "lockey_identity_foundation_refused"),
    };

    /// <summary>
    /// A 409 with the reason on the field it concerns. The top-level code stays
    /// <c>business_rule_violation</c> because the status map is closed and a module-specific code would
    /// answer 500.
    /// </summary>
    internal static Result<Guid> Rule(string field, string reason) => Result<Guid>.Fail(new Error(
        new LocalizedMessage("lockey_business_rule_violation"),
        new Dictionary<string, IReadOnlyList<LocalizedMessage>>(StringComparer.Ordinal)
        {
            [field] = [new LocalizedMessage(reason)],
        }));
}

/// <summary>
/// Creates one finite Role template for an operator (ADR-0102 § 2, § 6): under the tenant's ordered
/// lock, before any Membership exists, once.
/// </summary>
internal sealed class OperatorInitializeRoleCommandHandler(
    IRoleWriteStore store,
    ITenantContext context,
    IIdentityScopeLifecycleReader lifecycle,
    OperatorActGuard guard,
    IClock clock) : IRequestHandler<OperatorInitializeRoleCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(OperatorInitializeRoleCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!context.IsResolved || context.OrganizationId is not null || !SeedFoundationGate.ValidRoot(request.RoleId))
            return OperatorIdentityFailures.Rule("RoleId", "lockey_validation_failed");
        if (IdentityFoundationManifest.Version1.FindTemplate(request.BuiltInKey, request.CapabilityScope) is null)
            return OperatorIdentityFailures.Rule("BuiltInKey", "lockey_role_template_unknown");
        if (!await SeedFoundationGate.AllowsCurrentLifecycleAsync(context, lifecycle, cancellationToken))
            return OperatorIdentityFailures.NotFound();

        var plan = new IdentityMutationLockPlan([], context.TenantId, IdentityTenantLockMode.Exclusive);
        var admission = await guard.AdmitAsync(request, plan, cancellationToken);
        if (admission.IsFailure) return Result<Guid>.Fail(admission.Error);
        try
        {
            var id = RoleId.From(request.RoleId);
            if (!await SeedFoundationGate.AllowsCurrentLifecycleAsync(context, lifecycle, cancellationToken))
                return OperatorIdentityFailures.NotFound();

            // The same template named again is the same act, so a reply lost on the wire costs nothing.
            if (await store.FindAsync(id, cancellationToken) is { } existing)
            {
                // The act is identified by the Role's id, tenant, key, scope and creator. Its grants are not part of
                // that: once an administrator exists the template may be edited, and a retry of the original act
                // must still find its Role.
                if (existing.TenantId != context.TenantId || existing.BuiltInKey != request.BuiltInKey
                    || existing.PermissionScope != request.CapabilityScope || existing.CreatedBy != UserId.SystemActor)
                    return OperatorIdentityFailures.Rule("RoleId", "lockey_identifier_taken");
                guard.MarkReplayed();
                return Result<Guid>.Ok(request.RoleId);
            }

            if (await store.HasMembershipHistoryAsync(cancellationToken))
                return OperatorIdentityFailures.Rule("$", "lockey_tenant_has_memberships");
            if (guard.RequireCurrent(admission.Value) is { IsFailure: true } changed)
                return Result<Guid>.Fail(changed.Error);

            var role = Role.Create(
                id, context.TenantId, request.BuiltInKey, request.CapabilityScope, clock.UtcNow, UserId.SystemActor);
            if (role.IsFailure) return Result<Guid>.Fail(role.Error);
            await store.AddAsync(role.Value, cancellationToken);
            await store.ValidateGraphAsync(cancellationToken);
            return Result<Guid>.Ok(request.RoleId);
        }
        catch (IdentityFoundationStorageException failure)
        {
            return OperatorIdentityFailures.Storage(failure);
        }
        catch (IdentityMutationLockException)
        {
            return OperatorIdentityFailures.Unavailable();
        }
    }
}

/// <summary>
/// The shared core of the two administrator acts: name the person, take the ordered locks, judge the
/// act's own predicate under them, create the Membership (ADR-0102 § 5, § 7, § 8).
/// </summary>
internal static class OperatorAdministratorCreation
{
    internal static async Task<Result<Guid>> CreateAsync(
        object request,
        Guid membershipId,
        Guid roleId,
        string providerSubject,
        bool firstAdmin,
        IMembershipWriteStore store,
        IRoleReadStore roles,
        IOperatorSubjectResolver subjects,
        ITenantContext context,
        IIdentityScopeLifecycleReader lifecycle,
        OperatorActGuard guard,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (!context.IsResolved || context.OrganizationId is not null || !SeedFoundationGate.ValidRoot(membershipId)
            || !SeedFoundationGate.ValidRoot(roleId) || string.IsNullOrWhiteSpace(providerSubject) || providerSubject.Length > OperatorSubjectLimits.MaximumProviderSubjectLength)
            return OperatorIdentityFailures.Rule("MembershipId", "lockey_validation_failed");
        if (!await SeedFoundationGate.AllowsCurrentLifecycleAsync(context, lifecycle, cancellationToken))
            return OperatorIdentityFailures.NotFound();

        // The confined resolver is reachable only from inside a live operator act, so the check is made before
        // it is called and not left to the lock plan that needs its answer.
        if (guard.RequireLiveAct(request) is { IsFailure: true } notLive)
            return Result<Guid>.Fail(notLive.Error);

        // The person first: naming a person who has not signed in refuses before any lock or write.
        if (await subjects.ResolveActiveUserAsync(providerSubject, cancellationToken) is not { } userId
            || !SeedFoundationGate.ValidUser(userId))
            return OperatorIdentityFailures.Rule("ProviderSubject", "lockey_bootstrap_required");

        // The User fence before the tenant fence, as every Membership admission takes them.
        var plan = new IdentityMutationLockPlan([userId], context.TenantId, IdentityTenantLockMode.Exclusive);
        var admission = await guard.AdmitAsync(request, plan, cancellationToken);
        if (admission.IsFailure) return Result<Guid>.Fail(admission.Error);
        try
        {
            if (!await SeedFoundationGate.AllowsCurrentLifecycleAsync(context, lifecycle, cancellationToken))
                return OperatorIdentityFailures.NotFound();
            var id = MembershipId.From(membershipId);

            // The same act named again finds its Membership, so a reply lost on the wire costs nothing. This is
            // asked before the Role's present state is judged: the new administrator may have edited the
            // template since, and a retry of the original act must not be refused for that.
            if (await store.FindAsync(id, cancellationToken) is { } existing)
            {
                if (existing.TenantId != context.TenantId || existing.UserId != userId
                    || existing.OrganizationId is not null || existing.Status != MembershipStatus.Active
                    || !existing.RoleAssignments.Any(assignment => assignment.RoleId.Value == roleId))
                    return OperatorIdentityFailures.Rule("MembershipId", "lockey_identifier_taken");
                guard.MarkReplayed();
                return Result<Guid>.Ok(membershipId);
            }

            var role = await roles.ReadAsync(RoleId.From(roleId), cancellationToken);
            if (!IsAdministratorRole(role, context.TenantId, firstAdmin))
                return OperatorIdentityFailures.Rule("RoleId", "lockey_role_not_initialized");

            // The act's own predicate, under the locks. The first administrator refuses any Membership
            // history, so it is not a way to add an administrator to a tenant that has had members; the
            // recovery refuses while an active tenant-wide administrator exists.
            if (firstAdmin ? await store.HasMembershipHistoryAsync(cancellationToken)
                : await store.HasActiveTenantAdministratorAsync(cancellationToken))
            {
                return OperatorIdentityFailures.Rule(
                    "$", firstAdmin ? "lockey_tenant_has_memberships" : "lockey_administrator_exists");
            }

            if (guard.RequireCurrent(admission.Value) is { IsFailure: true } changed)
                return Result<Guid>.Fail(changed.Error);
            var membership = Membership.Create(
                id, context.TenantId, null, userId, role!, clock.UtcNow, UserId.SystemActor);
            if (membership.IsFailure) return Result<Guid>.Fail(membership.Error);
            await store.AddAsync(membership.Value, cancellationToken);
            await store.ValidateGraphAsync(cancellationToken);
            return Result<Guid>.Ok(membershipId);
        }
        catch (IdentityFoundationStorageException failure)
        {
            return OperatorIdentityFailures.Storage(failure);
        }
        catch (IdentityMutationLockException)
        {
            return OperatorIdentityFailures.Unavailable();
        }
    }

    // The first administrator takes the pristine template the operator initialized; a recovery takes any
    // current tenant administrator Role of the tenant, because the Role may have been edited since.
    private static bool IsAdministratorRole(Role? role, TenantId tenant, bool firstAdmin) => firstAdmin
        ? SeedMembershipCreation.IsDeclaredRole(role, tenant, PermissionScope.Tenant, firstAdmin: true)
        : role is not null && role.TenantId == tenant && role.PermissionScope == PermissionScope.Tenant
            && role.IsTenantAdministrator;
}

/// <summary>Creates the first tenant-wide administrator of a tenant that has never had a Membership.</summary>
internal sealed class OperatorCreateFirstAdminCommandHandler(
    IMembershipWriteStore store,
    IRoleReadStore roles,
    IOperatorSubjectResolver subjects,
    ITenantContext context,
    IIdentityScopeLifecycleReader lifecycle,
    OperatorActGuard guard,
    IClock clock) : IRequestHandler<OperatorCreateFirstAdminCommand, Result<Guid>>
{
    public Task<Result<Guid>> Handle(OperatorCreateFirstAdminCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return OperatorAdministratorCreation.CreateAsync(
            request, request.MembershipId, request.RoleId, request.ProviderSubject, true,
            store, roles, subjects, context, lifecycle, guard, clock, cancellationToken);
    }
}

/// <summary>Creates a replacement administrator for a tenant that has none active.</summary>
internal sealed class OperatorRecoverAdminCommandHandler(
    IMembershipWriteStore store,
    IRoleReadStore roles,
    IOperatorSubjectResolver subjects,
    ITenantContext context,
    IIdentityScopeLifecycleReader lifecycle,
    OperatorActGuard guard,
    IClock clock) : IRequestHandler<OperatorRecoverAdminCommand, Result<Guid>>
{
    public Task<Result<Guid>> Handle(OperatorRecoverAdminCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return OperatorAdministratorCreation.CreateAsync(
            request, request.MembershipId, request.RoleId, request.ProviderSubject, false,
            store, roles, subjects, context, lifecycle, guard, clock, cancellationToken);
    }
}
