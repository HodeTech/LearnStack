using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Tools.Seeder;
using Xunit;

namespace LearnStack.Tests.Integration.TenancyIdentity;

public sealed partial class TenantRealmReconciliationTests
{
    private static readonly string[] NativeClientWitnessMetadataFields =
        ["attributes", "protocolMappers", "registeredNodes", "access", "notBefore", "secret"];
    private static readonly string[] NativeClientSetFields =
        ["defaultClientScopes", "optionalClientScopes", "redirectUris"];

    [Fact]
    public async Task Warm_native_web_scope_replacement_preserves_API_graph_and_real_code_refresh_identity()
    {
        var realm = JsonNode.Parse(await File.ReadAllTextAsync(KeycloakFixture.RealmPath("learnstack")))!.AsObject();
        var scopes = realm["clientScopes"]!.AsArray();
        scopes.Remove(scopes.Single(scope => scope!["name"]!.GetValue<string>() == "learnstack-native-session"));
        var webDefinition = realm["clients"]!.AsArray().Single(client => client!["clientId"]!.GetValue<string>() == "learnstack-web")!;
        var defaults = webDefinition["defaultClientScopes"]!.AsArray();
        defaults[defaults.IndexOf(defaults.Single(scope => scope!.GetValue<string>() == "learnstack-native-session"))] = "learnstack-tenant-api";
        await using var provider = await KeycloakFixture.StartAsync(realm.ToJsonString());
        using var admin = await provider.CreateAdminClientAsync();
        var api = await admin.ClientAsync("learnstack-api");
        var apiPath = "clients/" + Id(api);
        var apiDefaults = await admin.GetAsync(apiPath + "/default-client-scopes");
        var apiOptional = await admin.GetAsync(apiPath + "/optional-client-scopes");
        var apiMappers = await admin.GetAsync(apiPath + "/protocol-mappers/models");
        var legacy = await ScopeAsync(admin, "learnstack-tenant-api");
        var legacyMapperPath = "client-scopes/" + Id(legacy) + "/protocol-mappers/models";
        var legacyMappers = await admin.GetAsync(legacyMapperPath);
        var identity = SeedData.IdentityUsers.First(user => user.Tenant.MapHostToDefaultOrganization);
        var account = await UserDetailsAsync(admin, identity.Username);
        var accountId = Id(account);
        using (var oldBrowser = provider.Browser())
        {
            var authorization = await oldBrowser.AuthorizeAsync(identity.Username, KeycloakFixture.DemoPassword, Callback(identity));
            authorization.Authorized.Should().BeTrue();
            var grant = await oldBrowser.RedeemAsync(authorization);
            grant.StatusCode.Should().Be(HttpStatusCode.OK);
            var token = await KeycloakTokenVerifier.ValidateAsync(provider, grant.AccessToken, "learnstack-api");
            token.TryGetProperty("tenant_id", out _).Should().BeTrue("the warm control begins with actual scoped web issuance");
            token.TryGetProperty("organization_id", out _).Should().BeTrue();
        }
        var changed = await KeycloakReconciliationDriver.RunAsync(provider);
        changed.Success.Should().BeTrue("closed outcome: {0}/{1}", changed.Stage, changed.Reason);
        changed.Mutations.Should().Be(3, "one fixed scope creation, one legacy default detachment and one native attachment");
        var apiReadback = await admin.ClientAsync("learnstack-api");
        var apiComparison = NativeApiClientReadbackComparison(Object(api), Object(apiReadback));
        apiComparison.Matches.Should().BeTrue("the API client representation is preserved; " + apiComparison.Witness);
        NativeIdGraphMatches(JsonNode.Parse(apiDefaults.GetRawText()),
            JsonNode.Parse((await admin.GetAsync(apiPath + "/default-client-scopes")).GetRawText()))
            .Should().BeTrue("every API default scope ID and full representation is preserved");
        NativeIdGraphMatches(JsonNode.Parse(apiOptional.GetRawText()),
            JsonNode.Parse((await admin.GetAsync(apiPath + "/optional-client-scopes")).GetRawText()))
            .Should().BeTrue("every API optional scope ID and full representation is preserved");
        NativeIdGraphMatches(JsonNode.Parse(apiMappers.GetRawText()),
            JsonNode.Parse((await admin.GetAsync(apiPath + "/protocol-mappers/models")).GetRawText()))
            .Should().BeTrue("every API direct mapper ID and full configuration is preserved");
        NativeIdGraphMatches(JsonNode.Parse(legacyMappers.GetRawText()),
            JsonNode.Parse((await admin.GetAsync(legacyMapperPath)).GetRawText()))
            .Should().BeTrue("every legacy scope mapper ID and full configuration is preserved");
        AssertNativeClientComparisonControls(Object(api));
        AssertNativeIdGraphControls(JsonNode.Parse(apiDefaults.GetRawText())!.AsArray(), mapperValues: false);
        AssertNativeIdGraphControls(JsonNode.Parse(legacyMappers.GetRawText())!.AsArray(), mapperValues: true);
        AssertSame(legacy, await ScopeAsync(admin, "learnstack-tenant-api"), "the legacy scope and mapper IDs are preserved");
        AssertSame(account, await UserDetailsAsync(admin, identity.Username), "provider identity is preserved");
        var web = await admin.ClientAsync("learnstack-web");
        var webDefaults = await admin.GetAsync("clients/" + Id(web) + "/default-client-scopes");
        webDefaults.EnumerateArray().Select(scope => scope.GetProperty("name").GetString()).Should().Contain("learnstack-native-session").And.NotContain("learnstack-tenant-api");
        var webOptional = await admin.GetAsync("clients/" + Id(web) + "/optional-client-scopes");
        webOptional.EnumerateArray().Select(scope => scope.GetProperty("name").GetString()).Should().NotContain("learnstack-tenant-api");
        using var browser = provider.Browser();
        var selected = await browser.AuthorizeAsync(identity.Username, KeycloakFixture.DemoPassword,
            "https://manage." + identity.Tenant.Host + ":3000/api/auth/callback/keycloak");
        selected.Authorized.Should().BeTrue("manage callback is an exact registered HTTPS origin");
        var code = await browser.RedeemAsync(selected);
        code.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertNativeGrantAsync(code);
        var refresh = await browser.RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = "learnstack-web",
            ["client_secret"] = KeycloakFixture.WebClientSecret,
            ["refresh_token"] = code.RefreshToken,
        });
        refresh.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertNativeGrantAsync(refresh);
        for (var pass = 0; pass < 2; pass++)
        {
            var repeated = await KeycloakReconciliationDriver.RunAsync(provider);
            repeated.Success.Should().BeTrue("closed outcome: {0}/{1}", repeated.Stage, repeated.Reason);
            repeated.Mutations.Should().Be(0, "warm repeat performs no provider write");
        }

        async Task AssertNativeGrantAsync(KeycloakTokenExchange grant)
        {
            foreach (var (token, audience) in new[] { (grant.AccessToken, "learnstack-api"), (grant.IdToken, "learnstack-web") })
            {
                var payload = await KeycloakTokenVerifier.ValidateAsync(provider, token, audience);
                (payload.GetProperty("sub").GetString() == accountId).Should().BeTrue();
                (payload.GetProperty("user_id").GetString() == identity.UserId.Value.ToString("D")).Should().BeTrue();
                payload.TryGetProperty("tenant_id", out _).Should().BeFalse();
                payload.TryGetProperty("organization_id", out _).Should().BeFalse();
                grant.Window.AcceptsFresh(payload, 900).Should().BeTrue();
            }
        }
    }

    private static (bool Matches, string Witness) NativeApiClientReadbackComparison(JsonObject before, JsonObject after)
    {
        var normalizedBefore = before.DeepClone().AsObject();
        var normalizedAfter = after.DeepClone().AsObject();
        var observations = new List<string>();
        var exactUniqueSets = true;
        foreach (var name in NativeClientSetFields)
        {
            var left = Strings(before[name]);
            var right = Strings(after[name]);
            var uniqueCardinality = left is not null && right is not null && left.Length == right.Length
                && left.Distinct(StringComparer.Ordinal).Count() == left.Length
                && right.Distinct(StringComparer.Ordinal).Count() == right.Length;
            var sameExactSet = uniqueCardinality && left!.ToHashSet(StringComparer.Ordinal).SetEquals(right!);
            exactUniqueSets &= sameExactSet;
            var sameOrder = left is not null && right is not null && left.SequenceEqual(right, StringComparer.Ordinal);
            observations.Add($"{name}:SameExactSet={sameExactSet},SameOrder={sameOrder},UniqueCardinality={uniqueCardinality}");
            if (left is not null) normalizedBefore[name] = new JsonArray(left.Order(StringComparer.Ordinal)
                .Select(value => JsonValue.Create(value)).ToArray<JsonNode?>());
            if (right is not null) normalizedAfter[name] = new JsonArray(right.Order(StringComparer.Ordinal)
                .Select(value => JsonValue.Create(value)).ToArray<JsonNode?>());
        }
        var knownFields = NativeClientSetFields.Concat(NativeClientWitnessMetadataFields).ToArray();
        var changedKnownFields = knownFields.Where(name => !JsonNode.DeepEquals(before[name], after[name]));
        var otherFieldChanged = before.Select(field => field.Key).Concat(after.Select(field => field.Key))
            .Distinct(StringComparer.Ordinal).Except(knownFields, StringComparer.Ordinal)
            .Any(name => !JsonNode.DeepEquals(before[name], after[name]));
        observations.Add("KnownChangedFields=" + string.Join(",", changedKnownFields));
        observations.Add($"OtherFieldChanged={otherFieldChanged}");
        var allFieldsEqual = JsonNode.DeepEquals(normalizedBefore, normalizedAfter);
        observations.Add($"WholeClientEqualAfterOnlySetOrderNormalization={allFieldsEqual}");
        return (exactUniqueSets && allFieldsEqual, string.Join("; ", observations));

        static string[]? Strings(JsonNode? node)
        {
            if (node is not JsonArray values || values.Any(value => value is not JsonValue item
                || !item.TryGetValue<string>(out var text) || text is null)) return null;
            return values.Select(value => value!.GetValue<string>()).ToArray();
        }
    }

    private static bool NativeIdGraphMatches(JsonNode? before, JsonNode? after)
    {
        var left = Read(before);
        var right = Read(after);
        return left is not null && right is not null && left.Count == right.Count
            && left.All(entry => right.TryGetValue(entry.Key, out var item) && JsonNode.DeepEquals(entry.Value, item));

        static Dictionary<string, JsonObject>? Read(JsonNode? node)
        {
            if (node is not JsonArray values) return null;
            var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var value in values)
            {
                if (value is not JsonObject item || item["id"] is not JsonValue identifier
                    || !identifier.TryGetValue<string>(out var id) || string.IsNullOrEmpty(id)
                    || !result.TryAdd(id, item)) return null;
            }
            return result;
        }
    }

    private static void AssertNativeClientComparisonControls(JsonObject baseline)
    {
        var reordered = baseline.DeepClone().AsObject();
        foreach (var name in NativeClientSetFields)
            reordered[name] = new JsonArray(reordered[name]!.AsArray().Reverse().Select(value => value!.DeepClone()).ToArray());
        NativeApiClientReadbackComparison(baseline, reordered).Matches.Should().BeTrue();
        foreach (var defect in new[] { "duplicate", "missing", "changed", "missing-field", "other-field" })
        {
            var mutant = reordered.DeepClone().AsObject();
            var values = mutant["defaultClientScopes"]!.AsArray();
            values.Count.Should().BeGreaterThan(0);
            switch (defect)
            {
                case "duplicate": values.Add(values[0]!.DeepClone()); break;
                case "missing": values.RemoveAt(0); break;
                case "changed": values[0] = "foreign-fixture-scope"; break;
                case "missing-field": mutant.Remove("defaultClientScopes"); break;
                case "other-field": mutant["description"] = "unexpected fixture client change"; break;
            }
            NativeApiClientReadbackComparison(baseline, mutant).Matches.Should().BeFalse(
                "only ordering of the three exact unique string sets is insignificant: " + defect);
        }
    }

    private static void AssertNativeIdGraphControls(JsonArray baseline, bool mapperValues)
    {
        baseline.Count.Should().BeGreaterThan(0);
        var reordered = new JsonArray(baseline.Reverse().Select(value => value!.DeepClone()).ToArray());
        NativeIdGraphMatches(baseline, reordered).Should().BeTrue();
        foreach (var defect in new[] { "duplicate", "missing", "changed-id", "changed-value" })
        {
            var mutant = reordered.DeepClone().AsArray();
            switch (defect)
            {
                case "duplicate": mutant.Add(mutant[0]!.DeepClone()); break;
                case "missing": mutant.RemoveAt(0); break;
                case "changed-id": mutant[0]!["id"] = "replacement-fixture-id"; break;
                case "changed-value":
                    if (mapperValues) mutant[0]!["config"]!["claim.name"] = "changed-fixture-claim";
                    else mutant[0]!["name"] = "changed-fixture-name";
                    break;
            }
            NativeIdGraphMatches(baseline, mutant).Should().BeFalse(
                "the ID-keyed comparison preserves unique cardinality and every representation field: " + defect);
        }
    }
}
