using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Tests.Integration.Database;
using LearnStack.Tools.Seeder;
using Xunit;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>Actual stock bootstrap parity and the bounded shared account-console capability.</summary>
[Collection(SharedKeycloakRealm.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class TenantRealmSelfServiceTests(KeycloakFixture fresh)
{
    private const string MapperName = "learnstack-account-self-service-roles";

    [Fact]
    public async Task Explicit_fresh_builtins_preserve_stock_bootstrap_and_add_only_the_approved_mapper_and_four_grants()
    {
        // Reconstruct the previous automatic bootstrap by removing only the new
        // built-in declarations and grants, leaving the same realm/profile/scopes.
        var stock = await RealmAsync();
        foreach (var client in stock["clients"]!.AsArray().Where(value =>
            Text(value?["clientId"]) is "account" or "account-console").ToArray()) stock["clients"]!.AsArray().Remove(client);
        stock["roles"]!["client"]!.AsObject().Remove("account");
        stock["roles"]!["realm"]!.AsArray().Remove(stock["roles"]!["realm"]!.AsArray()
            .Single(role => Text(role?["name"]) == "default-roles-learnstack"));
        stock.Remove("defaultRole");
        stock.Remove("clientScopeMappings");
        foreach (var user in stock["users"]!.AsArray()) user!.AsObject().Remove("clientRoles");
        await using var baseline = await KeycloakFixture.StartAsync(stock.ToJsonString());
        using var oldAdmin = await baseline.CreateAdminClientAsync();
        using var newAdmin = await fresh.CreateAdminClientAsync();
        var oldState = await BuiltinsAsync(oldAdmin);
        var newState = await BuiltinsAsync(newAdmin);
        await AssertNormalizedAccountMapperAsync(newAdmin);
        var newMappers = newState["account-console"]!["protocolMappers"]!.AsArray();
        newMappers.Remove(newMappers.Single(mapper => Text(mapper?["name"]) == MapperName));
        (Normalize(oldState).ToJsonString() == Normalize(newState).ToJsonString()).Should().BeTrue(
            "both clients, all eight roles/composites, stock defaults and scope allowances match automatic 26.6.2 bootstrap exactly");
        foreach (var identity in SeedData.IdentityUsers)
        {
            await AssertCapabilityAsync(newAdmin, identity.Username, true);
            await AssertCapabilityAsync(oldAdmin, identity.Username, false);
        }
        var identityUser = SeedData.IdentityUsers[0];
        using (var refused = await AccountClientAsync(baseline, identityUser.Username))
        using (var response = await KeycloakHttp.SendAsync(refused, HttpMethod.Get, ""))
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "the original stock bootstrap with inert-only seed roles cannot satisfy the account positive control");
        var result = await KeycloakReconciliationDriver.RunAsync(baseline);
        result.Success.Should().BeTrue("closed reconciliation outcome: {0}/{1}", result.Stage, result.Reason);
        result.Mutations.Should().Be(5, "one console emitter plus exactly four missing account grants");
        await AssertNormalizedAccountMapperAsync(oldAdmin);
        foreach (var identity in SeedData.IdentityUsers) await AssertCapabilityAsync(oldAdmin, identity.Username, true);
        using (var admitted = await AccountClientAsync(baseline, identityUser.Username))
        using (var response = await KeycloakHttp.SendAsync(admitted, HttpMethod.Get, ""))
            await AssertAccountJsonAsync(response, identityUser.Username);
        var rerun = await KeycloakReconciliationDriver.RunAsync(baseline);
        rerun.Success.Should().BeTrue("closed rerun outcome: {0}/{1}", rerun.Stage, rerun.Reason);
        rerun.Mutations.Should().Be(0);
    }

    [Fact]
    public async Task Shared_mapper_restores_an_already_eligible_nonmanifest_account_without_changing_its_data_or_grants()
    {
        const string username = "existing-self-service@fixture.test";
        var realm = await RealmAsync();
        RemoveMapper(realm);
        var user = realm["users"]![0]!.DeepClone().AsObject();
        user["username"] = username;
        user["email"] = username;
        user["firstName"] = "Existing";
        user["attributes"] = new JsonObject();
        realm["users"]!.AsArray().Add(user);
        await using var fixture = await KeycloakFixture.StartAsync(realm.ToJsonString());
        using var admin = await fixture.CreateAdminClientAsync();
        using var hub = await fixture.CreateAdminClientAsync("learnstack-hub");
        var userId = Id(await admin.UserAsync(username));
        var before = await AccountStateAsync(admin, userId);
        var hubBefore = (await hub.GetAsync()).GetRawText();
        using (var refused = await AccountClientAsync(fixture, username))
        using (var response = await KeycloakHttp.SendAsync(refused, HttpMethod.Get, ""))
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "the eligible role alone cannot replace the console claim emitter");
        var result = await KeycloakReconciliationDriver.RunAsync(fixture);
        result.Success.Should().BeTrue("closed reconciliation outcome: {0}/{1}", result.Stage, result.Reason);
        result.Mutations.Should().Be(1, "only the shared mapper is missing; no persona or nonmanifest grant changes");
        await AssertNormalizedAccountMapperAsync(admin);
        (await AccountStateAsync(admin, userId) == before).Should().BeTrue();
        ((await hub.GetAsync()).GetRawText() == hubBefore).Should().BeTrue();
        using (var admitted = await AccountClientAsync(fixture, username))
        {
            using var response = await KeycloakHttp.SendAsync(admitted, HttpMethod.Get, "");
            await AssertAccountJsonAsync(response, username);
            using var edit = await KeycloakHttp.SendAsync(admitted, HttpMethod.Post, "",
                JsonContent.Create(new { firstName = "Ordinary edit", lastName = "Account", email = username, attributes = new { } }));
            edit.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
        var updated = await admin.GetAsync("users/" + userId);
        (updated.GetProperty("firstName").GetString() == "Ordinary edit").Should().BeTrue();
        foreach (var name in new[] { "user_id", "tenant_id", "organization_id" })
            (updated.TryGetProperty("attributes", out var attributes) && attributes.TryGetProperty(name, out _)).Should().BeFalse(
                "the shared mapper does not provision actor or scope attributes for this nonmanifest account");
        var rerun = await KeycloakReconciliationDriver.RunAsync(fixture);
        rerun.Success.Should().BeTrue("closed rerun outcome: {0}/{1}", rerun.Stage, rerun.Reason);
        rerun.Mutations.Should().Be(0);
    }

    [Theory]
    [InlineData("group")]
    [InlineData("composite")]
    public async Task Effective_inherited_capability_and_a_compatible_existing_mapper_need_no_redundant_additions(string source)
    {
        var realm = await RealmAsync();
        var user = realm["users"]![0]!.AsObject();
        user.Remove("clientRoles");
        if (source == "group")
        {
            realm["groups"] = new JsonArray(new JsonObject
            {
                ["name"] = "existing-account-access",
                ["clientRoles"] = new JsonObject { ["account"] = new JsonArray("manage-account") }
            });
            user["groups"] = new JsonArray("/existing-account-access");
        }
        else
        {
            realm["roles"]!["realm"]!.AsArray().Add(new JsonObject
            {
                ["name"] = "existing-account-access",
                ["composite"] = true,
                ["composites"] = new JsonObject { ["client"] = new JsonObject { ["account"] = new JsonArray("manage-account") } }
            });
            user["realmRoles"]!.AsArray().Add("existing-account-access");
        }
        var console = realm["clients"]!.AsArray().Single(value => Text(value?["clientId"]) == "account-console")!;
        console["protocolMappers"]!.AsArray().Single(mapper => Text(mapper?["name"]) == MapperName)!["name"] = "compatible-existing-emitter";
        await using var fixture = await KeycloakFixture.StartAsync(realm.ToJsonString());
        using var admin = await fixture.CreateAdminClientAsync();
        var userId = Id(await admin.UserAsync(SeedData.IdentityUsers[0].Username));
        var before = await AccountStateAsync(admin, userId);
        var clientsBefore = (await BuiltinsAsync(admin)).ToJsonString();
        var result = await KeycloakReconciliationDriver.RunAsync(fixture);
        result.Success.Should().BeTrue("closed reconciliation outcome: {0}/{1}", result.Stage, result.Reason);
        result.Mutations.Should().Be(0);
        (await AccountStateAsync(admin, userId) == before).Should().BeTrue();
        ((await BuiltinsAsync(admin)).ToJsonString() == clientsBefore).Should().BeTrue();
        var account = await admin.ClientAsync("account");
        (await admin.GetAsync("users/" + userId + "/role-mappings/clients/" + Id(account))).GetArrayLength().Should().Be(0,
            "the existing inherited capability never becomes a new direct grant");
        using var admitted = await AccountClientAsync(fixture, SeedData.IdentityUsers[0].Username);
        using var response = await KeycloakHttp.SendAsync(admitted, HttpMethod.Get, "");
        await AssertAccountJsonAsync(response, SeedData.IdentityUsers[0].Username);
    }

    [Theory]
    [InlineData("composite")]
    [InlineData("default_scope")]
    [InlineData("optional_scope")]
    [InlineData("direct_mapper")]
    [InlineData("implicit_mapper")]
    public async Task Unsafe_account_customization_refuses_before_any_pending_write_and_preserves_the_customization(string variant)
    {
        await using var fixture = await KeycloakFixture.StartAsync();
        using var admin = await fixture.CreateAdminClientAsync();
        // A clean twin proves the same pending owned mutation is otherwise executable.
        (await admin.PutAsync("", new { accessTokenLifespan = 1800 })).Should().Be(HttpStatusCode.NoContent);
        var clean = await KeycloakReconciliationDriver.RunAsync(fixture);
        clean.Success.Should().BeTrue("closed clean outcome: {0}/{1}", clean.Stage, clean.Reason);
        clean.Mutations.Should().Be(1);
        (await admin.PutAsync("", new { accessTokenLifespan = 1800 })).Should().Be(HttpStatusCode.NoContent);
        var account = await admin.ClientAsync("account");
        var console = await admin.ClientAsync("account-console");
        if (variant == "composite")
        {
            var role = await admin.GetAsync("clients/" + Id(account) + "/roles/manage-account");
            var realmRole = await admin.GetAsync("roles/tenant-admin");
            (await admin.PostAsync("roles-by-id/" + Id(role) + "/composites", new[] { realmRole })).Should().Be(HttpStatusCode.NoContent);
        }
        else if (variant is "default_scope" or "optional_scope")
        {
            var scopes = await admin.GetAsync("client-scopes");
            var scope = scopes.EnumerateArray().Single(value => value.GetProperty("name").GetString() == "phone");
            var wider = await admin.GetAsync("clients/" + Id(account) + "/roles/view-profile");
            (await admin.PostAsync("client-scopes/" + Id(scope) + "/scope-mappings/clients/" + Id(account), new[] { wider }))
                .Should().Be(HttpStatusCode.NoContent);
            (await admin.PutAsync("clients/" + Id(console) + "/" + (variant == "default_scope" ? "default" : "optional")
                + "-client-scopes/" + Id(scope), new { })).Should().Be(HttpStatusCode.NoContent);
        }
        else
        {
            var mapper = variant == "implicit_mapper"
                ? new JsonObject
                {
                    ["name"] = "unqualified role transformer",
                    ["protocol"] = "openid-connect",
                    ["protocolMapper"] = "oidc-hardcoded-role-mapper",
                    ["config"] = new JsonObject { ["role"] = "account.view-profile" }
                }
                : new JsonObject
                {
                    ["name"] = "unqualified realm emitter",
                    ["protocol"] = "openid-connect",
                    ["protocolMapper"] = "oidc-usermodel-realm-role-mapper",
                    ["config"] = new JsonObject
                    { ["claim.name"] = "realm_access.roles", ["jsonType.label"] = "String", ["multivalued"] = "true", ["access.token.claim"] = "true" }
                };
            (await admin.PostAsync("clients/" + Id(console) + "/protocol-mappers/models", mapper)).Should().Be(HttpStatusCode.Created);
        }
        var before = await RefusalStateAsync(admin);
        var refused = await KeycloakReconciliationDriver.RunAsync(fixture);
        refused.Success.Should().BeFalse();
        refused.Stage.Should().Be("plan");
        refused.Reason.Should().Be(variant is "direct_mapper" or "implicit_mapper" ? "mapper_conflict" : "unsupported");
        refused.Mutations.Should().Be(0);
        (await RefusalStateAsync(admin) == before).Should().BeTrue("the pending TTL and incompatible provider customization remain untouched");
    }

    private static async Task AssertCapabilityAsync(KeycloakAdminClient admin, string username, bool present)
    {
        var user = await admin.UserAsync(username);
        var account = await admin.ClientAsync("account");
        var path = "users/" + Id(user) + "/role-mappings/clients/" + Id(account);
        var direct = await admin.GetAsync(path);
        var effective = await admin.GetAsync(path + "/composite");
        direct.EnumerateArray().Select(role => role.GetProperty("name").GetString())
            .Should().BeEquivalentTo(present ? ["manage-account"] : Array.Empty<string>());
        effective.EnumerateArray().Select(role => role.GetProperty("name").GetString())
            .Should().BeEquivalentTo(present ? ["manage-account", "manage-account-links"] : Array.Empty<string>());
        var mappings = await admin.GetAsync("users/" + Id(user) + "/role-mappings");
        mappings.GetProperty("realmMappings").EnumerateArray().Select(role => role.GetProperty("name").GetString()).Should()
            .Equal(SeedData.IdentityUsers.Single(identity => identity.Username == username).RealmRole);
    }

    private static async Task<HttpClient> AccountClientAsync(KeycloakFixture fixture, string username)
    {
        using var browser = fixture.Browser();
        var callback = new Uri(fixture.BaseAddress, "realms/learnstack/account/").AbsoluteUri;
        var authorization = await browser.AuthorizeAsync(username, KeycloakFixture.DemoPassword, callback,
            clientId: "account-console", scope: "openid");
        authorization.Authorized.Should().BeTrue("closed account authorization outcome: {0}", authorization.Error);
        var exchange = await browser.RedeemAsync(authorization, secret: null);
        exchange.StatusCode.Should().Be(HttpStatusCode.OK);
        var client = KeycloakHttp.Create(new Uri(fixture.BaseAddress, "realms/learnstack/account/"), fixture.RootCertificate);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", exchange.AccessToken);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static async Task AssertAccountJsonAsync(HttpResponseMessage response, string username)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (response.Content.Headers.ContentType?.MediaType).Should().Be("application/json",
            "the positive control requires the account REST JSON representation");
        using var json = await KeycloakHttp.ReadJsonAsync(response);
        json.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        (json.RootElement.GetProperty("username").GetString() == username).Should().BeTrue();
        json.RootElement.GetProperty("firstName").ValueKind.Should().Be(JsonValueKind.String);
        json.RootElement.GetProperty("email").ValueKind.Should().Be(JsonValueKind.String);
    }

    private static async Task AssertNormalizedAccountMapperAsync(KeycloakAdminClient admin)
    {
        var console = await admin.ClientAsync("account-console");
        var mappers = await admin.GetAsync("clients/" + Id(console) + "/protocol-mappers/models");
        var mapper = mappers.EnumerateArray().Single(value => value.GetProperty("name").GetString() == MapperName);
        (mapper.GetProperty("protocolMapper").GetString() == "oidc-usermodel-client-role-mapper").Should().BeTrue();
        var config = mapper.GetProperty("config");
        config.TryGetProperty("usermodel.clientRoleMapping.rolePrefix", out _).Should().BeFalse(
            "the pinned provider removes the declared empty prefix during import and Admin REST creation");
        var expected = new Dictionary<string, string>
        {
            ["usermodel.clientRoleMapping.clientId"] = "account",
            ["claim.name"] = "resource_access.account.roles",
            ["jsonType.label"] = "String",
            ["multivalued"] = "true",
            ["access.token.claim"] = "true",
            ["introspection.token.claim"] = "true",
            ["id.token.claim"] = "false",
            ["userinfo.token.claim"] = "false",
            ["lightweight.claim"] = "false",
        };
        config.EnumerateObject().Count().Should().Be(expected.Count);
        expected.All(entry => config.TryGetProperty(entry.Key, out var value) && value.GetString() == entry.Value)
            .Should().BeTrue("every required mapper setting survives the provider's empty-string normalization");
    }

    private static async Task<JsonObject> BuiltinsAsync(KeycloakAdminClient admin)
    {
        var state = new JsonObject();
        var account = await admin.ClientAsync("account");
        foreach (var name in new[] { "account", "account-console" })
        {
            var client = await admin.ClientAsync(name);
            var value = Node(await admin.GetAsync("clients/" + Id(client))).AsObject();
            value["protocolMappers"] = Node(await admin.GetAsync("clients/" + Id(client) + "/protocol-mappers/models"));
            value["scopeMappings"] = Node(await admin.GetAsync("clients/" + Id(client) + "/scope-mappings"));
            value["defaultScopes"] = Node(await admin.GetAsync("clients/" + Id(client) + "/default-client-scopes"));
            value["optionalScopes"] = Node(await admin.GetAsync("clients/" + Id(client) + "/optional-client-scopes"));
            state[name] = value;
        }
        var roles = await admin.GetAsync("clients/" + Id(account) + "/roles?briefRepresentation=false");
        roles.GetArrayLength().Should().Be(8);
        var definitions = new JsonArray();
        foreach (var role in roles.EnumerateArray())
        {
            var definition = Node(role).AsObject();
            definition["children"] = Node(await admin.GetAsync("roles-by-id/" + Id(role) + "/composites"));
            definitions.Add(definition);
        }
        state["accountRoles"] = definitions;
        var defaultRole = await admin.GetAsync("roles/default-roles-learnstack");
        state["defaultRole"] = Node(defaultRole);
        state["defaultComposites"] = Node(await admin.GetAsync("roles-by-id/" + Id(defaultRole) + "/composites"));
        return state;
    }

    private static async Task<string> AccountStateAsync(KeycloakAdminClient admin, string id)
    {
        var state = new JsonObject();
        foreach (var suffix in new[] { "", "/credentials", "/role-mappings", "/groups" })
            state[suffix] = Node(await admin.GetAsync("users/" + id + suffix));
        return state.ToJsonString();
    }

    private static async Task<string> RefusalStateAsync(KeycloakAdminClient admin)
    {
        var state = await BuiltinsAsync(admin);
        state["realm"] = Node(await admin.GetAsync());
        var scopes = await admin.GetAsync("client-scopes");
        foreach (var scope in scopes.EnumerateArray())
            state["scope/" + Id(scope)] = Node(await admin.GetAsync("client-scopes/" + Id(scope) + "/scope-mappings"));
        foreach (var identity in SeedData.IdentityUsers)
            state[identity.Username] = await AccountStateAsync(admin, Id(await admin.UserAsync(identity.Username)));
        return state.ToJsonString();
    }

    // Provider resource IDs and secret creation timestamps are generated independently
    // for the two imports. Every other stock field and collection is compared.
    private static JsonNode Normalize(JsonNode node) => node switch
    {
        JsonObject value => new JsonObject(value.Where(property => property.Key is not ("id" or "containerId" or "client.secret.creation.time"))
            .OrderBy(property => property.Key, StringComparer.Ordinal).Select(property =>
                new KeyValuePair<string, JsonNode?>(property.Key, property.Value is null ? null : Normalize(property.Value)))),
        JsonArray value => new JsonArray(value.Select(item => item is null ? null : Normalize(item))
            .OrderBy(item => item?.ToJsonString(), StringComparer.Ordinal).ToArray()),
        _ => node.DeepClone(),
    };

    private static void RemoveMapper(JsonObject realm)
    {
        var mappers = realm["clients"]!.AsArray().Single(client => Text(client?["clientId"]) == "account-console")!["protocolMappers"]!.AsArray();
        mappers.Remove(mappers.Single(mapper => Text(mapper?["name"]) == MapperName));
    }
    private static async Task<JsonObject> RealmAsync() =>
        JsonNode.Parse(await File.ReadAllTextAsync(KeycloakFixture.RealmPath("learnstack")))!.AsObject();
    private static JsonNode Node(JsonElement value) => JsonNode.Parse(value.GetRawText())!;
    private static string Id(JsonElement value) => value.GetProperty("id").GetString()!;
    private static string Text(JsonNode? value) => value?.GetValue<string>() ?? "";
}
