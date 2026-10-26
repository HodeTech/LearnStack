using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.Api.Tenancy;
using LearnStack.SharedKernel.Hosting;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>Real JwtBearer, discovery, JWKS and signatures in an isolated composition root.</summary>
public sealed partial class TenantAuthenticationProfileTests
{
    [Fact]
    public async Task Tenant_Authentication_Profile_Is_Closed()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var app = await CreateHost(issuer);
        using var client = app.GetTestClient();
        await AssertAccepted(client, issuer.Token(), issuer);
        issuer.MetadataRequests.Should().Be(1);
        issuer.JwksRequests.Should().Be(1);
        var acceptedParts = issuer.Token().Split('.');
        var modifiedParts = issuer.Token(issuer.Payload(("sub", "substituted-subject"))).Split('.');
        await AssertRejected(client, modifiedParts[0] + "." + modifiedParts[1] + "." + acceptedParts[2],
            "payload substitution under a known kid still requires its signature");

        // These tokens retain the trusted RSA signature. Each rejection therefore
        // exercises the named profile property rather than an unrelated bad key.
        var failures = new List<(string Reason, string Payload)>
        {
            ("other realm under the same trusted key", issuer.Payload(("iss", issuer.Issuer + "-hub"))),
            ("other audience", issuer.Payload(("aud", "another-api"))),
            ("ID-token purpose with correct audience/client", issuer.Payload(("typ", "ID"))),
            ("refresh-token purpose", issuer.Payload(("typ", "Refresh"))),
            ("other issuing client", issuer.Payload(("azp", "another-client"))),
            ("reserved system actor", issuer.Payload(("user_id", UserId.SystemActor.Value.ToString("D")))),
            ("reserved tenant", issuer.Payload(("tenant_id", TenantId.PlatformSentinel.Value.ToString("D")))),
            ("platform OAuth scope", issuer.Payload(("scope", "openid platform profile"))),
            ("organization without tenant", issuer.Without("tenant_id")),
            ("subject exceeds the opaque subject bound", issuer.Payload(("sub", new string('s', 256)))),
            ("subject contains only whitespace", issuer.Payload(("sub", " \t\r\n\u2003"))),
            ("duplicate audience value", issuer.Payload(("aud", (string[])["learnstack-api", "learnstack-api"]))),
            ("empty audience array", issuer.Payload(("aud", Array.Empty<string>()))),
            ("nonstring audience entry", issuer.PayloadWithRaw("aud", "[\"learnstack-api\",1]")),
            ("null audience entry", issuer.PayloadWithRaw("aud", "[\"learnstack-api\",null]")),
            ("empty audience entry", issuer.Payload(("aud", (string[])["learnstack-api", ""]))),
        };

        foreach (var name in new[] { "iss", "sub", "aud", "iat", "exp", "azp", "typ" })
        {
            failures.Add(($"missing {name}", issuer.Without(name)));
        }

        foreach (var name in new[] { "iss", "sub", "aud", "azp", "typ", "user_id", "tenant_id", "organization_id", "scope" })
        {
            foreach (var raw in new[] { "null", "{}", "[]", "1", "true", "\"\"" })
            {
                failures.Add(($"{name} has forbidden JSON shape {raw}", issuer.PayloadWithRaw(name, raw)));
            }
        }

        foreach (var name in new[] { "user_id", "tenant_id", "organization_id" })
        {
            var id = TenantAuthenticationIssuer.Actor.ToString("D");
            foreach (var value in new[] { Guid.Empty.ToString("D"), "not-a-uuid", " " + id, id + " ",
                TenantAuthenticationIssuer.Actor.ToString("N"), "{" + id + "}" })
            {
                failures.Add(($"{name} is not a permitted exact UUID", issuer.Payload((name, value))));
            }
            failures.Add(($"{name} is a singleton array", issuer.Payload((name, new[] { id }))));
        }

