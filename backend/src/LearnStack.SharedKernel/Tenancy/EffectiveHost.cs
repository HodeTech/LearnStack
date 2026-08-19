using System.Globalization;
using System.Net;

namespace LearnStack.SharedKernel.Tenancy;

/// <summary>
/// Normalises a raw host string into the single form used both as the
/// <c>platform_host_to_tenant</c> lookup key and as the
/// <c>app.resolving_host</c> session variable, per
/// <see href="../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036
/// § Effective host and the trusted hop</see>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Total.</b> Every failure returns <c>null</c>; nothing throws. The input is
/// attacker-controlled on every anonymous request, so an exception here is a
/// remote client writing entries into the error tracker at will.
/// </para>
/// <para>
/// <see cref="Microsoft.AspNetCore.Http.HostString"/>'s <c>FromUriComponent</c>
/// is deliberately not used: it performs a punycode <i>decode</i> nobody asked
/// for, and it raises <see cref="ArgumentException"/> on inputs such as
/// <c>xn--</c>, <c>xn--a</c> and <c>a.xn--.b</c> — exactly the "total" property
/// this type exists to provide.
/// </para>
/// </remarks>
public static class EffectiveHost
{
    /// <summary>
    /// The DNS limit. A longer string cannot name a host, so rejecting it
    /// early keeps every later step working on a bounded input.
    /// </summary>
    public const int MaxLength = 253;

    private static readonly IdnMapping Idn = new();

    /// <summary>
    /// Returns the normalised host, or <c>null</c> when the input cannot be
    /// one. The steps are ordered so each works on what the previous
    /// guaranteed.
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaxLength)
        {
            return null;
        }

        // Reject before parsing rather than after. A host is a name, and every
        // one of these characters means the caller sent something that is not
        // one — a path, a userinfo section, a percent-escape that would let
        // two spellings of the same string reach the lookup, or a NUL that
        // truncates in whatever reads it next.
        foreach (var character in raw)
        {
            if (char.IsWhiteSpace(character)
                || character is '/' or '@' or '%' or '\0' or '\\' or '?' or '#')
            {
                return null;
            }
        }

        // IPv6 literals arrive bracketed. Refused, not normalised: a host
        // mapping is a name, and an address that resolves to the same server
        // is not the tenant's identity.
        if (raw.StartsWith('[') || raw.Contains(']', StringComparison.Ordinal))
        {
            return null;
        }

        var withoutPort = StripPort(raw);
        if (withoutPort is null)
        {
            return null;
        }

        // IPv4 literal after the port is gone, so `1.2.3.4:443` is caught too.
        if (IPAddress.TryParse(withoutPort, out _))
        {
            return null;
        }

        // Exactly one trailing dot is the fully-qualified form and means the
        // same host. Two is malformed, and so is a bare ".".
        var withoutTrailingDot = withoutPort;
        if (withoutTrailingDot.EndsWith('.'))
        {
            withoutTrailingDot = withoutTrailingDot[..^1];
            if (withoutTrailingDot.Length == 0 || withoutTrailingDot.EndsWith('.'))
            {
                return null;
            }
        }

        string ascii;
        try
        {
            ascii = Idn.GetAscii(withoutTrailingDot);
        }
        catch (ArgumentException)
        {
            // The documented failure mode for `xn--`, `xn--a`, `a.xn--.b`, an
            // empty label, and an over-long label. Caught rather than
            // prevented, because the rule set is IDNA's and restating it here
            // would be a second, drifting copy.
            return null;
        }

        // Invariant, not current-culture. This team's default culture is
        // tr-TR, where ToLower() maps 'I' to 'ı' — which would turn every host
        // containing a capital I into a key that matches no row.
        var lowered = ascii.ToLowerInvariant();

        // Validate the OUTPUT, not just the input. Measured: GetAscii performs
        // a compatibility mapping, so the fullwidth solidus U+FF0F arrives as a
        // literal '/', U+FF20 as '@', U+FF05 as '%' — every one of them past
        // the raw-input scan above, which by then has already run. And ';',
        // '\'' and '"' were never on that scan at all. The result was a
        // "normalised host" carrying the characters this type promises to
        // reject, on its way to being a SQL lookup key and the
        // app.resolving_host session variable.
        //
        // A whitelist is the right shape here and a denylist never was: the
        // set of things a hostname may contain is small and closed, and the
        // set of things it may not is neither.
        return IsLdh(lowered) ? lowered : null;
    }

    /// <summary>
    /// True when every character is a letter, digit, hyphen or dot, and no
    /// label begins or ends with a hyphen — the LDH rule a hostname actually
    /// obeys.
    /// </summary>
    private static bool IsLdh(string host)
    {
        var labelStart = true;

        for (var index = 0; index < host.Length; index++)
        {
            var character = host[index];

            if (character == '.')
            {
                // A label ending in '-' is invalid, and so is an empty one.
                if (labelStart || host[index - 1] == '-')
                {
                    return false;
                }

                labelStart = true;
                continue;
            }

            if (!char.IsAsciiLetterOrDigit(character) && character != '-')
            {
                return false;
            }

            if (labelStart && character == '-')
            {
                return false;
            }

            labelStart = false;
        }

        return !labelStart && host[^1] != '-';
    }

    /// <summary>
    /// Removes a trailing <c>:port</c>, and only that. A colon whose tail is
    /// not all digits is not a port — it is a malformed host, and silently
    /// keeping the part before it would map two different inputs to one key.
    /// </summary>
    private static string? StripPort(string raw)
    {
        var colon = raw.LastIndexOf(':');
        if (colon < 0)
        {
            return raw;
        }

        var tail = raw.AsSpan(colon + 1);
        if (tail.Length == 0)
        {
            return null;
        }

        foreach (var character in tail)
        {
            if (!char.IsAsciiDigit(character))
            {
                return null;
            }
        }

        var head = raw[..colon];
        return head.Length == 0 ? null : head;
    }
}
