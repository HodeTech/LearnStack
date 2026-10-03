using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using LearnStack.Modules.Tenancy.Application.Tenant;
using LearnStack.Modules.Tenancy.Application.Contracts.Settings;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Results;

namespace LearnStack.Modules.Tenancy.Application.Branding;

public sealed record BrandingColorDescriptor(string JsonName, string CssVariable, double MinimumContrast);

/// <summary>The closed authoring registry; generic settings remain unconstrained.</summary>
public static class BrandingThemeRegistry
{
    private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789abcdefABCDEF");
    public const string SettingKey = TenantSettingKeys.BrandingThemeName;
    public static ImmutableArray<BrandingColorDescriptor> Colors { get; } =
    [
        new("primary", "--ls-primary", 3),
        new("background", "--ls-bg", 0),
        new("foreground", "--ls-fg", 4.5),
        new("muted", "--ls-muted", 4.5),
    ];

    public static Result<string> ValidateAndCanonicalize(string theme)
    {
        if (string.IsNullOrWhiteSpace(theme) || !JsonValue.IsWithinRowCap(theme))
        {
            return Invalid("lockey_branding_invalid");
        }

        try
        {
            using var document = JsonDocument.Parse(theme);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Invalid("lockey_branding_invalid");
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!Colors.Any(color => color.JsonName == property.Name)
                    || property.Value.ValueKind != JsonValueKind.String
                    || !IsHex(property.Value.GetString())
                    || !values.TryAdd(property.Name, property.Value.GetString()!.ToLowerInvariant()))
                {
                    return Invalid("lockey_branding_invalid");
                }
            }

            if (values.Count != Colors.Length)
            {
                return Invalid("lockey_branding_invalid");
            }

            var background = Luminance(values["background"]);
            foreach (var color in Colors.Where(color => color.MinimumContrast > 0))
            {
                var luminance = Luminance(values[color.JsonName]);
                var ratio = (Math.Max(luminance, background) + 0.05) / (Math.Min(luminance, background) + 0.05);
                if (ratio < color.MinimumContrast)
                {
                    return TenantWriteFailures.Field<string>("lockey_validation_failed",
                        "/" + color.JsonName, "lockey_branding_contrast");
                }
            }

            // Registry order is canonical; authored JSON property order has no semantics.
            return Result.Ok(JsonSerializer.Serialize(Colors.ToDictionary(color => color.JsonName,
                color => values[color.JsonName], StringComparer.Ordinal)));
        }
        catch (JsonException)
        {
            return Invalid("lockey_branding_invalid");
        }
    }

    /// <summary>Uses the authoring grammar and contrast policy for a complete typed read.</summary>
    public static Result<BrandingTheme> Read(string theme)
    {
        var validated = ValidateAndCanonicalize(theme);
        if (validated.IsFailure)
        {
            return Result<BrandingTheme>.Fail(validated.Error);
        }

        using var document = JsonDocument.Parse(validated.Value);
        var root = document.RootElement;
        return Result.Ok(new BrandingTheme(root.GetProperty("primary").GetString()!,
            root.GetProperty("background").GetString()!, root.GetProperty("foreground").GetString()!,
            root.GetProperty("muted").GetString()!));
    }

    private static Result<string> Invalid(string reason) =>
        TenantWriteFailures.Field<string>("lockey_validation_failed", "Theme", reason);

    private static bool IsHex(string? value) => value is { Length: 7 } && value[0] == '#'
        && value.AsSpan(1).IndexOfAnyExcept(HexDigits) < 0;

    private static double Luminance(string color)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(byte.Parse(color.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture))
            + 0.7152 * Linear(byte.Parse(color.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture))
            + 0.0722 * Linear(byte.Parse(color.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }
}
