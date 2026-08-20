using System.Net;
using FluentAssertions;
using LearnStack.Api.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Tenancy;

/// <summary>
/// The trusted hop, per
/// <see href="../../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036
/// § Effective host and the trusted hop</see>.
/// </summary>
/// <remarks>
/// This predicate is the only thing in the system that lets a client-supplied
/// header displace <c>Request.Host</c>, and it shipped with no test at all. Its
/// two halves — network position and secret — are deliberately an AND, because
/// either alone has a topology that defeats it: everything in a Docker bridge or
/// a pod CIDR is the gateway's neighbour, and a secret that leaks into a bundle
/// or a log is no longer a secret. Every row below is one way that AND can be
/// wrong.
/// </remarks>
public sealed class EffectiveHostAccessorTests
{
    private const string Secret = "a-secret-long-enough-to-be-a-secret-32";
    private const string Rotated = "the-other-secret-during-a-rotation-32b";

    [Fact]
    public void An_Unconfigured_Hop_Trusts_Nothing()
    {
        // Absence of configuration is not a permissive default. A deployment
        // that has not declared a hop has no hop, however convincing the
        // request looks.
        var accessor = Accessor(networks: [], secrets: []);

        accessor.IsTrustedHop(Request(peer: "10.0.0.5", secret: Secret)).Should().BeFalse();
    }

    [Fact]
    public void The_Right_Peer_With_The_Right_Secret_Is_The_Hop()
    {
        Accessor().IsTrustedHop(Request(peer: "10.0.0.5", secret: Secret)).Should().BeTrue();
    }

    [Fact]
    public void The_Right_Peer_With_The_Wrong_Secret_Is_Not()
    {
        Accessor().IsTrustedHop(Request(peer: "10.0.0.5", secret: "wrong")).Should().BeFalse();
    }

    [Fact]
    public void The_Right_Secret_From_The_Wrong_Peer_Is_Not()
    {
        // The half that survives a leaked secret.
        Accessor().IsTrustedHop(Request(peer: "203.0.113.9", secret: Secret)).Should().BeFalse();
    }

    [Fact]
    public void A_Request_With_No_Peer_Is_Not()
    {
        var context = Request(peer: null, secret: Secret);

        Accessor().IsTrustedHop(context).Should().BeFalse();
    }

    [Fact]
    public void A_Missing_Secret_Header_Is_Not()
    {
        Accessor().IsTrustedHop(Request(peer: "10.0.0.5", secret: null)).Should().BeFalse();
    }

    [Fact]
    public void A_Repeated_Secret_Header_Is_Not()
    {
        // Refused rather than resolved by first-or-last, like every other
        // header on this surface.
        var context = Request(peer: "10.0.0.5", secret: null);
        context.Request.Headers[TrustedHopOptions.SecretHeaderName] = new[] { Secret, Secret };

        Accessor().IsTrustedHop(context).Should().BeFalse();
    }

    [Fact]
    public void Either_Configured_Secret_Matches_So_A_Rotation_Does_Not_Break_The_Hop()
    {
        var accessor = Accessor(secrets: [Secret, Rotated]);

        accessor.IsTrustedHop(Request(peer: "10.0.0.5", secret: Rotated)).Should().BeTrue();
        accessor.IsTrustedHop(Request(peer: "10.0.0.5", secret: Secret)).Should().BeTrue();
    }

    // ---- what the host ends up being ---------------------------------------

    [Fact]
    public void An_Untrusted_Request_Ignores_The_Header_Entirely()
    {
        // The whole point of the predicate: an attacker who guesses the header
        // name gets nothing, and does not learn that they guessed right.
        var context = Request(peer: "203.0.113.9", secret: null, host: "real.example.com");
        context.Request.Headers[TrustedHopOptions.HostHeaderName] = "attacker.example.com";

        Accessor().For(context).Should().Be("real.example.com");
    }

    [Fact]
    public void A_Trusted_Request_Uses_The_Header()
    {
        var context = Request(peer: "10.0.0.5", secret: Secret, host: "gateway.internal");
        context.Request.Headers[TrustedHopOptions.HostHeaderName] = "tenant.example.com";

        Accessor().For(context).Should().Be("tenant.example.com");
    }

    [Fact]
    public void A_Repeated_Host_Header_Is_Ignored_Even_Over_The_Hop()
    {
        // A proxy in front of a client that already sent one produces two, and
        // whichever end you pick, some topology makes it the attacker's. So
        // neither is picked.
        var context = Request(peer: "10.0.0.5", secret: Secret, host: "gateway.internal");
        context.Request.Headers[TrustedHopOptions.HostHeaderName] =
            new[] { "one.example.com", "two.example.com" };

        Accessor().For(context).Should().Be("gateway.internal");
    }

    [Fact]
    public void A_Stated_Host_Still_Goes_Through_Normalization()
    {
        // The hop is trusted to name a host, not to name a valid one.
        var context = Request(peer: "10.0.0.5", secret: Secret, host: "gateway.internal");
        context.Request.Headers[TrustedHopOptions.HostHeaderName] = "1.2.3.4:443";

        Accessor().For(context).Should().BeNull(
            "an IP literal is not a tenant's identity, with or without a port");
    }

    [Fact]
    public void A_Port_Is_Stripped_From_Either_Source()
    {
        Accessor().For(Request(peer: "203.0.113.9", secret: null, host: "site.example.com:8443"))
            .Should().Be("site.example.com");
    }

    private static EffectiveHostAccessor Accessor(
        IReadOnlyList<string>? networks = null, IReadOnlyList<string>? secrets = null) =>
        new(Options.Create(new TrustedHopOptions
        {
            Networks = networks ?? ["10.0.0.0/8"],
            Secrets = secrets ?? [Secret],
        }));

    private static DefaultHttpContext Request(
        string? peer, string? secret, string host = "default.example.com")
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);

        context.Features.Set<IHttpConnectionFeature>(new HttpConnectionFeature
        {
            RemoteIpAddress = peer is null ? null : IPAddress.Parse(peer),
        });

        if (secret is not null)
        {
            context.Request.Headers[TrustedHopOptions.SecretHeaderName] = secret;
        }

        return context;
    }
}
