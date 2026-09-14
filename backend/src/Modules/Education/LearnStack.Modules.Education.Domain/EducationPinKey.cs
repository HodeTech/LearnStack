using LearnStack.SharedKernel.Domain;

namespace LearnStack.Modules.Education.Domain;

/// <summary>Value reference keys; their definitions belong to another module.</summary>
public static class EducationPinKey
{
    public const int MaxLength = 100;

    public static bool IsValid(string value) =>
        !string.IsNullOrEmpty(value) && value.Length <= MaxLength && UrlSlug.IsUrlSafe(value);

    public static void EnsureValid(string value, string parameterName)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException(
                "A definition key is 1–100 lowercase ASCII letters or digits with single interior hyphens.",
                parameterName);
        }
    }
}
