using System.Collections.Generic;

namespace LearnStack.SharedKernel.Secrets;

/// <summary>
/// The canonical list of word-tokens whose presence as a <em>segment</em> of
/// a property name (or AdditionalTags key) marks the value as sensitive —
/// passwords, tokens, secrets, payment identifiers, national / corporate IDs.
/// The Serilog <c>RedactSensitiveFieldsEnricher</c> and the air-gapped
/// <c>LocalFileErrorTracker</c> consume this single source of truth so the
/// redaction surfaces and the LS0002 analyzer cannot drift.
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
/// New tokens land in the shared internal matcher, so a future addition
/// reaches runtime redaction and compile-time checking together.
/// </para>
/// </remarks>
public static class SensitiveTokenCatalog
{
    /// <summary>Substituted in place of any matched property value.</summary>
    public const string RedactedValue = "***REDACTED***";

    /// <summary>
    /// An immutable, cached snapshot of the complete single-word and two-word
    /// token union. The backing matcher sets are never exposed.
    /// </summary>
    public static IReadOnlyCollection<string> DefaultTokens => SensitiveTokenMatcher.DefaultTokens;

    /// <summary>
    /// Returns <c>true</c> when the name contains a sensitive whole word or
    /// adjacent word pair, using the canonical word-boundary matcher.
    /// </summary>
    public static bool IsSensitive(string propertyName) => SensitiveTokenMatcher.IsSensitive(propertyName);
}
