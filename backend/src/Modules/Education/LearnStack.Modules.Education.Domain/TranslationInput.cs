using System.Text.Json;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;

namespace LearnStack.Modules.Education.Domain;

internal static class TranslationInput
{
    internal static Error? Validate(string locale, string title, string slug)
    {
        // LocaleTag has no predicate counterpart. Keep this conversion limited to
        // its argument guard so audit/programmer exceptions cannot become refusals.
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(locale);
            MappedLength.EnsureAtMost(locale, LocaleTag.MaxLength, nameof(locale));
            LocaleTag.EnsureWellFormed(locale, nameof(locale));
        }
        catch (ArgumentException)
        {
            return EducationFailures.Validation("Locale", "lockey_education_locale_invalid");
        }

        if (string.IsNullOrWhiteSpace(title) || !JsonValue.IsStorableText(title))
        {
            return EducationFailures.Validation("Title", "lockey_education_title_invalid");
        }

        return EducationSlug.IsValid(slug)
            ? null
            : EducationFailures.Validation("Slug", "lockey_education_slug_invalid");
    }

    internal static bool IsObjectBody(string body)
    {
        // IsWellFormed includes PostgreSQL text/numeric restrictions; its separate
        // customization-row size cap does not apply to Education lesson content.
        if (!JsonValue.IsWellFormed(body))
        {
            return false;
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.ValueKind == JsonValueKind.Object;
    }
}
