using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Tests.Integration.Database;
using LearnStack.Tools.Seeder;
using Xunit;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>
/// ADR-0099 § Realm invariants on the pinned provider: a fresh import of the committed export already carries every
/// invariant, and a realm imported from the pre-Step-3 export converges to them through the shared reconciler, after
/// which a rerun writes nothing.
/// </summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class TenantRealmFederationReconciliationTests
{
    private static readonly string[] Retired = ["manage-account", "manage-account-links"];

    [Fact]
    public async Task A_fresh_import_carries_every_federation_invariant_and_reconciles_with_zero_writes()
    {
        await using var fixture = await KeycloakFixture.StartAsync();
        using var admin = await fixture.CreateAdminClientAsync();
        await AssertInvariantsAsync(fixture, admin);
        var result = await KeycloakReconciliationDriver.RunAsync(fixture);
        result.Success.Should().BeTrue("closed outcome: {0}/{1}", result.Stage, result.Reason);
        result.Mutations.Should().Be(0);
    }

    [Fact]
    public async Task A_pre_step_three_realm_converges_to_every_federation_invariant_and_a_rerun_writes_nothing()
    {
        var realm = JsonNode.Parse(await File.ReadAllTextAsync(KeycloakFixture.RealmPath("learnstack")))!.AsObject();
        KeycloakFixture.ToPreStepThree(realm);
        await using var fixture = await KeycloakFixture.StartAsync(realm.ToJsonString());
        using var admin = await fixture.CreateAdminClientAsync();
        // Dirty control: the warm realm really lacks the invariants before reconciliation.
        (await admin.GetAsync("clients?clientId=learnstack-federation-admin")).GetArrayLength().Should().Be(0);
        (await admin.GetAsync("clients?clientId=learnstack-account-removal")).GetArrayLength().Should().Be(0);
        (await admin.GetAsync("authentication/required-actions/idp_link")).GetProperty("enabled").GetBoolean().Should().BeTrue();
        (await admin.ClientAsync("account-console")).GetProperty("enabled").GetBoolean().Should().BeTrue();
        var persona = await admin.UserAsync(SeedData.IdentityUsers[0].Username);
        var before = await admin.GetAsync("users/" + persona.GetProperty("id").GetString() + "/role-mappings");
        before.GetProperty("clientMappings").GetProperty("account").GetProperty("mappings").EnumerateArray()
            .Select(role => role.GetProperty("name").GetString()).Should().Contain("manage-account");

        var first = await KeycloakReconciliationDriver.RunAsync(fixture);
        first.Success.Should().BeTrue("closed outcome: {0}/{1}", first.Stage, first.Reason);
        first.Mutations.Should().BeGreaterThan(0);
        await AssertInvariantsAsync(fixture, admin);
        foreach (var pass in new[] { 2, 3 })
        {
            var rerun = await KeycloakReconciliationDriver.RunAsync(fixture);
            rerun.Success.Should().BeTrue("pass {0}: {1}/{2}", pass, rerun.Stage, rerun.Reason);
            rerun.Mutations.Should().Be(0, "pass {0} is read only", pass);
        }
        // Retirement detached only the account grants: realm roles and credentials are unchanged.
        var after = await admin.GetAsync("users/" + persona.GetProperty("id").GetString() + "/role-mappings");
        JsonNode.DeepEquals(JsonNode.Parse(after.GetProperty("realmMappings").GetRawText()),
            JsonNode.Parse(before.GetProperty("realmMappings").GetRawText())).Should().BeTrue();
        using var browser = fixture.Browser();
        var login = await browser.AuthorizeAsync(SeedData.IdentityUsers[0].Username, KeycloakFixture.DemoPassword,
            "https://" + SeedData.IdentityUsers[0].Tenant.Host + ":3000/api/auth/callback/keycloak");
        login.Authorized.Should().BeTrue("local sign-in is untouched by every federation invariant");
    }

    [Fact]
    public async Task An_existing_federation_secret_mismatch_refuses_rather_than_rotating()
    {
        await using var fixture = await KeycloakFixture.StartAsync();
        using var admin = await fixture.CreateAdminClientAsync();
        var client = await admin.ClientAsync("learnstack-federation-admin");
        // An operator rotation through the provider's own regenerate endpoint (no client PUT side effects).
        (await admin.PostAsync("clients/" + client.GetProperty("id").GetString() + "/client-secret", new { }))
            .Should().Be(HttpStatusCode.OK);
        var rotated = (await admin.GetAsync("clients/" + client.GetProperty("id").GetString() + "/client-secret")).GetProperty("value").GetString();
        rotated.Should().NotBe(KeycloakFixture.FederationAdminSecret);
        var result = await KeycloakReconciliationDriver.RunAsync(fixture);
        result.Success.Should().BeFalse();
        result.Stage.Should().Be("plan");
        result.Reason.Should().Be("secret_conflict");
        result.Mutations.Should().Be(0);
        (await admin.GetAsync("clients/" + client.GetProperty("id").GetString() + "/client-secret")).GetProperty("value").GetString()
            .Should().Be(rotated, "the reconciler never rotates an existing secret");
    }

    [Fact]
    public async Task An_existing_account_removal_secret_mismatch_refuses_rather_than_rotating()
    {
        await using var fixture = await KeycloakFixture.StartAsync();
        using var admin = await fixture.CreateAdminClientAsync();
        var client = await admin.ClientAsync("learnstack-account-removal");
        (await admin.PostAsync("clients/" + client.GetProperty("id").GetString() + "/client-secret", new { }))
            .Should().Be(HttpStatusCode.OK);
        var rotated = (await admin.GetAsync("clients/" + client.GetProperty("id").GetString() + "/client-secret")).GetProperty("value").GetString();
        rotated.Should().NotBe(KeycloakFixture.AccountRemovalSecret);
        var result = await KeycloakReconciliationDriver.RunAsync(fixture);
        result.Success.Should().BeFalse();
        result.Stage.Should().Be("plan");
        result.Reason.Should().Be("secret_conflict");
        result.Mutations.Should().Be(0);
        (await admin.GetAsync("clients/" + client.GetProperty("id").GetString() + "/client-secret")).GetProperty("value").GetString()
            .Should().Be(rotated, "the reconciler never rotates an existing secret");
    }

    private static async Task AssertMachineClientAsync(KeycloakFixture fixture, KeycloakAdminClient admin, string clientId, string secret, string role)
    {
        var client = await admin.ClientAsync(clientId);
        var id = client.GetProperty("id").GetString();
        client.GetProperty("fullScopeAllowed").GetBoolean().Should().BeFalse();
        client.GetProperty("standardFlowEnabled").GetBoolean().Should().BeFalse();
        client.GetProperty("implicitFlowEnabled").GetBoolean().Should().BeFalse();
        client.GetProperty("directAccessGrantsEnabled").GetBoolean().Should().BeFalse();
        (await admin.GetAsync("clients/" + id + "/client-secret")).GetProperty("value").GetString().Should().Be(secret);
        var user = await admin.GetAsync("clients/" + id + "/service-account-user");
        var roles = await admin.GetAsync("users/" + user.GetProperty("id").GetString() + "/role-mappings");
        (!roles.TryGetProperty("realmMappings", out var realmRoles) || realmRoles.GetArrayLength() == 0).Should().BeTrue("no realm role");
        var management = roles.GetProperty("clientMappings").EnumerateObject().Should().ContainSingle().Subject;
        management.Name.Should().Be("realm-management");
        management.Value.GetProperty("mappings").EnumerateArray().Select(granted => granted.GetProperty("name").GetString())
            .Should().Equal(role);
        using (var token = KeycloakHttp.Create(fixture.BaseAddress, fixture.RootCertificate))
        {
            using var issued = await KeycloakHttp.JsonAsync(token, HttpMethod.Post, "realms/learnstack/protocol/openid-connect/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = clientId,
                    ["client_secret"] = secret,
                }));
            var access = issued.RootElement.GetProperty("access_token").GetString()!;
            using var payload = JsonDocument.Parse(Microsoft.IdentityModel.Tokens.Base64UrlEncoder.DecodeBytes(access.Split('.')[1]));
            payload.RootElement.GetProperty("resource_access").EnumerateObject().Select(value => value.Name).Should().Equal("realm-management");
            payload.RootElement.GetProperty("resource_access").GetProperty("realm-management").GetProperty("roles").EnumerateArray()
                .Select(value => value.GetString()).Should().Equal(role);
        }
    }

    private static async Task AssertInvariantsAsync(KeycloakFixture fixture, KeycloakAdminClient admin)
    {
        // (1) each confined machine client holds its one realm-management role only.
        await AssertMachineClientAsync(fixture, admin, "learnstack-federation-admin", KeycloakFixture.FederationAdminSecret, "manage-identity-providers");
        // ADR-0107 § 7: the closure's account-removal client, manage-users only.
        await AssertMachineClientAsync(fixture, admin, "learnstack-account-removal", KeycloakFixture.AccountRemovalSecret, "manage-users");
        // (2) the link-only first-broker flow, exactly.
        var executions = await admin.GetAsync("authentication/flows/learnstack-first-broker-login/executions");
        executions.EnumerateArray().Select(execution => execution.GetProperty("level").GetInt32() + ":"
            + (execution.TryGetProperty("providerId", out var provider) ? provider.GetString() : execution.GetProperty("displayName").GetString())
            + ":" + execution.GetProperty("requirement").GetString()).Should().Equal(
            "0:idp-detect-existing-broker-user:REQUIRED", "0:idp-confirm-link:REQUIRED", "0:learnstack-first-broker-reauthentication:REQUIRED",
            "1:idp-username-password-form:REQUIRED", "1:learnstack-first-broker-conditional-otp:CONDITIONAL",
            "2:conditional-user-configured:REQUIRED", "2:auth-otp-form:REQUIRED");
        // (3) idp_link disabled; the stock credential actions remain.
        (await admin.GetAsync("authentication/required-actions/idp_link")).GetProperty("enabled").GetBoolean().Should().BeFalse();
        foreach (var action in new[] { "UPDATE_PASSWORD", "CONFIGURE_TOTP", "VERIFY_EMAIL" })
            (await admin.GetAsync("authentication/required-actions/" + action)).GetProperty("enabled").GetBoolean().Should().BeTrue();
        // (4) account clients disabled; neither the default role nor any persona carries manage-account(-links).
        foreach (var name in new[] { "account", "account-console" })
            (await admin.ClientAsync(name)).GetProperty("enabled").GetBoolean().Should().BeFalse();
        var realm = await admin.GetAsync();
        var defaults = await admin.GetAsync("roles-by-id/" + realm.GetProperty("defaultRole").GetProperty("id").GetString() + "/composites");
        defaults.EnumerateArray().Select(role => role.GetProperty("name").GetString()).Should().NotContain(Retired);
        var account = await admin.ClientAsync("account");
        foreach (var identity in SeedData.IdentityUsers)
        {
            var persona = await admin.UserAsync(identity.Username);
            var effective = await admin.GetAsync("users/" + persona.GetProperty("id").GetString() + "/role-mappings/clients/"
                + account.GetProperty("id").GetString() + "/composite");
            effective.EnumerateArray().Select(role => role.GetProperty("name").GetString()).Should().NotContain(Retired);
        }
        // (5) the idp ID-token mapper on learnstack-web, from the session note only.
        var web = await admin.ClientAsync("learnstack-web");
        var mappers = await admin.GetAsync("clients/" + web.GetProperty("id").GetString() + "/protocol-mappers/models");
        var idp = mappers.EnumerateArray().Single(mapper => mapper.GetProperty("name").GetString() == "learnstack-identity-provider");
        idp.GetProperty("protocolMapper").GetString().Should().Be("oidc-usersessionmodel-note-mapper");
        idp.GetProperty("config").GetProperty("user.session.note").GetString().Should().Be("identity_provider");
        idp.GetProperty("config").GetProperty("claim.name").GetString().Should().Be("idp");
        idp.GetProperty("config").GetProperty("access.token.claim").GetString().Should().Be("false");
        // (6) the federation.test_record scope (Amendment 6): defined without mappers, optional on the web client, default nowhere,
        // so the real provider issues it only on an explicit scope request and never as a side effect of a login.
        var recorder = (await admin.GetAsync("client-scopes")).EnumerateArray()
            .Single(scope => scope.GetProperty("name").GetString() == "federation.test_record");
        recorder.GetProperty("protocol").GetString().Should().Be("openid-connect");
        (recorder.TryGetProperty("protocolMappers", out var recorderMappers) ? recorderMappers.GetArrayLength() : 0)
            .Should().Be(0, "the scope carries no claim of its own");
        var webId = web.GetProperty("id").GetString();
        (await admin.GetAsync("clients/" + webId + "/optional-client-scopes")).EnumerateArray()
            .Select(scope => scope.GetProperty("name").GetString()).Should().Contain("federation.test_record");
        foreach (var candidate in (await admin.GetAsync("clients")).EnumerateArray())
            (await admin.GetAsync("clients/" + candidate.GetProperty("id").GetString() + "/default-client-scopes")).EnumerateArray()
                .Select(scope => scope.GetProperty("name").GetString()).Should().NotContain("federation.test_record",
                    candidate.GetProperty("clientId").GetString() + " must not default the test-record scope");
    }
}
