using LearnStack.Modules.Tenancy.Application.Contracts.PublicReads;
using LearnStack.Modules.Tenancy.Application.Contracts.Settings;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;

namespace LearnStack.Modules.Tenancy.Application.PublicReads;

internal static class PublicSiteProjection
{
    internal static async Task<Result<PublicSite>> ReadAsync(
        IPublicTenantConfigurationReader configuration, ITenantSettingsAccessor settings,
        IFeatureFlags features, CancellationToken cancellationToken)
    {
        var loaded = await configuration.ReadAsync(cancellationToken);
        if (loaded.IsFailure) return Result<PublicSite>.Fail(loaded.Error);
        var site = loaded.Value;
        if (site.EnabledLocales.Count == 0 || site.DefaultLocale is null)
            return Result<PublicSite>.Fail(new Error(new LocalizedMessage("lockey_not_found")));

        var setting = await settings.ReadAsync(TenantSettingKeys.BrandingTheme, cancellationToken);
        var theme = setting.IsSuccess ? setting.Value.Value : null;
        var attribution = !await features.IsEnabledAsync(FeatureKeys.WhiteLabelBranding, cancellationToken);
        return Result.Ok(new PublicSite(site.DisplayName, site.EnabledLocales, site.DefaultLocale,
            theme is null ? null : new PublicTheme(theme.Primary, theme.Background, theme.Foreground, theme.Muted),
            attribution));
    }
}
