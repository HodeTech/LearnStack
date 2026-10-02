using LearnStack.Modules.Customization.Domain;
using LearnStack.Modules.Customization.Application.Abstractions;
using LearnStack.Modules.Customization.Application.Contracts.Seeding;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;

namespace LearnStack.Modules.Customization.Application.Seeding;

internal sealed class GetContentTypeSeedStateQueryHandler(ISeedStateReader reader, ITenantContext context)
    : IRequestHandler<GetContentTypeSeedStateQuery, Result<SeedLookup<ContentTypeSeedDto>>>
{
    public async Task<Result<SeedLookup<ContentTypeSeedDto>>> Handle(
        GetContentTypeSeedStateQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!context.IsResolved)
        {
            return Result<SeedLookup<ContentTypeSeedDto>>.Fail(new Error(new LocalizedMessage("lockey_tenant_mismatch")));
        }

        return Result.Ok(new SeedLookup<ContentTypeSeedDto>(
            await reader.ReadContentTypeAsync(TenantContentTypeId.From(request.ContentTypeId), cancellationToken)));
    }
}

internal sealed class GetTaxonomySeedStateQueryHandler(ISeedStateReader reader, ITenantContext context)
    : IRequestHandler<GetTaxonomySeedStateQuery, Result<SeedLookup<TaxonomySeedDto>>>
{
    public async Task<Result<SeedLookup<TaxonomySeedDto>>> Handle(
        GetTaxonomySeedStateQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!context.IsResolved)
        {
            return Result<SeedLookup<TaxonomySeedDto>>.Fail(new Error(new LocalizedMessage("lockey_tenant_mismatch")));
        }

        return Result.Ok(new SeedLookup<TaxonomySeedDto>(
            await reader.ReadTaxonomyAsync(TenantLevelTaxonomyId.From(request.TaxonomyId), cancellationToken)));
    }
}
