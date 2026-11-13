using System.Globalization;
using System.Text;

namespace LearnStack.Modules.Identity.Domain;

/// <summary>
/// The broker's display name (ADR-0099 § Fixed broker profile): NFC-normalized and trimmed, 1 to 80 Unicode
/// scalar values, with no control, format (which includes every bidirectional override, embedding and isolate),
/// surrogate, private-use, unassigned or line/paragraph-separator character, and no space other than U+0020.
/// A refused value is refused, never silently cleaned: the name is shown on the shared login page.
/// </summary>
public static class FederationDisplayName
{
    public const int MaximumLength = 80;

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null || value.Length == 0 || value.Length > MaximumLength * 4) return false;
        // Unpaired surrogates are refused before normalization, which would otherwise throw.
        if (!HasOnlyValidScalars(value)) return false;
        var composed = value.Normalize(NormalizationForm.FormC).Trim(' ');
        if (composed.Length == 0) return false;
        var count = 0;
        foreach (var rune in composed.EnumerateRunes())
        {
            if (!Admitted(rune) || ++count > MaximumLength) return false;
        }
        normalized = composed;
        return true;
    }

    private static bool HasOnlyValidScalars(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (Rune.DecodeFromUtf16(value.AsSpan(index), out _, out var consumed) != System.Buffers.OperationStatus.Done) return false;
            index += consumed - 1;
        }
        return true;
    }

    private static bool Admitted(Rune rune)
    {
        if (rune.Value == ' ') return true;
        return Rune.GetUnicodeCategory(rune) switch
        {
            UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
                or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.SpaceSeparator => false,
            _ => true,
        };
    }
}
