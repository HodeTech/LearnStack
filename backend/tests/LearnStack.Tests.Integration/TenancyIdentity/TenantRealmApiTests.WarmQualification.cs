using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Tools.Seeder;
using Xunit;

namespace LearnStack.Tests.Integration.TenancyIdentity;

public sealed partial class TenantRealmApiTests
{
    private static readonly string[] WarmProtectedNames = ["user_id", "tenant_id", "organization_id"];
    private static readonly string[] WarmAccountRoles = ["manage-account", "manage-account-links"];
    private static readonly string[] WarmUnrelatedRealmRoles = ["offline_access", "tenant-instructor"];
    private static readonly string[] WarmOfflineRole = ["offline_access"];

    [Fact]
    public async Task Historical_automatic_upgrade_preserves_live_password_and_MFA_then_serves_signed_API_and_console_users()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(KeycloakFixture.RealmPath("learnstack"))!, "..", "..", ".."));
        var historical = JsonNode.Parse(await TenantRealmStockBaselineTests.HistoricalImportAsync(root))!.AsObject();
        var imported = historical["users"]!.AsArray();
        var passwords = imported.ToDictionary(user => user!["username"]!.GetValue<string>(),
            user => user!["credentials"]![0]!["value"]!.GetValue<string>(), StringComparer.Ordinal);
        var legacy = SeedData.IdentityUsers[0];
        var mfaIdentity = SeedData.IdentityUsers[1];
        var otp = new WarmTotp();
        imported.Single(user => user!["username"]!.GetValue<string>() == mfaIdentity.Username)!["credentials"]!.AsArray()
            .Add(otp.Credential());
        const string unrelated = "warm-eligible@provider.test";
        imported.Add(JsonSerializer.SerializeToNode(new
        {
            username = unrelated,
            email = unrelated,
            emailVerified = true,
            enabled = true,
            firstName = "Preserved",
            lastName = "Unrelated",
            credentials = new[] { new { type = "password", value = KeycloakFixture.DemoPassword, temporary = false } },
            realmRoles = WarmUnrelatedRealmRoles,
            clientRoles = new Dictionary<string, string[]> { ["account"] = ["manage-account"] },
        }));
        await using var warm = await KeycloakFixture.StartAsync(historical.ToJsonString());
        var resources = await warm.StockProviderResourcesAsync();
        var journey = Stopwatch.StartNew();
        try
        {
            (await warm.StockProviderVersionAsync()).Should().Be("26.6.2");
            using var admin = await warm.CreateAdminClientAsync();
            using var hub = await warm.CreateAdminClientAsync("learnstack-hub");
            var profile = await admin.GetAsync("users/profile");
            profile.GetProperty("attributes").EnumerateArray().Any(definition =>
                WarmProtectedNames.Contains(definition.GetProperty("name").GetString(), StringComparer.Ordinal)).Should().BeFalse();
            profile.TryGetProperty("unmanagedAttributePolicy", out _).Should().BeFalse(
                "the genuine historical stock visibility boundary is retained");
            output.WriteLine("Historical journey stage: stock profile observed.");
            var expectedProfile = await WarmAppendedProfileAsync(profile);
            var changedPassword = Guid.NewGuid().ToString("N");
            var originalLegacyPassword = passwords[legacy.Username];
            var legacyPath = UserPath(await admin.UserAsync(legacy.Username));
            (await admin.PutAsync(legacyPath + "/reset-password", new { type = "password", value = changedPassword, temporary = false }))
                .Should().Be(HttpStatusCode.NoContent);
            await WarmPasswordRefusedAsync(warm, legacy.Username, passwords[legacy.Username]);
            passwords[legacy.Username] = changedPassword;
            await WarmMfaRefusalAsync(warm, mfaIdentity.Username, passwords[mfaIdentity.Username], otp);
            await WarmConsoleTokensAsync(warm, mfaIdentity.Username, passwords[mfaIdentity.Username], false, 1800, otp: otp);
            output.WriteLine("Historical journey stage: changed password and actual MFA challenge controls passed.");

            var legacyBefore = await WarmConsoleTokensAsync(warm, legacy.Username, changedPassword, false, 1800);
            var unrelatedBefore = await WarmConsoleTokensAsync(warm, unrelated, KeycloakFixture.DemoPassword, true, 1800);
            var offlineBefore = await WarmConsoleScopeEffectsAsync(warm, unrelated, unrelatedBefore, beforeUpgrade: true);
            var beforeLegacyStatus = await WarmAccountStatusAsync(warm, legacyBefore.Exchange);
            var beforeUnrelatedStatus = await WarmAccountStatusAsync(warm, unrelatedBefore.Exchange);
            output.WriteLine("Before console Account REST status: manifest={0}; eligible nonmanifest={1}.",
                (int)beforeLegacyStatus, (int)beforeUnrelatedStatus);
            beforeLegacyStatus.Should().Be(HttpStatusCode.Unauthorized, "the observed historical token has no account audience");
            beforeUnrelatedStatus.Should().Be(HttpStatusCode.OK);
            output.WriteLine("Historical journey stage: signed stock console and optional-scope controls passed.");

            // Real logins can update credential metadata. These snapshots bracket only
            // reconciliation, before any post-upgrade login can legitimately change it.
            var usernames = new[] { legacy.Username, mfaIdentity.Username, unrelated };
            var accountsBefore = await WarmAccountsAsync(admin, usernames);
            var graphBefore = await WarmGraphAsync(admin);
            var hubBefore = await WarmRealmStateAsync(hub);
            var first = await KeycloakReconciliationDriver.RunAsync(warm);
            first.Success.Should().BeTrue("closed historical reconciliation outcome: {0}/{1}", first.Stage, first.Reason);
            first.PreparationDispatched.Should().BeTrue();
            first.Mutations.Should().Be(17, "one preparation and sixteen ordinary writes; the eligible unrelated account needs no grant");
            output.WriteLine("Historical journey stage: automatic reconciliation completed.");
            var accountsAfter = await WarmAccountsAsync(admin, usernames);
            WarmSame(expectedProfile, WarmNode(await admin.GetAsync("users/profile"))).Should().BeTrue(
                "only the three canonical definitions are appended to the entire historical profile");
            await WarmAssertPreservationAsync(admin, accountsBefore, accountsAfter, unrelated);
            await WarmAssertGraphAsync(admin, graphBefore);
            WarmSame(hubBefore, await WarmRealmStateAsync(hub)).Should().BeTrue("the complete observed Hub state is preserved");
            WarmAssertPass(first, preparation: true);
            var second = await KeycloakReconciliationDriver.RunAsync(warm);
            second.Success.Should().BeTrue("closed explicit rerun outcome: {0}/{1}", second.Stage, second.Reason);
            second.Mutations.Should().Be(0);
            second.PreparationDispatched.Should().BeFalse();
            WarmAssertPass(second, preparation: false);
            var accountsRerun = await WarmAccountsAsync(admin, usernames);
            WarmSame(accountsAfter, accountsRerun).Should().BeTrue("an explicit zero-write pass preserves all captured account state");
            await WarmAssertGraphAsync(admin, graphBefore);
            output.WriteLine("Historical journey stage: exact preservation and zero-write rerun passed.");

            var tokenFlow = Stopwatch.StartNew();
            await WarmPasswordRefusedAsync(warm, legacy.Username, originalLegacyPassword);
            (await admin.GetAsync("attack-detection/brute-force/" + legacyPath)).GetProperty("disabled").GetBoolean()
                .Should().BeFalse("the old-password control must not lock the account before its positive login");
            await WarmMfaRefusalAsync(warm, mfaIdentity.Username, passwords[mfaIdentity.Username], otp);
            var auditCounts = await AuditCountsAsync();
            var reads = database.Observation.Reads;
            foreach (var identity in SeedData.IdentityUsers)
            {
                var password = passwords.GetValueOrDefault(identity.Username) ?? KeycloakFixture.DemoPassword;
                var tokens = await WarmWebTokensAsync(warm, identity, password,
                    identity.Username == mfaIdentity.Username ? otp : null);
                using var client = Client(identity.Tenant, out var observation, warm.RootCertificate, warm.Issuer);
                using var response = await Send(client, identity.Tenant, tokens.AccessToken);
                await AccountAsync(response, identity, observation);
                observation.Reads.Should().HaveCount(1);
            }
            database.Observation.Reads.Should().Be(reads + 4,
                "each genuine warm-provider persona reaches the physical ReadOnly learnstack_app transaction");
            (await AuditCountsAsync()).Should().Equal(auditCounts);
            output.WriteLine("Historical journey stage: four signed API personas passed physical ReadOnly application-role reads.");

            var legacyAfter = await WarmConsoleTokensAsync(warm, legacy.Username, changedPassword, true, 900);
            var unrelatedAfter = await WarmConsoleTokensAsync(warm, unrelated, KeycloakFixture.DemoPassword, true, 900);
            WarmAssertConsoleDelta(legacyBefore, legacyAfter, addedCapability: true);
            WarmAssertConsoleDelta(unrelatedBefore, unrelatedAfter, addedCapability: false);
            await WarmConsoleScopeEffectsAsync(warm, unrelated, unrelatedAfter, beforeUpgrade: false);
            await WarmExistingOfflineAsync(warm, unrelated, offlineBefore!);
            await WarmProfileActionsAsync(warm, admin, legacy.Username, legacyAfter.Exchange, legacy);
            await WarmProfileActionsAsync(warm, admin, unrelated, unrelatedAfter.Exchange, identity: null);
            await WarmWebTokensAsync(warm, legacy, changedPassword);
            await WarmNonmanifestWebTokensAsync(warm, unrelated);
            var unrelatedFresh = await WarmConsoleTokensAsync(warm, unrelated, KeycloakFixture.DemoPassword, true, 900);
            WarmSame(WarmClaims(unrelatedAfter.Access, ordinaryNameChanged: true), WarmClaims(unrelatedFresh.Access, ordinaryNameChanged: true))
                .Should().BeTrue("ordinary profile editing cannot add identity authority or other unexpected signed claims");
            WarmSame(WarmClaims(unrelatedAfter.Id, ordinaryNameChanged: true), WarmClaims(unrelatedFresh.Id, ordinaryNameChanged: true))
                .Should().BeTrue("fresh independently verified ID claims retain the same nonmanifest identity boundary");
            WarmSame(accountsAfter[unrelated]!["/role-mappings"],
                WarmNode(await admin.GetAsync(UserPath(await admin.UserAsync(unrelated)) + "/role-mappings")))
                .Should().BeTrue("nonmanifest grants remain unchanged after self-service too");
            var edited = await WarmAccountsAsync(admin, usernames);
            var afterSelfService = await KeycloakReconciliationDriver.RunAsync(warm);
            afterSelfService.Success.Should().BeTrue("closed post-self-service pass: {0}/{1}", afterSelfService.Stage, afterSelfService.Reason);
            afterSelfService.Mutations.Should().Be(0);
            afterSelfService.PreparationDispatched.Should().BeFalse();
            WarmAssertPass(afterSelfService, preparation: false);
            WarmSame(edited, await WarmAccountsAsync(admin, usernames)).Should().BeTrue(
                "the final explicit pass preserves ordinary edits, credentials, identity attributes and grants without login between snapshots");
            foreach (var username in new[] { legacy.Username, unrelated })
                (edited[username]![""]!["firstName"]!.GetValue<string>() == "Ordinary warm edit").Should().BeTrue();
            output.WriteLine("Historical live journey: personas=4; account POST/readback=2; protected fields=3 per subject; MFA before/after=true; wrong OTP before/after=true.");
            output.WriteLine("Costs seconds: fixture={0:F3}; token/API/profile={1:F3}; journey={2:F3}. Small owned fixture only; no worst-case capacity claim.",
                warm.StartupElapsed.TotalSeconds, tokenFlow.Elapsed.TotalSeconds, journey.Elapsed.TotalSeconds);
        }
        finally
        {
            await warm.DisposeAsync();
            await KeycloakFixture.AssertStockResourcesAbsentAsync(resources);
            output.WriteLine("Joined cleanup: two owned provider containers and one owned network independently absent=true.");
        }
    }

    private void WarmAssertPass(ReconciliationOutcome outcome, bool preparation)
    {
        outcome.Elapsed.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThan(TimeSpan.FromSeconds(120));
        outcome.Requests.Should().NotBeNull();
        var requests = outcome.Requests!;
        requests["discovery"].Should().Be(1);
        requests["version"].Should().Be(1);
        requests["authentication"].Should().BeInRange(1, 3);
        requests["initialPlan"].Should().BeGreaterThan(0);
        requests["prepare"].Should().Be(preparation ? 2 : 0);
        if (preparation) requests["freshPlan"].Should().BeGreaterThan(0);
        else requests["freshPlan"].Should().Be(0);
        requests["readback"].Should().BeGreaterThan(0);
        output.WriteLine("Reconciler finite observation: mutations={0}; preparation={1}; elapsed={2:F3}s; total requests={3}; phases={4}.",
            outcome.Mutations, outcome.PreparationDispatched, outcome.Elapsed.TotalSeconds, requests.Values.Sum(),
            string.Join(",", requests.Select(value => value.Key + "=" + value.Value.ToString(CultureInfo.InvariantCulture))));
    }

    private static async Task<JsonObject> WarmAccountsAsync(KeycloakAdminClient admin, IEnumerable<string> usernames)
    {
        var result = new JsonObject();
        foreach (var username in usernames)
        {
            var user = await admin.UserAsync(username);
            var values = new JsonObject();
            foreach (var suffix in new[] { "", "/credentials", "/role-mappings", "/groups" })
                values[suffix] = WarmNode(await admin.GetAsync(UserPath(user) + suffix));
            result[username] = values;
        }
        return result;
    }

    private static async Task WarmAssertPreservationAsync(KeycloakAdminClient admin, JsonObject before, JsonObject after, string unrelated)
    {
        foreach (var (username, previous) in before)
        {
            var current = after[username]!;
            (previous![""]!["id"]!.GetValue<string>() == current[""]!["id"]!.GetValue<string>()).Should().BeTrue();
            foreach (var suffix in new[] { "/credentials", "/groups" })
                WarmSame(previous[suffix], current[suffix]).Should().BeTrue("credential DTOs and membership survive without login between captures");
            var oldUser = previous[""]!.DeepClone().AsObject();
            var newUser = current[""]!.DeepClone().AsObject();
            if (username != unrelated)
            {
                foreach (var user in new[] { oldUser, newUser })
                    if (user["attributes"] is JsonObject attributes)
                    {
                        foreach (var name in WarmProtectedNames) attributes.Remove(name);
                        if (attributes.Count == 0) user.Remove("attributes");
                    }
            }
            WarmSame(oldUser, newUser).Should().BeTrue("all unrelated account fields are preserved");
            var grants = current["/role-mappings"]!.DeepClone().AsObject();
            if (username != unrelated)
            {
                var added = grants["clientMappings"]!["account"]!["mappings"]!.AsArray();
                added.Count.Should().Be(1);
                (added[0]!["name"]!.GetValue<string>() == "manage-account").Should().BeTrue();
                var role = await admin.GetAsync("clients/" + WarmId(await admin.ClientAsync("account")) + "/roles/manage-account");
                (added[0]!["id"]!.GetValue<string>() == WarmId(role)).Should().BeTrue();
                grants["clientMappings"]!.AsObject().Remove("account");
                if (grants["clientMappings"]!.AsObject().Count == 0) grants.Remove("clientMappings");
            }
            WarmSame(previous["/role-mappings"], grants).Should().BeTrue("only the declared manifest account capability may be added");
        }
        var mfa = after[SeedData.IdentityUsers[1].Username]!["/credentials"]!.AsArray();
        mfa.Count(value => value!["type"]!.GetValue<string>() == "otp").Should().Be(1);
        mfa.Count(value => value!["type"]!.GetValue<string>() == "password").Should().Be(1);
    }

    private static async Task<JsonObject> WarmAppendedProfileAsync(JsonElement before)
    {
        var expected = WarmNode(before).AsObject();
        var desired = JsonNode.Parse(await File.ReadAllTextAsync(KeycloakFixture.RealmPath("learnstack")))!;
        var definitions = JsonNode.Parse(desired["components"]!["org.keycloak.userprofile.UserProfileProvider"]![0]!
            ["config"]!["kc.user.profile.config"]![0]!.GetValue<string>())!["attributes"]!.AsArray();
        foreach (var name in WarmProtectedNames)
            expected["attributes"]!.AsArray().Add(definitions.Single(value => value!["name"]!.GetValue<string>() == name)!.DeepClone());
        return expected;
    }

    private static async Task<Dictionary<string, JsonElement>> WarmGraphAsync(KeycloakAdminClient admin)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        async Task<JsonElement> Read(string path)
        {
            if (result.TryGetValue(path, out var cached)) return cached;
            var value = await admin.GetAsync(path);
            result.Add(path, value);
            return value;
        }
        var clients = await Read("clients");
        var scopes = await Read("client-scopes");
        foreach (var scope in scopes.EnumerateArray())
            foreach (var suffix in new[] { "", "/protocol-mappers/models", "/scope-mappings" })
                await Read("client-scopes/" + WarmId(scope) + suffix);
        foreach (var client in clients.EnumerateArray())
            foreach (var category in new[] { "default", "optional" })
                await Read("clients/" + WarmId(client) + "/" + category + "-client-scopes");
        var account = await admin.ClientAsync("account");
        await Read("clients/" + WarmId(account));
        var consolePath = "clients/" + WarmId(await admin.ClientAsync("account-console"));
        foreach (var suffix in new[] { "", "/protocol-mappers/models", "/scope-mappings" }) await Read(consolePath + suffix);
        foreach (var (category, names) in new[]
        {
            ("default", new[] { "acr", "basic", "email", "profile", "roles", "web-origins" }),
            ("optional", new[] { "address", "microprofile-jwt", "offline_access", "organization", "phone" }),
        }) result[consolePath + "/" + category + "-client-scopes"].EnumerateArray()
            .Select(value => value.GetProperty("name").GetString()).Order(StringComparer.Ordinal).Should().Equal(names);
        var roles = await Read("roles");
        var accountRoles = await Read("clients/" + WarmId(account) + "/roles");
        var pending = new Queue<JsonElement>(roles.EnumerateArray().Concat(accountRoles.EnumerateArray()));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryDequeue(out var role))
        {
            if (!seen.Add(WarmId(role))) continue;
            seen.Count.Should().BeLessThanOrEqualTo(200);
            var path = "roles-by-id/" + WarmId(role);
            await Read(path);
            foreach (var child in (await Read(path + "/composites")).EnumerateArray()) pending.Enqueue(child);
        }
        await Read("groups");
        await Read("clients/" + WarmId(await admin.ClientAsync("learnstack-api")) + "/client-secret");
        return result;
    }

    private static async Task WarmAssertGraphAsync(KeycloakAdminClient admin, Dictionary<string, JsonElement> before)
    {
        var console = await admin.ClientAsync("account-console");
        var consolePath = "clients/" + WarmId(console);
        var desired = JsonNode.Parse(await File.ReadAllTextAsync(KeycloakFixture.RealmPath("learnstack")))!;
        var currentScopes = await admin.GetAsync("client-scopes");
        foreach (var (path, prior) in before)
        {
            var current = await admin.GetAsync(path);
            if (path == consolePath + "/protocol-mappers/models")
            {
                var afterMappers = WarmNode(current).AsArray();
                var owned = afterMappers.Single(mapper => mapper!["name"]!.GetValue<string>() == "learnstack-account-self-service-roles")!;
                var declared = desired["clients"]!.AsArray().Single(client => client!["clientId"]!.GetValue<string>() == "account-console")!
                    ["protocolMappers"]!.AsArray().Single(mapper => mapper!["name"]!.GetValue<string>() == "learnstack-account-self-service-roles")!;
                var expectedConfig = declared["config"]!.DeepClone().AsObject();
                expectedConfig.Remove("usermodel.clientRoleMapping.rolePrefix");
                WarmSame(expectedConfig, owned["config"]).Should().BeTrue("the sole new mapper is exactly the approved fixed-account emitter");
                (owned["protocol"]!.GetValue<string>() == "openid-connect"
                    && owned["protocolMapper"]!.GetValue<string>() == "oidc-usermodel-client-role-mapper").Should().BeTrue();
                afterMappers.Remove(owned);
                WarmSame(WarmNode(prior), afterMappers).Should().BeTrue("every preexisting direct console mapper survives unchanged");
                continue;
            }
            if (path == consolePath)
            {
                var expectedClient = WarmNode(prior).AsObject();
                var actualClient = WarmNode(current).AsObject();
                foreach (var client in new[] { expectedClient, actualClient }) client.Remove("protocolMappers");
                expectedClient["defaultClientScopes"] = new JsonArray(expectedClient["defaultClientScopes"]!.AsArray()
                    .Where(scope => scope!.GetValue<string>() != "roles").Select(scope => scope!.DeepClone()).ToArray());
                expectedClient["optionalClientScopes"] = new JsonArray(expectedClient["optionalClientScopes"]!.AsArray()
                    .Where(scope => scope!.GetValue<string>() is not ("microprofile-jwt" or "offline_access")).Select(scope => scope!.DeepClone()).ToArray());
                WarmSame(expectedClient, actualClient).Should().BeTrue("all console settings retain their exact preexisting values beyond the three edge removals");
                continue;
            }
            if (path is "clients" or "client-scopes")
            {
                var oldIds = prior.EnumerateArray().Select(WarmId).ToHashSet(StringComparer.Ordinal);
                var currentIds = current.EnumerateArray().Select(WarmId).ToHashSet(StringComparer.Ordinal);
                oldIds.IsSubsetOf(currentIds).Should().BeTrue("no preexisting client or scope is deleted or recreated");
                if (path == "clients") currentIds.SetEquals(oldIds).Should().BeTrue();
                else current.EnumerateArray().Where(scope => !oldIds.Contains(WarmId(scope)))
                    .Select(scope => scope.GetProperty("name").GetString()).Order(StringComparer.Ordinal)
                    .Should().Equal(desired["clientScopes"]!.AsArray().Select(scope => scope!["name"]!.GetValue<string>())
                        .Except(prior.EnumerateArray().Select(scope => scope.GetProperty("name").GetString()), StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal));
                continue;
            }
            if (path.EndsWith("-client-scopes", StringComparison.Ordinal))
            {
                var owner = before["clients"].EnumerateArray().Single(client => path.StartsWith("clients/" + WarmId(client) + "/", StringComparison.Ordinal));
                var expected = prior.EnumerateArray().Where(scope => path == consolePath + "/default-client-scopes"
                    ? scope.GetProperty("name").GetString() != "roles"
                    : path != consolePath + "/optional-client-scopes" || scope.GetProperty("name").GetString() is not ("microprofile-jwt" or "offline_access"))
                    .Select(scope => (Id: WarmId(scope), Name: scope.GetProperty("name").GetString())).ToHashSet();
                var clientId = owner.GetProperty("clientId").GetString();
                if (clientId is "learnstack-web" or "learnstack-api")
                {
                    var declared = desired["clients"]!.AsArray().Single(client => client!["clientId"]!.GetValue<string>() == clientId)!;
                    var category = path.EndsWith("/default-client-scopes", StringComparison.Ordinal) ? "defaultClientScopes" : "optionalClientScopes";
                    foreach (var name in declared[category]!.AsArray().Select(value => value!.GetValue<string>()))
                    {
                        var scope = currentScopes.EnumerateArray().Single(value => value.GetProperty("name").GetString() == name);
                        expected.Add((WarmId(scope), name));
                    }
                }
                var actual = current.EnumerateArray().Select(scope => (Id: WarmId(scope), Name: scope.GetProperty("name").GetString())).ToArray();
                (actual.Length == expected.Count && expected.SetEquals(actual)).Should().BeTrue(
                    "exact edge delta: three console removals and approved web/API additions; every other assignment is preserved");
                continue;
            }
            WarmSame(WarmNode(prior), WarmNode(current)).Should().BeTrue("global definitions, role graphs, account client, groups and API secret are preserved");
        }
        foreach (var (category, count) in new[] { ("default", 5), ("optional", 3) })
            (await admin.GetAsync(consolePath + "/" + category + "-client-scopes")).GetArrayLength().Should().Be(count);
    }

    private static async Task<JsonObject> WarmRealmStateAsync(KeycloakAdminClient admin)
    {
        var state = new JsonObject();
        foreach (var path in new[] { "", "users/profile", "clients", "client-scopes", "roles", "groups", "users?max=100&briefRepresentation=false" })
            state[path] = WarmNode(await admin.GetAsync(path));
        foreach (var user in state["users?max=100&briefRepresentation=false"]!.AsArray())
            foreach (var suffix in new[] { "", "/credentials", "/role-mappings", "/groups" })
            {
                var path = "users/" + user!["id"]!.GetValue<string>() + suffix;
                state[path] = WarmNode(await admin.GetAsync(path));
            }
        foreach (var client in state["clients"]!.AsArray())
            foreach (var suffix in new[] { "", "/protocol-mappers/models", "/scope-mappings", "/default-client-scopes", "/optional-client-scopes" })
            {
                var path = "clients/" + client!["id"]!.GetValue<string>() + suffix;
                state[path] = WarmNode(await admin.GetAsync(path));
            }
        return state;
    }

    private static async Task WarmPasswordRefusedAsync(KeycloakFixture fixture, string username, string password)
    {
        using var browser = fixture.Browser();
        var refusal = await browser.AuthorizeAsync(username, password, WarmConsoleCallback(fixture), clientId: "account-console", scope: "openid");
        refusal.Authorized.Should().BeFalse();
        refusal.Error.Should().Be("invalid_credentials");
    }

    private static async Task WarmMfaRefusalAsync(KeycloakFixture fixture, string username, string password, WarmTotp otp)
    {
        using (var browser = fixture.Browser())
        {
            var missing = await browser.AuthorizeAsync(username, password, WarmConsoleCallback(fixture), clientId: "account-console", scope: "openid");
            missing.Authorized.Should().BeFalse();
            missing.MfaChallenged.Should().BeTrue();
            missing.Error.Should().Be("mfa_challenge_required");
        }
        using var wrong = fixture.Browser();
        var refused = await wrong.AuthorizeAsync(username, password, WarmConsoleCallback(fixture), clientId: "account-console", scope: "openid",
            otpCode: () => Task.FromResult(otp.WrongCode()));
        refused.Authorized.Should().BeFalse();
        refused.MfaChallenged.Should().BeTrue();
        refused.Error.Should().Be("invalid_otp");
    }

    private static async Task<KeycloakTokenExchange> WarmWebTokensAsync(KeycloakFixture fixture, SeedIdentity identity, string password, WarmTotp? otp = null)
    {
        using var browser = fixture.Browser();
        var authorization = await browser.AuthorizeAsync(identity.Username, password, Callback(identity.Tenant), otpCode: otp is null ? null : otp.NextAsync);
        authorization.Authorized.Should().BeTrue("warm code/S256 authorization must complete for persona {0}: {1}",
            SeedData.IdentityUsers.IndexOf(identity), authorization.Error);
        authorization.MfaChallenged.Should().Be(otp is not null);
        var exchange = await browser.RedeemAsync(authorization);
        exchange.StatusCode.Should().Be(HttpStatusCode.OK);
        var access = await KeycloakTokenVerifier.ValidateAsync(fixture, exchange.AccessToken, "learnstack-api");
        var id = await KeycloakTokenVerifier.ValidateAsync(fixture, exchange.IdToken, "learnstack-web");
        WarmAccessHashMatches(id, exchange.AccessToken).Should().BeTrue("the signed ID-token hash binds the same independently verified access token");
        using var admin = await fixture.CreateAdminClientAsync();
        var account = await admin.UserAsync(identity.Username);
        foreach (var payload in new[] { access, id })
        {
            (payload.GetProperty("sub").GetString() == WarmId(account)).Should().BeTrue();
            WarmIdentityMatches(payload, identity).Should().BeTrue("fresh signed user/tenant/organization values agree exactly with typed SeedData");
            exchange.Window.AcceptsFresh(payload, 900).Should().BeTrue();
        }
        HasAudience(id, "learnstack-api").Should().BeFalse();
        return exchange;
    }

    private static async Task<WarmConsoleTokens> WarmConsoleTokensAsync(KeycloakFixture fixture, string username, string password,
        bool accountAudience, int lifespan, string scope = "openid", WarmTotp? otp = null)
    {
        using var browser = fixture.Browser();
        var authorization = await browser.AuthorizeAsync(username, password, WarmConsoleCallback(fixture), clientId: "account-console", scope: scope,
            otpCode: otp is null ? null : otp.NextAsync);
        authorization.Authorized.Should().BeTrue("console code/S256 authorization must complete: {0}", authorization.Error);
        authorization.MfaChallenged.Should().Be(otp is not null);
        var exchange = await browser.RedeemAsync(authorization, secret: null);
        exchange.StatusCode.Should().Be(HttpStatusCode.OK);
        var access = await KeycloakTokenVerifier.ValidateConsoleAsync(fixture, exchange.AccessToken, idToken: false, accountAudience: accountAudience);
        var id = await KeycloakTokenVerifier.ValidateConsoleAsync(fixture, exchange.IdToken, idToken: true);
        WarmAccessHashMatches(id, exchange.AccessToken).Should().BeTrue("the console ID-token hash binds this independently verified access token");
        foreach (var payload in new[] { access, id })
        {
            exchange.Window.AcceptsFresh(payload, lifespan).Should().BeTrue();
            WarmProtectedNames.All(name => !payload.TryGetProperty(name, out _)).Should().BeTrue(
                "the console mapper never invents LearnStack authority, including for nonmanifest users");
        }
        if (accountAudience)
        {
            WarmAudiences(access).Should().Equal("account");
            access.GetProperty("resource_access").EnumerateObject().Select(value => value.Name).Should().Equal("account");
            access.GetProperty("resource_access").GetProperty("account").GetProperty("roles").EnumerateArray()
                .Select(value => value.GetString()).Order(StringComparer.Ordinal).Should().Equal(WarmAccountRoles);
        }
        else
        {
            access.TryGetProperty("aud", out _).Should().BeFalse();
            access.TryGetProperty("resource_access", out _).Should().BeFalse();
        }
        id.TryGetProperty("resource_access", out _).Should().BeFalse();
        WarmAudiences(id).Should().Equal("account-console");
        return new WarmConsoleTokens(exchange, access, id);
    }

    private static async Task WarmNonmanifestWebTokensAsync(KeycloakFixture fixture, string username)
    {
        using var browser = fixture.Browser();
        var authorization = await browser.AuthorizeAsync(username, KeycloakFixture.DemoPassword, Callback(SeedData.English));
        authorization.Authorized.Should().BeTrue();
        var exchange = await browser.RedeemAsync(authorization);
        exchange.StatusCode.Should().Be(HttpStatusCode.OK);
        var access = await KeycloakTokenVerifier.ValidateAsync(fixture, exchange.AccessToken, "learnstack-api");
        var id = await KeycloakTokenVerifier.ValidateAsync(fixture, exchange.IdToken, "learnstack-web");
        WarmAccessHashMatches(id, exchange.AccessToken).Should().BeTrue();
        using var admin = await fixture.CreateAdminClientAsync();
        var user = await admin.UserAsync(username);
        foreach (var token in new[] { access, id })
        {
            (token.GetProperty("sub").GetString() == WarmId(user)).Should().BeTrue();
            exchange.Window.AcceptsFresh(token, 900).Should().BeTrue();
            WarmProtectedNames.All(name => !token.TryGetProperty(name, out _)).Should().BeTrue(
                "even the web client's real identity mappers do not invent authority for an eligible nonmanifest account");
        }
    }

    private static void WarmAssertConsoleDelta(WarmConsoleTokens before, WarmConsoleTokens after, bool addedCapability)
    {
        var expected = WarmClaims(before.Access);
        if (addedCapability)
        {
            expected["aud"] = "account";
            expected["resource_access"] = JsonSerializer.SerializeToNode(new
            { account = new { roles = WarmAccountRoles } });
        }
        WarmSame(expected, WarmClaims(after.Access)).Should().BeTrue(
            "the full signed console access claim set changes only by the approved account capability");
        WarmSame(WarmClaims(before.Id), WarmClaims(after.Id)).Should().BeTrue(
            "the full signed console ID claim set remains unchanged apart from fresh issuance/session fields");
    }

    private static async Task<KeycloakTokenExchange?> WarmConsoleScopeEffectsAsync(KeycloakFixture fixture, string username,
        WarmConsoleTokens ordinary, bool beforeUpgrade)
    {
        KeycloakTokenExchange? offline = null;
        foreach (var scope in new[] { "microprofile-jwt", "offline_access" })
        {
            if (!beforeUpgrade)
            {
                using var browser = fixture.Browser();
                var refused = await browser.AuthorizeAsync(username, KeycloakFixture.DemoPassword, WarmConsoleCallback(fixture),
                    clientId: "account-console", scope: "openid " + scope);
                refused.Authorized.Should().BeFalse("the detached optional scope cannot be requested through the console");
                refused.StatusCode.Should().Be(HttpStatusCode.Found);
                refused.Error.Should().Be("invalid_scope", "the registered callback receives the closed OAuth scope refusal with its state and issuer checked");
                continue;
            }
            var selected = await WarmConsoleTokensAsync(fixture, username, KeycloakFixture.DemoPassword, true, 1800, "openid " + scope);
            var expected = WarmClaims(ordinary.Access);
            var expectedId = WarmClaims(ordinary.Id);
            var actual = WarmClaims(selected.Access);
            if (scope == "microprofile-jwt")
            {
                (selected.Access.GetProperty("upn").GetString() == username).Should().BeTrue();
                expected["upn"] = username;
                expectedId["upn"] = username;
            }
            else
            {
                expected["realm_access"] = JsonSerializer.SerializeToNode(new { roles = WarmOfflineRole });
                // The token endpoint's refresh credential is checked only in memory.
                // Offline support is observed by an actual refresh exchange, not decoded without validation.
                string.IsNullOrEmpty(selected.Exchange.RefreshToken).Should().BeFalse();
                using var browser = fixture.Browser();
                var refreshed = await browser.RequestTokenAsync(new Dictionary<string, string>
                { ["grant_type"] = "refresh_token", ["client_id"] = "account-console", ["refresh_token"] = selected.Exchange.RefreshToken });
                refreshed.StatusCode.Should().Be(HttpStatusCode.OK);
                string.IsNullOrEmpty(refreshed.RefreshToken).Should().BeFalse(
                    "the later continuity observation must submit the nonempty credential returned by this successful refresh");
                offline = refreshed;
                var signed = await KeycloakTokenVerifier.ValidateConsoleAsync(fixture, refreshed.AccessToken, idToken: false);
                signed.GetProperty("scope").GetString()!.Split(' ').Should().Contain("offline_access");
                using var admin = await fixture.CreateAdminClientAsync();
                var user = await admin.UserAsync(username);
                var console = await admin.ClientAsync("account-console");
                (await admin.GetAsync(UserPath(user) + "/offline-sessions/" + WarmId(console))).GetArrayLength().Should().Be(1,
                    "the explicit scope really creates an owned offline session before its assignment is detached");
            }
            var expectedScopes = ordinary.Access.GetProperty("scope").GetString()!.Split(' ').Append(scope).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            selected.Access.GetProperty("scope").GetString()!.Split(' ').Order(StringComparer.Ordinal).Should().Equal(expectedScopes);
            expected.Remove("scope");
            actual.Remove("scope");
            WarmSame(expected, actual).Should().BeTrue("explicit optional selection has only its measured full signed claim delta");
            WarmSame(expectedId, WarmClaims(selected.Id)).Should().BeTrue(
                "the complete ID claim set observes the optional mapper's own ID-token setting");
        }
        return offline;
    }

    private async Task WarmExistingOfflineAsync(KeycloakFixture fixture, string username, KeycloakTokenExchange prior)
    {
        using var admin = await fixture.CreateAdminClientAsync();
        var user = await admin.UserAsync(username);
        var console = await admin.ClientAsync("account-console");
        var path = UserPath(user) + "/offline-sessions/" + WarmId(console);
        (await admin.GetAsync(path)).GetArrayLength().Should().Be(1,
            "detaching an optional scope does not authorize deleting the preexisting offline session");
        using var browser = fixture.Browser();
        var refreshed = await browser.RequestTokenAsync(new Dictionary<string, string>
        { ["grant_type"] = "refresh_token", ["client_id"] = "account-console", ["refresh_token"] = prior.RefreshToken });
        // Pin this fixture's observed provider behavior, without claiming that
        // scope detachment universally revokes previously issued credentials.
        refreshed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        refreshed.Error.Should().Be("invalid_grant");
        string.IsNullOrEmpty(refreshed.AccessToken).Should().BeTrue();
        string.IsNullOrEmpty(refreshed.IdToken).Should().BeTrue();
        (await admin.GetAsync(path)).GetArrayLength().Should().Be(1);
        output.WriteLine("Existing offline observation: session retained=true; refresh=invalid_grant; signed token not issued.");
    }

    private static async Task<HttpStatusCode> WarmAccountStatusAsync(KeycloakFixture fixture, KeycloakTokenExchange exchange)
    {
        using var account = WarmAccountClient(fixture, exchange);
        using var response = await KeycloakHttp.SendAsync(account, HttpMethod.Get, "?userProfileMetadata=true");
        return response.StatusCode;
    }

    private static async Task WarmProfileActionsAsync(KeycloakFixture fixture, KeycloakAdminClient admin, string username,
        KeycloakTokenExchange exchange, SeedIdentity? identity)
    {
        using var account = WarmAccountClient(fixture, exchange);
        var original = await WarmAccountReadAsync(account);
        WarmHidden(original).Should().BeTrue();
        var update = WarmAccountUpdate(original);
        update["firstName"] = "Ordinary warm edit";
        (await WarmAccountWriteAsync(account, update)).Should().Be(HttpStatusCode.NoContent);
        var stored = await admin.GetAsync(UserPath(await admin.UserAsync(username)));
        (stored.GetProperty("firstName").GetString() == "Ordinary warm edit").Should().BeTrue(
            "a separate authenticated admin GET proves the ordinary Account REST POST reached storage");
        WarmStoredIdentityMatches(stored, identity).Should().BeTrue();
        foreach (var name in WarmProtectedNames)
        {
            var payload = WarmAccountUpdate(await WarmAccountReadAsync(account));
            payload["attributes"]![name] = new JsonArray(Guid.NewGuid().ToString("D"));
            (await WarmAccountWriteAsync(account, payload)).Should().BeOneOf(HttpStatusCode.NoContent, HttpStatusCode.BadRequest, HttpStatusCode.Forbidden);
            var after = await admin.GetAsync(UserPath(await admin.UserAsync(username)));
            WarmSame(WarmNode(stored), WarmNode(after)).Should().BeTrue(
                "protected replacements cannot persist for either manifest or nonmanifest accounts; HTTP status alone is insufficient");
            WarmHidden(await WarmAccountReadAsync(account)).Should().BeTrue();
        }
    }

    private static HttpClient WarmAccountClient(KeycloakFixture fixture, KeycloakTokenExchange exchange)
    {
        var client = KeycloakHttp.Create(new Uri(fixture.BaseAddress, "realms/learnstack/account/"), fixture.RootCertificate);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", exchange.AccessToken);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static async Task<JsonElement> WarmAccountReadAsync(HttpClient account)
    {
        using var response = await KeycloakHttp.SendAsync(account, HttpMethod.Get, "?userProfileMetadata=true");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (response.Content.Headers.ContentType?.MediaType).Should().Be("application/json");
        using var json = await KeycloakHttp.ReadJsonAsync(response);
        return json.RootElement.Clone();
    }

    private static async Task<HttpStatusCode> WarmAccountWriteAsync(HttpClient account, JsonObject body)
    {
        using var response = await KeycloakHttp.SendAsync(account, HttpMethod.Post, "", JsonContent.Create(body));
        return response.StatusCode;
    }

    private static JsonObject WarmAccountUpdate(JsonElement account)
    {
        var result = WarmNode(account).AsObject();
        result.Remove("id");
        result.Remove("userProfileMetadata");
        result["attributes"] ??= new JsonObject();
        return result;
    }

    private static bool WarmHidden(JsonElement account) =>
        (!account.TryGetProperty("attributes", out var attributes) || WarmProtectedNames.All(name => !attributes.TryGetProperty(name, out _)))
        && account.TryGetProperty("userProfileMetadata", out var metadata)
        && metadata.TryGetProperty("attributes", out var definitions)
        && !definitions.EnumerateArray().Any(value => WarmProtectedNames.Contains(value.GetProperty("name").GetString(), StringComparer.Ordinal));

    private static bool WarmStoredIdentityMatches(JsonElement user, SeedIdentity? identity)
    {
        if (identity is null) return !user.TryGetProperty("attributes", out var absent)
            || WarmProtectedNames.All(name => !absent.TryGetProperty(name, out _));
        if (!user.TryGetProperty("attributes", out var attributes)) return false;
        bool One(string name, string expected) => attributes.TryGetProperty(name, out var values)
            && values.ValueKind == JsonValueKind.Array && values.GetArrayLength() == 1 && values[0].GetString() == expected;
        return One("user_id", identity.UserId.Value.ToString("D")) && One("tenant_id", identity.Tenant.TenantId.Value.ToString("D"))
            && (identity.Tenant.MapHostToDefaultOrganization ? One("organization_id", identity.Tenant.DefaultOrganization.OrganizationId.Value.ToString("D"))
                : !attributes.TryGetProperty("organization_id", out _));
    }

    private static bool WarmIdentityMatches(JsonElement payload, SeedIdentity identity)
    {
        bool Scalar(string name, string expected) => payload.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String && value.GetString() == expected;
        return Scalar("user_id", identity.UserId.Value.ToString("D")) && Scalar("tenant_id", identity.Tenant.TenantId.Value.ToString("D"))
            && (identity.Tenant.MapHostToDefaultOrganization ? Scalar("organization_id", identity.Tenant.DefaultOrganization.OrganizationId.Value.ToString("D"))
                : !payload.TryGetProperty("organization_id", out _));
    }

    private static JsonObject WarmClaims(JsonElement payload, bool ordinaryNameChanged = false)
    {
        var claims = WarmNode(payload).AsObject();
        foreach (var name in new[] { "exp", "iat", "jti", "sid", "nonce", "auth_time", "at_hash" }) claims.Remove(name);
        if (ordinaryNameChanged) foreach (var name in new[] { "name", "given_name" }) claims.Remove(name);
        if (claims["scope"] is JsonValue scope)
            claims["scope"] = string.Join(' ', scope.GetValue<string>().Split(' ').Order(StringComparer.Ordinal));
        if (claims["resource_access"] is JsonObject clients)
            foreach (var (_, client) in clients)
                if (client?["roles"] is JsonArray roles)
                    client["roles"] = new JsonArray(roles.Select(role => role!.GetValue<string>()).Order(StringComparer.Ordinal)
                        .Select(role => (JsonNode?)JsonValue.Create(role)).ToArray());
        return claims;
    }

    private static string[] WarmAudiences(JsonElement token) => !token.TryGetProperty("aud", out var audience) ? []
        : audience.ValueKind == JsonValueKind.String ? [audience.GetString()!]
        : audience.EnumerateArray().Select(value => value.GetString()!).Order(StringComparer.Ordinal).ToArray();
    private static bool WarmAccessHashMatches(JsonElement id, string accessToken)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(accessToken));
        var expected = Convert.ToBase64String(hash.AsSpan(0, hash.Length / 2)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return id.TryGetProperty("at_hash", out var actual) && actual.ValueKind == JsonValueKind.String && actual.GetString() == expected;
    }
    private static string WarmConsoleCallback(KeycloakFixture fixture) => new Uri(fixture.BaseAddress, "realms/learnstack/account/").AbsoluteUri;
    private static JsonNode WarmNode(JsonElement value) => JsonNode.Parse(value.GetRawText())!;
    private static string WarmId(JsonElement value) => value.GetProperty("id").GetString()!;
    private static bool WarmSame(JsonNode? first, JsonNode? second) => JsonNode.DeepEquals(first, second);

    private sealed class WarmConsoleTokens(KeycloakTokenExchange exchange, JsonElement access, JsonElement id)
    {
        internal KeycloakTokenExchange Exchange { get; } = exchange;
        internal JsonElement Access { get; } = access;
        internal JsonElement Id { get; } = id;
        public override string ToString() => "Signed console observations (credentials omitted)";
    }

    private sealed class WarmTotp
    {
        private readonly string _secret = Guid.NewGuid().ToString("N");
        private long _lastIssued = -1;
        internal JsonNode Credential() => JsonSerializer.SerializeToNode(new
        {
            type = "otp",
            userLabel = "Owned warm proof",
            secretData = JsonSerializer.Serialize(new { value = _secret }),
            credentialData = JsonSerializer.Serialize(new { subType = "totp", digits = 6, counter = 0, period = 30, algorithm = "HmacSHA1" }),
        })!;

        internal async Task<string> NextAsync()
        {
            var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
            if (counter <= _lastIssued)
            {
                var wait = DateTimeOffset.FromUnixTimeSeconds((_lastIssued + 1) * 30).AddMilliseconds(50) - DateTimeOffset.UtcNow;
                if (wait < TimeSpan.Zero || wait > TimeSpan.FromSeconds(31))
                    throw new InvalidOperationException("Owned OTP clock exceeded its one-period bound.");
                await Task.Delay(wait);
                counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
                if (counter <= _lastIssued) throw new InvalidOperationException("Owned OTP clock did not advance.");
            }
            _lastIssued = counter;
            return Code(counter);
        }

        internal string WrongCode()
        {
            var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
            var valid = Enumerable.Range(-2, 5).Select(offset => Code(counter + offset)).ToHashSet(StringComparer.Ordinal);
            return Enumerable.Range(0, 10).Select(value => value.ToString("D6", CultureInfo.InvariantCulture)).First(value => !valid.Contains(value));
        }

        private string Code(long counter)
        {
            Span<byte> input = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(input, counter);
            // HMAC-SHA1 is the pinned provider's TOTP algorithm, not a new password/signature choice.
#pragma warning disable CA5350 // Exercise the unchanged stock Keycloak TOTP protocol with an invocation-owned credential.
            var hash = HMACSHA1.HashData(Encoding.UTF8.GetBytes(_secret), input);
#pragma warning restore CA5350
            var offset = hash[^1] & 15;
            var number = BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset, 4)) & 0x7fffffff;
            return (number % 1000000).ToString("D6", CultureInfo.InvariantCulture);
        }
        public override string ToString() => "Owned OTP credential (secret omitted)";
    }
}
