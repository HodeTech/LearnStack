using System.Net;
using System.Net.Sockets;

namespace LearnStack.Infrastructure.Identity.Federation;

/// <summary>
/// ADR-0099 § Bounded egress address classes. An address is admitted for a tenant-supplied host only when it is
/// globally routable: loopback, link-local (169.254.0.0/16 cloud metadata included), private, CGNAT, documentation,
/// benchmarking, multicast, reserved, unspecified and every IPv4-embedding IPv6 form (mapped, compatible, NAT64,
/// 6to4, Teredo) and unique-local or site-local IPv6 refuse. IPv6 must be global unicast (2000::/3).
/// </summary>
internal static class FederationAddressPolicy
{
    private static readonly (byte[] Prefix, int Bits)[] RefusedV4 =
    [
        ([0, 0, 0, 0], 8),          // "this network"
        ([10, 0, 0, 0], 8),         // private
        ([100, 64, 0, 0], 10),      // CGNAT
        ([127, 0, 0, 0], 8),        // loopback
        ([169, 254, 0, 0], 16),     // link-local, cloud metadata
        ([172, 16, 0, 0], 12),      // private
        ([192, 0, 0, 0], 24),       // IETF protocol assignments
        ([192, 0, 2, 0], 24),       // documentation TEST-NET-1
        ([192, 31, 196, 0], 24),    // AS112
        ([192, 52, 193, 0], 24),    // AMT
        ([192, 88, 99, 0], 24),     // deprecated 6to4 relay
        ([192, 168, 0, 0], 16),     // private
        ([192, 175, 48, 0], 24),    // direct delegation AS112
        ([198, 18, 0, 0], 15),      // benchmarking
        ([198, 51, 100, 0], 24),    // documentation TEST-NET-2
        ([203, 0, 113, 0], 24),     // documentation TEST-NET-3
        ([224, 0, 0, 0], 4),        // multicast
        ([240, 0, 0, 0], 4),        // reserved, broadcast
    ];

    private static readonly (byte[] Prefix, int Bits)[] RefusedV6 =
    [
        (Parse("64:ff9b::"), 96),   // NAT64 (embeds IPv4)
        (Parse("64:ff9b:1::"), 48), // local-use NAT64
        (Parse("100::"), 64),       // discard-only
        (Parse("2001::"), 23),      // IETF protocol assignments, Teredo, ORCHID
        (Parse("2001:db8::"), 32),  // documentation
        (Parse("2002::"), 16),      // 6to4 (embeds IPv4)
        (Parse("3fff::"), 20),      // documentation (RFC 9637)
        (Parse("5f00::"), 16),      // SRv6 SIDs
    ];

    /// <summary>True only for a globally routable unicast address a tenant-supplied host may resolve to.</summary>
    internal static bool IsGlobal(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return !RefusedV4.Any(range => Matches(bytes, range.Prefix, range.Bits));
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6 || address.IsIPv4MappedToIPv6 || address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal || address.IsIPv6Teredo
            || address.ScopeId != 0)
            return false;
        var v6 = address.GetAddressBytes();
        // Global unicast is 2000::/3; everything else (unspecified, loopback, IPv4-compatible, ULA, link-local,
        // multicast, reserved) lies outside it.
        if ((v6[0] & 0xE0) != 0x20) return false;
        return !RefusedV6.Any(range => Matches(v6, range.Prefix, range.Bits));
    }

    private static readonly (byte[] Prefix, int Bits)[] PrivateV4 =
    [
        ([10, 0, 0, 0], 8),
        ([172, 16, 0, 0], 12),
        ([192, 168, 0, 0], 16),
    ];

    /// <summary>
    /// Cloud instance-metadata addresses inside a range an allow-list entry otherwise admits: fd00:ec2::/32 holds the AWS
    /// IPv6 metadata endpoint (fd00:ec2::254) and its other services. It refuses even for a listed host.
    /// </summary>
    private static readonly (byte[] Prefix, int Bits)[] MetadataV6 =
    [
        (Parse("fd00:ec2::"), 32),
    ];

    /// <summary>
    /// An operator allow-listed host may additionally resolve to a private address: RFC 1918 IPv4 or unique-local IPv6
    /// (fc00::/7) outside fd00:ec2::/32. Nothing else widens. Loopback, link-local and cloud metadata (169.254.0.0/16,
    /// fe80::/10, fd00:ec2::/32), CGNAT, documentation, benchmarking, reserved, multicast, unspecified, scoped and every
    /// IPv4-embedding IPv6 form (mapped, compatible, NAT64, 6to4, Teredo) still refuse, as for a tenant-supplied host
    /// (ADR-0099 Amendment 5).
    /// </summary>
    internal static bool IsAllowListable(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (IsGlobal(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return PrivateV4.Any(range => Matches(bytes, range.Prefix, range.Bits));
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6 || !address.IsIPv6UniqueLocal || address.ScopeId != 0)
            return false;
        var v6 = address.GetAddressBytes();
        return !MetadataV6.Any(range => Matches(v6, range.Prefix, range.Bits));
    }

    private static bool Matches(byte[] address, byte[] prefix, int bits)
    {
        if (address.Length != prefix.Length) return false;
        var whole = bits / 8;
        for (var index = 0; index < whole; index++)
            if (address[index] != prefix[index]) return false;
        var rest = bits % 8;
        if (rest == 0) return true;
        var mask = (byte)(0xFF << (8 - rest));
        return (address[whole] & mask) == (prefix[whole] & mask);
    }

    private static byte[] Parse(string value) => IPAddress.Parse(value).GetAddressBytes();
}
