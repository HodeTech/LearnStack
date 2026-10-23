using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Tools.Seeder;
using Xunit;

namespace LearnStack.Tests.Integration.TenancyIdentity;

public sealed partial class TenantRealmReconciliationTests
{
    private static readonly string[] ProtectedPreparationNames = ["user_id", "tenant_id", "organization_id"];

    [Fact]
    public Task Historical_stock_upgrade_prepares_once_and_counts_the_named_event_policy_write() =>
        WithHistoricalPreparationAsync(async (fixture, admin) =>
        {
            var profileBefore = await admin.GetAsync("users/profile");
            var expectedProfile = await AppendedPreparationProfileAsync(profileBefore);
            var originalUsers = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var credentials = new Dictionary<string, string>(StringComparer.Ordinal);
            var grants = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var identity in SeedData.IdentityUsers.Take(2))
            {
                var user = await UserDetailsAsync(admin, identity.Username);
                originalUsers.Add(identity.Username, user);
                credentials.Add(Id(user), await CredentialHashAsync(admin, Id(user)));
                grants.Add(Id(user), await admin.GetAsync("users/" + Id(user) + "/role-mappings"));
            }
            var clientsBefore = await admin.GetAsync("clients");
            var scopesBefore = await PreparationScopeStateAsync(admin);
            var api = await admin.ClientAsync("learnstack-api");
            var apiSecret = await admin.GetAsync("clients/" + Id(api) + "/client-secret");
            using var hub = await fixture.CreateAdminClientAsync("learnstack-hub");
            var hubBefore = await StateAsync(hub);

            var first = await KeycloakReconciliationDriver.RunAsync(fixture);
            first.Success.Should().BeTrue("the automatic historical path reports {0}/{1}", first.Stage, first.Reason);
            first.PreparationDispatched.Should().BeTrue();
            first.Mutations.Should().Be(18, "one preparation, sixteen prior stock-upgrade writes and one ADR-0070 event-policy PUT");
            AssertPreparationProfile(expectedProfile, await admin.GetAsync("users/profile"));
            foreach (var identity in SeedData.IdentityUsers) await AssertStoredIdentityAsync(admin, identity);
            foreach (var (username, original) in originalUsers)
            {
                var after = await UserDetailsAsync(admin, username);
                (Id(after) == Id(original)).Should().BeTrue("historical accounts retain their provider IDs");
                AssertPreparationPreservedUser(original, after);
                (await CredentialHashAsync(admin, Id(after)) == credentials[Id(original)]).Should().BeTrue(
                    "the actual historical password credential DTO remains unchanged");
                await AssertOnlyAccountGrantAddedAsync(admin, Id(after), grants[Id(original)]);
            }
            (await admin.GetAsync("clients")).EnumerateArray().Select(Id).Order(StringComparer.Ordinal).Should().Equal(
                clientsBefore.EnumerateArray().Select(Id).Order(StringComparer.Ordinal), "all existing clients retain their provider IDs");
            foreach (var (path, before) in scopesBefore)
                AssertSame(before, await admin.GetAsync(path), "preparation and normalization preserve each preexisting global scope definition");
            AssertSame(apiSecret, await admin.GetAsync("clients/" + Id(api) + "/client-secret"), "the API credential survives the automatic path");
            (await StateAsync(hub) == hubBefore).Should().BeTrue("the separate Hub realm remains untouched");
            await AssertPreparedConsoleAssignmentsAsync(admin);
            var afterFirst = await StateAsync(admin);
            var second = await KeycloakReconciliationDriver.RunAsync(fixture);
            second.Success.Should().BeTrue("the explicit subsequent pass reports {0}/{1}", second.Stage, second.Reason);
            second.Mutations.Should().Be(0);
            second.PreparationDispatched.Should().BeFalse();
            (await StateAsync(admin) == afterFirst).Should().BeTrue("zero writes preserves independently read provider state");
        });

    [Fact]
    public Task Hidden_historical_actor_conflict_is_revealed_after_preparation_without_any_ordinary_write() =>
        WithHistoricalPreparationAsync(async (fixture, admin) =>
        {
            var identity = SeedData.IdentityUsers[0];
            var stockProfile = await admin.GetAsync("users/profile");
            var visibleProfile = Object(stockProfile);
            visibleProfile["unmanagedAttributePolicy"] = "ADMIN_EDIT";
            (await admin.PutAsync("users/profile", visibleProfile)).Should().Be(HttpStatusCode.OK);
            var user = await UserDetailsAsync(admin, identity.Username);
            var conflictingActor = Guid.NewGuid().ToString("D");
            var changed = Object(user);
            changed["attributes"]!["user_id"] = new JsonArray(conflictingActor);
            (await admin.PutAsync("users/" + Id(user), changed)).Should().Be(HttpStatusCode.NoContent);
            var visibleUser = await UserDetailsAsync(admin, identity.Username);
            (visibleUser.GetProperty("attributes").GetProperty("user_id")[0].GetString() == conflictingActor).Should().BeTrue(
                "the planted actor exists in actual provider storage before it is hidden");
            (await admin.PutAsync("users/profile", Object(stockProfile))).Should().Be(HttpStatusCode.OK);
            AssertSame(stockProfile, await admin.GetAsync("users/profile"), "test-owned visibility is restored before invoking the real reconciler");
            var hidden = await UserDetailsAsync(admin, identity.Username);
            (hidden.TryGetProperty("attributes", out var attributes) && attributes.TryGetProperty("user_id", out _))
                .Should().BeFalse("the stock provider actually conceals the conflicting actor");
            var before = await PreparationOrdinaryStateAsync(admin);
            var credentialHash = await CredentialHashAsync(admin, Id(user));

            var outcome = await KeycloakReconciliationDriver.RunAsync(fixture);
            AssertFailure(outcome, "plan", "identity_conflict");
            outcome.Mutations.Should().Be(1, "only the admitted metadata preparation may precede the fresh-plan refusal");
            outcome.PreparationDispatched.Should().BeTrue();
            AssertPreparationProfile(await AppendedPreparationProfileAsync(stockProfile), await admin.GetAsync("users/profile"));
            var revealed = await UserDetailsAsync(admin, identity.Username);
            AssertSame(visibleUser, revealed, "the fresh plan refuses the actual actor without rewriting the account");
            (Id(revealed) == Id(user)).Should().BeTrue();
            (await CredentialHashAsync(admin, Id(user)) == credentialHash).Should().BeTrue();
            (await PreparationOrdinaryStateAsync(admin) == before).Should().BeTrue("no realm, account, credential, grant, mapper or assignment mutation follows preparation");
        });

    [Fact]
    public Task Missing_protected_definitions_do_not_allow_preparation_before_an_independent_secret_conflict() =>
        WithHistoricalPreparationAsync(async (fixture, admin) =>
        {
            var web = Object(await admin.ClientAsync("learnstack-web"));
            web["publicClient"] = false;
            web["secret"] = Guid.NewGuid().ToString("N");
            (await admin.PutAsync("clients/" + web["id"]!.GetValue<string>(), web)).Should().Be(HttpStatusCode.NoContent);
            await AssertPreparationRefusesWithoutWriteAsync(fixture, admin, "secret_conflict");
        });

    [Fact]
    public Task Missing_protected_definitions_do_not_allow_preparation_for_a_customized_stock_scope_lookalike() =>
        WithHistoricalPreparationAsync(async (fixture, admin) =>
        {
            var roles = Object(await ScopeAsync(admin, "roles"));
            roles["attributes"]!["fixture.customized"] = "true";
            (await admin.PutAsync("client-scopes/" + roles["id"]!.GetValue<string>(), roles)).Should().Be(HttpStatusCode.NoContent);
            (await ScopeAsync(admin, "roles")).GetProperty("attributes").GetProperty("fixture.customized").GetString().Should().Be("true");
            await AssertPreparationRefusesWithoutWriteAsync(fixture, admin, "unsupported");
        });

    [Theory]
    [InlineData("lost_acknowledgement", "transport")]
    [InlineData("partial_cancel", "cancelled")]
    public Task Interrupted_historical_preparation_counts_the_dispatched_write_and_requires_an_explicit_resume(string scenario, string reason) =>
        WithHistoricalPreparationAsync(async (fixture, admin) =>
        {
            var profileBefore = await admin.GetAsync("users/profile");
            var before = await PreparationOrdinaryStateAsync(admin);
            var interrupted = await KeycloakReconciliationDriver.RunAsync(fixture, scenario);
            AssertFailure(interrupted, "prepare", reason);
            interrupted.Mutations.Should().Be(1, "a real provider commit with a lost response or cancellation cannot report zero writes");
            interrupted.PreparationDispatched.Should().BeTrue();
            AssertPreparationProfile(await AppendedPreparationProfileAsync(profileBefore), await admin.GetAsync("users/profile"));
            (await PreparationOrdinaryStateAsync(admin) == before).Should().BeTrue("the interrupted preparation dispatches no ordinary mutation");

            var resumed = await KeycloakReconciliationDriver.RunAsync(fixture);
            resumed.Success.Should().BeTrue("an explicit fresh invocation reports {0}/{1}", resumed.Stage, resumed.Reason);
            resumed.Mutations.Should().Be(17, "the durable preparation is reused; sixteen prior stock writes and one event-policy PUT remain");
            resumed.PreparationDispatched.Should().BeFalse();
            var completed = await KeycloakReconciliationDriver.RunAsync(fixture);
            completed.Success.Should().BeTrue();
            completed.Mutations.Should().Be(0);
        });

    [Fact]
    public Task Partial_admin_view_only_definition_is_preserved_by_preparation_then_normalized_by_a_distinct_ordinary_write() =>
        WithHistoricalPreparationAsync(async (fixture, admin) =>
        {
            var profile = Object(await admin.GetAsync("users/profile"));
            var canonical = await CanonicalPreparationDefinitionsAsync();
            var readable = canonical.Single(value => value["name"]!.GetValue<string>() == "user_id").DeepClone();
            readable["permissions"]!["edit"] = new JsonArray();
            profile["attributes"]!.AsArray().Add(readable);
            (await admin.PutAsync("users/profile", profile)).Should().Be(HttpStatusCode.OK);
            var before = await admin.GetAsync("users/profile");
            AssertPreparationProfile(profile, before);
            var ordinaryBefore = await PreparationOrdinaryStateAsync(admin);

            var interrupted = await KeycloakReconciliationDriver.RunAsync(fixture, "partial_cancel");
            AssertFailure(interrupted, "prepare", "cancelled");
            interrupted.Mutations.Should().Be(1);
            interrupted.PreparationDispatched.Should().BeTrue();
            var prepared = await admin.GetAsync("users/profile");
            var expectedPreparation = await AppendedPreparationProfileAsync(before);
            AssertPreparationProfile(expectedPreparation, prepared);
            (await PreparationOrdinaryStateAsync(admin) == ordinaryBefore).Should().BeTrue();
            var actualReadable = prepared.GetProperty("attributes").EnumerateArray().Single(value => value.GetProperty("name").GetString() == "user_id");
            JsonNode.DeepEquals(readable, JsonNode.Parse(actualReadable.GetRawText())).Should().BeTrue(
                "preparation preserves the existing admin-view-only definition exactly, including its empty edit list");

            var resumed = await KeycloakReconciliationDriver.RunAsync(fixture);
            resumed.Success.Should().BeTrue("the separate ordinary plan reports {0}/{1}", resumed.Stage, resumed.Reason);
            resumed.PreparationDispatched.Should().BeFalse();
            resumed.Mutations.Should().Be(18, "sixteen prior stock writes, separate profile normalization and one event-policy PUT");
            var expectedFinal = expectedPreparation.DeepClone();
            var definitions = expectedFinal["attributes"]!.AsArray();
            var index = definitions.IndexOf(definitions.Single(value => value!["name"]!.GetValue<string>() == "user_id"));
            definitions[index] = canonical.Single(value => value["name"]!.GetValue<string>() == "user_id").DeepClone();
            AssertPreparationProfile(expectedFinal, await admin.GetAsync("users/profile"));
            var completed = await KeycloakReconciliationDriver.RunAsync(fixture);
            completed.Success.Should().BeTrue();
            completed.Mutations.Should().Be(0);
        });

    private static async Task WithHistoricalPreparationAsync(Func<KeycloakFixture, KeycloakAdminClient, Task> proof)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(KeycloakFixture.RealmPath("learnstack"))!, "..", "..", ".."));
        var historical = await TenantRealmStockBaselineTests.HistoricalImportAsync(root);
        var fixture = await KeycloakFixture.StartAsync(historical);
        StockProviderResources? resources = null;
        try
        {
            resources = await fixture.StockProviderResourcesAsync();
            (await fixture.StockProviderVersionAsync()).Should().Be("26.6.2");
            using var admin = await fixture.CreateAdminClientAsync();
            await proof(fixture, admin);
        }
        finally
        {
            try { await fixture.DisposeAsync(); }
            finally
            {
                if (resources is not null) await KeycloakFixture.AssertStockResourcesAbsentAsync(resources);
            }
        }
    }

    private static async Task AssertPreparationRefusesWithoutWriteAsync(KeycloakFixture fixture, KeycloakAdminClient admin, string reason)
    {
        var before = await StateAsync(admin);
        var profile = await admin.GetAsync("users/profile");
        var outcome = await KeycloakReconciliationDriver.RunAsync(fixture);
        AssertFailure(outcome, "plan", reason);
        outcome.Mutations.Should().Be(0, "the independent conflict must refuse before the exceptional metadata write");
        outcome.PreparationDispatched.Should().BeFalse();
        AssertSame(profile, await admin.GetAsync("users/profile"), "the entire stock profile remains unchanged");
        (await StateAsync(admin) == before).Should().BeTrue("independent real-provider snapshots prove zero mutations");
    }

    private static async Task<JsonNode[]> CanonicalPreparationDefinitionsAsync()
    {
        var desired = JsonNode.Parse(await File.ReadAllTextAsync(KeycloakFixture.RealmPath("learnstack")))!;
        var profile = JsonNode.Parse(desired["components"]!["org.keycloak.userprofile.UserProfileProvider"]![0]!
            ["config"]!["kc.user.profile.config"]![0]!.GetValue<string>())!;
        return ProtectedPreparationNames.Select(name => profile["attributes"]!.AsArray()
            .Single(value => value!["name"]!.GetValue<string>() == name)!.DeepClone()).ToArray();
    }

    private static async Task<JsonNode> AppendedPreparationProfileAsync(JsonElement before)
    {
        var expected = Object(before);
        var attributes = expected["attributes"]!.AsArray();
        var appended = 0;
        foreach (var definition in await CanonicalPreparationDefinitionsAsync())
        {
            if (attributes.Any(value => value!["name"]!.GetValue<string>() == definition["name"]!.GetValue<string>())) continue;
            attributes.Add(definition);
            appended++;
        }
        appended.Should().BeGreaterThan(0, "this provider control must reach the missing-definition preparation path");
        return expected;
    }

    private static void AssertPreparationProfile(JsonNode expected, JsonElement actual) =>
        JsonNode.DeepEquals(expected, JsonNode.Parse(actual.GetRawText())).Should().BeTrue(
            "a separate provider GET proves the exact complete profile, including ordered attributes, groups, permissions and the unchanged unmanaged policy");

    private static async Task<string> CredentialHashAsync(KeycloakAdminClient admin, string userId)
    {
        var credentials = await admin.GetAsync("users/" + userId + "/credentials");
        credentials.EnumerateArray().Any(value => value.GetProperty("type").GetString() == "password").Should().BeTrue(
            "the historical preservation control contains an actual password credential");
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(credentials)));
    }

    private static void AssertPreparationPreservedUser(JsonElement before, JsonElement after)
    {
        var prior = Object(before);
        var current = Object(after);
        foreach (var user in new[] { prior, current })
        {
            if (user["attributes"] is not JsonObject attributes) continue;
            foreach (var name in ProtectedPreparationNames) attributes.Remove(name);
            if (attributes.Count == 0) user.Remove("attributes");
        }
        JsonNode.DeepEquals(prior, current).Should().BeTrue(
            "all preexisting account fields survive, allowing only the declared protected identity attributes to change");
    }

    private static async Task<Dictionary<string, JsonElement>> PreparationScopeStateAsync(KeycloakAdminClient admin)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var scope in (await admin.GetAsync("client-scopes")).EnumerateArray())
            foreach (var suffix in new[] { "", "/protocol-mappers/models", "/scope-mappings" })
            {
                var path = "client-scopes/" + Id(scope) + suffix;
                result.Add(path, await admin.GetAsync(path));
            }
        return result;
    }

    private static async Task AssertPreparedConsoleAssignmentsAsync(KeycloakAdminClient admin)
    {
        var console = await admin.ClientAsync("account-console");
        foreach (var (category, expected) in new[]
        {
            ("default", new[] { "acr", "basic", "email", "profile", "web-origins" }),
            ("optional", new[] { "address", "organization", "phone" }),
        })
        {
            var assigned = await admin.GetAsync("clients/" + Id(console) + "/" + category + "-client-scopes");
            assigned.EnumerateArray().Select(value => value.GetProperty("name").GetString()).Order(StringComparer.Ordinal)
                .Should().Equal(expected, "only the three incompatible console assignment edges are detached");
        }
    }

    private static async Task<string> PreparationOrdinaryStateAsync(KeycloakAdminClient admin)
    {
        var state = JsonNode.Parse(await StateAsync(admin))!.AsObject();
        state.Remove("users/profile");
        // Profile metadata and newly visible identity attributes are the read projection
        // changed by preparation. Account values receive separate exact assertions.
        void RemoveVisibilityProjection(JsonNode? node)
        {
            if (node is JsonObject value)
            {
                value.Remove("userProfileMetadata");
                if (value["attributes"] is JsonObject attributes)
                {
                    foreach (var name in ProtectedPreparationNames) attributes.Remove(name);
                    if (attributes.Count == 0) value.Remove("attributes");
                }
                foreach (var child in value.ToArray()) RemoveVisibilityProjection(child.Value);
            }
            else if (node is JsonArray array)
                foreach (var child in array) RemoveVisibilityProjection(child);
        }
        RemoveVisibilityProjection(state);
        return Canonical(state).ToJsonString();
    }
}
