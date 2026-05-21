namespace LearnStack.SharedKernel.Secrets;

/// <summary>
/// The canonical list of substrings whose presence in a property name (or
/// AdditionalTags key) marks the value as sensitive — passwords, tokens,
/// secrets, payment identifiers, national / corporate IDs. The Serilog
/// <c>RedactSensitiveFieldsEnricher</c> and the air-gapped
/// <c>LocalFileErrorTracker</c> consume this single source of truth so the
/// two redaction surfaces cannot drift.
/// </summary>
/// <remarks>
/// <para>
/// Matching is case-insensitive and substring-based: a property named
/// <c>UserPassword</c> matches the <c>password</c> token. Standards 11
/// § Sensitive Data Exposure is authoritative for what counts as
/// sensitive; this list is the runtime expression of that rule.
/// </para>
/// <para>
/// New tokens land here, not in the consuming projects. The
/// architecture-test suite asserts both consumers route through
/// <see cref="DefaultTokens"/> so a future addition lights up the
/// Serilog path and the air-gapped path together.
/// </para>
/// </remarks>
public static class SensitiveTokenCatalog
{
    /// <summary>Substituted in place of any matched property value.</summary>
    public const string RedactedValue = "***REDACTED***";

    /// <summary>
    /// Substring tokens that mark a property as sensitive. Sorted
    /// alphabetically; additions go anywhere in the list.
    /// </summary>
    public static IReadOnlyList<string> DefaultTokens { get; } =
    [
        "apikey",
        "api_key",
        "authorization",
        "auth_header",
        "cardnumber",
        "card_number",
        "credential",
        "cvc",
        "cvv",
        "dsn",
        "iban",
        "jwt",
        "passwd",
        "password",
        "secret",
        "ssn",
        "tckn", // Turkish national ID
        "token",
        "vkn",  // Turkish corporate tax number (Vergi Kimlik Numarası)
    ];

    /// <summary>
    /// Returns <c>true</c> when the property name contains any token
    /// from <see cref="DefaultTokens"/> (case-insensitive substring
    /// match).
    /// </summary>
    public static bool IsSensitive(string propertyName)
    {
        if (string.IsNullOrEmpty(propertyName))
        {
            return false;
        }

        foreach (var token in DefaultTokens)
        {
            if (propertyName.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
