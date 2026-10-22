using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Tests.Integration.Database;
using LearnStack.Tools.Seeder;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>Real provider account and registration protection; no application-database isolation claim.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class TenantRealmProfileProtectionTests(KeycloakFixture fixture) : IClassFixture<KeycloakFixture>
{
    private static readonly string[] ProtectedAttributes = ["user_id", "tenant_id", "organization_id"];
    private static readonly string[] ProfilePermissions = ["view", "edit"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Same_seed_account_can_edit_ordinary_profile_but_cannot_see_write_or_delete_authority(bool yoga)
    {
        var identity = SeedData.IdentityUsers.First(user => user.Tenant.MapHostToDefaultOrganization == yoga);
        using var admin = await fixture.CreateAdminClientAsync();
        SeedAttributesMatch(await admin.UserAsync(identity.Username), identity).Should().BeTrue();
        var profile = await admin.GetAsync("users/profile");
        AdminOnlyDefinitions(profile).Should().BeTrue("the provider's independent profile configuration is admin-only");
        using var account = await AccountClientAsync(identity);
        var original = await AccountAsync(account);
        HiddenFromAccount(original).Should().BeTrue("both user data and user-profile metadata hide every authority attribute");
        var ordinary = AccountUpdate(original);
        var originalName = original.GetProperty("firstName").GetString();
        ordinary["firstName"] = "Profile proof";
        try
        {
            (await UpdateAsync(account, ordinary)).Should().Be(HttpStatusCode.NoContent);
            var updated = await admin.UserAsync(identity.Username);
            (updated.GetProperty("firstName").GetString() == "Profile proof").Should().BeTrue(
                "independent admin readback proves this same user's ordinary write reached storage");
            SeedAttributesMatch(updated, identity).Should().BeTrue();
            foreach (var attribute in ProtectedAttributes)
                foreach (var attempt in new[] { "replace", "empty", "multiple", "delete", "omit" })
                {
                    var payload = AccountUpdate(await AccountAsync(account));
                    var attributes = payload["attributes"]?.AsObject() ?? new JsonObject();
                    if (payload["attributes"] is null) payload["attributes"] = attributes;
                    switch (attempt)
                    {
                        case "replace": attributes[attribute] = new JsonArray(Guid.NewGuid().ToString("D")); break;
                        case "empty": attributes[attribute] = new JsonArray(""); break;
                        case "multiple": attributes[attribute] = new JsonArray(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D")); break;
                        case "delete": attributes[attribute] = new JsonArray(); break;
                        case "omit": attributes.Remove(attribute); break;
                    }
                    (await UpdateAsync(account, payload)).Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Forbidden, HttpStatusCode.NoContent);
                    SeedAttributesMatch(await admin.UserAsync(identity.Username), identity).Should().BeTrue(
                        "a 204 can mean a protected omission was ignored; the stored typed values decide protection");
                    HiddenFromAccount(await AccountAsync(account)).Should().BeTrue();
                }
            await AssertFreshIdentityAsync(identity);
        }
        finally
        {
            // Restore only the ordinary field through the same owner's real account API.
            var restore = AccountUpdate(await AccountAsync(account));
            restore["firstName"] = originalName;
            (await UpdateAsync(account, restore)).Should().Be(HttpStatusCode.NoContent);
            var restored = await admin.UserAsync(identity.Username);
            (restored.GetProperty("firstName").GetString() == originalName).Should().BeTrue();
            SeedAttributesMatch(restored, identity).Should().BeTrue();
        }
    }

    [Fact]
    public async Task Ordinary_registration_works_and_injected_authority_never_becomes_new_account_data()
    {
        using var admin = await fixture.CreateAdminClientAsync();
        var owned = new List<string>();
        var callback = Callback(SeedData.IdentityUsers[0]);
        try
        {
            var ordinary = OwnedRegistrationName();
            owned.Add(ordinary);
            using (var browser = fixture.Browser())
            {
                var result = await browser.RegisterAsync(ordinary, KeycloakFixture.DemoPassword, callback);
                result.Authorized.Should().BeTrue("the ordinary submitted form must genuinely create an account and complete authorization");
                RegistrationFieldsHidden(result).Should().BeTrue();
            }
            var created = await FindOwnedRegistrationAsync(admin, ordinary);
            created.HasValue.Should().BeTrue();
            NoAuthorityAttributes(created!.Value).Should().BeTrue();
            foreach (var attribute in ProtectedAttributes)
                foreach (var variant in new[] { "replace", "empty", "multiple" })
                {
                    var username = OwnedRegistrationName();
                    owned.Add(username);
                    using var browser = fixture.Browser();
                    var injected = new List<KeyValuePair<string, string>>
                    { new(attribute, variant == "empty" ? "" : Guid.NewGuid().ToString("D")) };
                    if (variant == "multiple") injected.Add(new(attribute, Guid.NewGuid().ToString("D")));
                    var result = await browser.RegisterAsync(username, KeycloakFixture.DemoPassword, callback, injected);
                    RegistrationFieldsHidden(result).Should().BeTrue();
                    result.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Found,
                        HttpStatusCode.SeeOther, HttpStatusCode.BadRequest);
                    var attempted = await FindOwnedRegistrationAsync(admin, username);
                    if (attempted.HasValue)
                        NoAuthorityAttributes(attempted.Value).Should().BeTrue(
                            "an accepted registration cannot persist an injected authority attribute");
                    else result.Authorized.Should().BeFalse("a refused injection must not claim completed registration");
                }
        }
        finally
        {
            foreach (var username in owned)
            {
                var user = await FindOwnedRegistrationAsync(admin, username);
                if (user.HasValue)
                    (await admin.DeleteAsync("users/" + RequiredId(user.Value))).Should().Be(HttpStatusCode.NoContent);
            }
        }
    }

    [Theory]
    [InlineData("user_id")]
    [InlineData("tenant_id")]
    [InlineData("organization_id")]
    public async Task User_editable_profile_counterfactual_breaks_the_same_protection_predicate(string attribute)
    {
        var identity = SeedData.IdentityUsers.First(user => user.Tenant.MapHostToDefaultOrganization);
        using var admin = await fixture.CreateAdminClientAsync();
        var originalProfile = await admin.GetAsync("users/profile");
        AdminOnlyDefinitions(originalProfile).Should().BeTrue();
        SeedAttributesMatch(await admin.UserAsync(identity.Username), identity).Should().BeTrue();
        var dirtyProfile = JsonNode.Parse(originalProfile.GetRawText())!.AsObject();
        var definition = dirtyProfile["attributes"]!.AsArray().Single(node => node!["name"]!.GetValue<string>() == attribute)!;
        definition["permissions"] = new JsonObject
        {
            ["view"] = new JsonArray("admin", "user"),
            ["edit"] = new JsonArray("admin", "user"),
        };
        HttpClient? account = null;
        JsonObject? restore = null;
        string? registered = null;
        try
        {
            (await admin.PutAsync("users/profile", dirtyProfile)).Should().Be(HttpStatusCode.OK);
            AdminOnlyDefinitions(await admin.GetAsync("users/profile")).Should().BeFalse();
            account = await AccountClientAsync(identity);
            var visible = await AccountAsync(account);
            HiddenFromAccount(visible).Should().BeFalse("the same visibility predicate detects a user-visible authority definition");
            restore = AccountUpdate(visible);
            var payload = restore.DeepClone().AsObject();
            var replacement = Guid.NewGuid().ToString("D");
            payload["attributes"]!.AsObject()[attribute] = new JsonArray(replacement);
            (await UpdateAsync(account, payload)).Should().Be(HttpStatusCode.NoContent);
            var changed = await admin.UserAsync(identity.Username);
            StoredScalar(changed, attribute, replacement).Should().BeTrue(
                "this counterfactual must positively reach the protected field, rather than receive a generic denial");
            SeedAttributesMatch(changed, identity).Should().BeFalse("the exact predicate used by the clean proof must detect the actual stored mutation");
            registered = OwnedRegistrationName();
            using var registration = fixture.Browser();
            var dirtyRegistration = await registration.RegisterAsync(registered, KeycloakFixture.DemoPassword, Callback(identity),
                new Dictionary<string, string> { [attribute] = replacement });
            dirtyRegistration.Authorized.Should().BeTrue("a writable registration definition must positively reach account creation");
            RegistrationFieldsHidden(dirtyRegistration).Should().BeFalse();
            var registeredUser = await FindOwnedRegistrationAsync(admin, registered);
            registeredUser.HasValue.Should().BeTrue();
            StoredScalar(registeredUser!.Value, attribute, replacement).Should().BeTrue(
                "the injected field name must be proven effective in the actual registration parser");
            NoAuthorityAttributes(registeredUser.Value).Should().BeFalse(
                "the same new-account protection predicate detects the user-editable registration variant");
        }
        finally
        {
            try
            {
                try
                {
                    if (account is not null && restore is not null)
                        (await UpdateAsync(account, restore)).Should().Be(HttpStatusCode.NoContent);
                }
                finally
                {
                    if (registered is not null)
                    {
                        var registeredUser = await FindOwnedRegistrationAsync(admin, registered);
                        if (registeredUser.HasValue)
                            (await admin.DeleteAsync("users/" + RequiredId(registeredUser.Value))).Should().Be(HttpStatusCode.NoContent);
                    }
                }
            }
            finally
            {
                account?.Dispose();
                (await admin.PutAsync("users/profile", originalProfile)).Should().Be(HttpStatusCode.OK);
            }
            AdminOnlyDefinitions(await admin.GetAsync("users/profile")).Should().BeTrue();
            SeedAttributesMatch(await admin.UserAsync(identity.Username), identity).Should().BeTrue();
        }
        using var restoredAccount = await AccountClientAsync(identity);
        HiddenFromAccount(await AccountAsync(restoredAccount)).Should().BeTrue();
        await AssertFreshIdentityAsync(identity);
    }

    private async Task<HttpClient> AccountClientAsync(SeedIdentity identity)
    {
        using var browser = fixture.Browser();
        var authorization = await browser.AuthorizeAsync(identity.Username, KeycloakFixture.DemoPassword,
            new Uri(fixture.BaseAddress, "realms/learnstack/account/").AbsoluteUri,
            clientId: "account-console", scope: "openid");
        authorization.Authorized.Should().BeTrue("the delivered account-console code/S256 configuration must work");
        var exchange = await browser.RedeemAsync(authorization, secret: null);
        exchange.StatusCode.Should().Be(HttpStatusCode.OK);
        (exchange.AccessToken.Length > 0).Should().BeTrue();
        var client = KeycloakHttp.Create(fixture.BaseAddress, fixture.RootCertificate);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", exchange.AccessToken);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private async Task AssertFreshIdentityAsync(SeedIdentity identity)
    {
        using var browser = fixture.Browser();
        var authorization = await browser.AuthorizeAsync(identity.Username, KeycloakFixture.DemoPassword, Callback(identity));
        authorization.Authorized.Should().BeTrue();
        var exchange = await browser.RedeemAsync(authorization);
        exchange.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = await KeycloakTokenVerifier.ValidateAsync(fixture, exchange.AccessToken, "learnstack-api");
        TokenIdentityMatches(token, identity).Should().BeTrue("fresh BFF issuance still uses exactly the typed seed authority");
    }

    private static async Task<JsonElement> AccountAsync(HttpClient client)
    {
        using var response = await KeycloakHttp.SendAsync(client, HttpMethod.Get,
            "realms/learnstack/account/?userProfileMetadata=true");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (response.Content.Headers.ContentType?.MediaType == "application/json").Should().BeTrue(
            "the account request must reach Account REST rather than the HTML console shell");
        using var json = await KeycloakHttp.ReadJsonAsync(response);
        return json.RootElement.Clone();
    }

    private static async Task<HttpStatusCode> UpdateAsync(HttpClient client, JsonObject payload)
    {
        using var response = await KeycloakHttp.SendAsync(client, HttpMethod.Post, "realms/learnstack/account/",
            JsonContent.Create(payload));
        return response.StatusCode;
    }

    private static JsonObject AccountUpdate(JsonElement account)
    {
        var payload = JsonNode.Parse(account.GetRawText())!.AsObject();
        payload.Remove("userProfileMetadata");
        payload.Remove("id");
        if (payload["attributes"] is null) payload["attributes"] = new JsonObject();
        return payload;
    }

    private static bool HiddenFromAccount(JsonElement account) => NoAuthorityAttributes(account)
        && account.TryGetProperty("userProfileMetadata", out var metadata)
        && metadata.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Array
        && !attributes.EnumerateArray().Any(definition => ProtectedAttributes.Contains(definition.GetProperty("name").GetString()));

    private static bool AdminOnlyDefinitions(JsonElement profile) => ProtectedAttributes.All(name =>
    {
        var matches = profile.GetProperty("attributes").EnumerateArray()
            .Where(definition => definition.GetProperty("name").GetString() == name).ToArray();
        return matches.Length == 1 && ProfilePermissions.All(permission =>
        {
            var values = matches[0].GetProperty("permissions").GetProperty(permission);
            return values.GetArrayLength() == 1 && values[0].GetString() == "admin";
        });
    });

    private static bool NoAuthorityAttributes(JsonElement user) => !user.TryGetProperty("attributes", out var attributes)
        || ProtectedAttributes.All(name => !attributes.TryGetProperty(name, out _));

    private static bool SeedAttributesMatch(JsonElement user, SeedIdentity identity) =>
        StoredScalar(user, "user_id", identity.UserId.Value.ToString("D"))
        && StoredScalar(user, "tenant_id", identity.Tenant.TenantId.Value.ToString("D"))
        && (identity.Tenant.MapHostToDefaultOrganization
            ? StoredScalar(user, "organization_id", identity.Tenant.DefaultOrganization.OrganizationId.Value.ToString("D"))
            : !user.GetProperty("attributes").TryGetProperty("organization_id", out _));

    private static bool StoredScalar(JsonElement user, string name, string expected) =>
        user.TryGetProperty("attributes", out var attributes) && attributes.TryGetProperty(name, out var values)
        && values.ValueKind == JsonValueKind.Array && values.GetArrayLength() == 1
        && values[0].ValueKind == JsonValueKind.String && values[0].GetString() == expected;

    private static bool TokenIdentityMatches(JsonElement token, SeedIdentity identity) =>
        Scalar(token, "user_id", identity.UserId.Value.ToString("D"))
        && Scalar(token, "tenant_id", identity.Tenant.TenantId.Value.ToString("D"))
        && (identity.Tenant.MapHostToDefaultOrganization
            ? Scalar(token, "organization_id", identity.Tenant.DefaultOrganization.OrganizationId.Value.ToString("D"))
            : !token.TryGetProperty("organization_id", out _));

    private static bool Scalar(JsonElement payload, string name, string expected) => payload.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String && value.GetString() == expected;

    private static bool RegistrationFieldsHidden(KeycloakRegistration result) => ProtectedAttributes.All(name =>
        !result.Fields.Contains(name) && !result.Fields.Contains("user.attributes." + name));

    private static async Task<JsonElement?> FindOwnedRegistrationAsync(KeycloakAdminClient admin, string username)
    {
        var users = await admin.GetAsync(QueryHelpers.AddQueryString("users", new Dictionary<string, string?>
        {
            ["username"] = username,
            ["exact"] = "true",
        }));
        if (users.GetArrayLength() == 0) return null;
        (users.GetArrayLength() == 1 && users[0].GetProperty("username").GetString() == username
            && users[0].GetProperty("email").GetString() == username).Should().BeTrue(
                "cleanup and readback select only the exact invocation-owned registration");
        return users[0];
    }

    private static string OwnedRegistrationName() => "profile-proof-" + Guid.NewGuid().ToString("N") + "@provider.test";
    private static string Callback(SeedIdentity identity) => "https://" + identity.Tenant.Host + ":3000/api/auth/callback/keycloak";
    private static string RequiredId(JsonElement entity) => entity.GetProperty("id").GetString()
        ?? throw new InvalidOperationException("Keycloak fixture entity ID is missing.");
}
