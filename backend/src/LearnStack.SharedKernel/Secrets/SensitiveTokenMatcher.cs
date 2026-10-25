using System;
using System.Collections.Generic;
using System.Linq;

namespace LearnStack.SharedKernel.Secrets;

/// <summary>
/// Owns the sensitive word tokens and matcher shared by runtime redaction and
/// the LS0002 analyzer. This source also compiles under netstandard2.0.
/// </summary>
internal static class SensitiveTokenMatcher
{
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
            "connectionstring",  // embeds the database password
            "cookie",            // session material
            "credential",
            "creditcard",
            "cvc",
            "cvv",
            "dsn",
            "encryptionkey",
            "hmac",              // ADR-0019's Hub request envelope
            "iban",
            "jwt",
            "otp",
            "pan",               // primary account number
            "passwd",
            "password",
            "pin",
            "privatekey",        // signed-licence RSA material, per ISecretProvider
            "pwd",
            "secret",
            "signature",
            "signingkey",
            "ssn",     // national ID (US SSN / TR shorthand)
            "tckn",    // Turkish national ID
            "token",
            "vkn",     // Turkish corporate tax number
        };

    /// <summary>
    /// Joined forms of two-word tokens, matched against adjacent
    /// camelCase / separated segments (<c>Api</c>+<c>Key</c> → <c>apikey</c>).
    /// </summary>
    private static readonly HashSet<string> TwoWordTokens =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "apikey",           // api_key / ApiKey
            "cardnumber",       // card_number / CardNumber
            "authheader",       // auth_header / AuthHeader
            "authenticationheader", // authentication_header / AuthenticationHeader
            "connectionstring", // connection_string / ConnectionString
            "creditcard",       // credit_card / CreditCard
            "encryptionkey",    // encryption_key / EncryptionKey
            "privatekey",       // private_key / PrivateKey
            "setcookie",        // set_cookie / SetCookie
            "signingkey",       // signing_key / SigningKey
        };

    /// <summary>
    /// The immutable complete union of both token sets.
    /// </summary>
    internal static IReadOnlyCollection<string> DefaultTokens { get; } =
        Array.AsReadOnly(SingleWordTokens.Concat(TwoWordTokens)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(token => token, StringComparer.Ordinal)
            .ToArray());

    /// <summary>
    /// Returns <c>true</c> when any whole segment of <paramref name="propertyName"/>
    /// (split on camelCase boundaries and <c>_ . -</c> separators) matches a
    /// sensitive token. Word-boundary matching prevents the substring
    /// false-positives that a naive <c>Contains</c> would produce (e.g.
    /// <c>ssn</c> inside <c>className</c>).
    /// </summary>
    internal static bool IsSensitive(string propertyName)
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
            if (SingleWordTokens.Contains(segments[i]) || TwoWordTokens.Contains(segments[i]))
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
                    segments.Add(name.Substring(start, i - start).ToLowerInvariant());
                }

                start = i + 1;
            }
            else if (isCamelBoundary)
            {
                segments.Add(name.Substring(start, i - start).ToLowerInvariant());
                start = i;
            }
        }

        if (start < name.Length)
        {
            segments.Add(name.Substring(start).ToLowerInvariant());
        }

        return segments;
    }
}
