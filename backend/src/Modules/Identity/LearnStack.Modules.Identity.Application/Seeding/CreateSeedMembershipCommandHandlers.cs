using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;

namespace LearnStack.Modules.Identity.Application.Seeding;

public sealed class CreateFirstSeedAdminMembershipCommandHandler(IMembershipWriteStore store, IRoleReadStore roles,
    ITenantContext context, ISeedIdentityFoundationAdmission admission, IIdentityScopeLifecycleReader lifecycle,
    IIdentityMutationLocks locks, IClock clock, IFixedSeedPrerequisiteVerifier prerequisites)
    : IRequestHandler<CreateFirstSeedAdminMembershipCommand, Result<Guid>>
{
    public Task<Result<Guid>> Handle(CreateFirstSeedAdminMembershipCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SeedMembershipCreation.CreateAsync(request, request.MembershipId, request.UserId, request.RoleId,
            PermissionScope.Tenant, request.ManifestVersion, request.ExpectedFingerprint, true,
            store, roles, context, admission, lifecycle, locks, clock, prerequisites, cancellationToken);
    }
}

public sealed class CreateSeedMembershipCommandHandler(IMembershipWriteStore store, IRoleReadStore roles,
    ITenantContext context, ISeedIdentityFoundationAdmission admission, IIdentityScopeLifecycleReader lifecycle,
    IIdentityMutationLocks locks, IClock clock, IFixedSeedPrerequisiteVerifier prerequisites) : IRequestHandler<CreateSeedMembershipCommand, Result<Guid>>
{
    public Task<Result<Guid>> Handle(CreateSeedMembershipCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SeedMembershipCreation.CreateAsync(request, request.MembershipId, request.UserId, request.RoleId,
            request.MembershipScope, request.ManifestVersion, request.ExpectedFingerprint, false,
            store, roles, context, admission, lifecycle, locks, clock, prerequisites, cancellationToken);
    }
}

internal static class SeedMembershipCreation
{
    internal static async Task<Result<Guid>> CreateAsync(object request, Guid membershipId, UserId userId, Guid roleId,
        PermissionScope scope, int manifestVersion, string fingerprint, bool firstAdmin,
        IMembershipWriteStore store, IRoleReadStore roles, ITenantContext context,
        ISeedIdentityFoundationAdmission admission, IIdentityScopeLifecycleReader lifecycle,
        IIdentityMutationLocks locks, IClock clock, IFixedSeedPrerequisiteVerifier prerequisites, CancellationToken cancellationToken)
    {
        if (!SeedFoundationGate.HasActor(context) || scope != SeedFoundationGate.Scope(context)
            || !SeedFoundationGate.ValidRoot(membershipId) || !SeedFoundationGate.ValidRoot(roleId)
            || !SeedFoundationGate.ValidUser(userId) || firstAdmin && context.OrganizationId is not null)
            return Result<Guid>.Fail(IdentityFailures.Refused());
        var operation = firstAdmin ? SeedIdentityFoundationOperation.CreateFirstAdmin : SeedIdentityFoundationOperation.CreateMembership;
        var act = SeedFoundationGate.Act(context, operation, membershipId, userId, roleId, null,
            scope, manifestVersion, fingerprint);
        if (!await SeedFoundationGate.AllowsAsync(context, admission, lifecycle, act,
            IdentityFoundationManifest.Version1.MembershipFingerprint(context.TenantId, context.OrganizationId,
                membershipId, userId, roleId), cancellationToken))
            return Result<Guid>.Fail(IdentityFailures.Refused());
        try
        {
            var id = MembershipId.From(membershipId);
            var referencedRoleId = RoleId.From(roleId);
            var role = await roles.ReadAsync(referencedRoleId, cancellationToken);
            if (!IsDeclaredRole(role, context.TenantId, scope, firstAdmin)
                || await store.FindAsync(id, cancellationToken) is not null
                || firstAdmin && await store.HasMembershipHistoryAsync(cancellationToken))
                return Result<Guid>.Fail(IdentityFailures.Refused());
            // Every seed continuation is serialized against ordinary tenant writers.
            var plan = new IdentityMutationLockPlan([userId], context.TenantId, IdentityTenantLockMode.Exclusive);
            await locks.AcquireAsync(plan, cancellationToken);
            var freshRole = await roles.ReadAsync(referencedRoleId, cancellationToken);
            if (!await SeedFoundationGate.AllowsCurrentLifecycleAsync(context, lifecycle, cancellationToken)
                || !IsDeclaredRole(freshRole, context.TenantId, scope, firstAdmin)
                || freshRole!.IsTenantAdministrator != role!.IsTenantAdministrator
                || await store.FindAsync(id, cancellationToken) is not null
                || firstAdmin && await store.HasMembershipHistoryAsync(cancellationToken)
                || !await prerequisites.VerifyAsync(request, plan, cancellationToken))
                return Result<Guid>.Fail(IdentityFailures.Refused());
            locks.RequireCurrent(plan);
            var membership = Membership.Create(id, context.TenantId, context.OrganizationId, userId,
                freshRole, clock.UtcNow, context.UserId!.Value);
            if (membership.IsFailure) return Result<Guid>.Fail(membership.Error);
            await store.AddAsync(membership.Value, cancellationToken);
            await store.ValidateGraphAsync(cancellationToken);
            return Result<Guid>.Ok(membershipId);
        }
        catch (Exception failure) when (failure is IdentityFoundationStorageException or IdentityMutationLockException)
        {
            return Result<Guid>.Fail(IdentityFailures.Refused());
        }
    }

    private static bool IsDeclaredRole(Role? role, TenantId tenant, PermissionScope scope, bool firstAdmin) =>
        role is { Version: 0, UpdatedAt: null, UpdatedBy: null } && role.TenantId == tenant
        && role.CreatedBy == UserId.SystemActor && role.CreatedAt != default
        && role.AdoptedReleaseVersion == 1
        && role.AdoptedReleaseFingerprint == IdentityFoundationManifest.Version1.Fingerprint
        && role.PermissionScope == scope && role.MatchesInitialTemplate()
        && (firstAdmin ? role.IsTenantAdministrator : role.BuiltInKey == BuiltInRoleKey.Learner);
}
