using System.Security.Cryptography;
using System.Text;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace LearnStack.Api.Tenancy;

/// <summary>
/// The one place a request host is read, per
/// <see href="../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036
/// § Effective host and the trusted hop</see>. Everything downstream — the
/// resolver in Packet 7, <c>app.resolving_host</c>, host classification — reads
/// this and never <c>Request.Host</c>.
/// </summary>
public sealed class EffectiveHostAccessor(IOptions<TrustedHopOptions> options)
{
    private readonly TrustedHopOptions _options = options.Value;

    /// <summary>
    /// The normalised host this request is for, or <c>null</c> when the input
    /// cannot name one.
    /// </summary>
    public string? For(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return EffectiveHost.Normalize(RawHostFor(context));
    }

    /// <summary>
    /// True when this request arrived over the trusted hop — both the socket
    /// peer and the secret.
    /// </summary>
    public bool IsTrustedHop(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return _options.IsConfigured
            && PeerIsInsideTrustedNetwork(context)
            && SecretMatches(context);
    }

    private string? RawHostFor(HttpContext context)
    {
        if (!IsTrustedHop(context))
        {
            return context.Request.Host.Value;
        }

        var stated = context.Request.Headers[TrustedHopOptions.HostHeaderName];

        // Present more than once is ignored entirely, not resolved by taking
        // the first or the last. A multi-valued original-host header is the
        // classic host-confusion bug: a proxy in front of a client that
        // already sent one produces two, and whichever end you pick, some
        // topology makes it the attacker's.
        return stated.Count == 1 ? stated[0] : context.Request.Host.Value;
    }

    /// <summary>
    /// Reads the <b>socket</b> peer, never
    /// <c>HttpContext.Connection.RemoteIpAddress</c>.
    /// </summary>
    /// <remarks>
    /// With <c>UseForwardedHeaders</c> and <c>XForwardedFor</c> enabled — which
    /// the API needs for rate limiting and audit —
    /// <c>Connection.RemoteIpAddress</c> becomes a client-supplied value for
    /// exactly the peers designated as the hop, and in SaaS the gateway
    /// legitimately forwards the visitor's address, so the check would be
    /// permanently false. <c>ForwardedHeadersOptions.KnownNetworks</c> and
    /// <see cref="TrustedHopOptions.Networks"/> answer different questions and
    /// stay separate lists.
    /// </remarks>
    private bool PeerIsInsideTrustedNetwork(HttpContext context)
    {
        var peer = context.Features.Get<IHttpConnectionFeature>()?.RemoteIpAddress;
        if (peer is null)
        {
            return false;
        }

        foreach (var network in _options.Networks)
        {
            if (System.Net.IPNetwork.TryParse(network, out var parsed)
                && parsed.Contains(peer))
            {
                return true;
            }
        }

        return false;
    }

    private bool SecretMatches(HttpContext context)
    {
        var presented = context.Request.Headers[TrustedHopOptions.SecretHeaderName];
        if (presented.Count != 1 || string.IsNullOrEmpty(presented[0]))
        {
            return false;
        }

        var candidate = Encoding.UTF8.GetBytes(presented[0]!);
        var matched = false;

        foreach (var secret in _options.Secrets)
        {
            // Fixed-time, and every configured secret is compared even after a
            // match: returning early would leak which one matched, and how
            // many were tried, through timing.
            matched |= CryptographicOperations.FixedTimeEquals(
                candidate, Encoding.UTF8.GetBytes(secret));
        }

        return matched;
    }
}
