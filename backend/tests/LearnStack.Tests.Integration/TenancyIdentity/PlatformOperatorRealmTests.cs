using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.SharedKernel.Hosting;
using LearnStack.Tests.Integration.Database;
using LearnStack.SharedKernel.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Xunit;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>
/// What the pinned <c>learnstack-hub</c> realm actually issues to an operator, judged by the
/// same token profile the scheme and the rebind tool use (ADR-0101 § 2, § 3).
/// </summary>
/// <remarks>
/// <para>
/// A controlled issuer isolates protocol refusals in <see cref="PlatformOperatorHttpTests"/>; this
/// is the other half: the realm export, as shipped, emits an audience, a client, an <c>acr</c>
/// that shows the second factor, an <c>auth_time</c> and a lifetime inside the cap. A realm that
/// stopped doing any of these would pass every controlled-token case and fail here.
/// </para>
/// <para>
/// The shipped demo user must set up OTP at its first login, which a headless fixture cannot do,
/// so this case imports the same realm with that one user holding a known OTP credential.
/// Everything else is the file under <c>infra/keycloak/realms</c>.
/// </para>
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class PlatformOperatorRealmTests : IAsyncLifetime
{
    private const string OtpSecret = "JBSWY3DPEHPK3PXP";
    private const string Redirect = "http://127.0.0.1:8765/callback";
    private const string OperatorClient = "learnstack-operator";

    private KeycloakFixture _keycloak = null!;

    public async Task InitializeAsync()
    {
        var realm = JsonNode.Parse(await File.ReadAllTextAsync(KeycloakFixture.RealmPath("learnstack-hub")))!.AsObject();
        var user = realm["users"]!.AsArray()[0]!.AsObject();
        user["requiredActions"] = new JsonArray();
        user["credentials"]!.AsArray().Add(new JsonObject
        {
            ["type"] = "otp",
            ["userLabel"] = "fixture",
            ["secretData"] = JsonSerializer.Serialize(new { value = OtpSecret }),
            ["credentialData"] = JsonSerializer.Serialize(new
            {
                subType = "totp",
                digits = 6,
                period = 30,
                algorithm = "HmacSHA1",
                secretEncoding = "BASE32",
            }),
        });
        _keycloak = await KeycloakFixture.StartAsync(hubRealmJson: realm.ToJsonString());
    }

    public async Task DisposeAsync() => await _keycloak.DisposeAsync();

    [Fact]
    public async Task The_Operator_Client_Issues_A_Token_That_Meets_The_Operator_Profile()
    {
        var token = await OperatorTokenAsync();

        token.Issuer.Should().Be(_keycloak.IssuerFor("learnstack-hub"));
        token.Audiences.Should().Contain("learnstack-platform");
        token.GetPayloadValue<string>("azp").Should().Be(OperatorClient);
        token.Subject.Should().Be(LearnStack.Infrastructure.PlatformOperator.PlatformOperatorGrantOptions.DemoOperatorSubject,
            "the export pins the demo operator's subject so a Development grant can name it");
        (new DateTimeOffset(token.ValidTo, TimeSpan.Zero) - DateTimeOffset.FromUnixTimeSeconds(token.GetPayloadValue<long>("iat")))
            .Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(15), "the realm caps the operator client's access token");

        var settings = Settings(token.GetPayloadValue<string>("acr"));
        PlatformOperatorTokenProfile.TryRead(token, settings, DateTimeOffset.UtcNow, null, out var witness)
            .Should().BeTrue("the token the realm issues is the token the scheme accepts");
        witness!.SubjectDigest.Should().Be(PlatformOperatorSubject.Digest(_keycloak.IssuerFor("learnstack-hub"), token.Subject));
    }

    [Fact]
    public async Task The_Acr_Shows_The_Second_Factor_And_The_Shipped_Development_Config_Accepts_It()
    {
        var token = await OperatorTokenAsync();
        var acr = token.GetPayloadValue<string>("acr");

        var development = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(KeycloakFixture.RepositoryRoot(), "backend", "src", "LearnStack.Api", "appsettings.Development.json")));
        var accepted = development.RootElement.GetProperty("PlatformOperator").GetProperty("AcceptedAcr")
            .EnumerateArray().Select(value => value.GetString()).ToList();

        accepted.Should().Contain(acr, "Development configuration accepts exactly what the realm emits for MFA");
        token.TryGetPayloadValue<long>("auth_time", out _).Should().BeTrue();
    }

    [Fact]
    public async Task A_Token_From_The_Portal_Client_Does_Not_Meet_The_Operator_Profile()
    {
        // The Hub portal client shares the realm and the user, but not the audience, the
        // client allow-list or the step-up: it is not an operator credential for LearnStack.
        using var browser = _keycloak.Browser("learnstack-hub");
        var authorization = await browser.AuthorizeAsync(
            "demo-operator@learnstack.test", KeycloakFixture.DemoPassword, "http://localhost:3100/callback",
            clientId: "learnstack-hub-web", otpCode: () => Task.FromResult(Totp()));
        authorization.Authorized.Should().BeTrue();
        var exchange = await browser.RedeemAsync(authorization, secret: null);
        exchange.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var token = new JsonWebToken(exchange.AccessToken);

        token.Audiences.Should().NotContain("learnstack-platform");
        PlatformOperatorTokenProfile.TryRead(token, Settings("mfa", "2", "1"), DateTimeOffset.UtcNow, null, out _)
            .Should().BeFalse("a portal token carries neither the operator client nor the platform audience");
    }

    private async Task<JsonWebToken> OperatorTokenAsync()
    {
        using var browser = _keycloak.Browser("learnstack-hub");
        var authorization = await browser.AuthorizeAsync(
            "demo-operator@learnstack.test", KeycloakFixture.DemoPassword, Redirect,
            clientId: OperatorClient, scope: "openid", otpCode: () => Task.FromResult(Totp()));
        authorization.Authorized.Should().BeTrue("password, then the one-time password, completes the step-up");
        authorization.MfaChallenged.Should().BeTrue("the operator flow asks for the second factor on every login");
        var exchange = await browser.RedeemAsync(authorization, secret: null);
        exchange.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        return new JsonWebToken(exchange.AccessToken);
    }

    private PlatformOperatorSettings Settings(params string[] acr)
    {
        var values = new Dictionary<string, string?>
        {
            ["PlatformOperator:Issuer"] = _keycloak.IssuerFor("learnstack-hub"),
            ["PlatformOperator:AuthorizedParties:0"] = OperatorClient,
        };
        for (var index = 0; index < acr.Length; index++)
        {
            values[$"PlatformOperator:AcceptedAcr:{index}"] = acr[index];
        }

        return PlatformOperatorSettings.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(), DeploymentMode.Development)!;
    }

    /// <summary>RFC 6238, HMAC-SHA1, six digits, thirty seconds; the realm accepts one step either side.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "RFC 6238 TOTP is defined over HMAC-SHA1; this generates the fixture user's one-time code.")]
    private static string Totp()
    {
        var key = Base32(OtpSecret);
        var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var message = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(message);
        }

        var hash = HMACSHA1.HashData(key, message);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static byte[] Base32(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        var buffer = 0;
        var bits = 0;
        foreach (var character in value.TrimEnd('='))
        {
            buffer = (buffer << 5) | alphabet.IndexOf(character, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }

        return [.. bytes];
    }
}
