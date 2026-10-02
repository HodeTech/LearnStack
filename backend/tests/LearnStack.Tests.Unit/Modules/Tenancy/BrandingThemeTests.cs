using FluentAssertions;
using LearnStack.Modules.Tenancy.Application.Branding;
using LearnStack.SharedKernel.Domain;
using Xunit;

namespace LearnStack.Tests.Unit.Modules.Tenancy;

public sealed class BrandingThemeTests
{
    private const string Valid = """
        {"primary":"#2345AA","background":"#FFFFFF","foreground":"#111111","muted":"#555555"}
        """;

    [Fact]
    public void Complete_palette_is_canonical_and_registry_carries_the_closed_css_mapping()
    {
        var result = BrandingThemeRegistry.ValidateAndCanonicalize(Valid);
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("""{"primary":"#2345aa","background":"#ffffff","foreground":"#111111","muted":"#555555"}""");
        BrandingThemeRegistry.Colors.Select(color => (color.JsonName, color.CssVariable)).Should().Equal(
            ("primary", "--ls-primary"), ("background", "--ls-bg"), ("foreground", "--ls-fg"), ("muted", "--ls-muted"));
        BrandingThemeRegistry.SettingKey.Should().Be("branding.theme");
    }

    [Fact]
    public void Input_property_order_does_not_change_the_canonical_palette()
    {
        var reordered = """{"muted":"#555555","foreground":"#111111","background":"#FFFFFF","primary":"#2345AA"}""";
        BrandingThemeRegistry.ValidateAndCanonicalize(reordered).Value.Should().Be(
            """{"primary":"#2345aa","background":"#ffffff","foreground":"#111111","muted":"#555555"}""");
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void Complete_valid_json_respects_the_input_byte_cap(int excess, bool accepted)
    {
        var padded = Valid.PadRight(JsonValue.MaxRowBytes + excess);
        var result = BrandingThemeRegistry.ValidateAndCanonicalize(padded);
        result.IsSuccess.Should().Be(accepted);
        if (!accepted)
        {
            result.Error!.Code.Should().Be("validation_failed");
            result.Error.Details.Should().ContainSingle().Which.Key.Should().Be("Theme");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"primary\":\"#2345aa\",\"background\":\"#ffffff\",\"foreground\":\"#111111\"}")]
    [InlineData("{\"primary\":\"#2345aa\",\"background\":\"#ffffff\",\"foreground\":\"#111111\",\"muted\":\"#555555\",\"font\":\"https://example.com\"}")]
    [InlineData("{\"primary\":\"#2345aa\",\"primary\":\"#2345aa\",\"background\":\"#ffffff\",\"foreground\":\"#111111\",\"muted\":\"#555555\"}")]
    [InlineData("{\"Primary\":\"#2345aa\",\"background\":\"#ffffff\",\"foreground\":\"#111111\",\"muted\":\"#555555\"}")]
    [InlineData("{\"primary\":42,\"background\":\"#ffffff\",\"foreground\":\"#111111\",\"muted\":\"#555555\"}")]
    public void Invalid_object_shape_is_a_bounded_validation_refusal(string? theme)
    {
        var result = BrandingThemeRegistry.ValidateAndCanonicalize(theme!);
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("validation_failed");
        result.Error.Details.Should().ContainSingle().Which.Value.Should().ContainSingle();
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#fff")]
    [InlineData("#2345aaff")]
    [InlineData("rgb(0,0,0)")]
    [InlineData("var(--token)")]
    [InlineData("url(https://example.com)")]
    [InlineData("#zzzzzz")]
    [InlineData(" #2345aa")]
    public void Active_or_non_rgb_values_are_refused(string color)
    {
        BrandingThemeRegistry.ValidateAndCanonicalize(Valid.Replace("#2345AA", color, StringComparison.Ordinal))
            .IsFailure.Should().BeTrue();
    }

    [Theory]
    [InlineData("foreground", "#777777", false)]
    [InlineData("foreground", "#767676", true)]
    [InlineData("muted", "#777777", false)]
    [InlineData("muted", "#767676", true)]
    [InlineData("primary", "#959595", false)]
    [InlineData("primary", "#949494", true)]
    public void Contrast_is_enforced_independently_for_each_supported_pair(string field, string color, bool accepted)
    {
        var original = field switch { "foreground" => "#111111", "muted" => "#555555", _ => "#2345AA" };
        var result = BrandingThemeRegistry.ValidateAndCanonicalize(Valid.Replace(original, color, StringComparison.Ordinal));
        result.IsSuccess.Should().Be(accepted);
        if (!accepted)
        {
            result.Error!.Details.Should().ContainKey("/" + field)
                .WhoseValue.Should().ContainSingle().Which.Key.Should().Be("lockey_branding_contrast");
        }
    }

    [Fact]
    public void Dark_background_uses_the_lighter_luminance_as_the_numerator()
    {
        var dark = """{"primary":"#ffffff","background":"#000000","foreground":"#757575","muted":"#ffffff"}""";
        BrandingThemeRegistry.ValidateAndCanonicalize(dark).IsSuccess.Should().BeTrue();
        BrandingThemeRegistry.ValidateAndCanonicalize(dark.Replace("#757575", "#747474", StringComparison.Ordinal))
            .IsFailure.Should().BeTrue();
    }
}
