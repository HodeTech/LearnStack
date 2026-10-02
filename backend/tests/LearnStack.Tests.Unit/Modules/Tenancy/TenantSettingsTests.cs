using FluentAssertions;
using LearnStack.Modules.Tenancy.Application.Branding;
using LearnStack.Modules.Tenancy.Application.Contracts.Settings;
using LearnStack.Modules.Tenancy.Application.Settings;
using Xunit;

namespace LearnStack.Tests.Unit.Modules.Tenancy;

public sealed class TenantSettingsTests
{
    [Fact]
    public void Branding_read_uses_the_same_whole_theme_contrast_and_grammar_as_authoring()
    {
        const string valid = """{"primary":"#2345AA","background":"#FFFFFF","foreground":"#111111","muted":"#555555"}""";
        BrandingThemeRegistry.Read(valid).Value.Should().Be(new BrandingTheme("#2345aa", "#ffffff", "#111111", "#555555"));
        BrandingThemeRegistry.Read(valid.Replace("#111111", "#FFFFFF", StringComparison.Ordinal)).IsFailure.Should().BeTrue();
        BrandingThemeRegistry.Read("{}").IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Registry_requires_both_registered_key_and_exact_value_type()
    {
        var registry = TenantSettingRegistry.Default;
        registry.Find(TenantSettingKeys.BrandingTheme)!.AllowsOrganizationScope.Should().BeFalse();
        registry.Find(new TenantSettingKey<string>(TenantSettingKeys.BrandingTheme.Value)).Should().BeNull();
        registry.Find(new TenantSettingKey<BrandingTheme>("private.unknown")).Should().BeNull();
        new TenantSettingRead<BrandingTheme>(null).IsPresent.Should().BeFalse();
    }
}
