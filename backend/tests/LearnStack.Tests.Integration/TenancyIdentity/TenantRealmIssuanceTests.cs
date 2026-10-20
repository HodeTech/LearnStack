using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Tests.Integration.Database;
using LearnStack.Tools.Seeder;
using Xunit;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>Committed realm import and real confidential code/S256 issuance, before any BFF implementation.</summary>
[Collection(SharedKeycloakRealm.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class TenantRealmIssuanceTests(KeycloakFixture fixture)
{
    [Fact]
    public async Task Fixture_HTTPS_requires_its_owned_certificate_authority()
    {
        using var trusted = KeycloakHttp.Create(fixture.BaseAddress, fixture.RootCertificate);
        using (var positive = await KeycloakHttp.SendAsync(trusted, HttpMethod.Get,
            "realms/learnstack/.well-known/openid-configuration"))
            positive.StatusCode.Should().Be(HttpStatusCode.OK);
        using var otherAuthority = new KeycloakTls();
        using var untrusted = KeycloakHttp.Create(fixture.BaseAddress, otherAuthority.RootCertificate);
        var rejectedByTls = false;
        try
        {
            using var response = await untrusted.GetAsync("realms/learnstack/.well-known/openid-configuration");
        }
        catch (HttpRequestException exception) when (exception.InnerException is AuthenticationException)
        {
            rejectedByTls = true;
        }
        catch (Exception)
        {
            throw new InvalidOperationException("Keycloak fixture TLS negative control failed outside certificate authentication.");
        }
        rejectedByTls.Should().BeTrue("a different owned CA cannot authenticate the same provider certificate");
        using var stillTrusted = await KeycloakHttp.SendAsync(trusted, HttpMethod.Get,
            "realms/learnstack/.well-known/openid-configuration");
        stillTrusted.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Fresh_import_issues_the_API_profile_for_all_four_declared_identities()
    {
        using var admin = await fixture.CreateAdminClientAsync();
        var subjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var identity in SeedData.IdentityUsers)
        {
            using var browser = fixture.Browser();
            var authorization = await browser.AuthorizeAsync(identity.Username, KeycloakFixture.DemoPassword, Callback(identity));
            AssertAuthorized(authorization);
            var exchange = await browser.RedeemAsync(authorization);
            exchange.StatusCode.Should().Be(HttpStatusCode.OK);
            (exchange.AccessToken.Length > 0 && exchange.IdToken.Length > 0 && exchange.RefreshToken.Length > 0)
                .Should().BeTrue("the confidential code exchange returns all three credentials");
            var access = await KeycloakTokenVerifier.ValidateAsync(fixture, exchange.AccessToken, "learnstack-api");
            var account = await admin.UserAsync(identity.Username);
            var subject = access.GetProperty("sub").GetString();
            (subject == account.GetProperty("id").GetString()).Should().BeTrue("sub remains the real provider account ID");
            subjects.Add(subject ?? throw new InvalidOperationException("Provider subject is missing."));
            AssertIdentity(access, identity);
            AssertScalar(access, "typ", "Bearer");
            AssertScalar(access, "azp", "learnstack-web");
            (access.GetProperty("iss").GetString() == fixture.Issuer).Should().BeTrue("the issuer uses the mapped loopback endpoint");
            HasAudience(access, "learnstack-api").Should().BeTrue();
            var header = KeycloakTokenVerifier.Header(exchange.AccessToken);
            header.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(["alg", "typ", "kid"]);
            AssertScalar(header, "alg", "RS256");
            AssertScalar(header, "typ", "JWT");
            string.IsNullOrEmpty(header.GetProperty("kid").GetString()).Should().BeFalse();
            exchange.Window.AcceptsFresh(access, 900).Should().BeTrue("signed expiration fits the observed UTC/monotonic exchange window");
            AssertExpirationControls(access, exchange.Window);

            var idToken = await KeycloakTokenVerifier.ValidateAsync(fixture, exchange.IdToken, "learnstack-web");
            AssertIdentity(idToken, identity);
            AssertScalar(idToken, "typ", "ID");
            HasAudience(idToken, "learnstack-api").Should().BeFalse("the explicit API audience belongs only in access tokens");
        }
        subjects.Count.Should().Be(SeedData.IdentityUsers.Length);
    }

    [Fact]
    public async Task Live_import_has_exact_lifespan_callbacks_and_confidential_PKCE_configuration()
    {
        using var admin = await fixture.CreateAdminClientAsync();
        var realm = await admin.GetAsync();
        realm.GetProperty("accessTokenLifespan").GetInt32().Should().Be(900);
        var web = await admin.ClientAsync("learnstack-web");
        web.GetProperty("publicClient").GetBoolean().Should().BeFalse();
        web.GetProperty("standardFlowEnabled").GetBoolean().Should().BeTrue();
        web.GetProperty("implicitFlowEnabled").GetBoolean().Should().BeFalse();
        web.GetProperty("directAccessGrantsEnabled").GetBoolean().Should().BeFalse();
        web.GetProperty("serviceAccountsEnabled").GetBoolean().Should().BeFalse();
        AssertScalar(web.GetProperty("attributes"), "pkce.code.challenge.method", "S256");
        var redirects = web.GetProperty("redirectUris").EnumerateArray().Select(value => value.GetString()).ToArray();
        redirects.Should().BeEquivalentTo(SeedData.All.Select(tenant =>
            "https://" + tenant.Host + ":3000/api/auth/callback/keycloak"));
        var logout = web.GetProperty("attributes").GetProperty("post.logout.redirect.uris").GetString();
        (logout ?? throw new InvalidOperationException("Provider logout registrations are missing."))
            .Split("##", StringSplitOptions.None).Should().BeEquivalentTo(
            SeedData.All.Select(tenant => "https://" + tenant.Host + ":3000/"));
        var secret = await admin.GetAsync("clients/" + RequiredId(web) + "/client-secret");
        (secret.GetProperty("value").GetString() == KeycloakFixture.WebClientSecret)
            .Should().BeTrue("the committed import substitutes the private fixture source");

        var api = await admin.ClientAsync("learnstack-api");
        api.GetProperty("standardFlowEnabled").GetBoolean().Should().BeFalse();
        api.GetProperty("redirectUris").GetArrayLength().Should().Be(0);
        foreach (var client in new[] { web, api })
        {
            client.GetProperty("attributes").TryGetProperty("access.token.lifespan", out _).Should().BeFalse();
            var defaults = await admin.GetAsync("clients/" + RequiredId(client) + "/default-client-scopes");
            var scopes = defaults.EnumerateArray().ToArray();
            scopes.Select(scope => scope.GetProperty("name").GetString()).Should().Contain("learnstack-tenant-api");
            var basic = scopes.Single(scope => scope.GetProperty("name").GetString() == "basic");
            var mappers = await admin.GetAsync("client-scopes/" + RequiredId(basic) + "/protocol-mappers/models");
            mappers.EnumerateArray().Any(mapper => mapper.GetProperty("protocolMapper").GetString() == "oidc-sub-mapper")
                .Should().BeTrue("actual basic scope must retain its subject mapper");
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("plain")]
    public async Task Authorization_refuses_a_missing_or_plain_challenge(string challenge)
    {
        var identity = SeedData.IdentityUsers[0];
        using var browser = fixture.Browser();
        var authorization = await browser.AuthorizeAsync(identity.Username, KeycloakFixture.DemoPassword,
            Callback(identity), challenge == "plain" ? KeycloakPkce.Plain : KeycloakPkce.Missing);
        authorization.Authorized.Should().BeFalse();
        authorization.Error.Should().Be("invalid_request");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Code_exchange_refuses_a_missing_or_wrong_client_secret(bool missing)
    {
        var identity = SeedData.IdentityUsers[0];
        using var browser = fixture.Browser();
        var authorization = await browser.AuthorizeAsync(identity.Username, KeycloakFixture.DemoPassword, Callback(identity));
        AssertAuthorized(authorization);
        var exchange = await browser.RedeemAsync(authorization, missing ? null : "unmatched-fixture-secret");
        exchange.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        exchange.Error.Should().Be("unauthorized_client");
        (exchange.AccessToken.Length == 0).Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Code_exchange_refuses_a_missing_or_wrong_verifier(bool missing)
    {
        var identity = SeedData.IdentityUsers[0];
        using var browser = fixture.Browser();
        var authorization = await browser.AuthorizeAsync(identity.Username, KeycloakFixture.DemoPassword, Callback(identity));
        AssertAuthorized(authorization);
        var exchange = await browser.RedeemAsync(authorization, verifier: new string('v', 43), omitVerifier: missing);
        exchange.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        exchange.Error.Should().Be("invalid_grant");
        (exchange.AccessToken.Length == 0).Should().BeTrue();
    }

    [Fact]
    public async Task Authorization_refuses_an_unregistered_callback()
    {
        var identity = SeedData.IdentityUsers[0];
        using var browser = fixture.Browser();
        var authorization = await browser.AuthorizeAsync(identity.Username, KeycloakFixture.DemoPassword,
            "https://unregistered.learnstack.local:3000/api/auth/callback/keycloak");
        authorization.Authorized.Should().BeFalse();
        authorization.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Tenant_password_grant_stays_disabled()
    {
        using var browser = fixture.Browser();
        var refusal = await browser.RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "learnstack-web",
            ["client_secret"] = KeycloakFixture.WebClientSecret,
            ["username"] = SeedData.IdentityUsers[0].Username,
            ["password"] = KeycloakFixture.DemoPassword,
        });
        refusal.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        refusal.Error.Should().Be("unauthorized_client");
        (refusal.AccessToken.Length == 0).Should().BeTrue();
    }

    private static string Callback(SeedIdentity identity) =>
        "https://" + identity.Tenant.Host + ":3000/api/auth/callback/keycloak";

    private static void AssertAuthorized(KeycloakAuthorization authorization) => authorization.Authorized.Should().BeTrue(
        "ordinary code/S256 login must complete (closed outcome: {0})", authorization.Error ?? "no_error_code");

    private static string RequiredId(JsonElement entity) => entity.GetProperty("id").GetString()
        ?? throw new InvalidOperationException("Keycloak fixture entity ID is missing.");

    private static void AssertIdentity(JsonElement payload, SeedIdentity identity)
    {
        AssertScalar(payload, "user_id", identity.UserId.Value.ToString("D"));
        AssertScalar(payload, "tenant_id", identity.Tenant.TenantId.Value.ToString("D"));
        if (identity.Tenant.MapHostToDefaultOrganization)
            AssertScalar(payload, "organization_id", identity.Tenant.DefaultOrganization.OrganizationId.Value.ToString("D"));
        else payload.TryGetProperty("organization_id", out _).Should().BeFalse();
    }

    private static void AssertScalar(JsonElement payload, string name, string value)
    {
        var actual = payload.GetProperty(name);
        actual.ValueKind.Should().Be(JsonValueKind.String, name + " is a scalar JSON string");
        (actual.GetString() == value).Should().BeTrue(name + " agrees with the declared profile");
    }

    private static bool HasAudience(JsonElement payload, string audience)
    {
        var value = payload.GetProperty("aud");
        return value.ValueKind == JsonValueKind.String ? value.GetString() == audience
            : value.ValueKind == JsonValueKind.Array && value.EnumerateArray().Any(item =>
                item.ValueKind == JsonValueKind.String && item.GetString() == audience);
    }

    private static void AssertExpirationControls(JsonElement valid, KeycloakIssuanceWindow window)
    {
        foreach (var offset in new[] { -60, 60 })
        {
            var mutant = JsonNode.Parse(valid.GetRawText())?.AsObject()
                ?? throw new InvalidOperationException("Expected a token payload object.");
            mutant["exp"] = valid.GetProperty("exp").GetInt64() + offset;
            using var json = JsonDocument.Parse(mutant.ToJsonString());
            window.AcceptsFresh(json.RootElement, 900).Should().BeFalse("an independently wrong expiration cannot fit the measured issuance interval");
        }
        (window with { CompletedUtc = window.StartedUtc.AddSeconds(-1) }).AcceptsFresh(valid, 900).Should().BeFalse();
        (window with { Elapsed = TimeSpan.FromMinutes(1) }).AcceptsFresh(valid, 900).Should().BeFalse();
        (window with { CompletedUtc = window.CompletedUtc.AddSeconds(10) }).AcceptsFresh(valid, 900).Should().BeFalse();
    }
}
