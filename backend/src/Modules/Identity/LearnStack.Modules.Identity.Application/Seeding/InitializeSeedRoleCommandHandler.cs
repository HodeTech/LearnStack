using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;

namespace LearnStack.Modules.Identity.Application.Seeding;

public sealed class InitializeSeedRoleCommandHandler(IRoleWriteStore store, ITenantContext context,
    ISeedIdentityFoundationAdmission admission, IIdentityScopeLifecycleReader lifecycle,
    IIdentityMutationLocks locks, IClock clock, IFixedSeedPrerequisiteVerifier prerequisites) : IRequestHandler<InitializeSeedRoleCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(InitializeSeedRoleCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!SeedFoundationGate.HasActor(context) || context.OrganizationId is not null
            || !SeedFoundationGate.ValidRoot(request.RoleId)
            || IdentityFoundationManifest.Version1.FindTemplate(request.BuiltInKey, request.CapabilityScope) is null)
            return Result<Guid>.Fail(IdentityFailures.Refused());
        var act = SeedFoundationGate.Act(context, SeedIdentityFoundationOperation.InitializeRole, request.RoleId,
            null, null, request.BuiltInKey, request.CapabilityScope, request.ManifestVersion, request.ExpectedFingerprint);
        if (!await SeedFoundationGate.AllowsAsync(context, admission, lifecycle, act,
            IdentityFoundationManifest.Version1.RoleFingerprint(context.TenantId, request.RoleId,
                request.BuiltInKey, request.CapabilityScope), cancellationToken))
            return Result<Guid>.Fail(IdentityFailures.Refused());
        try
        {
            var id = RoleId.From(request.RoleId);
            if (await store.FindAsync(id, cancellationToken) is not null
                || await store.HasMembershipHistoryAsync(cancellationToken))
                return Result<Guid>.Fail(IdentityFailures.Refused());
            var plan = new IdentityMutationLockPlan([], context.TenantId, IdentityTenantLockMode.Exclusive);
            await locks.AcquireAsync(plan, cancellationToken);
            if (!await SeedFoundationGate.AllowsCurrentLifecycleAsync(context, lifecycle, cancellationToken)
                || await store.FindAsync(id, cancellationToken) is not null
                || await store.HasMembershipHistoryAsync(cancellationToken)
                || !await prerequisites.VerifyAsync(request, plan, cancellationToken))
                return Result<Guid>.Fail(IdentityFailures.Refused());
            locks.RequireCurrent(plan);
            var role = Role.Create(id, context.TenantId, request.BuiltInKey, request.CapabilityScope,
                clock.UtcNow, context.UserId!.Value);
            if (role.IsFailure) return Result<Guid>.Fail(role.Error);
            await store.AddAsync(role.Value, cancellationToken);
            await store.ValidateGraphAsync(cancellationToken);
            return Result<Guid>.Ok(request.RoleId);
        }
        catch (Exception failure) when (failure is IdentityFoundationStorageException or IdentityMutationLockException)
        {
            return Result<Guid>.Fail(IdentityFailures.Refused());
        }
    }
}
