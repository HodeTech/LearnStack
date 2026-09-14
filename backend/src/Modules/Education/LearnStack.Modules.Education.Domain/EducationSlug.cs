using LearnStack.SharedKernel.Domain;

namespace LearnStack.Modules.Education.Domain;

/// <summary>Education URL segments and stable course handles, never host labels.</summary>
public static class EducationSlug
{
    public const int MaxLength = 160;

    public static bool IsValid(string value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= MaxLength
        && UrlSlug.IsUrlSafe(value)
        && !Guid.TryParseExact(value, "N", out _)
        && !Guid.TryParseExact(value, "D", out _);

    public static void EnsureValid(string value, string parameterName)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException(
                "An Education slug is 1–160 lowercase ASCII letters or digits with single interior hyphens, excluding UUIDs.",
                parameterName);
        }
    }
}
