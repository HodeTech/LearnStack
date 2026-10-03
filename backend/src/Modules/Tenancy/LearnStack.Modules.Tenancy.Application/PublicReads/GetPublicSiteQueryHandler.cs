using FluentValidation;
using LearnStack.Modules.Tenancy.Application.Contracts.PublicReads;
using LearnStack.Modules.Tenancy.Application.Contracts.Settings;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Localization;
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
    public async Task<Result<PublicSite>> Handle(GetPublicSiteQuery request, CancellationToken cancellationToken)
    {
        var loaded = await configuration.ReadAsync(cancellationToken);
        if (loaded.IsFailure) return Result<PublicSite>.Fail(loaded.Error!);
        var site = loaded.Value!;
        if (site.EnabledLocales.Count == 0 || site.DefaultLocale is null)
            return Result<PublicSite>.Fail(new Error(new LocalizedMessage("lockey_not_found")));

        var setting = await settings.ReadAsync(TenantSettingKeys.BrandingTheme, cancellationToken);
        var theme = setting.IsSuccess ? setting.Value!.Value : null;
        var attribution = !await features.IsEnabledAsync(FeatureKeys.WhiteLabelBranding, cancellationToken);
        return Result.Ok(new PublicSite(site.DisplayName, site.EnabledLocales, site.DefaultLocale,
            theme is null ? null : new PublicTheme(theme.Primary, theme.Background, theme.Foreground, theme.Muted),
            attribution));
    }
}
