namespace LearnStack.Modules.Identity.Domain;

/// <summary>ADR-0093's ASCII mailbox matching; this never merges provider identities.</summary>
public static class InvitationMailbox
{
    public const int MaximumLength = 254;

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null || value.Length is 0 or > MaximumLength || value.Any(c => c > 127)) return false;
        var separator = value.IndexOf('@');
        if (separator <= 0 || separator != value.LastIndexOf('@') || separator == value.Length - 1) return false;
        var local = value.AsSpan(0, separator);
        if (local.Length > 64 || local[0] == '.' || local[^1] == '.' || local.Contains("..", StringComparison.Ordinal)) return false;
        foreach (var character in local)
            if (!char.IsAsciiLetterOrDigit(character) && !".!#$%&'*+-/=?^_`{|}~".Contains(character, StringComparison.Ordinal))
                return false;
        var domain = value.AsSpan(separator + 1);
        foreach (var label in domain.ToString().Split('.'))
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-'
                || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) return false;
        }
        normalized = value.ToLowerInvariant();
        return true;
    }
}
