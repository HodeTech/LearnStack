using System.Net;
using FluentAssertions;
using LearnStack.Api.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Tenancy;

public sealed class AnonymousRequestIdentityTests
{
    private const string Secret = "a-test-only-visitor-hop-secret-32-characters";
    private static readonly AnonymousRequestIdentity Identity = new(new EffectiveHostAccessor(Options.Create(
        new TrustedHopOptions { Networks = ["127.0.0.1/32"], Secrets = [Secret] })));

    [Fact]
    public void Budgets_match_the_accepted_contract_independently_of_request_loops()
    {
        RateLimitingExtensions.AnonymousPermitPerWindow.Should().Be(60);
        RateLimitingExtensions.PeerPermitPerWindow.Should().Be(600);
        RateLimitingExtensions.Window.Should().Be(TimeSpan.FromMinutes(1));
    }

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("::ffff:127.0.0.1", "127.0.0.1")]
    [InlineData("0:0:0:0:0:ffff:7f00:1", "127.0.0.1")]
    [InlineData("2001:0DB8::0001", "2001:db8::1")]
    [InlineData("::1", "::1")]
    [InlineData("127.000.0.1", null)]
    [InlineData("127.1", null)]
    [InlineData("2130706433", null)]
    [InlineData("0x7f000001", null)]
    [InlineData("::ffff:127.000.0.1", null)]
    [InlineData("2001:db8::1%eth0", null)]
    [InlineData("[::1]", null)]
    [InlineData("1.2.3.4:80", null)]
    [InlineData(" 127.0.0.1", null)]
    [InlineData("127.0.0.1, 1.2.3.4", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Only_strict_bounded_IP_literals_are_admitted(string? value, string? expected) =>
        AnonymousRequestIdentity.ParseAddress(value).Should().Be(expected);

    [Fact]
    public void Oversized_addresses_are_refused() =>
        AnonymousRequestIdentity.ParseAddress(new string('1', AnonymousRequestIdentity.MaximumAddressLength + 1)).Should().BeNull();

    [Fact]
    public void Trusted_visitor_and_direct_socket_use_one_canonical_namespace()
    {
        var direct = Request("::ffff:203.0.113.9");
        var hop = Request("127.0.0.1", Secret, "0:0:0:0:0:ffff:cb00:7109");
        Identity.For(direct).VisitorKey.Should().Be("203.0.113.9");
        Identity.For(hop).VisitorKey.Should().Be(Identity.For(direct).VisitorKey);
        Identity.For(hop).PeerKey.Should().Be("127.0.0.1");
        Identity.For(hop).Refuse.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("127.1")]
    [InlineData("127.0.0.1, 203.0.113.9")]
    public void Invalid_trusted_metadata_uses_bounded_peer_fallback_before_refusal(string? visitor)
    {
        var result = Identity.For(Request("127.0.0.1", Secret, visitor));
        result.Should().Be(new AnonymousVisitor("127.0.0.1", "127.0.0.1", true));
    }

    [Fact]
    public void Repeated_trusted_metadata_is_not_first_or_last_wins()
    {
        var request = Request("127.0.0.1", Secret);
        request.Request.Headers[AnonymousRequestIdentity.VisitorHeaderName] = new[] { "203.0.113.9", "203.0.113.10" };
        Identity.For(request).Refuse.Should().BeTrue();
    }

    [Theory]
    [InlineData("203.0.113.9", Secret)]
    [InlineData("127.0.0.1", "wrong")]
    [InlineData("127.0.0.1", null)]
    public void Both_network_and_secret_are_required_to_state_a_visitor(string peer, string? secret)
    {
        var request = Request(peer, secret, "198.51.100.4");
        request.Request.Headers["X-Forwarded-For"] = "192.0.2.4";
        Identity.For(request).Should().Be(new AnonymousVisitor(peer, peer, false));
    }

    [Fact]
    public void Repeated_secret_is_untrusted_even_with_valid_visitor()
    {
        var request = Request("127.0.0.1", visitor: "203.0.113.9");
        request.Request.Headers[TrustedHopOptions.SecretHeaderName] = new[] { Secret, Secret };
        Identity.For(request).Should().Be(new AnonymousVisitor("127.0.0.1", "127.0.0.1", false));
    }

    [Fact]
    public void Unknown_peers_share_one_fixed_partition_and_ignore_metadata()
    {
        var request = Request(null, Secret, "203.0.113.9");
        Identity.For(request).Should().Be(new AnonymousVisitor(
            RateLimitingExtensions.UnknownPeerPartition, RateLimitingExtensions.UnknownPeerPartition, false));
    }

    private static DefaultHttpContext Request(string? peer, string? secret = null, string? visitor = null)
    {
        var request = new DefaultHttpContext();
        request.Connection.RemoteIpAddress = peer is null ? null : IPAddress.Parse(peer);
        if (secret is not null) request.Request.Headers[TrustedHopOptions.SecretHeaderName] = secret;
        if (visitor is not null) request.Request.Headers[AnonymousRequestIdentity.VisitorHeaderName] = visitor;
        return request;
    }
}
