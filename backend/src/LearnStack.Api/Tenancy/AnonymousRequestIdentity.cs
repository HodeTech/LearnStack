using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http.Features;

namespace LearnStack.Api.Tenancy;

/// <summary>One request-local identity for ADR-0053/0054's visitor and peer budgets.</summary>
public sealed class AnonymousRequestIdentity(EffectiveHostAccessor hosts)
{
    public const string VisitorHeaderName = "X-LearnStack-Visitor-Address";
    public const int MaximumAddressLength = 45;

    public AnonymousVisitor For(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Features.Get<AnonymousVisitor>() is { } captured) return captured;

        var peer = context.Features.Get<IHttpConnectionFeature>()?.RemoteIpAddress;
        var peerKey = peer is null ? RateLimitingExtensions.UnknownPeerPartition : Canonical(peer);
        var trusted = hosts.IsTrustedHop(context);
        var stated = context.Request.Headers[VisitorHeaderName];
        var visitor = trusted && stated.Count == 1 ? ParseAddress(stated[0]) : null;
        var identity = new AnonymousVisitor(peerKey, visitor ?? peerKey, trusted && visitor is null);
        // Coordinated admission and the pre-lookup refusal use the same capture.
        context.Features.Set(identity);
        return identity;
    }

    /// <summary>Strict IP literals only; legacy abbreviated IPv4 and scoped IPv6 are refused.</summary>
    public static string? ParseAddress(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaximumAddressLength
            || value.Any(character => !char.IsAsciiHexDigit(character) && character is not ':' and not '.')
            || !IPAddress.TryParse(value, out var address)) return null;

        if (value.Contains('.', StringComparison.Ordinal))
        {
            var dotted = value[(value.LastIndexOf(':') + 1)..];
            if (!IPAddress.TryParse(dotted, out var ipv4)
                || ipv4.AddressFamily != AddressFamily.InterNetwork
                || !string.Equals(dotted, ipv4.ToString(), StringComparison.Ordinal)) return null;
        }
        // TryParse accepts integer/hex IPv4 without any dots; those are not literals.
        if (address.AddressFamily == AddressFamily.InterNetwork
            && !string.Equals(value, address.ToString(), StringComparison.Ordinal)) return null;
        return Canonical(address);
    }

    private static string Canonical(IPAddress address) =>
        (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : new IPAddress(address.GetAddressBytes())).ToString();
}

/// <summary>Immutable metadata, never a tenant selector or an audit actor.</summary>
public sealed record AnonymousVisitor(string PeerKey, string VisitorKey, bool Refuse);