        foreach (var name in new[] { "iss", "sub", "aud", "exp", "iat", "nbf", "azp", "typ",
            "user_id", "tenant_id", "organization_id", "scope" })
        {
            var payload = issuer.Payload(("nbf", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 10));
            using var parsed = JsonDocument.Parse(payload);
            var value = parsed.RootElement.GetProperty(name);
            failures.Add(($"equal duplicate security property {name}",
                payload[..^1] + "," + JsonSerializer.Serialize(name) + ":" + value.GetRawText() + "}"));
            var unequal = value.ValueKind == JsonValueKind.Number
                ? (value.GetInt64() + 1).ToString(CultureInfo.InvariantCulture)
                : JsonSerializer.Serialize(value.GetString() + "-different");
            failures.Add(($"unequal duplicate security property {name}",
                payload[..^1] + "," + JsonSerializer.Serialize(name) + ":" + unequal + "}"));
        }

        foreach (var (reason, payload) in failures)
        {
            await AssertRejected(client, issuer.Token(payload), reason);
        }

        await AssertAccepted(client, issuer.Token(issuer.Payload(
            ("aud", (string[])["another-audience", "learnstack-api"]),
            ("user_id", TenantAuthenticationIssuer.Actor.ToString("D").ToUpperInvariant()),
            ("tenant_id", TenantAuthenticationIssuer.Tenant.ToString("D").ToUpperInvariant()),
            ("organization_id", TenantAuthenticationIssuer.Organization.ToString("D").ToUpperInvariant()),
            ("scope", "openid platform-reader"))), issuer);
        await AssertAccepted(client, issuer.Token(issuer.Without("organization_id", "scope")), issuer,
            tenant: TenantAuthenticationIssuer.Tenant, organization: null);
        await AssertAccepted(client, issuer.Token(issuer.Without("tenant_id", "organization_id")), issuer,
            tenant: null, organization: null);
        using var actorless = await Send(client, "Bearer " + issuer.Token(issuer.Without("user_id")));
        actorless.StatusCode.Should().Be(HttpStatusCode.OK);
        (await actorless.Content.ReadFromJsonAsync<AuthenticationObservation>())!.Actor.Should().BeNull(
            "stock verification admits an absent compatibility actor without inventing application identity");
        issuer.CanaryRequests.Should().Be(0);
    }

    [Fact]
    public async Task Protected_header_has_exact_duplicate_free_allowlist_and_never_selects_token_supplied_keys()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        var oversizedKid = issuer.NewKey(new string('k', 129));
        var whitespaceKid = issuer.NewKey(" \t\u2003");
        issuer.Publish(issuer.CurrentKey, oversizedKid, whitespaceKid);
        await using var app = await CreateHost(issuer);
        using var client = app.GetTestClient();
        await AssertAccepted(client, issuer.Token(), issuer);
        var baseline = TenantAuthenticationIssuer.Header(issuer.CurrentKey.Kid);

        foreach (var name in new[] { "alg", "typ", "kid" })
        {
            using var parsed = JsonDocument.Parse(baseline);
            var value = parsed.RootElement.GetProperty(name).GetString();
            await AssertRejected(client, issuer.Token(header:
                TenantAuthenticationIssuer.AddProperty(baseline, name, value)), "equal duplicate JOSE " + name);
            await AssertRejected(client, issuer.Token(header:
                TenantAuthenticationIssuer.AddProperty(baseline, name, value + "-different")), "unequal duplicate JOSE " + name);
            foreach (var raw in new[] { "null", "{}", "[]", "1", "true", "\"\"" })
            {
                var header = "{" + string.Join(",", parsed.RootElement.EnumerateObject().Select(property =>
                    JsonSerializer.Serialize(property.Name) + ":" +
                    (property.NameEquals(name) ? raw : property.Value.GetRawText()))) + "}";
                await AssertRejected(client, issuer.Token(header: header), "JOSE " + name + " shape " + raw);
            }
            var missing = "{" + string.Join(",", parsed.RootElement.EnumerateObject()
                .Where(property => !property.NameEquals(name)).Select(property =>
                    JsonSerializer.Serialize(property.Name) + ":" + property.Value.GetRawText())) + "}";
            await AssertRejected(client, issuer.Token(header: missing), "missing JOSE " + name);
        }

        var rogue = issuer.NewKey("unpublished-key");
        foreach (var (name, value) in new (string, object?)[]
        {
            ("crit", new[] { "example" }), ("cty", "JWT"),
            ("jku", issuer.CanaryAddress), ("x5u", issuer.CanaryAddress),
            ("jwk", rogue.PublicJwk), ("x5c", new[] { "certificate-canary" }),
            ("x5t", "certificate-thumbprint"), ("x5t#S256", "certificate-thumbprint"),
            ("b64", true), ("unknown-extension", "ignored-by-a-permissive-validator"),
        })
        {
            await AssertRejected(client, issuer.Token(header:
                TenantAuthenticationIssuer.AddProperty(baseline, name, value)), "forbidden JOSE " + name);
        }

        await AssertRejected(client, issuer.Token(header: "{\"alg\":\"none\",\"typ\":\"JWT\",\"kid\":\"initial-key\"}"),
            "unsigned algorithm");
        await AssertRejected(client, issuer.Token(header: "{\"alg\":\"RS512\",\"typ\":\"JWT\",\"kid\":\"initial-key\"}"),
            "other RSA algorithm");
        await AssertRejected(client, issuer.Token(header: "{\"alg\":\"RS256\",\"typ\":\"at+jwt\",\"kid\":\"initial-key\"}"),
            "other JOSE purpose");
        await AssertRejected(client, issuer.Token(key: oversizedKid),
            "oversized key id");
        await AssertRejected(client, issuer.Token(key: rogue), "unpublished signing key");
        await AssertRejected(client, issuer.Token(header: TenantAuthenticationIssuer.Header("unknown-key")),
            "a trusted signature cannot replace an unknown kid selector");
        await AssertRejected(client, issuer.Token(key: whitespaceKid),
            "a whitespace-only kid is not a key selector");
        var signed = issuer.Token();
        await AssertRejected(client, signed[..(signed.LastIndexOf('.') + 1)],
            "an actual unsigned compact JWS cannot authenticate with a permitted alg header");
        await AssertRejected(client, issuer.Token(key: rogue, header:
            TenantAuthenticationIssuer.AddProperty(TenantAuthenticationIssuer.Header(rogue.Kid), "jwk", rogue.PublicJwk)),
            "embedded signing key cannot establish trust");
        await AssertAccepted(client, issuer.Token(), issuer);
        issuer.CanaryRequests.Should().Be(0, "JOSE URL fields are never a metadata source");
    }

    [Fact]
    public async Task Lifetime_requires_integer_timestamps_and_enforces_hour_and_thirty_second_skew_bounds()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var app = await CreateHost(issuer);
        using var client = app.GetTestClient();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await AssertAccepted(client, issuer.Token(issuer.Payload(("iat", now - 60), ("exp", now + 3540))), issuer);
        await AssertRejected(client, issuer.Token(issuer.Payload(("iat", now - 60), ("exp", now + 3541))),
            "one hour plus one second");
        await AssertAccepted(client, issuer.Token(issuer.Payload(("iat", now + 20), ("exp", now + 600))), issuer);
        await AssertRejected(client, issuer.Token(issuer.Payload(("iat", now + 40))), "future iat beyond skew");
        await AssertAccepted(client, issuer.Token(issuer.Payload(("iat", now - 600), ("exp", now - 20))), issuer);
        await AssertRejected(client, issuer.Token(issuer.Payload(("iat", now - 600), ("exp", now - 40))),
            "expired beyond skew");
        await AssertAccepted(client, issuer.Token(issuer.Payload(("nbf", now + 20))), issuer);
        await AssertRejected(client, issuer.Token(issuer.Payload(("nbf", now + 40))), "future nbf beyond skew");
        await AssertRejected(client, issuer.Token(issuer.Payload(("iat", now), ("exp", now))), "exp equals iat");
        await AssertRejected(client, issuer.Token(issuer.Payload(("iat", now), ("exp", now - 1))), "exp precedes iat");

        foreach (var name in new[] { "iat", "exp", "nbf" })
        {
            foreach (var raw in new[] { "null", "{}", "[]", "true", "\"" + now.ToString(CultureInfo.InvariantCulture) + "\"",
                now.ToString(CultureInfo.InvariantCulture) + ".5", "1e100" })
            {
                var payload = name == "nbf"
                    ? TenantAuthenticationIssuer.AddProperty(issuer.Payload(), name, JsonSerializer.Deserialize<JsonElement>(raw))
                    : issuer.PayloadWithRaw(name, raw);
                await AssertRejected(client, issuer.Token(payload), "noninteger " + name + "=" + raw);
            }
        }
        await AssertAccepted(client, issuer.Token(), issuer);
    }

    [Fact]
    public async Task Validated_profile_enforces_the_exact_thirty_second_skew_at_tick_precision()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        var settings = TenantAuthenticationOptions.Create(issuer.Issuer, DeploymentMode.Development);
        // The production profile has an injected clock. Qualify its exact edge
        // separately from the real handler's wall-clock tests above; truncation
        // to token seconds must not make a +/-30 boundary test depend on timing.
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        foreach (var claim in new[] { "iat", "exp", "nbf" })
        {
            var boundary = now.ToUnixTimeSeconds() + (claim == "exp" ? -30 : 30);
            var token = issuer.Token(issuer.Payload(("iat", now.ToUnixTimeSeconds() - 600),
                ("exp", now.ToUnixTimeSeconds() + 600), (claim, boundary)));

            TenantTokenProfile.TryReadValidated(token, settings, now, out _, out _)
                .Should().BeTrue(claim + " at exactly thirty seconds is within skew");
            var outside = now.AddTicks(claim == "exp" ? 1 : -1);
            TenantTokenProfile.TryReadValidated(token, settings, outside, out _, out _)
                .Should().BeFalse(claim + " one tick beyond thirty seconds is outside skew");
        }
    }

    [Fact]
    public async Task Malformed_escaped_unicode_is_refused_while_valid_unicode_remains_opaque_identity_data()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        var unicodeKey = issuer.NewKey("clé-😀");
        var maximumKid = issuer.NewKey(new string('k', 128));
        issuer.Publish(issuer.CurrentKey, unicodeKey, maximumKid);
        await using var app = await CreateHost(issuer);
        using var client = app.GetTestClient();
        await AssertAccepted(client, issuer.Token(), issuer);
        foreach (var raw in new[] { "\"\\ud800\"", "\"\\udfff\"" })
        {
            await AssertRejected(client, issuer.Token(issuer.PayloadWithRaw("sub", raw)), "unpaired subject surrogate");
            await AssertRejected(client, issuer.Token(header:
                "{\"alg\":\"RS256\",\"typ\":\"JWT\",\"kid\":" + raw + "}"), "unpaired kid surrogate");
        }
        await AssertRejected(client, issuer.Token(issuer.Payload()[..^1] + ",\"\\ud800\":true}"),
            "unpaired surrogate in an otherwise unknown payload property name");
        await AssertRejected(client, issuer.Token(header:
            TenantAuthenticationIssuer.Header(issuer.CurrentKey.Kid)[..^1] + ",\"\\ud800\":true}"),
            "unpaired surrogate in an unknown JOSE property name");

        const string subject = "opaque-é-😀";
        var payload = issuer.Payload(("sub", subject), ("label", "valid-😀"));
        using var response = await Send(client, "Bearer " + issuer.Token(payload, key: unicodeKey));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var observation = (await response.Content.ReadFromJsonAsync<AuthenticationObservation>())!;
        observation.Subject.Should().Be(subject);
        observation.Actor.Should().Be(TenantAuthenticationIssuer.Actor);
        await AssertAccepted(client, issuer.Token(key: maximumKid), issuer);
        using var maximumSubjectResponse = await Send(client, "Bearer " + issuer.Token(issuer.Payload(("sub", new string('s', 255)))));
        maximumSubjectResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var maximumSubject = (await maximumSubjectResponse.Content.ReadFromJsonAsync<AuthenticationObservation>())!;
        maximumSubject.Subject.Should().HaveLength(255);
    }

    private static async Task<WebApplication> CreateHost(TenantAuthenticationIssuer issuer,
        Action<JwtBearerOptions>? configure = null, Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddLearnStackTenantAuthentication(
            TenantAuthenticationOptions.Create(issuer.Issuer, DeploymentMode.Development));
        if (configure is not null)
        {
            builder.Services.PostConfigure(TenantAuthenticationRegistration.Scheme, configure);
        }
        configureServices?.Invoke(builder.Services);
        var app = builder.Build();
        app.Use((context, next) =>
        {
            if (HostClassificationMiddleware.ClassifiesPath(context.Request.Path)
                && context.Request.Host.Host != "unknown.test")
            {
                context.Features.Set(HostClassification.Platform("known.test"));
            }
            return next(context);
        });
        app.UseAuthentication();
        app.Run(async context =>
        {
            var result = await context.AuthenticateAsync(TenantAuthenticationRegistration.Scheme);
            var feature = context.Features.Get<TenantCredentialFeature>();
            var identity = feature?.VerifiedSubject;
            context.Response.StatusCode = result.Succeeded ? StatusCodes.Status200OK
                : result.None ? StatusCodes.Status204NoContent : StatusCodes.Status401Unauthorized;
            if (context.Response.StatusCode != StatusCodes.Status204NoContent)
            {
                await context.Response.WriteAsJsonAsync(new AuthenticationObservation(
                    result.Succeeded, context.Request.Headers.ContainsKey("Authorization"),
                    context.User.Identity?.IsAuthenticated == true,
                    identity?.Issuer, identity?.Subject, identity?.ClaimedActor?.Value,
                    identity?.TenantId?.Value, identity?.OrganizationId?.Value));
            }
        });
        await app.StartAsync();
        return app;
    }

    private static async Task AssertAccepted(HttpClient client, string token, TenantAuthenticationIssuer issuer) =>
        await AssertAccepted(client, token, issuer, TenantAuthenticationIssuer.Tenant, TenantAuthenticationIssuer.Organization);

    private static async Task AssertAccepted(HttpClient client, string token, TenantAuthenticationIssuer issuer,
        Guid? tenant = null, Guid? organization = null)
    {
        using var response = await Send(client, "Bearer " + token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var observation = (await response.Content.ReadFromJsonAsync<AuthenticationObservation>())!;
        observation.Should().BeEquivalentTo(new AuthenticationObservation(true, true, true, issuer.Issuer,
            "opaque-keycloak-subject", TenantAuthenticationIssuer.Actor, tenant, organization));
    }

    private static async Task AssertRejected(HttpClient client, string token, string reason)
    {
        using var response = await Send(client, "Bearer " + token);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, reason);
        var observation = (await response.Content.ReadFromJsonAsync<AuthenticationObservation>())!;
        observation.Should().BeEquivalentTo(new AuthenticationObservation(false, true, false,
            null, null, null, null, null), reason);
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, string? authorization,
        string path = "/api/v1/auth-profile", string host = "known.test")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Host = host;
        if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return SendAndDispose(client, request);
    }

    private static async Task<HttpResponseMessage> SendAndDispose(HttpClient client, HttpRequestMessage request)
    {
        using (request) return await client.SendAsync(request);
    }

    private sealed record AuthenticationObservation(bool Succeeded, bool CredentialPresent, bool PrincipalAuthenticated,
        string? Issuer, string? Subject, Guid? Actor, Guid? Tenant, Guid? Organization);
}
