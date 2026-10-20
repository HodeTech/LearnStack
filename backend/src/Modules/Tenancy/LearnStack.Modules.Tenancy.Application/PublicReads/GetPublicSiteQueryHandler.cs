using FluentValidation;
using LearnStack.Modules.Tenancy.Application.Contracts.PublicReads;
using LearnStack.Modules.Tenancy.Application.Contracts.Settings;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.PublicReads;

public sealed class GetPublicSiteQueryValidator : AbstractValidator<GetPublicSiteQuery>
{
    public GetPublicSiteQueryValidator() =>
        RuleFor(request => request.HasUnexpectedQuery).Equal(false)
            .OverridePropertyName("Query").WithErrorCode("lockey_invalid_value");
}

public sealed class GetPublicSiteQueryHandler(
    IPublicTenantConfigurationReader configuration, ITenantSettingsAccessor settings, IFeatureFlags features)
    : IRequestHandler<GetPublicSiteQuery, Result<PublicSite>>
{
    public Task<Result<PublicSite>> Handle(GetPublicSiteQuery request, CancellationToken cancellationToken) =>
        PublicSiteProjection.ReadAsync(configuration, settings, features, cancellationToken);
}
