using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Infrastructure.Identity.Federation;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Resilience;
using LearnStack.SharedKernel.Secrets;
using Polly;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Identity;

/// <summary>The confined Keycloak adapter against a scripted provider: admitted calls, ownership, the profile and the secret.</summary>
public sealed class KeycloakFederationProviderTests
{
    private const string Issuer = "https://provider.invalid/realms/learnstack";
    private const string AdminSecret = "federation-admin-secret-canary";
    private const string TenantSecret = "tenant-idp-secret-canary";
    private static readonly TenantId Tenant = TenantId.From(Guid.CreateVersion7());
    private static readonly Guid Connection = Guid.CreateVersion7();
    private static readonly FederationBrokerOwner Owner = new(Tenant, Connection, FederationBrokerOwner.AliasPrefix + Connection.ToString("N"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("**********")]
    [InlineData("${vault.federation}")]
    public async Task An_unusable_admin_secret_refuses_before_any_provider_call(string? secret)
    {
        var provider = new FakeKeycloak();
        using var adapter = Create(provider, secret);
        (await adapter.ApplyAsync(new(State(), Fresh()))).Should().Be(FederationProviderResult.Unavailable);
        (await adapter.ReadAsync(Owner)).Should().Be(FederationBrokerReadback.Unavailable);
        (await adapter.DeleteAsync(Owner)).Should().Be(FederationProviderResult.Unavailable);
        provider.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task An_oversized_admin_secret_refuses_before_any_provider_call()
    {
        var provider = new FakeKeycloak();
        using var adapter = Create(provider, new string('s', 1025));
        (await adapter.ReadAsync(Owner)).Should().Be(FederationBrokerReadback.Unavailable);
        provider.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_writes_the_closed_profile_once_and_the_readback_conforms_without_returning_the_secret()
    {
        var provider = new FakeKeycloak();
        using var adapter = Create(provider);
        (await adapter.ApplyAsync(new(State(), Fresh()))).Should().Be(FederationProviderResult.Applied);
        provider.Requests.Select(request => request.Method + " " + request.Path).Should().Equal(
            "POST /realms/learnstack/protocol/openid-connect/token",
            "GET /admin/realms/learnstack/identity-provider/instances/" + Owner.Alias,
            "POST /admin/realms/learnstack/identity-provider/instances");
        var created = provider.Brokers[Owner.Alias];
        created["config"]!["clientSecret"]!.GetValue<string>().Should().Be(TenantSecret);
        created["config"]![KeycloakFederationProfile.OwnerKey]!.GetValue<string>().Should().Be($"{Tenant.Value:D}:{Connection:D}");
        created["firstBrokerLoginFlowAlias"]!.GetValue<string>().Should().Be("learnstack-first-broker-login");
        created["trustEmail"]!.GetValue<bool>().Should().BeFalse();
        created["hideOnLogin"]!.GetValue<bool>().Should().BeTrue();
        created["config"]!["showInAccountConsole"]!.GetValue<string>().Should().Be("NEVER");
        created.ContainsKey("postBrokerLoginFlowAlias").Should().BeFalse();

        var readback = await adapter.ReadAsync(Owner);
        readback.Status.Should().Be(FederationReadbackStatus.Present);
        readback.ProfileConforms.Should().BeTrue();
        readback.MapperCount.Should().Be(0);
        readback.State.Should().Be(State());
        JsonSerializer.Serialize(readback.State!.Configuration, readback.State.Configuration.GetType()).Should().NotContain(TenantSecret);
        provider.Requests.Should().NotContain(request => request.Body.Contains("fromUrl", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_update_without_a_fresh_secret_sends_the_mask_and_never_creates_a_secretless_broker()
    {
        var provider = new FakeKeycloak();
        using var adapter = Create(provider);
        (await adapter.ApplyAsync(new(State(), null))).Should().Be(FederationProviderResult.Refused);
        provider.Brokers.Should().BeEmpty();
        (await adapter.ApplyAsync(new(State(), Fresh()))).Should().Be(FederationProviderResult.Applied);
        (await adapter.ApplyAsync(new(State(enabled: true), null))).Should().Be(FederationProviderResult.Applied);
        provider.Requests.Last().Method.Should().Be("PUT");
        provider.Requests.Last().Body.Should().Contain("\"clientSecret\":\"**********\"").And.NotContain(TenantSecret);
        (await adapter.ReadAsync(Owner)).State!.Enabled.Should().BeTrue();
    }

    [Theory]
    [InlineData("token-endpoint")]
    [InlineData("client-authentication")]
    [InlineData("client-id")]
    public async Task A_changed_secret_target_without_a_fresh_secret_refuses_before_any_write(string change)
    {
        var provider = new FakeKeycloak();
        using var adapter = Create(provider);
        (await adapter.ApplyAsync(new(State(), Fresh()))).Should().Be(FederationProviderResult.Applied);
        var held = provider.Brokers[Owner.Alias].ToJsonString();
        var oidc = (FederationOidcConfiguration)State().Configuration;
        var changed = change switch
        {
            "token-endpoint" => oidc with { TokenEndpoint = new Uri("https://attacker.example.com/token") },
            "client-authentication" => oidc with { ClientAuthentication = FederationClientAuthentication.ClientSecretBasic },
            _ => oidc with { ClientId = "another-client" },
        };
        var desired = State() with { Configuration = changed };
        var puts = provider.Requests.Count(request => request.Method == "PUT");
        (await adapter.ApplyAsync(new(desired, null))).Should().Be(FederationProviderResult.Refused,
            "the stored secret is never sent to a changed token endpoint, client or authentication method (ADR-0099 Amendment 3)");
        provider.Requests.Count(request => request.Method == "PUT").Should().Be(puts, "no provider write was made");
        provider.Brokers[Owner.Alias].ToJsonString().Should().Be(held);
        // Dirty controls: the same change with a fresh secret is written, and an unchanged configuration keeps the secret.
        (await adapter.ApplyAsync(new(desired, Fresh()))).Should().Be(FederationProviderResult.Applied);
        (await adapter.ApplyAsync(new(desired with { Enabled = true, DisplayName = "Renamed" }, null))).Should().Be(FederationProviderResult.Applied);
        provider.Requests.Count(request => request.Method == "PUT").Should().Be(puts + 2);
    }

    [Fact]
    public async Task An_issuer_or_entity_change_refuses_even_with_a_fresh_secret()
    {
        var provider = new FakeKeycloak();
        using var adapter = Create(provider);
        (await adapter.ApplyAsync(new(State(), Fresh()))).Should().Be(FederationProviderResult.Applied);
        var moved = State() with { Configuration = (FederationOidcConfiguration)State().Configuration with { Issuer = "https://idp.example.com/realms/other" } };
        (await adapter.ApplyAsync(new(moved, Fresh()))).Should().Be(FederationProviderResult.Refused, "a new issuer needs a new connection");
        (await adapter.ApplyAsync(new(moved, null))).Should().Be(FederationProviderResult.Refused);
        provider.Requests.Should().NotContain(request => request.Method == "PUT");
        ((FederationOidcConfiguration)(await adapter.ReadAsync(Owner)).State!.Configuration).Issuer.Should().Be("https://idp.example.com/realms/acme");

        var samlConnection = Guid.CreateVersion7();
        var samlOwner = new FederationBrokerOwner(Tenant, samlConnection, FederationBrokerOwner.AliasPrefix + samlConnection.ToString("N"));
        var saml = new FederationBrokerState(samlOwner, "Acme SAML", false, new FederationSamlConfiguration("https://saml.example.com/entity",
            new Uri("https://saml.example.com/sso"), [Pem()]));
        (await adapter.ApplyAsync(new(saml, null))).Should().Be(FederationProviderResult.Applied);
        var otherEntity = saml with { Configuration = ((FederationSamlConfiguration)saml.Configuration) with { EntityId = "https://saml.example.com/other" } };
        (await adapter.ApplyAsync(new(otherEntity, null))).Should().Be(FederationProviderResult.Refused);
        // Dirty control: the same entity with a new single sign-on endpoint is an edit.
        var moveSso = saml with { Configuration = ((FederationSamlConfiguration)saml.Configuration) with { SingleSignOnEndpoint = new Uri("https://saml.example.com/sso2") } };
        (await adapter.ApplyAsync(new(moveSso, null))).Should().Be(FederationProviderResult.Applied);
    }

    [Fact]
    public async Task A_foreign_owner_marker_is_reported_and_never_written_read_or_deleted()
    {
        var provider = new FakeKeycloak();
        using var adapter = Create(provider);
        (await adapter.ApplyAsync(new(State(), Fresh()))).Should().Be(FederationProviderResult.Applied);
        provider.Brokers[Owner.Alias]["config"]![KeycloakFederationProfile.OwnerKey] = $"{Guid.CreateVersion7():D}:{Connection:D}";
        var writes = provider.Requests.Count(request => request.Method is "PUT" or "POST" or "DELETE" && !request.Path.EndsWith("/token", StringComparison.Ordinal));
        (await adapter.ApplyAsync(new(State(enabled: true), Fresh()))).Should().Be(FederationProviderResult.Foreign);
        (await adapter.ReadAsync(Owner)).Should().Be(FederationBrokerReadback.Foreign);
        (await adapter.DeleteAsync(Owner)).Should().Be(FederationProviderResult.Foreign);
        provider.Requests.Count(request => request.Method is "PUT" or "POST" or "DELETE" && !request.Path.EndsWith("/token", StringComparison.Ordinal))
            .Should().Be(writes);
        provider.Requests.Should().NotContain(request => request.Path.EndsWith("/mappers", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("extra-key")]
    [InlineData("trust-email")]
    [InlineData("signature-off")]
    [InlineData("post-broker-flow")]
    [InlineData("secret-removed")]
    [InlineData("account-console")]
    public async Task Any_profile_difference_reads_back_as_nonconforming(string change)
    {
        var provider = new FakeKeycloak();
        using var adapter = Create(provider);
        (await adapter.ApplyAsync(new(State(), Fresh()))).Should().Be(FederationProviderResult.Applied);
        (await adapter.ReadAsync(Owner)).ProfileConforms.Should().BeTrue("the dirty control conforms");
        var broker = provider.Brokers[Owner.Alias];
        var config = broker["config"]!.AsObject();
        switch (change)
        {
            case "extra-key": config["tokenIntrospectionUrl"] = "https://idp.example.com/introspect"; break;
            case "trust-email": broker["trustEmail"] = true; break;
            case "signature-off": config["validateSignature"] = "false"; break;
            case "post-broker-flow": broker["postBrokerLoginFlowAlias"] = "post"; break;
            case "secret-removed": config.Remove("clientSecret"); break;
            case "account-console": config["showInAccountConsole"] = "ALWAYS"; break;
        }
        var readback = await adapter.ReadAsync(Owner);
        readback.Status.Should().Be(FederationReadbackStatus.Present);
        readback.ProfileConforms.Should().BeFalse();
    }

    [Fact]
    public async Task A_mapper_on_the_owned_alias_is_counted()
    {
        var provider = new FakeKeycloak();
        using var adapter = Create(provider);
        (await adapter.ApplyAsync(new(State(), Fresh()))).Should().Be(FederationProviderResult.Applied);
        provider.Mappers = 1;
        (await adapter.ReadAsync(Owner)).MapperCount.Should().Be(1);
    }

    [Fact]
    public async Task A_saml_broker_round_trips_its_certificates_and_fixed_trust()
    {
        var provider = new FakeKeycloak();
        using var adapter = Create(provider);
        var saml = new FederationBrokerState(Owner, "Acme SAML", false, new FederationSamlConfiguration("https://saml.example.com/entity",
            new Uri("https://saml.example.com/sso"), [Pem()]));
        (await adapter.ApplyAsync(new(saml, null))).Should().Be(FederationProviderResult.Applied);
        var config = provider.Brokers[Owner.Alias]["config"]!.AsObject();
        config["validateSignature"]!.GetValue<string>().Should().Be("true");
        config["wantAssertionsSigned"]!.GetValue<string>().Should().Be("true");
        config.ContainsKey("clientSecret").Should().BeFalse();
        config.ContainsKey("metadataDescriptorUrl").Should().BeFalse();
        var readback = await adapter.ReadAsync(Owner);
        readback.ProfileConforms.Should().BeTrue();
        var configuration = (FederationSamlConfiguration)readback.State!.Configuration;
        configuration.SigningCertificates.Should().ContainSingle();
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("client")]
    [InlineData("extra-role")]
    [InlineData("realm-role")]
    public async Task A_token_broader_or_foreign_to_the_admin_client_writes_nothing(string defect)
    {
        var provider = new FakeKeycloak { TokenDefect = defect };
        using var adapter = Create(provider);
        (await adapter.ApplyAsync(new(State(), Fresh()))).Should().Be(FederationProviderResult.Unavailable);
        provider.Requests.Should().ContainSingle().Which.Path.Should().EndWith("/token");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_provider_fault_is_unavailable_and_carries_no_provider_text(HttpStatusCode status)
    {
        var provider = new FakeKeycloak { WriteStatus = status };
        using var adapter = Create(provider);
        (await adapter.ApplyAsync(new(State(), Fresh()))).Should().Be(FederationProviderResult.Unavailable);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task A_definitive_provider_refusal_is_refused(HttpStatusCode status)
    {
        var provider = new FakeKeycloak { WriteStatus = status };
        using var adapter = Create(provider);
        (await adapter.ApplyAsync(new(State(), Fresh()))).Should().Be(FederationProviderResult.Refused);
    }

    [Fact]
    public async Task Delete_of_an_absent_owned_alias_is_already_applied()
    {
        var provider = new FakeKeycloak();
        using var adapter = Create(provider);
        (await adapter.DeleteAsync(Owner)).Should().Be(FederationProviderResult.Applied);
        provider.Requests.Should().NotContain(request => request.Method == "DELETE");
        (await adapter.ApplyAsync(new(State(), Fresh()))).Should().Be(FederationProviderResult.Applied);
        (await adapter.DeleteAsync(Owner)).Should().Be(FederationProviderResult.Applied);
        provider.Brokers.Should().BeEmpty();
    }

    [Fact]
    public async Task Every_admitted_request_targets_the_enumerated_routes_only()
    {
        var provider = new FakeKeycloak();
        using var adapter = Create(provider);
        await adapter.ApplyAsync(new(State(), Fresh()));
        await adapter.ApplyAsync(new(State(enabled: true), null));
        await adapter.ReadAsync(Owner);
        await adapter.DeleteAsync(Owner);
        var admitted = new[]
        {
            "POST /realms/learnstack/protocol/openid-connect/token",
            "POST /admin/realms/learnstack/identity-provider/instances",
            "GET /admin/realms/learnstack/identity-provider/instances/" + Owner.Alias,
            "PUT /admin/realms/learnstack/identity-provider/instances/" + Owner.Alias,
            "DELETE /admin/realms/learnstack/identity-provider/instances/" + Owner.Alias,
            "GET /admin/realms/learnstack/identity-provider/instances/" + Owner.Alias + "/mappers",
        };
        provider.Requests.Select(request => request.Method + " " + request.Path).Distinct().Should().BeSubsetOf(admitted);
        provider.Requests.Should().OnlyContain(request => request.Query.Length == 0);
    }

    private static KeycloakFederationProvider Create(FakeKeycloak provider, string? secret = AdminSecret) =>
        new(new KeycloakFederationOptions(Issuer), new Secrets(secret), new Resilience(), provider);

    private static FederationClientSecret Fresh() =>
        FederationClientSecret.TryCreate(TenantSecret, out var secret) ? secret! : throw new InvalidOperationException();

    private static FederationBrokerState State(bool enabled = false) => new(Owner, "Acme SSO", enabled, new FederationOidcConfiguration(
        "https://idp.example.com/realms/acme", "acme-client", FederationClientAuthentication.ClientSecretPost,
        new Uri("https://idp.example.com/realms/acme/protocol/openid-connect/auth"), new Uri("https://idp.example.com/realms/acme/protocol/openid-connect/token"),
        new Uri("https://idp.example.com/realms/acme/protocol/openid-connect/certs"), new Uri("https://idp.example.com/realms/acme/protocol/openid-connect/userinfo")));

    private static string Pem()
    {
        using var key = System.Security.Cryptography.ECDsa.Create();
        using var certificate = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=saml", key,
            System.Security.Cryptography.HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return certificate.ExportCertificatePem();
    }

    private sealed class Secrets(string? value) : ISecretProvider
    {
        public string? GetSecret(string key) => key == "Federation:ClientSecret" ? value : null;
    }

    private sealed class Resilience : IProviderResilience<IFederationProvider>
    {
        public ResiliencePipeline Pipeline => ResiliencePipeline.Empty;
        public string PortName => "federation";
    }

    internal sealed record Recorded(string Method, string Path, string Query, string Body);

    /// <summary>A scripted provider holding brokers by alias, as the pinned Admin REST API stores and masks them.</summary>
    private sealed class FakeKeycloak : HttpMessageHandler
    {
        internal ConcurrentDictionary<string, JsonObject> Brokers { get; } = new(StringComparer.Ordinal);
        internal List<Recorded> Requests { get; } = [];
        internal int Mappers { get; set; }
        internal string? TokenDefect { get; init; }
        internal HttpStatusCode? WriteStatus { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(new(request.Method.Method, path, request.RequestUri.Query, body));
            if (path.EndsWith("/protocol/openid-connect/token", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, new JsonObject { ["access_token"] = Token(TokenDefect), ["token_type"] = "Bearer", ["expires_in"] = 60 });
            if (request.Headers.Authorization?.Scheme != "Bearer") return new(HttpStatusCode.Unauthorized);
            const string prefix = "/admin/realms/learnstack/identity-provider/instances";
            if (request.Method == HttpMethod.Post && path == prefix)
            {
                if (WriteStatus is { } status) return new(status);
                var created = JsonNode.Parse(body)!.AsObject();
                var alias = created["alias"]!.GetValue<string>();
                if (Brokers.ContainsKey(alias)) return new(HttpStatusCode.Conflict);
                Brokers[alias] = created;
                return new(HttpStatusCode.Created);
            }
            var rest = path[(prefix.Length + 1)..];
            var mappers = rest.EndsWith("/mappers", StringComparison.Ordinal);
            var key = mappers ? rest[..^"/mappers".Length] : rest;
            if (mappers) return Json(HttpStatusCode.OK, new JsonArray([.. Enumerable.Range(0, Mappers).Select(_ => (JsonNode)new JsonObject { ["name"] = "m" })]));
            if (request.Method == HttpMethod.Get)
            {
                if (!Brokers.TryGetValue(key, out var broker)) return new(HttpStatusCode.NotFound);
                var view = broker.DeepClone().AsObject();
                if (view["config"]!.AsObject().ContainsKey("clientSecret")) view["config"]!["clientSecret"] = "**********";
                view["internalId"] = Guid.NewGuid().ToString();
                view["types"] = new JsonArray("USER_AUTHENTICATION");
                return Json(HttpStatusCode.OK, view);
            }
            if (request.Method == HttpMethod.Put)
            {
                if (WriteStatus is { } status) return new(status);
                if (!Brokers.TryGetValue(key, out var existing)) return new(HttpStatusCode.NotFound);
                var replacement = JsonNode.Parse(body)!.AsObject();
                var config = replacement["config"]!.AsObject();
                if (config["clientSecret"]?.GetValue<string>() == "**********")
                    config["clientSecret"] = existing["config"]!["clientSecret"]!.GetValue<string>();
                Brokers[key] = replacement;
                return new(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Delete)
                return new(Brokers.TryRemove(key, out _) ? HttpStatusCode.NoContent : HttpStatusCode.NotFound);
            return new(HttpStatusCode.MethodNotAllowed);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, JsonNode value) =>
            new(status) { Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json") };

        private static string Token(string? defect)
        {
            var roles = defect == "extra-role" ? new JsonArray("manage-identity-providers", "manage-users") : new JsonArray("manage-identity-providers");
            var payload = new JsonObject
            {
                ["iss"] = defect == "issuer" ? "https://other.invalid/realms/learnstack" : Issuer,
                ["azp"] = defect == "client" ? "learnstack-web" : "learnstack-federation-admin",
                ["resource_access"] = new JsonObject { ["realm-management"] = new JsonObject { ["roles"] = roles } },
            };
            if (defect == "realm-role") payload["realm_access"] = new JsonObject { ["roles"] = new JsonArray("default-roles-learnstack") };
            static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return Part("{\"alg\":\"none\"}") + "." + Part(payload.ToJsonString()) + ".c2ln";
        }
    }
}
