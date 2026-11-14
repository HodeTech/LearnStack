using System.Text;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.SharedKernel.Hosting;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Authentication;

/// <summary>
/// ADR-0099 Amendment 6: the profile's test-record flag is exactly "the validated <c>scope</c> names
/// <c>federation.test_record</c> as a whole token". The projection runs after the stock handler validated the
/// signature, so these unsigned fixtures exercise only the claim reading.
/// </summary>
public sealed class TenantTokenProfileScopeTests
{
    private const string Issuer = "https://identity.example.test/realms/learnstack";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-15T12:00:00Z",
        System.Globalization.CultureInfo.InvariantCulture);
    private static readonly TenantAuthenticationOptions Settings =
        TenantAuthenticationOptions.Create(Issuer, DeploymentMode.SaaS);

    [Theory]
    [InlineData("federation.test_record", true)]
    [InlineData("openid federation.test_record profile", true)]
    [InlineData("openid profile federation.test_record", true)]
    [InlineData("openid profile", false)]
    [InlineData("federation.test_record_extra", false)]
    [InlineData("xfederation.test_record", false)]
    [InlineData("federation.TEST_RECORD", false)]
    [InlineData("federation", false)]
    public void The_test_record_flag_names_the_whole_scope_token_only(string scope, bool expected)
    {
        TenantTokenProfile.TryReadValidated(Token(scope), Settings, Now, out var identity, out var failure)
            .Should().BeTrue();
        failure.Should().Be(TenantTokenFailure.None);
        identity!.FederationTestRecord.Should().Be(expected);
    }

    [Fact]
    public void A_token_without_a_scope_claim_carries_no_test_record()
    {
        TenantTokenProfile.TryReadValidated(Token(null), Settings, Now, out var identity, out _).Should().BeTrue();
        identity!.FederationTestRecord.Should().BeFalse();
    }

    [Theory]
    [InlineData("openid\tfederation.test_record")]
    [InlineData("openid  federation.test_record")]
    [InlineData("platform federation.test_record")]
    public void A_malformed_or_refused_scope_is_refused_before_the_flag_is_read(string scope)
    {
        TenantTokenProfile.TryReadValidated(Token(scope), Settings, Now, out var identity, out var failure)
            .Should().BeFalse();
        failure.Should().Be(TenantTokenFailure.Scope);
        identity.Should().BeNull();
    }

    private static string Token(string? scope)
    {
        var issued = Now.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object>
        {
            ["iss"] = Issuer,
            ["aud"] = TenantAuthenticationOptions.Audience,
            ["typ"] = "Bearer",
            ["azp"] = TenantAuthenticationOptions.ClientId,
            ["iat"] = issued,
            ["exp"] = issued + 300,
            ["sub"] = "provider-subject",
        };
        if (scope is not null) claims["scope"] = scope;
        return Segment(new { alg = "RS256", typ = "JWT", kid = "owned-key" }) + "." + Segment(claims) + ".c2ln";
    }

    private static string Segment(object value) =>
        WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));
}
