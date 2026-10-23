using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Tests.Integration.Database;
using LearnStack.Tools.Seeder;
using Xunit;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>Actual pinned provider storage, separate from a durable LearnStack session ledger.</summary>
[Collection(SharedKeycloakRealm.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class TenantRealmEventTests(KeycloakFixture fresh)
{
    private static readonly string[] Required =
        ["LOGIN", "LOGIN_ERROR", "LOGOUT", "LOGOUT_ERROR", "CODE_TO_TOKEN", "CODE_TO_TOKEN_ERROR", "REFRESH_TOKEN", "REFRESH_TOKEN_ERROR"];

    [Fact]
    public async Task Fresh_provider_stores_real_login_code_refresh_and_refresh_error_events()
    {
        using var admin = await fresh.CreateAdminClientAsync();
        var configuration = await admin.GetAsync("events/config");
        configuration.GetProperty("eventsEnabled").GetBoolean().Should().BeTrue();
        configuration.GetProperty("eventsExpiration").GetInt64().Should().Be(604800);
        Types(configuration).Should().Contain(Required);
        Types(configuration).Should().Contain("UPDATE_CREDENTIAL", "existing default-saved security types remain selected");

        using var browser = fresh.Browser();
        var declaration = SeedData.IdentityUsers[0];
        var authorization = await browser.AuthorizeAsync(declaration.Username, KeycloakFixture.DemoPassword, Callback(declaration));
        authorization.Authorized.Should().BeTrue();
        var exchange = await browser.RedeemAsync(authorization);
        exchange.StatusCode.Should().Be(HttpStatusCode.OK);
        var refreshed = await browser.RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = exchange.RefreshToken,
            ["client_id"] = "learnstack-web",
            ["client_secret"] = KeycloakFixture.WebClientSecret,
        });
        refreshed.StatusCode.Should().Be(HttpStatusCode.OK);
        var refused = await browser.RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = "owned-invalid-fixture-refresh",
            ["client_id"] = "learnstack-web",
            ["client_secret"] = KeycloakFixture.WebClientSecret,
        });
        refused.Error.Should().Be("invalid_grant");
        foreach (var type in new[] { "LOGIN", "CODE_TO_TOKEN", "REFRESH_TOKEN", "REFRESH_TOKEN_ERROR" })
        {
            // Assert only the bounded closed type/count; event identity/IP details never become assertion output.
            var events = await admin.GetAsync("events?max=100&type=" + type);
            events.EnumerateArray().Count(value => value.GetProperty("type").GetString() == type).Should().BeGreaterThan(0,
                "the enabled event type {0} must have an actual stored provider row", type);
        }
    }

    [Fact]
    public async Task Warm_empty_default_policy_converges_preserves_custom_selection_and_refuses_existing_indefinite_history()
    {
        var realm = JsonNode.Parse(await File.ReadAllTextAsync(KeycloakFixture.RealmPath("learnstack")))!.AsObject();
        realm["eventsEnabled"] = false;
        realm["eventsExpiration"] = 0;
        realm["enabledEventTypes"] = new JsonArray("LOGIN", "UPDATE_PROFILE");
        await using var fixture = await KeycloakFixture.StartAsync(realm.ToJsonString());
        using var admin = await fixture.CreateAdminClientAsync();
        var before = await admin.GetAsync("events/config");
        var profile = (await admin.GetAsync("users/profile")).GetRawText();
        (await admin.GetAsync("events?first=0&max=1")).GetArrayLength().Should().Be(0);
        var outcome = await KeycloakReconciliationDriver.RunAsync(fixture);
        outcome.Success.Should().BeTrue("qualified warm event policy converges: {0}/{1}", outcome.Stage, outcome.Reason);
        outcome.Mutations.Should().Be(1);
        var after = await admin.GetAsync("events/config");
        after.GetProperty("eventsEnabled").GetBoolean().Should().BeTrue();
        after.GetProperty("eventsExpiration").GetInt64().Should().Be(604800);
        Types(after).Should().Contain(Required).And.Contain("UPDATE_PROFILE");
        foreach (var field in new[] { "eventsListeners", "adminEventsEnabled", "adminEventsDetailsEnabled" })
            after.GetProperty(field).GetRawText().Should().Be(before.GetProperty(field).GetRawText());
        (await admin.GetAsync("users/profile")).GetRawText().Should().Be(profile);
        var repeated = await KeycloakReconciliationDriver.RunAsync(fixture);
        repeated.Success.Should().BeTrue();
        repeated.Mutations.Should().Be(0);

        using var browser = fixture.Browser();
        var user = SeedData.IdentityUsers[0];
        var authorization = await browser.AuthorizeAsync(user.Username, KeycloakFixture.DemoPassword, Callback(user));
        authorization.Authorized.Should().BeTrue();
        (await browser.RedeemAsync(authorization)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.GetAsync("events?first=0&max=1")).GetArrayLength().Should().Be(1);
        var indefinite = JsonNode.Parse(after.GetRawText())!.AsObject();
        indefinite["eventsEnabled"] = false;
        indefinite["eventsExpiration"] = 0;
        (await admin.PutAsync("events/config", indefinite)).Should().Be(HttpStatusCode.NoContent);
        var unchanged = (await admin.GetAsync("events/config")).GetRawText();
        var refusal = await KeycloakReconciliationDriver.RunAsync(fixture);
        refusal.Success.Should().BeFalse();
        refusal.Stage.Should().Be("plan");
        refusal.Reason.Should().Be("event_policy_conflict");
        refusal.Mutations.Should().Be(0);
        (await admin.GetAsync("events/config")).GetRawText().Should().Be(unchanged);
        (await admin.GetAsync("events?first=0&max=1")).GetArrayLength().Should().Be(1,
            "refusal never deletes prior history or invents a safe retention window");
    }

    private static string Callback(SeedIdentity identity) =>
        "https://" + identity.Tenant.Host + ":3000/api/auth/callback/keycloak";

    private static string[] Types(JsonElement configuration) => configuration.GetProperty("enabledEventTypes")
        .EnumerateArray().Select(value => value.GetString()!).ToArray();
}
