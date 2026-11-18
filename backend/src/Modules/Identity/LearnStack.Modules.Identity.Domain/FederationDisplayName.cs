using System.Globalization;
using System.Text;

namespace LearnStack.Modules.Identity.Domain;

/// <summary>
/// The broker's display name (ADR-0099 § Fixed broker profile): NFC-normalized and trimmed, 1 to 80 Unicode
/// scalar values, with no control, format (which includes every bidirectional override, embedding and isolate),
/// surrogate, private-use, unassigned or line/paragraph-separator character, no space other than U+0020, no
/// letter or symbol that renders blank (the Hangul fillers U+115F, U+1160, U+3164 and U+FFA0, and the blank
/// Braille pattern U+2800), and no combining mark (variation selectors included) as its first character.
/// A refused value is refused, never silently cleaned: the name is shown on the shared login page.
/// </summary>
public static class FederationDisplayName
{
    public const int MaximumLength = 80;

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null || value.Length == 0 || value.Length > MaximumLength * 4) return false;
        // Unpaired surrogates and noncharacters (U+FFFE throws) are refused before normalization, which would otherwise throw.
        if (!HasOnlyValidScalars(value)) return false;
        var composed = value.Normalize(NormalizationForm.FormC).Trim(' ');
        if (composed.Length == 0) return false;
        var count = 0;
        foreach (var rune in composed.EnumerateRunes())
        {
            // A leading mark has no base to attach to; it renders detached or blank.
            if (!Admitted(rune) || (count == 0 && IsMark(rune)) || ++count > MaximumLength) return false;
        }
        normalized = composed;
        return true;
    }

    private static bool IsMark(Rune rune) => Rune.GetUnicodeCategory(rune)
        is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    private static bool IsNoncharacter(Rune rune) => rune.Value is >= 0xFDD0 and <= 0xFDEF || (rune.Value & 0xFFFE) == 0xFFFE;

    private static bool HasOnlyValidScalars(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (Rune.DecodeFromUtf16(value.AsSpan(index), out var rune, out var consumed) != System.Buffers.OperationStatus.Done
                || IsNoncharacter(rune)) return false;
            index += consumed - 1;
        }
        return true;
    }

    private static bool Admitted(Rune rune)
    {
        if (rune.Value == ' ') return true;
        // Letters (Lo) and a symbol (So) by category, yet each renders as blank space.
        if (rune.Value is 0x115F or 0x1160 or 0x2800 or 0x3164 or 0xFFA0) return false;
        return Rune.GetUnicodeCategory(rune) switch
        {
            UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
                or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.SpaceSeparator => false,
            _ => true,
        };
    }
}
