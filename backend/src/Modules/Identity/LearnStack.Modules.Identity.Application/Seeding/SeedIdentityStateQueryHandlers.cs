using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;

namespace LearnStack.Modules.Identity.Application.Seeding;

public sealed class GetRoleSeedStateQueryHandler(ISeedStateReader reader, ITenantContext context,
    ISeedIdentityFoundationAdmission admission, IIdentityScopeLifecycleReader lifecycle)
    : IRequestHandler<GetRoleSeedStateQuery, Result<SeedLookup<RoleSeedDto>>>
{
    public async Task<Result<SeedLookup<RoleSeedDto>>> Handle(GetRoleSeedStateQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!SeedFoundationGate.HasActor(context) || context.OrganizationId is not null
            || !SeedFoundationGate.ValidRoot(request.RoleId)
            || IdentityFoundationManifest.Version1.FindTemplate(request.BuiltInKey, request.CapabilityScope) is null)
            return Result<SeedLookup<RoleSeedDto>>.Fail(IdentityFailures.Refused());
        var act = SeedFoundationGate.Act(context, SeedIdentityFoundationOperation.VerifyRole, request.RoleId,
            null, null, request.BuiltInKey, request.CapabilityScope, request.ManifestVersion, request.ExpectedFingerprint);
        if (!await SeedFoundationGate.AllowsAsync(context, admission, lifecycle, act,
            IdentityFoundationManifest.Version1.RoleFingerprint(context.TenantId, request.RoleId,
                request.BuiltInKey, request.CapabilityScope), cancellationToken, verification: true))
            return Result<SeedLookup<RoleSeedDto>>.Fail(IdentityFailures.Refused());
        return Result<SeedLookup<RoleSeedDto>>.Ok(new(await reader.ReadRoleAsync(RoleId.From(request.RoleId), cancellationToken)));
    }
}

public sealed class GetMembershipSeedStateQueryHandler(ISeedStateReader reader, ITenantContext context,
    ISeedIdentityFoundationAdmission admission, IIdentityScopeLifecycleReader lifecycle)
    : IRequestHandler<GetMembershipSeedStateQuery, Result<SeedLookup<MembershipSeedDto>>>
{
    public async Task<Result<SeedLookup<MembershipSeedDto>>> Handle(GetMembershipSeedStateQuery request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!SeedFoundationGate.HasActor(context) || request.MembershipScope != SeedFoundationGate.Scope(context)
            || !SeedFoundationGate.ValidRoot(request.MembershipId) || !SeedFoundationGate.ValidRoot(request.RoleId)
            || !SeedFoundationGate.ValidUser(request.UserId))
            return Result<SeedLookup<MembershipSeedDto>>.Fail(IdentityFailures.Refused());
        var act = SeedFoundationGate.Act(context, SeedIdentityFoundationOperation.VerifyMembership, request.MembershipId,
            request.UserId, request.RoleId, null, request.MembershipScope, request.ManifestVersion, request.ExpectedFingerprint);
        if (!await SeedFoundationGate.AllowsAsync(context, admission, lifecycle, act,
            IdentityFoundationManifest.Version1.MembershipFingerprint(context.TenantId, context.OrganizationId,
                request.MembershipId, request.UserId, request.RoleId), cancellationToken, verification: true))
            return Result<SeedLookup<MembershipSeedDto>>.Fail(IdentityFailures.Refused());
        return Result<SeedLookup<MembershipSeedDto>>.Ok(new(await reader.ReadMembershipAsync(
            MembershipId.From(request.MembershipId), cancellationToken)));
    }
}
