using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Nodes;
using Docker.DotNet.Models;
using FluentAssertions;
using LearnStack.Tests.Integration.Database;
using LearnStack.Tools.Seeder;
using Xunit;
using Xunit.Abstractions;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>Committed realm import and real confidential code/S256 issuance, with the native web scope separated from the scoped API client.</summary>
[Collection(SharedKeycloakRealm.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class TenantRealmIssuanceTests(KeycloakFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task Fixture_HTTPS_requires_its_owned_certificate_authority()
    {
        output.WriteLine("Owned provider startup wall time: {0:F3} seconds", fixture.StartupElapsed.TotalSeconds);
        await fixture.AssertLoopbackBindingsAsync();
        var inspection = new ContainerInspectResponse
        {
            HostConfig = new HostConfig
            {
                PortBindings = new Dictionary<string, IList<PortBinding>>
                { ["8443/tcp"] = [new() { HostIP = "127.0.0.1", HostPort = "" }] },
            },
            NetworkSettings = new NetworkSettings
            {
                Ports = new Dictionary<string, IList<PortBinding>>
                { ["8443/tcp"] = [new() { HostIP = "127.0.0.1", HostPort = "34443" }] },
            },
        };
        ContainerLoopbackPorts.IsExclusive(inspection, "8443/tcp").Should().BeTrue();
        foreach (var address in new[] { "", "0.0.0.0", "::", "192.0.2.1" })
        {
            inspection.NetworkSettings.Ports["8443/tcp"][0].HostIP = address;
            ContainerLoopbackPorts.IsExclusive(inspection, "8443/tcp").Should().BeFalse(
                "client-loopback URI checks cannot substitute for the actual Docker publication");
        }
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
    public async Task Fresh_import_issues_native_web_identity_without_provider_scope_for_all_four_declared_identities()
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
            AssertNativeIdentity(access, identity);
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
            AssertNativeIdentity(idToken, identity);
            exchange.Window.AcceptsFresh(idToken, 900).Should().BeTrue("signed ID-token expiration fits the same observed issuance window");
            (idToken.GetProperty("exp").GetInt64() == access.GetProperty("exp").GetInt64()).Should().BeTrue(
                "the pinned provider copies access-token expiration into the ID token from the same exchange");
            AssertExpirationControls(idToken, exchange.Window);
            FreshProfileMatches(access, idToken, identity, subject, exchange.Window).Should().BeTrue();
            foreach (var defect in new[] { "missing_subject", "wrong_subject", "wrong_party", "wrong_expiration" })
            {
                var mutant = JsonNode.Parse(idToken.GetRawText())!.AsObject();
                if (defect == "missing_subject") mutant.Remove("sub");
                else if (defect == "wrong_subject") mutant["sub"] = "another-provider-subject";
                else if (defect == "wrong_party") mutant["azp"] = "another-client";
                else
                {
                    // Keep this mismatch inside the fresh window so equality is the only violated expiration condition.
                    var expiration = exchange.Window.StartedUtc.ToUnixTimeSeconds() + 900;
                    mutant["exp"] = expiration == access.GetProperty("exp").GetInt64() ? expiration + 1 : expiration;
                }
                using var invalid = JsonDocument.Parse(mutant.ToJsonString());
                if (defect == "wrong_expiration")
                {
                    exchange.Window.AcceptsFresh(access, 900).Should().BeTrue("the unchanged access token remains fresh");
                    exchange.Window.AcceptsFresh(invalid.RootElement, 900).Should().BeTrue(
                        "the planted ID-token expiration differs while still fitting the fresh issuance window");
                }
                FreshProfileMatches(access, invalid.RootElement, identity, subject, exchange.Window).Should().BeFalse(
                    "the same profile predicate must detect an ID-token subject, authorized-party or expiration defect");
            }
            AssertScalar(idToken, "typ", "ID");
            HasAudience(idToken, "learnstack-api").Should().BeFalse("the explicit API audience belongs only in access tokens");
            var refresh = await browser.RequestTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = "learnstack-web",
                ["client_secret"] = KeycloakFixture.WebClientSecret,
                ["refresh_token"] = exchange.RefreshToken,
            });
            refresh.StatusCode.Should().Be(HttpStatusCode.OK);
            var refreshedAccess = await KeycloakTokenVerifier.ValidateAsync(fixture, refresh.AccessToken, "learnstack-api");
            var refreshedId = await KeycloakTokenVerifier.ValidateAsync(fixture, refresh.IdToken, "learnstack-web");
            FreshProfileMatches(refreshedAccess, refreshedId, identity, subject, refresh.Window).Should().BeTrue(
                "an actual refresh retains issuer/subject/actor and cannot reintroduce provider context");
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
        redirects.Should().BeEquivalentTo(SeedData.All.SelectMany(tenant => new[] { tenant.Host, "manage." + tenant.Host })
            .Select(host => "https://" + host + ":3000/api/auth/callback/keycloak"));
        var logout = web.GetProperty("attributes").GetProperty("post.logout.redirect.uris").GetString();
        (logout ?? throw new InvalidOperationException("Provider logout registrations are missing."))
            .Split("##", StringSplitOptions.None).Should().BeEquivalentTo(
            SeedData.All.SelectMany(tenant => new[] { tenant.Host, "manage." + tenant.Host })
                .Select(host => "https://" + host + ":3000/"));
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
            var isWeb = client.GetProperty("clientId").GetString() == "learnstack-web";
            scopes.Select(scope => scope.GetProperty("name").GetString()).Should().Contain(
                isWeb ? "learnstack-native-session" : "learnstack-tenant-api");
            if (isWeb)
            {
                scopes.Select(scope => scope.GetProperty("name").GetString()).Should().NotContain("learnstack-tenant-api");
                var optional = await admin.GetAsync("clients/" + RequiredId(client) + "/optional-client-scopes");
                optional.EnumerateArray().Select(scope => scope.GetProperty("name").GetString())
                    .Should().NotContain(["learnstack-tenant-api", "learnstack-native-session"]);
            }
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

    [Fact]
    public async Task Actual_signed_tokens_detect_live_mapper_scope_audience_scalar_and_lifespan_mutants()
    {
        // Mutation controls own their complete provider database. Shared import users/configuration stay pristine.
        await using var owned = await KeycloakFixture.StartAsync();
        using var admin = await owned.CreateAdminClientAsync();
        var identity = SeedData.IdentityUsers.First(user => user.Tenant.MapHostToDefaultOrganization);
        var subject = RequiredId(await admin.UserAsync(identity.Username));
        var scopes = await admin.GetAsync("client-scopes");
        var nativeScope = scopes.EnumerateArray().Single(scope => scope.GetProperty("name").GetString() == "learnstack-native-session");
        var basicScope = scopes.EnumerateArray().Single(scope => scope.GetProperty("name").GetString() == "basic");
        var nativeId = RequiredId(nativeScope);
        var basicId = RequiredId(basicScope);
        var web = await admin.ClientAsync("learnstack-web");
        var webId = RequiredId(web);
        var unusedName = "unattached-proof-" + Guid.NewGuid().ToString("N");
        (await admin.PostAsync("client-scopes", new { name = unusedName, protocol = "openid-connect" }))
            .Should().Be(HttpStatusCode.Created);
        scopes = await admin.GetAsync("client-scopes");
        var unusedId = RequiredId(scopes.EnumerateArray().Single(scope => scope.GetProperty("name").GetString() == unusedName));
        (await ProbeFreshProfileAsync(owned, identity, subject)).Should().BeTrue();

        // Deleted mappers and identical declarations attached to an unrequested scope both lose real claims.
        foreach (var target in new[] { (nativeId, "user_id"), (nativeId, "learnstack-api-audience"), (basicId, "sub") })
            foreach (var misattached in new[] { false, true })
            {
                var original = await MapperAsync(admin, target.Item1, target.Item2);
                try
                {
                    (await admin.DeleteAsync(MapperPath(target.Item1, RequiredId(original)))).Should().Be(HttpStatusCode.NoContent);
                    if (misattached)
                        (await admin.PostAsync(MapperPath(unusedId), WithoutId(original))).Should().Be(HttpStatusCode.Created);
                    (await ProbeFreshProfileAsync(owned, identity, subject, "missing_" + target.Item2)).Should().BeFalse(
                        "real signed issuance must detect a deleted or misattached identity/audience/basic mapper");
                }
                finally
                {
                    if (misattached)
                    {
                        var moved = await MapperAsync(admin, unusedId, target.Item2);
                        (await admin.DeleteAsync(MapperPath(unusedId, RequiredId(moved)))).Should().Be(HttpStatusCode.NoContent);
                    }
                    (await admin.PostAsync(MapperPath(target.Item1), WithoutId(original))).Should().Be(HttpStatusCode.Created);
                }
                (await ProbeFreshProfileAsync(owned, identity, subject)).Should().BeTrue("restored provider issuance is the recovery control");
            }

        foreach (var contextClaim in new[] { "tenant_id", "organization_id" })
        {
            var legacyScopeId = RequiredId(scopes.EnumerateArray().Single(scope => scope.GetProperty("name").GetString() == "learnstack-tenant-api"));
            var foreignContext = WithoutId(await MapperAsync(admin, legacyScopeId, contextClaim));
            try
            {
                (await admin.PostAsync(MapperPath(nativeId), foreignContext)).Should().Be(HttpStatusCode.Created);
                (await ProbeFreshProfileAsync(owned, identity, subject, "unexpected_" + contextClaim)).Should().BeFalse(
                    "actual signed context claims violate the native web profile");
            }
            finally
            {
                var inserted = await MapperAsync(admin, nativeId, contextClaim);
                (await admin.DeleteAsync(MapperPath(nativeId, RequiredId(inserted)))).Should().Be(HttpStatusCode.NoContent);
            }
            (await ProbeFreshProfileAsync(owned, identity, subject)).Should().BeTrue();
        }

        foreach (var scopeId in new[] { nativeId, basicId })
        {
            try
            {
                (await admin.DeleteAsync("clients/" + webId + "/default-client-scopes/" + scopeId)).Should().Be(HttpStatusCode.NoContent);
                (await admin.PutAsync("clients/" + webId + "/optional-client-scopes/" + scopeId, new { })).Should().Be(HttpStatusCode.NoContent);
                (await ProbeFreshProfileAsync(owned, identity, subject, scopeId == nativeId ? "missing_identity_scope" : "missing_sub")).Should().BeFalse(
                    "an optional scope omitted from the actual request is not an attached default scope");
            }
            finally
            {
                (await admin.DeleteAsync("clients/" + webId + "/optional-client-scopes/" + scopeId)).Should().Be(HttpStatusCode.NoContent);
                (await admin.PutAsync("clients/" + webId + "/default-client-scopes/" + scopeId, new { })).Should().Be(HttpStatusCode.NoContent);
            }
            (await ProbeFreshProfileAsync(owned, identity, subject)).Should().BeTrue();
        }

        foreach (var mutation in new[] { "array", "not_uuid", "wrong_uuid", "duplicate_conflict", "id_audience" })
        {
            var name = mutation == "id_audience" ? "learnstack-api-audience" : "user_id";
            var original = await MapperAsync(admin, nativeId, name);
            var dirty = JsonNode.Parse(original.GetRawText())!.AsObject();
            var config = dirty["config"]!.AsObject();
            switch (mutation)
            {
                case "array": config["multivalued"] = "true"; break;
                case "not_uuid":
                    dirty["protocolMapper"] = "oidc-usermodel-property-mapper";
                    config["user.attribute"] = "email";
                    break;
                case "wrong_uuid": config["user.attribute"] = "tenant_id"; break;
                case "duplicate_conflict": config["multivalued"] = "true"; break;
                case "id_audience": config["id.token.claim"] = "true"; break;
            }
            string? duplicateId = null;
            try
            {
                (await admin.PutAsync(MapperPath(nativeId, RequiredId(original)), dirty)).Should().Be(HttpStatusCode.NoContent);
                if (mutation == "duplicate_conflict")
                {
                    // Both emitters deliberately merge into an array, independent of mapper iteration order.
                    var duplicate = dirty.DeepClone().AsObject();
                    duplicate.Remove("id");
                    duplicate["name"] = "conflicting-user-id-proof";
                    duplicate["config"]!["user.attribute"] = "tenant_id";
                    (await admin.PostAsync(MapperPath(nativeId), duplicate)).Should().Be(HttpStatusCode.Created);
                    duplicateId = RequiredId(await MapperAsync(admin, nativeId, "conflicting-user-id-proof"));
                }
                (await ProbeFreshProfileAsync(owned, identity, subject, mutation)).Should().BeFalse(
                    "actual JWT scalar/UUID/duplicate and ID-audience mutations must break the clean predicate");
            }
            finally
            {
                if (duplicateId is not null)
                    (await admin.DeleteAsync(MapperPath(nativeId, duplicateId))).Should().Be(HttpStatusCode.NoContent);
                (await admin.PutAsync(MapperPath(nativeId, RequiredId(original)), original)).Should().Be(HttpStatusCode.NoContent);
            }
            (await ProbeFreshProfileAsync(owned, identity, subject)).Should().BeTrue();
        }

        var realm = await admin.GetAsync();
        realm.GetProperty("accessTokenLifespan").GetInt32().Should().Be(900);
        foreach (var client in new[] { web, await admin.ClientAsync("learnstack-api") })
            client.GetProperty("attributes").TryGetProperty("access.token.lifespan", out _).Should().BeFalse();
        try
        {
            var dirty = JsonNode.Parse(web.GetRawText())!.AsObject();
            dirty["attributes"]!["access.token.lifespan"] = "60";
            (await admin.PutAsync("clients/" + webId, dirty)).Should().Be(HttpStatusCode.NoContent);
            (await admin.ClientAsync("learnstack-web")).GetProperty("attributes").GetProperty("access.token.lifespan")
                .GetString().Should().Be("60");
            (await ProbeFreshProfileAsync(owned, identity, subject, "client_ttl")).Should().BeFalse("a live client override changes signed expiration");
        }
        finally
        {
            // Client updates merge supplied attribute keys; omission does not delete an existing override.
            var restore = JsonNode.Parse(web.GetRawText())!.AsObject();
            restore["attributes"]!["access.token.lifespan"] = null;
            (await admin.PutAsync("clients/" + webId, restore)).Should().Be(HttpStatusCode.NoContent);
            (await admin.ClientAsync("learnstack-web")).GetProperty("attributes")
                .TryGetProperty("access.token.lifespan", out _).Should().BeFalse();
        }
        (await ProbeFreshProfileAsync(owned, identity, subject)).Should().BeTrue();
        try
        {
            (await admin.PutAsync("", new { accessTokenLifespan = 1200 })).Should().Be(HttpStatusCode.NoContent);
            (await admin.GetAsync()).GetProperty("accessTokenLifespan").GetInt32().Should().Be(1200);
            (await ProbeFreshProfileAsync(owned, identity, subject, "realm_ttl")).Should().BeFalse("live realm drift changes signed expiration");
        }
        finally { (await admin.PutAsync("", new { accessTokenLifespan = 900 })).Should().Be(HttpStatusCode.NoContent); }
        (await ProbeFreshProfileAsync(owned, identity, subject)).Should().BeTrue();
    }

    [Fact]
    public async Task Late_session_refresh_is_clamped_while_the_configured_access_lifespan_remains_exactly_900()
    {
        await using var owned = await KeycloakFixture.StartAsync();
        using var admin = await owned.CreateAdminClientAsync();
        var identity = SeedData.IdentityUsers[0];
        var realm = await admin.GetAsync();
        realm.GetProperty("accessTokenLifespan").GetInt32().Should().Be(900);
        try
        {
            (await admin.PutAsync("", new { ssoSessionMaxLifespan = 30 })).Should().Be(HttpStatusCode.NoContent);
            using var browser = owned.Browser();
            var authorization = await browser.AuthorizeAsync(identity.Username, KeycloakFixture.DemoPassword, Callback(identity));
            AssertAuthorized(authorization);
            var initial = await browser.RedeemAsync(authorization);
            initial.StatusCode.Should().Be(HttpStatusCode.OK);
            var first = await KeycloakTokenVerifier.ValidateAsync(owned, initial.AccessToken, "learnstack-api");
            AssertNativeIdentity(first, identity);
            var firstId = await KeycloakTokenVerifier.ValidateAsync(owned, initial.IdToken, "learnstack-web");
            (firstId.GetProperty("exp").GetInt64() == first.GetProperty("exp").GetInt64()).Should().BeTrue(
                "initial session-clamped access and ID tokens share expiration");
            await Task.Delay(TimeSpan.FromSeconds(2));
            var late = await browser.RequestTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = "learnstack-web",
                ["client_secret"] = KeycloakFixture.WebClientSecret,
                ["refresh_token"] = initial.RefreshToken,
            });
            late.StatusCode.Should().Be(HttpStatusCode.OK);
            var refreshed = await KeycloakTokenVerifier.ValidateAsync(owned, late.AccessToken, "learnstack-api");
            AssertNativeIdentity(refreshed, identity);
            var refreshedId = await KeycloakTokenVerifier.ValidateAsync(owned, late.IdToken, "learnstack-web");
            (refreshedId.GetProperty("exp").GetInt64() == refreshed.GetProperty("exp").GetInt64()).Should().BeTrue(
                "the ID token from the same refresh shares the access token's session-clamped expiration");
            (refreshed.GetProperty("iat").GetInt64() > first.GetProperty("iat").GetInt64()).Should().BeTrue();
            (refreshed.GetProperty("auth_time").GetInt64() == first.GetProperty("auth_time").GetInt64()).Should().BeTrue(
                "the refresh belongs to the same earlier authenticated session");
            var expiration = refreshed.GetProperty("exp").GetInt64();
            (expiration <= first.GetProperty("exp").GetInt64() + 2
                && expiration <= initial.Window.CompletedUtc.ToUnixTimeSeconds() + 30 + 2
                && expiration > late.Window.CompletedUtc.ToUnixTimeSeconds()).Should().BeTrue(
                    "the live late-session expiration stays within its short session ceiling");
            late.Window.AcceptsFresh(refreshed, 900).Should().BeFalse("a shorter session clamp is separate from a fresh 900-second issuance proof");
            late.Window.AcceptsFresh(refreshedId, 900).Should().BeFalse("the shared shorter expiration also clamps the refreshed ID token");
            (await admin.GetAsync()).GetProperty("accessTokenLifespan").GetInt32().Should().Be(900);
            foreach (var name in new[] { "learnstack-web", "learnstack-api" })
                (await admin.ClientAsync(name)).GetProperty("attributes").TryGetProperty("access.token.lifespan", out _).Should().BeFalse();
        }
        finally
        {
            (await admin.PutAsync("", new { ssoSessionMaxLifespan = realm.GetProperty("ssoSessionMaxLifespan").GetInt32() }))
                .Should().Be(HttpStatusCode.NoContent);
        }
    }

    private static async Task<bool> ProbeFreshProfileAsync(KeycloakFixture provider, SeedIdentity identity, string subject,
        string? defect = null)
    {
        using var browser = provider.Browser();
        var authorization = await browser.AuthorizeAsync(identity.Username, KeycloakFixture.DemoPassword, Callback(identity));
        AssertAuthorized(authorization);
        var exchange = await browser.RedeemAsync(authorization);
        exchange.StatusCode.Should().Be(HttpStatusCode.OK);
        // The production role declaration and stock roles audience resolver retain this independent audience.
        // This checks signature/issuer/lifetime even when the deliberately dirty API audience is absent.
        var access = await KeycloakTokenVerifier.ValidateAsync(provider, exchange.AccessToken, "account",
            allowMissingSubject: defect == "missing_sub");
        var id = await KeycloakTokenVerifier.ValidateAsync(provider, exchange.IdToken, "learnstack-web",
            allowMissingSubject: defect == "missing_sub");
        var reachedDefect = defect switch
        {
            null => true,
            "missing_user_id" => !access.TryGetProperty("user_id", out _),
            "missing_learnstack-api-audience" => !HasAudience(access, "learnstack-api"),
            "missing_sub" => !access.TryGetProperty("sub", out _),
            "missing_identity_scope" => !access.TryGetProperty("user_id", out _) && !access.TryGetProperty("tenant_id", out _),
            "unexpected_tenant_id" => access.TryGetProperty("tenant_id", out _),
            "unexpected_organization_id" => access.TryGetProperty("organization_id", out _),
            "array" => access.GetProperty("user_id").ValueKind == JsonValueKind.Array,
            "not_uuid" => access.GetProperty("user_id").ValueKind == JsonValueKind.String
                && !Guid.TryParse(access.GetProperty("user_id").GetString(), out _),
            "wrong_uuid" => ScalarEquals(access, "user_id", identity.Tenant.TenantId.Value.ToString("D")),
            "duplicate_conflict" => access.GetProperty("user_id").ValueKind == JsonValueKind.Array
                && access.GetProperty("user_id").EnumerateArray().Any(value => value.GetString() == identity.UserId.Value.ToString("D"))
                && access.GetProperty("user_id").EnumerateArray().Any(value => value.GetString() == identity.Tenant.TenantId.Value.ToString("D")),
            "id_audience" => HasAudience(id, "learnstack-api"),
            "client_ttl" => exchange.Window.AcceptsFresh(access, 60),
            "realm_ttl" => exchange.Window.AcceptsFresh(access, 1200),
            _ => false,
        };
        reachedDefect.Should().BeTrue("a dirty provider must reach the specific signed-token defect being exercised");
        return FreshProfileMatches(access, id, identity, subject, exchange.Window);
    }

    private static bool FreshProfileMatches(JsonElement access, JsonElement id, SeedIdentity identity,
        string? subject, KeycloakIssuanceWindow window) => MatchesIdentity(access, identity) && MatchesIdentity(id, identity)
        && ScalarEquals(access, "sub", subject) && ScalarEquals(access, "typ", "Bearer")
        && ScalarEquals(access, "azp", "learnstack-web") && HasAudience(access, "learnstack-api")
        && ScalarEquals(id, "sub", subject) && ScalarEquals(id, "azp", "learnstack-web")
        && ScalarEquals(id, "typ", "ID") && !HasAudience(id, "learnstack-api")
        && window.AcceptsFresh(access, 900) && window.AcceptsFresh(id, 900)
        && access.GetProperty("exp").GetInt64() == id.GetProperty("exp").GetInt64();

    private static bool MatchesIdentity(JsonElement payload, SeedIdentity identity) =>
        ScalarEquals(payload, "user_id", identity.UserId.Value.ToString("D"))
        && !payload.TryGetProperty("tenant_id", out _)
        && !payload.TryGetProperty("organization_id", out _);

    private static bool ScalarEquals(JsonElement payload, string name, string? expected) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() == expected;

    private static async Task<JsonElement> MapperAsync(KeycloakAdminClient admin, string scopeId, string name)
    {
        var mappers = await admin.GetAsync(MapperPath(scopeId));
        var matches = mappers.EnumerateArray().Where(mapper => mapper.GetProperty("name").GetString() == name).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("Keycloak fixture expected exactly one named mapper.");
        return matches[0];
    }

    private static JsonObject WithoutId(JsonElement value)
    {
        var clone = JsonNode.Parse(value.GetRawText())!.AsObject();
        clone.Remove("id");
        return clone;
    }

    private static string MapperPath(string scopeId, string? mapperId = null) =>
        "client-scopes/" + scopeId + "/protocol-mappers/models" + (mapperId is null ? "" : "/" + mapperId);

    private static string Callback(SeedIdentity identity) =>
        "https://" + identity.Tenant.Host + ":3000/api/auth/callback/keycloak";

    private static void AssertAuthorized(KeycloakAuthorization authorization) => authorization.Authorized.Should().BeTrue(
        "ordinary code/S256 login must complete (closed outcome: {0})", authorization.Error ?? "no_error_code");

    private static string RequiredId(JsonElement entity) => entity.GetProperty("id").GetString()
        ?? throw new InvalidOperationException("Keycloak fixture entity ID is missing.");

    private static void AssertNativeIdentity(JsonElement payload, SeedIdentity identity)
    {
        AssertScalar(payload, "user_id", identity.UserId.Value.ToString("D"));
        payload.TryGetProperty("tenant_id", out _).Should().BeFalse("native web grants do not carry provider scope");
        payload.TryGetProperty("organization_id", out _).Should().BeFalse("native selection supplies organization scope separately");
    }

    private static void AssertScalar(JsonElement payload, string name, string value)
    {
        var actual = payload.GetProperty(name);
        actual.ValueKind.Should().Be(JsonValueKind.String, name + " is a scalar JSON string");
        (actual.GetString() == value).Should().BeTrue(name + " agrees with the declared profile");
    }

    private static bool HasAudience(JsonElement payload, string audience)
    {
        if (!payload.TryGetProperty("aud", out var value)) return false;
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
