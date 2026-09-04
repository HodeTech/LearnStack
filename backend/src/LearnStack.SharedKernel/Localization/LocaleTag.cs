namespace LearnStack.SharedKernel.Localization;

/// <summary>
/// Guards a locale tag against BCP-47 well-formedness.
/// </summary>
/// <remarks>
/// <see href="../../../../docs/architecture/12-localization.md">12-localization.md</see>
/// says the column bounds the length and "well-formedness itself is validated in
/// application code, not by this column". This is that code: without it a
/// 35-character run of one letter was a locale, and the repository's own test
/// pinned that as correct.
/// </remarks>
public static partial class LocaleTag
{
    /// <summary>
    /// The width every locale column maps, per
    /// <see href="../../../../docs/architecture/12-localization.md">12-localization.md</see>.
    /// </summary>
    /// <remarks>
    /// Named for the reason <c>UrlSlug.MaxLength</c> is: two layers read it — the
    /// factories, which throw, and the validators, which refuse — and a number
    /// written twice is a number that drifts in one of them.
    /// </remarks>
    public const int MaxLength = 35;

    public static void EnsureWellFormed(string value, string parameterName)
    {
        // Shape first, then the framework. CultureInfo alone is not a check:
        // .NET treats an unknown but well-formed tag as a valid custom culture,
        // and on some platforms accepts tags this pattern rejects.
        if (!Pattern().IsMatch(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a well-formed BCP-47 language tag — expected forms are "
                + "'tr', 'tr-TR', 'zh-Hans', 'zh-Hans-CN'.",
                parameterName);
        }
    }

    /// <summary>
    /// The tag in BCP-47 canonical case: language lowercase, a 4-letter script
    /// subtag Title-cased, a 2-letter region uppercased. Everything else is
    /// lowercased.
    /// </summary>
    /// <remarks>
    /// Case is not significant in BCP-47, which is precisely the problem for a
    /// column that is half a primary key: without this, `en-US` and `en-us` are two
    /// rows naming one locale.
    /// </remarks>
    public static string Canonicalize(string value)
    {
        var parts = value.Split('-');

        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i].ToLowerInvariant();

            if (i > 0 && part.Length == 4 && !char.IsDigit(part[0]))
            {
                // A 4-letter subtag in this position is a script: Title case.
                part = char.ToUpperInvariant(part[0]) + part[1..];
            }
            else if (i > 0 && part.Length == 2)
            {
                // A 2-letter subtag after the language is a region: uppercase.
                part = part.ToUpperInvariant();
            }

            parts[i] = part;
        }

        return string.Join('-', parts);
    }

    // language[-script][-region][-variant…]: 2-3 letter (or 4-8 for registered
    // subtags) primary, optional 4-letter script, optional 2-letter or 3-digit
    // region, then variant subtags.
    [System.Text.RegularExpressions.GeneratedRegex(
        "^[a-zA-Z]{2,8}(-[a-zA-Z]{4})?(-([a-zA-Z]{2}|[0-9]{3}))?(-([a-zA-Z0-9]{5,8}|[0-9][a-zA-Z0-9]{3}))*$")]
    private static partial System.Text.RegularExpressions.Regex Pattern();
}
