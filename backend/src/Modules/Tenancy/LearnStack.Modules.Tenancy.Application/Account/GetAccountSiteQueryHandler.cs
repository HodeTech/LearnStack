using LearnStack.Modules.Tenancy.Application.Contracts.Account;
using LearnStack.Modules.Tenancy.Application.Contracts.PublicReads;
using LearnStack.Modules.Tenancy.Application.Contracts.Settings;
using LearnStack.Modules.Tenancy.Application.PublicReads;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Account;

public sealed class GetAccountSiteQueryHandler(
    ITenantContext context, IPublicTenantConfigurationReader configuration,
    ITenantSettingsAccessor settings, IFeatureFlags features)
    : IRequestHandler<GetAccountSiteQuery, Result<AccountSite>>
{
    public async Task<Result<AccountSite>> Handle(GetAccountSiteQuery request, CancellationToken cancellationToken)
    {
        // Authentication alone cannot supply the independent institution-host ceiling.
        if (!context.IsResolved || context.Origin is not (TenantContextOrigin.HostAndClaim or TenantContextOrigin.HostAndNativeSessionScope)
            || context.UserId is not { } actor || !StronglyTypedId.IsAssigned(actor)
            || actor == UserId.SystemActor || context.HostScope is not { } host)
            return Result<AccountSite>.Fail(TenantContextFactory.Refused);

        var tenantId = context.TenantId;
        var organizationId = context.OrganizationId;
        if (!StronglyTypedId.IsAssigned(tenantId) || tenantId == TenantId.PlatformSentinel
            || !StronglyTypedId.IsAssigned(host.TenantId) || host.TenantId == TenantId.PlatformSentinel
            || host.TenantId != tenantId
            || !StronglyTypedId.IsAssignedWhenPresent(organizationId)
            || !StronglyTypedId.IsAssignedWhenPresent(host.OrganizationId)
            || (host.OrganizationId is not null && host.OrganizationId != organizationId))
            return Result<AccountSite>.Fail(TenantContextFactory.Refused);

        var site = await PublicSiteProjection.ReadAsync(configuration, settings, features, cancellationToken);
        return site.IsFailure
            ? Result<AccountSite>.Fail(site.Error)
            : Result.Ok(new AccountSite(actor, tenantId, organizationId, site.Value));
    }
}
