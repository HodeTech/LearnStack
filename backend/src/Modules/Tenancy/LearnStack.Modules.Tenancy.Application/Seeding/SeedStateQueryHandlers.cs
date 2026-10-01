using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Seeding;

internal sealed class GetTenantSeedStateQueryHandler(ISeedStateReader reader, ITenantContext context)
    : IRequestHandler<GetTenantSeedStateQuery, Result<SeedLookup<TenantSeedDto>>>
{
    public async Task<Result<SeedLookup<TenantSeedDto>>> Handle(
        GetTenantSeedStateQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!context.IsResolved)
        {
            return Result<SeedLookup<TenantSeedDto>>.Fail(new Error(new LocalizedMessage("lockey_tenant_mismatch")));
        }

        return Result.Ok(new SeedLookup<TenantSeedDto>(
            await reader.ReadTenantAsync(cancellationToken)));
    }
}

internal sealed class GetOrganizationSeedStateQueryHandler(ISeedStateReader reader, ITenantContext context)
    : IRequestHandler<GetOrganizationSeedStateQuery, Result<SeedLookup<OrganizationSeedDto>>>
{
    public async Task<Result<SeedLookup<OrganizationSeedDto>>> Handle(
        GetOrganizationSeedStateQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!context.IsResolved)
        {
            return Result<SeedLookup<OrganizationSeedDto>>.Fail(new Error(new LocalizedMessage("lockey_tenant_mismatch")));
        }

        return Result.Ok(new SeedLookup<OrganizationSeedDto>(
            await reader.ReadOrganizationAsync(request.OrganizationId, cancellationToken)));
    }
}

internal sealed class GetHostMappingSeedStateQueryHandler(ISeedStateReader reader, ITenantContext context)
    : IRequestHandler<GetHostMappingSeedStateQuery, Result<SeedLookup<HostMappingSeedDto>>>
{
    public async Task<Result<SeedLookup<HostMappingSeedDto>>> Handle(
        GetHostMappingSeedStateQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!context.IsResolved)
        {
            return Result<SeedLookup<HostMappingSeedDto>>.Fail(new Error(new LocalizedMessage("lockey_tenant_mismatch")));
        }

        return Result.Ok(new SeedLookup<HostMappingSeedDto>(
            await reader.ReadHostMappingAsync(request.Host, cancellationToken)));
    }
}

internal sealed class GetSettingSeedStateQueryHandler(ISeedStateReader reader, ITenantContext context)
    : IRequestHandler<GetSettingSeedStateQuery, Result<SeedLookup<SettingSeedDto>>>
{
    public async Task<Result<SeedLookup<SettingSeedDto>>> Handle(
        GetSettingSeedStateQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!context.IsResolved)
        {
            return Result<SeedLookup<SettingSeedDto>>.Fail(new Error(new LocalizedMessage("lockey_tenant_mismatch")));
        }

        return Result.Ok(new SeedLookup<SettingSeedDto>(
            await reader.ReadSettingAsync(request.SettingId, cancellationToken)));
    }
}
