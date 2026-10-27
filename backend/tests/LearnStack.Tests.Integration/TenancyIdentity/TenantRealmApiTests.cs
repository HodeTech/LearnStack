using System.Net;
using System.Text.Json;
using FluentAssertions;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tests.Integration.Database;
using LearnStack.Tools.Seeder;
using Xunit;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>Committed provider issuance through Program, stock bearer validation and real application-role reads.</summary>
[Collection(TenantRealmApiTestGroup.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed partial class TenantRealmApiTests(TenantRealmApiFixture fixture, Xunit.Abstractions.ITestOutputHelper output)
{
    private readonly IdentityPublicReadFixture _database = fixture.Database;
    private readonly KeycloakFixture _provider = fixture.Provider;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Every_seed_persona_reads_its_actual_account_site_under_the_matching_host_scope(int index)
    {
        SeedData.IdentityUsers.Length.Should().Be(4);
        var identity = SeedData.IdentityUsers[index];
        var tokens = await TokensAsync(identity);
        using var client = Client(identity.Tenant, out var observation);
        var audits = await AuditCountsAsync();
        var reads = _database.Observation.Reads;

        using var response = await Send(client, identity.Tenant, tokens.AccessToken);
        await AccountAsync(response, identity, observation);

        observation.Reads.Should().HaveCount(1);
        _database.Observation.Reads.Should().Be(reads + 1,
            "the existing observer must witness the physical ReadOnly learnstack_app transaction");
        (await AuditCountsAsync()).Should().Equal(audits, "successful account reads create no query audit");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_cross_tenant_tokens_refuse_before_configuration_and_audit_the_defended_host(bool yogaActor)
    {
        var identity = SeedData.IdentityUsers.First(user => user.Tenant == (yogaActor ? SeedData.Yoga : SeedData.English));
        var defended = yogaActor ? SeedData.English : SeedData.Yoga;
        var tokens = await TokensAsync(identity);
        using var client = Client(identity.Tenant, out var observation);
        using (var positive = await Send(client, identity.Tenant, tokens.AccessToken))
            await AccountAsync(positive, identity, observation);
        var reads = _database.Observation.Reads;
        var audits = await AuditCountsAsync();

        using var refusal = await Send(client, defended, tokens.AccessToken);
        await TenantAuthenticationHttpTests.Problem(refusal, HttpStatusCode.NotFound, "tenant_mismatch");

        _database.Observation.Reads.Should().Be(reads);
        observation.Reads.Should().HaveCount(1);
        await AssertRejectionAsync(refusal, defended, identity, "assertedTenantId", identity.Tenant.TenantId.Value);
        await AssertSingleAdditionalAuditAsync(audits, defended);
    }

    [Fact]
    public async Task A_real_provider_organization_disagreement_refuses_and_records_the_trusted_host_organization()
    {
        var identity = SeedData.IdentityUsers.First(user => user.Tenant == SeedData.Yoga);
        using var admin = await _provider.CreateAdminClientAsync();
        var user = await admin.UserAsync(identity.Username);
        var userPath = UserPath(user);
        var originalDetails = await admin.GetAsync(userPath);
        var original = user.GetProperty("attributes").Clone();
        var attributes = original.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
        attributes["organization_id"] = JsonSerializer.SerializeToElement(
            new[] { SeedData.English.DefaultOrganization.OrganizationId.Value.ToString("D") });
        using var client = Client(identity.Tenant, out var observation);
        var originalTokens = await TokensAsync(identity);
        using (var positive = await Send(client, identity.Tenant, originalTokens.AccessToken))
            await AccountAsync(positive, identity, observation);
        var audits = await AuditCountsAsync();
        var reads = _database.Observation.Reads;

        try
        {
            (await admin.PutAsync(userPath, UserAttributeUpdate(user, attributes))).Should().Be(HttpStatusCode.NoContent);
            var changedTokens = await TokensAsync(identity);
            using var refusal = await Send(client, identity.Tenant, changedTokens.AccessToken);
            await TenantAuthenticationHttpTests.Problem(refusal, HttpStatusCode.NotFound, "tenant_mismatch");

            _database.Observation.Reads.Should().Be(reads);
            observation.Reads.Should().HaveCount(1);
            await AssertRejectionAsync(refusal, identity.Tenant, identity, "assertedOrganizationId",
                SeedData.English.DefaultOrganization.OrganizationId.Value);
            await AssertSingleAdditionalAuditAsync(audits, identity.Tenant);
        }
        finally
        {
            (await admin.PutAsync(userPath, UserAttributeUpdate(user, original))).Should().Be(HttpStatusCode.NoContent);
            var restored = await admin.GetAsync(userPath);
            JsonElement.DeepEquals(restored.GetProperty("attributes"), original).Should().BeTrue(
                "only the owned provider mutation is restored, including all original attributes");
            JsonElement.DeepEquals(restored, originalDetails).Should().BeTrue(
                "restoring scope attributes cannot silently change the account's unrelated state");
        }

        var restoredTokens = await TokensAsync(identity);
        using var restoredResponse = await Send(client, identity.Tenant, restoredTokens.AccessToken);
        await AccountAsync(restoredResponse, identity, observation);
        observation.Reads.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_real_claimless_actor_retains_HostOnly_attribution_without_protected_read_authority()
    {
        var identity = SeedData.IdentityUsers.First(user => user.Tenant == SeedData.Yoga);
        var positiveTokens = await TokensAsync(identity);
        using var client = Client(identity.Tenant, out var observation);
        using (var positive = await Send(client, identity.Tenant, positiveTokens.AccessToken))
            await AccountAsync(positive, identity, observation);
        using var admin = await _provider.CreateAdminClientAsync();
        var username = "api-claimless-" + Guid.NewGuid().ToString("N") + "@example.test";
        var actor = Guid.CreateVersion7();
        var created = false;
        var audits = await AuditCountsAsync();
        try
        {
            var status = await CreateUserAsync(admin, username, new Dictionary<string, string[]>
            {
                ["user_id"] = [actor.ToString("D")],
            });
            created = status == HttpStatusCode.Created;
            status.Should().Be(HttpStatusCode.Created);
            var user = await admin.UserAsync(username);
            await _database.AdoptAsync(LearnStack.SharedKernel.Identifiers.UserId.From(actor), user.GetProperty("id").GetString()!);
            user.GetProperty("attributes").EnumerateObject().Select(property => property.Name)
                .Should().Equal("user_id");
            var tokens = await ExchangeAsync(username, Callback(identity.Tenant));
            var payload = await KeycloakTokenVerifier.ValidateAsync(_provider, tokens.AccessToken, "learnstack-api");
            payload.TryGetProperty("tenant_id", out _).Should().BeFalse();
            payload.TryGetProperty("organization_id", out _).Should().BeFalse();
            payload.GetProperty("user_id").GetGuid().Should().Be(actor);

            var reads = _database.Observation.Reads;
            using var protectedResponse = await Send(client, identity.Tenant, tokens.AccessToken);
            await TenantAuthenticationHttpTests.Problem(protectedResponse, HttpStatusCode.NotFound, "not_found");
            _database.Observation.Reads.Should().Be(reads);
            observation.Reads.Should().HaveCount(1);

            using var publicResponse = await Send(client, identity.Tenant, tokens.AccessToken,
                TenantAuthenticationHttpRequest.PublicPath);
            await PublicSiteAsync(publicResponse, identity.Tenant);
            observation.Reads.Should().HaveCount(2);
            AssertContext(observation.Reads.Last(), identity.Tenant, actor, TenantContextOrigin.HostOnly);
            _database.Observation.Reads.Should().Be(reads + 1);
            (await AuditCountsAsync()).Should().Equal(audits);
        }
        finally
        {
            if (created) await DeleteOwnedUserAsync(admin, username);
        }
    }

    [Fact]
    public async Task A_genuine_ID_token_without_API_audience_is_401_on_account_and_public_routes()
    {
        var identity = SeedData.IdentityUsers.First();
        var tokens = await TokensAsync(identity);
        var id = await KeycloakTokenVerifier.ValidateAsync(_provider, tokens.IdToken, "learnstack-web");
        HasAudience(id, "learnstack-api").Should().BeFalse();
        using var client = Client(identity.Tenant, out var observation);
        using (var positive = await Send(client, identity.Tenant, tokens.AccessToken))
            await AccountAsync(positive, identity, observation);
        var reads = _database.Observation.Reads;
        var audits = await AuditCountsAsync();

        foreach (var path in new[] { TenantAuthenticationHttpRequest.AccountPath, TenantAuthenticationHttpRequest.PublicPath })
        {
            using var response = await Send(client, identity.Tenant, tokens.IdToken, path);
            await UnauthorizedAsync(response);
        }
        // Keep purpose valid while independently removing only the API audience.
        using var admin = await _provider.CreateAdminClientAsync();
        var scope = (await admin.GetAsync("client-scopes")).EnumerateArray()
            .Single(value => value.GetProperty("name").GetString() == "learnstack-tenant-api");
        var mapperBase = "client-scopes/" + scope.GetProperty("id").GetString() + "/protocol-mappers/models";
        var mapper = (await admin.GetAsync(mapperBase)).EnumerateArray()
            .Single(value => value.GetProperty("name").GetString() == "learnstack-api-audience");
        var mapperPath = mapperBase + "/" + mapper.GetProperty("id").GetString();
        var differentAudience = System.Text.Json.Nodes.JsonNode.Parse(mapper.GetRawText())!.AsObject();
        differentAudience["config"]!["included.client.audience"] = "different-fixture-resource";
        try
        {
            (await admin.PutAsync(mapperPath, differentAudience)).Should().Be(HttpStatusCode.NoContent);
            var wrongAudience = await TokensAsync(identity);
            var access = await KeycloakTokenVerifier.ValidateAsync(_provider, wrongAudience.AccessToken, "account");
            HasAudience(access, "learnstack-api").Should().BeFalse();
            (access.GetProperty("typ").GetString() == "Bearer"
                && access.GetProperty("azp").GetString() == "learnstack-web").Should().BeTrue();
            foreach (var path in new[] { TenantAuthenticationHttpRequest.AccountPath, TenantAuthenticationHttpRequest.PublicPath })
            {
                using var response = await Send(client, identity.Tenant, wrongAudience.AccessToken, path);
                await UnauthorizedAsync(response);
            }
        }
        finally { (await admin.PutAsync(mapperPath, mapper)).Should().Be(HttpStatusCode.NoContent); }
        _database.Observation.Reads.Should().Be(reads);
        observation.Reads.Should().HaveCount(1);
        (await AuditCountsAsync()).Should().Equal(audits);
    }

    [Fact]
    public async Task A_genuine_Hub_issuer_token_is_rejected_without_changing_the_operator_or_Hub_client()
    {
        var identity = SeedData.IdentityUsers.First();
        var positiveTokens = await TokensAsync(identity);
        using var client = Client(identity.Tenant, out var observation);
        using (var positive = await Send(client, identity.Tenant, positiveTokens.AccessToken))
            await AccountAsync(positive, identity, observation);
        using var admin = await _provider.CreateAdminClientAsync("learnstack-hub");
        var originalOperator = await admin.UserAsync("demo-operator@learnstack.test");
        var originalClient = await admin.ClientAsync("learnstack-hub-web");
        var username = "api-hub-issuer-" + Guid.NewGuid().ToString("N") + "@example.test";
        var created = false;
        var reads = _database.Observation.Reads;
        var audits = await AuditCountsAsync();
        try
        {
            var status = await CreateUserAsync(admin, username);
            created = status == HttpStatusCode.Created;
            status.Should().Be(HttpStatusCode.Created);
            // This owned unrelated account qualifies a genuine issuer negative, not an operator/MFA journey.
            var tokens = await ExchangeAsync(username, "http://localhost:3100/api/auth/callback/keycloak",
                realm: "learnstack-hub", clientId: "learnstack-hub-web", secret: null);
            var payload = await KeycloakTokenVerifier.ValidateAsync(_provider, tokens.AccessToken,
                "account", "learnstack-hub");
            payload.GetProperty("iss").GetString().Should().Be(_provider.IssuerFor("learnstack-hub"));
            foreach (var path in new[] { TenantAuthenticationHttpRequest.AccountPath, TenantAuthenticationHttpRequest.PublicPath })
            {
                using var refusal = await Send(client, identity.Tenant, tokens.AccessToken, path);
                await UnauthorizedAsync(refusal);
            }
            _database.Observation.Reads.Should().Be(reads);
            observation.Reads.Should().HaveCount(1);
            (await AuditCountsAsync()).Should().Equal(audits);
        }
        finally
        {
            if (created) await DeleteOwnedUserAsync(admin, username);
            JsonElement.DeepEquals(await admin.UserAsync("demo-operator@learnstack.test"), originalOperator)
                .Should().BeTrue("the original operator's MFA, credentials and required actions are never changed");
            JsonElement.DeepEquals(await admin.ClientAsync("learnstack-hub-web"), originalClient)
                .Should().BeTrue("the registered Hub client remains unchanged");
        }
    }

    [Fact]
    public async Task The_real_backchannel_accepts_only_the_owned_CA_and_preserves_all_transport_bounds()
    {
        var identity = SeedData.IdentityUsers.First();
        var tokens = await TokensAsync(identity);
        using (var client = Client(identity.Tenant, out var observation))
        {
            using var positive = await Send(client, identity.Tenant, tokens.AccessToken);
            await AccountAsync(positive, identity, observation);
        }
        using var wrongAuthority = new KeycloakTls();
        using (var backchannel = Backchannel(wrongAuthority.RootCertificate))
        {
            var metadata = async () => { using var response = await backchannel.Client.GetAsync(_provider.Issuer + "/.well-known/openid-configuration"); };
            var tls = await metadata.Should().ThrowAsync<HttpRequestException>();
            tls.Which.InnerException.Should().BeOfType<System.Security.Authentication.AuthenticationException>(
                "the actual production backchannel must fail certificate authentication with this CA");
        }
        using var wrongClient = Client(identity.Tenant, out var wrongObservation, wrongAuthority.RootCertificate);
        var reads = _database.Observation.Reads;
        var audits = await AuditCountsAsync();

        using var refusal = await Send(wrongClient, identity.Tenant, tokens.AccessToken);
        await UnauthorizedAsync(refusal);

        wrongObservation.Reads.Should().BeEmpty();
        _database.Observation.Reads.Should().Be(reads);
        (await AuditCountsAsync()).Should().Equal(audits);
    }

    [Fact]
    public async Task Anonymous_public_reads_and_masked_account_refusals_do_not_contact_an_unavailable_identity_endpoint()
    {
        var identity = SeedData.IdentityUsers.First();
        var tokens = await TokensAsync(identity);
        using (var positiveClient = Client(identity.Tenant, out var positiveObservation))
        {
            using var positive = await Send(positiveClient, identity.Tenant, tokens.AccessToken);
            await AccountAsync(positive, identity, positiveObservation);
        }
        await using var unavailable = new UnavailableTenantIssuer();
        await using var unavailableDatabase = await IdentityPublicReadFixture.CreateAsync(fixture.PublicDatabase, unavailable.Issuer);
        using var client = Client(identity.Tenant, out var observation, issuer: unavailable.Issuer, sourceDatabase: unavailableDatabase);
        var audits = await AuditCountsAsync(unavailableDatabase);
        var reads = unavailableDatabase.Observation.Reads;
        using (var account = await Send(client, identity.Tenant))
        {
            await TenantAuthenticationHttpTests.Problem(account, HttpStatusCode.NotFound, "not_found");
            account.Headers.WwwAuthenticate.Should().BeEmpty();
        }
        unavailableDatabase.Observation.Reads.Should().Be(reads);
        using (var anonymous = await Send(client, identity.Tenant, path: TenantAuthenticationHttpRequest.PublicPath))
            await PublicSiteAsync(anonymous, identity.Tenant);
        unavailable.Connections.Should().Be(0, "absent credentials never attempt metadata transport");
        observation.Reads.Should().HaveCount(1);
        AssertContext(observation.Reads.Last(), identity.Tenant, null, TenantContextOrigin.HostOnly);

        using (var rejected = await Send(client, identity.Tenant, tokens.AccessToken,
                   TenantAuthenticationHttpRequest.PublicPath))
            await UnauthorizedAsync(rejected);
        unavailable.Connections.Should().BeGreaterThan(0, "the endpoint really fails a stock metadata TLS connection");
        observation.Reads.Should().HaveCount(1, "a real credential must not fall back to anonymous after transport failure");
        using (var anonymousAgain = await Send(client, identity.Tenant, path: TenantAuthenticationHttpRequest.PublicPath))
            await PublicSiteAsync(anonymousAgain, identity.Tenant);
        observation.Reads.Should().HaveCount(2);
        AssertContext(observation.Reads.Last(), identity.Tenant, null, TenantContextOrigin.HostOnly);
        unavailableDatabase.Observation.Reads.Should().Be(reads + 2);
        (await AuditCountsAsync(unavailableDatabase)).Should().Equal(audits);
    }
}
