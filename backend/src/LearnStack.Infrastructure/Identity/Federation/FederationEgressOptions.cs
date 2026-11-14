using System.Collections.Frozen;
using System.Globalization;

namespace LearnStack.Infrastructure.Identity.Federation;

/// <summary>
/// The operator allow-list <c>Federation:Egress:AllowedHosts</c> (ADR-0099 § Bounded egress): exact <c>host</c> or
/// <c>host:port</c> entries, no wildcard, no scheme, empty by default. A bare host admits that host on 443 whatever its
/// address class; <c>host:port</c> admits exactly that port. It is composition configuration, never tenant input.
/// </summary>
public sealed class FederationEgressOptions
{
    public const int MaximumEntries = 64;
    private readonly FrozenSet<string> _entries;

    public FederationEgressOptions(IEnumerable<string>? allowedHosts = null)
    {
        var entries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in allowedHosts ?? [])
        {
            if (Normalize(raw) is not { } entry || !entries.Add(entry))
                throw new ArgumentException("Federation egress allow-list entry is invalid or repeated.");
        }
        if (entries.Count > MaximumEntries) throw new ArgumentException("Federation egress allow-list is too long.");
        _entries = entries.ToFrozenSet(StringComparer.Ordinal);
    }

    public static FederationEgressOptions Empty { get; } = new();

    public int Count => _entries.Count;

    /// <summary>True when the operator named this exact host and port.</summary>
    internal bool Admits(string host, int port) =>
        _entries.Contains(Entry(host.ToLowerInvariant(), port));

    private static string Entry(string host, int port) =>
        port == 443 ? host : host + ":" + port.ToString(CultureInfo.InvariantCulture);

    private static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 260 || raw.Trim() != raw) return null;
        var value = raw.ToLowerInvariant();
        var separator = value.LastIndexOf(':');
        var host = separator < 0 ? value : value[..separator];
        var port = 443;
        if (separator >= 0 && (!int.TryParse(value[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port)
            || port is < 1 or > 65535 || value[(separator + 1)..].StartsWith('0')))
            return null;
        // A DNS name only: no wildcard, scheme, path, userinfo or IP literal.
        if (host.Length is 0 or > 253 || Uri.CheckHostName(host) != UriHostNameType.Dns || host.Contains('*', StringComparison.Ordinal)
            || host.StartsWith('.') || host.EndsWith('.') || host.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '.')))
            return null;
        return Entry(host, port);
    }

    public override string ToString() => nameof(FederationEgressOptions);
}
