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
        // A cheap early exit, and not the guarantee — the one below the
        // conversion is. See the return.
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
        // No second length check, and that is measured rather than assumed.
        // GetAscii expands — a Unicode label becomes a longer `xn--` A-label —
        // so an input inside 253 characters can convert to more. It cannot
        // *return* more: GetAscii enforces the 253-character total itself and
        // throws for 254, which the catch above already turns into null.
        // Measured on .NET 10: nine 20-character `ü` labels convert to 246 and
        // pass; twenty-one throw. A guard here would be unreachable code
        // claiming to prevent something that cannot happen.
        //
        // The IPv4 refusal, re-run on the value about to be RETURNED. The check
        // above sees `withoutPort`, and two later steps can PRODUCE a literal it
        // never saw: the trailing-dot strip turns `1.2.3.4.` into `1.2.3.4`, and
        // GetAscii's compatibility mapping folds U+3002 and U+FF0E into '.'.
        // Measured: `1.2.3.4.`, `9.`, `127.0.0.1.`, `2130706433.` and
        // `1.2.3.4.:443` all came back as accepted hosts, and every one of them
        // then threw in CacheKey.ForHostMapping — a 500 and an error-tracker
        // capture, per request, from an unauthenticated caller, where a bodyless
        // 404 was designed. That throw is also a host-existence oracle: only a
        // host that reaches the resolver can produce it.
        //
        // The general form, because this is the second instance of it and the
        // whitelist below was the first: **every rejection in this function is a
        // predicate on the produced value.** An input scan is an optimisation, and
        // an optimisation that is also the only check is a gate the next
        // normalization step walks around.
        if (IPAddress.TryParse(lowered, out _))
        {
            return null;
        }

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
