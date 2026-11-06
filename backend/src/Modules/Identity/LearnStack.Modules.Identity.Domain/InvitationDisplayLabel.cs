using System.Text;

namespace LearnStack.Modules.Identity.Domain;

/// <summary>
/// A tenant or branch name as an invitation shows it. Tenancy accepts any non-blank name up to its
/// length bound, control characters included, so a recipient-facing surface shows one clean line
/// rather than refusing the invitation. Escaping for the medium stays with the caller.
/// </summary>
public static class InvitationDisplayLabel
{
    public const int MaximumLength = 200;

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null || value.Length is 0 or > MaximumLength) return false;
        var line = new StringBuilder(value.Length);
        var gap = false;
        foreach (var character in value)
        {
            // A run of controls becomes one space, and only where no space already separates the text.
            if (char.IsControl(character)) { gap = true; continue; }
            if (gap && line.Length > 0 && line[^1] != ' ' && character != ' ') line.Append(' ');
            gap = false;
            line.Append(character);
        }
        normalized = line.ToString().Trim();
        return normalized.Length > 0;
    }
}
