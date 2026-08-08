namespace LearnStack.SharedKernel.Secrets;

/// <summary>
/// The canonical list of word-tokens whose presence as a <em>segment</em> of
/// a property name (or AdditionalTags key) marks the value as sensitive —
/// passwords, tokens, secrets, payment identifiers, national / corporate IDs.
/// The Serilog <c>RedactSensitiveFieldsEnricher</c> and the air-gapped
/// <c>LocalFileErrorTracker</c> consume this single source of truth so the
/// two redaction surfaces cannot drift.
/// </summary>
/// <remarks>
/// <para>
/// Matching is on <strong>word boundaries</strong>, not raw substrings, to
/// avoid over-redaction: a property named <c>ClassName</c> tokenises to
/// <c>["class", "name"]</c> and is NOT flagged for the <c>ssn</c> token (raw
/// <c>Contains("ssn")</c> would wrongly match <c>"classname"</c>), while
/// <c>UserPassword</c> → <c>["user", "password"]</c> still matches
/// <c>password</c>. Two-word tokens (<c>api_key</c>, <c>card_number</c>) match
/// either the joined form (<c>apikey</c>) or adjacent segments
/// (<c>Api</c>+<c>Key</c>). Standards 11 § Sensitive Data Exposure is
/// authoritative for what counts as sensitive; this list is the runtime
/// expression of that rule.
/// </para>
/// <para>
/// New tokens land here, not in the consuming projects, so a future addition
/// lights up the Serilog path and the air-gapped path together.
/// </para>
/// </remarks>
public static class SensitiveTokenCatalog
{
    /// <summary>Substituted in place of any matched property value.</summary>
    public const string RedactedValue = "***REDACTED***";

    /// <summary>
    /// Single-word tokens matched against a whole name-segment (or the
    /// separator-stripped full name). Sorted alphabetically.
    /// </summary>
    private static readonly HashSet<string> SingleWordTokens =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "apikey",
            "authorization",
            "cardnumber",
            "credential",
            "cvc",
            "cvv",
            "dsn",
            "iban",
            "jwt",
            "passwd",
            "password",
            "secret",
            "ssn",     // national ID (US SSN / TR shorthand)
            "tckn",    // Turkish national ID
            "token",
            "vkn",     // Turkish corporate tax number (Vergi Kimlik Numarası)
        };

    /// <summary>
    /// Joined forms of two-word tokens, matched against adjacent
    /// camelCase / separated segments (<c>Api</c>+<c>Key</c> → <c>apikey</c>).
    /// </summary>
    private static readonly HashSet<string> TwoWordTokens =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "apikey",      // api_key / ApiKey
            "cardnumber",  // card_number / CardNumber
            "authheader",  // auth_header / AuthHeader
        };

    /// <summary>
    /// The canonical token list (for docs / tests). Returns a snapshot, not
    /// the backing <see cref="SingleWordTokens"/> set, so a caller casting the
    /// result cannot mutate the catalogue.
    /// </summary>
    public static IReadOnlyCollection<string> DefaultTokens =>
        Array.AsReadOnly(SingleWordTokens.ToArray());

    /// <summary>
    /// Returns <c>true</c> when any whole segment of <paramref name="propertyName"/>
    /// (split on camelCase boundaries and <c>_ . -</c> separators) matches a
    /// sensitive token. Word-boundary matching prevents the substring
    /// false-positives that a naive <c>Contains</c> would produce (e.g.
    /// <c>ssn</c> inside <c>className</c>).
    /// </summary>
    public static bool IsSensitive(string propertyName)
    {
        if (string.IsNullOrEmpty(propertyName))
        {
            return false;
        }

        var segments = Tokenize(propertyName);

        // Whole-name fallback: a property literally named "apikey" (no
        // separators / case transitions) tokenises to a single segment that
        // the single-word set already covers, so this is implicit.
        for (var i = 0; i < segments.Count; i++)
        {
            if (SingleWordTokens.Contains(segments[i]))
            {
                return true;
            }

            if (i + 1 < segments.Count
                && TwoWordTokens.Contains(segments[i] + segments[i + 1]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Splits a property name into lowercase word segments on camelCase
    /// transitions and <c>_ . -</c> (and any non-alphanumeric) separators.
    /// </summary>
    private static List<string> Tokenize(string name)
    {
        var segments = new List<string>();
        var start = 0;

        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            var isSeparator = !char.IsLetterOrDigit(c);

            // camelCase boundaries:
            //   case 1  aA / 1A   — lower/digit → Upper  (userToken → user|Token)
            //   case 2  ABc       — Upper → Upper-then-lower (SSNToken → ssn|Token,
            //                       APIKey → api|Key) so trailing acronym letters
            //                       start the next word.
            var isCamelBoundary = i > start && char.IsUpper(c) &&
                ((char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]))
                 || (char.IsUpper(name[i - 1])
                     && i + 1 < name.Length
                     && char.IsLower(name[i + 1])));

            if (isSeparator)
            {
                if (i > start)
                {
                    segments.Add(name[start..i].ToLowerInvariant());
                }

                start = i + 1;
            }
            else if (isCamelBoundary)
            {
                segments.Add(name[start..i].ToLowerInvariant());
                start = i;
            }
        }

        if (start < name.Length)
        {
            segments.Add(name[start..].ToLowerInvariant());
        }

        return segments;
    }
}
