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

public sealed class SetSeedMembershipProfileCommandHandler(IMembershipWriteStore store, ITenantContext context,
    ISeedIdentityFoundationAdmission admission, IIdentityScopeLifecycleReader lifecycle,
    IIdentityMutationLocks locks, IClock clock) : IRequestHandler<SetSeedMembershipProfileCommand, Result<None>>
{
    public async Task<Result<None>> Handle(SetSeedMembershipProfileCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!SeedFoundationGate.HasActor(context) || request.MembershipScope != SeedFoundationGate.Scope(context)
            || !SeedFoundationGate.ValidRoot(request.MembershipId)
            || !MembershipProfile.IsValid(request.DisplayName, request.PreferredLocale))
            return Result<None>.Fail(IdentityFailures.Refused());
        var act = SeedFoundationGate.Act(context, SeedIdentityFoundationOperation.SetProfile, request.MembershipId,
            null, null, null, request.MembershipScope, request.ManifestVersion, request.ExpectedFingerprint);
        if (!await SeedFoundationGate.AllowsAsync(context, admission, lifecycle, act,
            IdentityFoundationManifest.Version1.ProfileFingerprint(context.TenantId, context.OrganizationId,
                request.MembershipId, request.DisplayName, request.PreferredLocale), cancellationToken))
            return Result<None>.Fail(IdentityFailures.Refused());
        try
        {
            var id = MembershipId.From(request.MembershipId);
            var before = await store.FindAsync(id, cancellationToken);
            if (!CanSet(before, request)) return Result<None>.Fail(IdentityFailures.Refused());
            var originalVersion = before!.Version;
            var originalUser = before.UserId;
            var plan = new IdentityMutationLockPlan([], context.TenantId, IdentityTenantLockMode.Shared);
            await locks.AcquireAsync(plan, cancellationToken);
            var membership = await store.FindAsync(id, cancellationToken);
            if (!CanSet(membership, request) || membership!.Version != originalVersion || membership.UserId != originalUser)
                return Result<None>.Fail(IdentityFailures.Refused());
            locks.RequireCurrent(plan);
            var changed = membership.SetProfile(request.DisplayName, request.PreferredLocale,
                clock.UtcNow, context.UserId!.Value);
            if (changed.IsFailure) return changed;
            if (membership.Version != originalVersion)
            {
                await store.UpdateAsync(membership, cancellationToken);
                await store.ValidateGraphAsync(cancellationToken);
            }
            return Result<None>.Ok(None.Value);
        }
        catch (Exception failure) when (failure is IdentityFoundationStorageException or IdentityMutationLockException)
        {
            return Result<None>.Fail(IdentityFailures.Refused());
        }
    }

    private bool CanSet(Membership? membership, SetSeedMembershipProfileCommand request) =>
        membership is { Status: MembershipStatus.Active, DeletedAt: null, DeletedBy: null }
        && membership.TenantId == context.TenantId && membership.OrganizationId == context.OrganizationId
        && (membership.Version == 0 && membership.Profile.DisplayName is null && membership.Profile.PreferredLocale is null
            || membership.Version == 1 && membership.Profile.DisplayName == request.DisplayName
                && membership.Profile.PreferredLocale == request.PreferredLocale);
}
