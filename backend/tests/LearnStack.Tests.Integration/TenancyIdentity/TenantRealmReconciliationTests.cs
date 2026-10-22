using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Tests.Integration.Database;
using LearnStack.Tools.Seeder;
using Xunit;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>Actual shared tooling against owned HTTPS Keycloak, including a controlled pre-mapper import.</summary>
[Collection(SharedKeycloakRealm.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed partial class TenantRealmReconciliationTests(KeycloakFixture fresh)
{
    private const string ChangedPassword = "preserved-fixture-password";
    private const string ExtraUsername = "unmanaged-account@fixture.test";
    private static readonly string[] AccountSelfServiceRoles = ["manage-account", "manage-account-links"];

    [Fact]
    public async Task Committed_fresh_import_is_already_converged_and_repeated_runs_have_zero_writes()
    {
        using var admin = await fresh.CreateAdminClientAsync();
        (await admin.GetAsync()).GetProperty("registrationEmailAsUsername").GetBoolean().Should().BeTrue();
        var before = await StateAsync(admin);
        for (var pass = 0; pass < 3; pass++)
        {
            var result = await KeycloakReconciliationDriver.RunAsync(fresh);
            result.Success.Should().BeTrue("the real committed import satisfies the shared planner (closed outcome: {0}/{1})", result.Stage, result.Reason);
            result.Mutations.Should().Be(0);
        }
        (await StateAsync(admin) == before).Should().BeTrue("even zero-write reconciliation preserves provider state");
        await AssertLoginAsync(fresh, SeedData.IdentityUsers[0], KeycloakFixture.DemoPassword);
    }

    [Fact]
    public async Task Legacy_upgrade_resumes_durable_partial_writes_and_preserves_accounts_and_unrelated_state()
    {
        await using var fixture = await KeycloakFixture.StartAsync(await LegacyRealmAsync());
        using var admin = await fixture.CreateAdminClientAsync();
        using var hub = await fixture.CreateAdminClientAsync("learnstack-hub");
        (await admin.GetAsync()).GetProperty("registrationEmailAsUsername").GetBoolean().Should().BeFalse(
            "the warm realm permits preserving an email distinct from its stable login username");
        var firstIdentity = SeedData.IdentityUsers[0];
        var secondIdentity = SeedData.IdentityUsers[1];
        var beforeAdmin = await UserDetailsAsync(admin, firstIdentity.Username);
        var beforeLearner = await UserDetailsAsync(admin, secondIdentity.Username);
        var beforeExtra = await UserDetailsAsync(admin, ExtraUsername);
        var adminId = Id(beforeAdmin);
        var learnerId = Id(beforeLearner);
        var extraId = Id(beforeExtra);
        var adminRoles = await admin.GetAsync("users/" + adminId + "/role-mappings");
        var learnerRoles = await admin.GetAsync("users/" + learnerId + "/role-mappings");
        var learnerCredentials = await admin.GetAsync("users/" + learnerId + "/credentials");
        learnerCredentials.EnumerateArray().Any(value => value.GetProperty("type").GetString() == "otp")
            .Should().BeTrue("the preservation control contains an actual imported MFA credential");
        beforeLearner.GetProperty("enabled").GetBoolean().Should().BeFalse();
        beforeLearner.GetProperty("requiredActions").EnumerateArray().Any(value => value.GetString() == "UPDATE_PASSWORD")
            .Should().BeTrue();
        var extraCredentials = await admin.GetAsync("users/" + extraId + "/credentials");
        var extraRoles = await admin.GetAsync("users/" + extraId + "/role-mappings");
        var hubBefore = await StateAsync(hub);
        var profileBefore = await admin.GetAsync("users/profile");
        var apiBefore = await admin.ClientAsync("learnstack-api");
        var apiSecretBefore = await admin.GetAsync("clients/" + Id(apiBefore) + "/client-secret");
        apiBefore.GetProperty("authorizationServicesEnabled").GetBoolean().Should().BeTrue();
        var resourceServerPath = "clients/" + Id(apiBefore) + "/authz/resource-server/settings";
        var resourceServerBefore = await admin.GetAsync(resourceServerPath);
        resourceServerBefore.GetProperty("allowRemoteResourceManagement").GetBoolean().Should().BeFalse();
        (resourceServerBefore.GetProperty("policyEnforcementMode").GetString() == "PERMISSIVE").Should().BeTrue();
        (resourceServerBefore.GetProperty("decisionStrategy").GetString() == "AFFIRMATIVE").Should().BeTrue();
        var serviceAccountPath = "clients/" + Id(apiBefore) + "/service-account-user";
        var serviceAccountBefore = await admin.GetAsync("users/" + Id(await admin.GetAsync(serviceAccountPath)));
        var serviceAccountCredentials = await admin.GetAsync("users/" + Id(serviceAccountBefore) + "/credentials");
        var apiDefaultScopesPath = "clients/" + Id(apiBefore) + "/default-client-scopes";
        var apiDefaults = await admin.GetAsync(apiDefaultScopesPath);
        var serviceAccountScope = apiDefaults.EnumerateArray().Single(scope => scope.GetProperty("name").GetString() == "service_account");
        (await admin.DeleteAsync(apiDefaultScopesPath + "/" + Id(serviceAccountScope))).Should().Be(HttpStatusCode.NoContent);
        (await admin.GetAsync(apiDefaultScopesPath)).EnumerateArray().Any(scope => scope.GetProperty("name").GetString() == "service_account")
            .Should().BeFalse("the warm service-account scope customization is deliberate");
        var customScopeBefore = await ScopeAsync(admin, "unrelated-fixture-scope");

        // Positive password proof before the conversion uses the old public client's exact legacy callback.
        using (var browser = fixture.Browser())
        {
            var authorization = await browser.AuthorizeAsync(firstIdentity.Username, ChangedPassword,
                "http://localhost:3000/legacy-callback");
            authorization.Authorized.Should().BeTrue("the pre-upgrade account accepts its changed password");
            var exchange = await browser.RedeemAsync(authorization, secret: null);
            exchange.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        var adminCredentials = await admin.GetAsync("users/" + adminId + "/credentials");

        var original = await StateAsync(admin);
        var uncertain = await KeycloakReconciliationDriver.RunAsync(fixture, "lost_acknowledgement");
        AssertFailure(uncertain, "apply", "transport");
        uncertain.Mutations.Should().Be(1, "acknowledgement loss is injected after one real committed response closes");
        (await StateAsync(admin) != original).Should().BeTrue("the failed run left a durable partial mutation");
        var partial = await KeycloakReconciliationDriver.RunAsync(fixture, "partial_cancel");
        AssertFailure(partial, "apply", "cancelled");
        partial.Mutations.Should().Be(1);
        var resumed = await KeycloakReconciliationDriver.RunAsync(fixture);
        resumed.Success.Should().BeTrue("closed outcome: {0}/{1} after {2} mutating calls", resumed.Stage, resumed.Reason, resumed.Mutations);
        resumed.Mutations.Should().BeGreaterThan(0);
        await AssertOwnedScopeAndMapperRepairAsync(admin, fixture);
        var second = await KeycloakReconciliationDriver.RunAsync(fixture);
        var third = await KeycloakReconciliationDriver.RunAsync(fixture);
        second.Success.Should().BeTrue("closed outcome: {0}/{1}", second.Stage, second.Reason);
        third.Success.Should().BeTrue("closed outcome: {0}/{1}", third.Stage, third.Reason);
        second.Mutations.Should().Be(0);
        third.Mutations.Should().Be(0, "a completed third pass performs no mutating Admin REST call");

        // Independent typed declarations also cover the disabled learner, whose login cannot prove migration.
        foreach (var identity in SeedData.IdentityUsers) await AssertStoredIdentityAsync(admin, identity);
        AssertSamePreservedUser(beforeAdmin, await UserDetailsAsync(admin, firstIdentity.Username));
        AssertSamePreservedUser(beforeLearner, await UserDetailsAsync(admin, secondIdentity.Username));
        AssertSame(beforeExtra, await UserDetailsAsync(admin, ExtraUsername), "nonmanifest accounts are never filled in or rewritten");
        AssertSame(adminCredentials, await admin.GetAsync("users/" + adminId + "/credentials"), "the existing password credential remains unchanged");
        AssertSame(learnerCredentials, await admin.GetAsync("users/" + learnerId + "/credentials"), "password and MFA credentials remain unchanged");
        AssertSame(extraCredentials, await admin.GetAsync("users/" + extraId + "/credentials"), "nonmanifest credentials remain unchanged");
        await AssertOnlyAccountGrantAddedAsync(admin, adminId, adminRoles);
        await AssertOnlyAccountGrantAddedAsync(admin, learnerId, learnerRoles);
        AssertSame(extraRoles, await admin.GetAsync("users/" + extraId + "/role-mappings"), "nonmanifest roles remain unchanged");
        (await StateAsync(hub) == hubBefore).Should().BeTrue("the separate Hub realm and operator account are untouched");
        AssertSame(apiSecretBefore, await admin.GetAsync("clients/" + Id(apiBefore) + "/client-secret"), "the API service credential is retained");
        (await admin.ClientAsync("learnstack-api")).GetProperty("authorizationServicesEnabled").GetBoolean().Should().BeTrue();
        AssertSame(resourceServerBefore, await admin.GetAsync(resourceServerPath), "unrelated authorization resource-server settings survive client convergence");
        (Id(await admin.GetAsync(serviceAccountPath)) == Id(serviceAccountBefore)).Should().BeTrue("the API retains its existing service account");
        AssertSame(serviceAccountBefore, await admin.GetAsync("users/" + Id(serviceAccountBefore)), "the existing API service account state is retained");
        AssertSame(serviceAccountCredentials, await admin.GetAsync("users/" + Id(serviceAccountBefore) + "/credentials"), "the service-account credential state is retained");
        (await admin.GetAsync(apiDefaultScopesPath)).EnumerateArray().Any(scope => scope.GetProperty("name").GetString() == "service_account")
            .Should().BeFalse("unchanged service-account enablement must not reattach a deliberately removed scope");
        AssertSame(customScopeBefore, await ScopeAsync(admin, "unrelated-fixture-scope"), "unrelated client scope definitions are retained");
        await AssertUnrelatedConfigurationAsync(admin, profileBefore);
        await AssertManagedConfigurationAsync(admin);
        await AssertLoginAsync(fixture, firstIdentity, ChangedPassword, adminId);
        foreach (var identity in SeedData.IdentityUsers.Where(value => value.Tenant != firstIdentity.Tenant))
            await AssertLoginAsync(fixture, identity, KeycloakFixture.DemoPassword);
        using (var extraBrowser = fixture.Browser())
        {
            var authorization = await extraBrowser.AuthorizeAsync(ExtraUsername, ChangedPassword, Callback(firstIdentity));
            authorization.Authorized.Should().BeTrue("an unrelated account's password remains usable after upgrade");
            var exchange = await extraBrowser.RedeemAsync(authorization);
            exchange.StatusCode.Should().Be(HttpStatusCode.OK);
            var payload = await KeycloakTokenVerifier.ValidateAsync(fixture, exchange.AccessToken, "learnstack-api");
            (payload.GetProperty("sub").GetString() == extraId).Should().BeTrue();
            payload.TryGetProperty("user_id", out _).Should().BeFalse("reconciliation never invents an actor for nonmanifest accounts");
        }

        // The old create-only password cannot replace an existing account's changed credential.
        using var rejectedBrowser = fixture.Browser();
        var rejected = await rejectedBrowser.AuthorizeAsync(firstIdentity.Username, KeycloakFixture.DemoPassword, Callback(firstIdentity));
        rejected.Authorized.Should().BeFalse();
        rejected.Error.Should().Be("invalid_credentials");
    }

    [Fact]
    public async Task Uninterrupted_legacy_upgrade_preserves_nonmanifest_actor_and_account_state()
    {
        var unrelatedActor = Guid.NewGuid().ToString("D");
        SeedData.IdentityUsers.All(identity => identity.UserId.Value.ToString("D") != unrelatedActor).Should().BeTrue();
        var realm = JsonNode.Parse(await LegacyRealmAsync())!.AsObject();
        var importedExtra = realm["users"]!.AsArray().Single(value => value!["username"]!.GetValue<string>() == ExtraUsername)!;
        importedExtra["attributes"]!["user_id"] = new JsonArray(unrelatedActor);
        await using var fixture = await KeycloakFixture.StartAsync(realm.ToJsonString());
        using var admin = await fixture.CreateAdminClientAsync();
        var defaultGroupId = await CreateGroupAsync(admin, "fixture-create-only-default",
            new Dictionary<string, string[]> { ["fixture_note"] = ["preserved-default-group"] });
        (await admin.PutAsync("default-groups/" + defaultGroupId, new { })).Should().Be(HttpStatusCode.NoContent);
        var defaultsBefore = await admin.GetAsync("default-groups");
        var defaultGroupBefore = await admin.GetAsync("groups/" + defaultGroupId);
        var before = await UserDetailsAsync(admin, ExtraUsername);
        var stableId = Id(before);
        (before.GetProperty("attributes").GetProperty("user_id")[0].GetString() == unrelatedActor).Should().BeTrue();
        var credentialsBefore = await admin.GetAsync("users/" + stableId + "/credentials");
        var rolesBefore = await admin.GetAsync("users/" + stableId + "/role-mappings");

        var first = await KeycloakReconciliationDriver.RunAsync(fixture);
        var after = await UserDetailsAsync(admin, ExtraUsername);
        var credentialsAfter = await admin.GetAsync("users/" + stableId + "/credentials");
        var rolesAfter = await admin.GetAsync("users/" + stableId + "/role-mappings");
        static bool Same(JsonElement left, JsonElement right) =>
            JsonNode.DeepEquals(JsonNode.Parse(left.GetRawText()), JsonNode.Parse(right.GetRawText()));
        var accountPreserved = Same(before, after);
        var attributesPreserved = Same(before.GetProperty("attributes"), after.GetProperty("attributes"));
        var actorPreserved = after.GetProperty("attributes").GetProperty("user_id")[0].GetString() == unrelatedActor;
        var credentialsPreserved = Same(credentialsBefore, credentialsAfter);
        var rolesPreserved = Same(rolesBefore, rolesAfter);
        var explicitRetry = await KeycloakReconciliationDriver.RunAsync(fixture);
        explicitRetry.Success.Should().BeTrue("the explicit new pass reports {0}/{1}", explicitRetry.Stage, explicitRetry.Reason);
        explicitRetry.Mutations.Should().Be(0, "the uninterrupted first pass already committed its managed changes");

        first.Success.Should().BeTrue("closed first outcome: {0}/{1} after {2} mutating calls; account/attributes/actor/credentials/roles preserved: {3}/{4}/{5}/{6}/{7}; explicit retry made zero writes",
            first.Stage, first.Reason, first.Mutations, accountPreserved, attributesPreserved, actorPreserved, credentialsPreserved, rolesPreserved);
        first.Mutations.Should().BeGreaterThan(0);
        accountPreserved.Should().BeTrue();
        attributesPreserved.Should().BeTrue();
        actorPreserved.Should().BeTrue();
        credentialsPreserved.Should().BeTrue();
        rolesPreserved.Should().BeTrue();
        (Id(after) == stableId).Should().BeTrue();
        AssertSame(defaultsBefore, await admin.GetAsync("default-groups"), "create-only import preserves configured realm defaults");
        AssertSame(defaultGroupBefore, await admin.GetAsync("groups/" + defaultGroupId), "the configured default group is not changed");
        foreach (var identity in SeedData.IdentityUsers.Where(identity => identity.Tenant != SeedData.IdentityUsers[0].Tenant))
        {
            var created = await UserDetailsAsync(admin, identity.Username);
            (await admin.GetAsync("users/" + Id(created) + "/groups?briefRepresentation=true")).GetArrayLength().Should().Be(0,
                "atomic create-only import suppresses automatic default-group membership");
        }
        var third = await KeycloakReconciliationDriver.RunAsync(fixture);
        third.Success.Should().BeTrue("the completed third pass reports {0}/{1}", third.Stage, third.Reason);
        third.Mutations.Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Group_carried_identity_requires_a_no_write_refusal(bool aggregateActor)
    {
        await using var fixture = await KeycloakFixture.StartAsync();
        using var admin = await fixture.CreateAdminClientAsync();
        var identity = SeedData.IdentityUsers[0];
        var user = await UserDetailsAsync(admin, identity.Username);
        var userId = Id(user);
        const string groupActor = "10000000-0000-7000-8000-000000000001";
        var groupOrganization = Guid.NewGuid().ToString("D");
        var groupName = aggregateActor ? "fixture-aggregate-identity" : "fixture-fallback-identity";
        var groupAttributes = aggregateActor
            ? new Dictionary<string, string[]> { ["user_id"] = [groupActor] }
            : new Dictionary<string, string[]> { ["organization_id"] = [groupOrganization] };
        var groupId = await CreateGroupAsync(admin, groupName, groupAttributes);
        (await admin.PutAsync("users/" + userId + "/groups/" + groupId, new { })).Should().Be(HttpStatusCode.NoContent);
        var scopes = await admin.GetAsync("client-scopes");
        var scopeId = Id(scopes.EnumerateArray().Single(value => value.GetProperty("name").GetString() == "learnstack-tenant-api"));
        var mapperPath = "client-scopes/" + scopeId + "/protocol-mappers/models";
        var mappers = await admin.GetAsync(mapperPath);
        var claim = aggregateActor ? "user_id" : "organization_id";
        var mapper = Object(mappers.EnumerateArray().Single(value => value.GetProperty("config").TryGetProperty("claim.name", out var claimName)
            && claimName.GetString() == claim));
        mapper["config"]!["aggregate.attrs"] = aggregateActor ? "true" : "false";
        (await admin.PutAsync(mapperPath + "/" + mapper["id"]!.GetValue<string>(), mapper)).Should().Be(HttpStatusCode.NoContent);
        var groupBefore = await admin.GetAsync("groups/" + groupId);
        var membershipsBefore = await admin.GetAsync("users/" + userId + "/groups?briefRepresentation=false&max=100");
        var before = await StateAsync(admin);
        var outcome = await KeycloakReconciliationDriver.RunAsync(fixture);
        var unchanged = await StateAsync(admin) == before;
        AssertSame(groupBefore, await admin.GetAsync("groups/" + groupId), "reconciliation never changes group attributes");
        AssertSame(membershipsBefore, await admin.GetAsync("users/" + userId + "/groups?briefRepresentation=false&max=100"), "reconciliation never changes group membership");
        using var browser = fixture.Browser();
        var authorization = await browser.AuthorizeAsync(identity.Username, KeycloakFixture.DemoPassword, Callback(identity));
        authorization.Authorized.Should().BeTrue("the isolated group control completes actual code/S256 authorization");
        var exchange = await browser.RedeemAsync(authorization);
        exchange.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = await KeycloakTokenVerifier.ValidateAsync(fixture, exchange.AccessToken, "learnstack-api");
        var actorWrong = token.GetProperty("user_id").GetString() != identity.UserId.Value.ToString("D");
        var actorFromGroup = token.GetProperty("user_id").GetString() == groupActor;
        var unexpectedOrganization = token.TryGetProperty("organization_id", out var organization);
        var organizationFromGroup = unexpectedOrganization && organization.GetString() == groupOrganization;
        actorWrong.Should().Be(aggregateActor, "the signed token demonstrates the pinned mapper's group resolution");
        actorFromGroup.Should().Be(aggregateActor);
        unexpectedOrganization.Should().Be(!aggregateActor);
        organizationFromGroup.Should().Be(!aggregateActor);
        outcome.Success.Should().BeFalse("closed outcome: {0}/{1} after {2} mutating calls; signed actor wrong/from group: {3}/{4}; organization unexpected/from group: {5}/{6}",
            outcome.Stage ?? "success", outcome.Reason ?? "none", outcome.Mutations, actorWrong, actorFromGroup, unexpectedOrganization, organizationFromGroup);
        outcome.Stage.Should().Be("plan");
        outcome.Reason.Should().Be(aggregateActor ? "mapper_conflict" : "identity_conflict");
        outcome.Mutations.Should().Be(0);
        unchanged.Should().BeTrue();
    }

    [Fact]
    public async Task Unrelated_nested_groups_converge_and_preserve_membership_and_strict_token_identity()
    {
        await using var fixture = await KeycloakFixture.StartAsync();
        using var admin = await fixture.CreateAdminClientAsync();
        (await admin.PutAsync("", new { accessTokenLifespan = 1800 })).Should().Be(HttpStatusCode.NoContent);
        var parentId = await CreateGroupAsync(admin, "fixture-unrelated-parent",
            new Dictionary<string, string[]> { ["fixture_note"] = ["preserved-parent"] });
        var childId = await CreateGroupAsync(admin, "fixture-unrelated-child",
            new Dictionary<string, string[]> { ["fixture_note"] = ["preserved-child"] }, parentId);
        var identity = SeedData.IdentityUsers[0];
        var userBefore = await UserDetailsAsync(admin, identity.Username);
        var userId = Id(userBefore);
        (await admin.PutAsync("users/" + userId + "/groups/" + childId, new { })).Should().Be(HttpStatusCode.NoContent);
        userBefore = await UserDetailsAsync(admin, identity.Username);
        var parentBefore = await admin.GetAsync("groups/" + parentId);
        var childBefore = await admin.GetAsync("groups/" + childId);
        var membershipsBefore = await admin.GetAsync("users/" + userId + "/groups?briefRepresentation=true");
        var credentialsBefore = await admin.GetAsync("users/" + userId + "/credentials");
        var rolesBefore = await admin.GetAsync("users/" + userId + "/role-mappings");
        var first = await KeycloakReconciliationDriver.RunAsync(fixture);
        first.Success.Should().BeTrue("closed outcome: {0}/{1} after {2} mutating calls", first.Stage, first.Reason, first.Mutations);
        first.Mutations.Should().Be(1);
        var second = await KeycloakReconciliationDriver.RunAsync(fixture);
        second.Success.Should().BeTrue("closed outcome: {0}/{1}", second.Stage, second.Reason);
        second.Mutations.Should().Be(0);
        AssertSame(parentBefore, await admin.GetAsync("groups/" + parentId), "the unrelated parent group is preserved");
        AssertSame(childBefore, await admin.GetAsync("groups/" + childId), "the unrelated nested group is preserved");
        AssertSame(membershipsBefore, await admin.GetAsync("users/" + userId + "/groups?briefRepresentation=true"), "nested membership is preserved");
        AssertSame(userBefore, await UserDetailsAsync(admin, identity.Username), "the member's stored account is preserved");
        AssertSame(credentialsBefore, await admin.GetAsync("users/" + userId + "/credentials"), "the member's credentials are preserved");
        AssertSame(rolesBefore, await admin.GetAsync("users/" + userId + "/role-mappings"), "the member's role assignments are preserved");
        await AssertLoginAsync(fixture, identity, KeycloakFixture.DemoPassword, userId);
    }

    [Fact]
    public async Task Retained_cached_organization_groups_do_not_override_strict_user_identity()
    {
        await using var fixture = await KeycloakFixture.StartAsync();
        using var admin = await fixture.CreateAdminClientAsync();
        var identity = SeedData.IdentityUsers[0];
        var user = await UserDetailsAsync(admin, identity.Username);
        var userId = Id(user);
        using var establishedBrowser = fixture.Browser();
        var authorization = await establishedBrowser.AuthorizeAsync(identity.Username, KeycloakFixture.DemoPassword, Callback(identity));
        authorization.Authorized.Should().BeTrue("the ordinary code/S256 session is established before enabling Organizations");
        var session = await establishedBrowser.RedeemAsync(authorization);
        session.StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.PutAsync("", new { organizationsEnabled = true })).Should().Be(HttpStatusCode.NoContent);
        const string organizationName = "Fixture retained organization";
        var canaries = new Dictionary<string, string[]>
        {
            ["user_id"] = ["10000000-0000-7000-8000-000000000001"],
            ["tenant_id"] = [Guid.NewGuid().ToString("D")],
            ["organization_id"] = [Guid.NewGuid().ToString("D")],
        };
        (await admin.PostAsync("organizations", new { name = organizationName, alias = "fixture-retained-organization", attributes = canaries }))
            .Should().Be(HttpStatusCode.Created);
        var organizations = await admin.GetAsync("organizations?max=100");
        var organizationId = Id(organizations.EnumerateArray().Single(value => value.GetProperty("name").GetString() == organizationName));
        (await admin.PostAsync("organizations/" + organizationId + "/members", userId)).Should().Be(HttpStatusCode.Created);
        var verifiedMember = await admin.GetAsync("organizations/" + organizationId + "/members/" + userId);
        (Id(verifiedMember) == userId).Should().BeTrue("the enabled organization API verifies the exact member");
        var organizationBefore = await admin.GetAsync("organizations/" + organizationId);
        foreach (var canary in canaries)
            (organizationBefore.GetProperty("attributes").GetProperty(canary.Key)[0].GetString() == canary.Value[0]).Should().BeTrue();
        var membershipPath = "users/" + userId + "/groups?briefRepresentation=true";
        (await admin.GetAsync(membershipPath)).GetArrayLength().Should().Be(0, "the pinned cached-user stream excludes organization groups");
        var enabledExchange = await establishedBrowser.RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = "learnstack-web",
            ["client_secret"] = KeycloakFixture.WebClientSecret,
            ["refresh_token"] = session.RefreshToken,
        });
        enabledExchange.StatusCode.Should().Be(HttpStatusCode.OK);
        var enabledToken = await KeycloakTokenVerifier.ValidateAsync(fixture, enabledExchange.AccessToken, "learnstack-api");
        (enabledToken.GetProperty("user_id").GetString() == identity.UserId.Value.ToString("D")).Should().BeTrue();
        (enabledToken.GetProperty("tenant_id").GetString() == identity.Tenant.TenantId.Value.ToString("D")).Should().BeTrue();
        enabledToken.TryGetProperty("organization_id", out _).Should().BeFalse();
        (enabledToken.GetProperty("sub").GetString() == userId).Should().BeTrue();
        enabledExchange.Window.AcceptsFresh(enabledToken, 900).Should().BeTrue();
        (await admin.PutAsync("", new { organizationsEnabled = false })).Should().Be(HttpStatusCode.NoContent);
        (await admin.GetAsync()).GetProperty("organizationsEnabled").GetBoolean().Should().BeFalse();
        (await admin.GetAsync(membershipPath)).GetArrayLength().Should().Be(0, "disabling the feature retains the same cached-user group resolution");
        await AssertLoginAsync(fixture, identity, KeycloakFixture.DemoPassword, userId);
        (await admin.PutAsync("", new { accessTokenLifespan = 1800 })).Should().Be(HttpStatusCode.NoContent);
        var memberBefore = await UserDetailsAsync(admin, identity.Username);
        var credentialsBefore = await admin.GetAsync("users/" + userId + "/credentials");
        var rolesBefore = await admin.GetAsync("users/" + userId + "/role-mappings");
        var outcome = await KeycloakReconciliationDriver.RunAsync(fixture);
        outcome.Success.Should().BeTrue("closed outcome: {0}/{1} after {2} mutating calls", outcome.Stage, outcome.Reason, outcome.Mutations);
        outcome.Mutations.Should().Be(1);
        var repeat = await KeycloakReconciliationDriver.RunAsync(fixture);
        repeat.Success.Should().BeTrue("closed outcome: {0}/{1}", repeat.Stage, repeat.Reason);
        repeat.Mutations.Should().Be(0);
        (await admin.GetAsync()).GetProperty("organizationsEnabled").GetBoolean().Should().BeFalse();
        (await admin.GetAsync(membershipPath)).GetArrayLength().Should().Be(0);
        AssertSame(memberBefore, await UserDetailsAsync(admin, identity.Username), "the member's stored account is preserved");
        AssertSame(credentialsBefore, await admin.GetAsync("users/" + userId + "/credentials"), "the member's credentials are preserved");
        AssertSame(rolesBefore, await admin.GetAsync("users/" + userId + "/role-mappings"), "the member's role mappings are preserved");
        // Test-owned visibility restoration occurs after the reconciliation results and preservation checks.
        (await admin.PutAsync("", new { organizationsEnabled = true })).Should().Be(HttpStatusCode.NoContent);
        AssertSame(organizationBefore, await admin.GetAsync("organizations/" + organizationId), "stored organization canaries are retained");
        AssertSame(verifiedMember, await admin.GetAsync("organizations/" + organizationId + "/members/" + userId), "the organization membership is retained");
        (await admin.PutAsync("", new { organizationsEnabled = false })).Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Identity_mapper_secret_and_duplicate_lookup_conflicts_refuse_before_the_first_write()
    {
        var identity = SeedData.IdentityUsers[0];
        await using (var fixture = await KeycloakFixture.StartAsync())
        {
            using var admin = await fixture.CreateAdminClientAsync();
            // A legitimate owned change is pending, so zero writes proves complete planning precedes apply.
            (await admin.PutAsync("", new { accessTokenLifespan = 1800 })).Should().Be(HttpStatusCode.NoContent);
            var user = await admin.UserAsync(identity.Username);
            var changedUser = Object(user);
            changedUser["attributes"]!["user_id"] = new JsonArray(Guid.NewGuid().ToString("D"));
            (await admin.PutAsync("users/" + Id(user), changedUser)).Should().Be(HttpStatusCode.NoContent);
            await AssertRefusesWithoutWriteAsync(fixture, admin, "identity_conflict");
            (await admin.PutAsync("users/" + Id(user), Object(user))).Should().Be(HttpStatusCode.NoContent);

            var web = await admin.ClientAsync("learnstack-web");
            var directMapper = new JsonObject
            {
                ["name"] = "unowned-identity-collision",
                ["protocol"] = "openid-connect",
                ["protocolMapper"] = "oidc-usermodel-attribute-mapper",
                ["config"] = new JsonObject
                {
                    ["user.attribute"] = "other-identity",
                    ["claim.name"] = "user_id",
                    ["jsonType.label"] = "String",
                    ["access.token.claim"] = "true",
                },
            };
            var mapperPath = "clients/" + Id(web) + "/protocol-mappers/models";
            (await admin.PostAsync(mapperPath, directMapper)).Should().Be(HttpStatusCode.Created);
            await AssertRefusesWithoutWriteAsync(fixture, admin, "mapper_conflict");
            var mappers = await admin.GetAsync(mapperPath);
            var collision = mappers.EnumerateArray().Single(value => value.GetProperty("name").GetString() == "unowned-identity-collision");
            (await admin.DeleteAsync(mapperPath + "/" + Id(collision))).Should().Be(HttpStatusCode.NoContent);

            var changedWeb = Object(web);
            changedWeb["secret"] = "different-private-fixture-source";
            (await admin.PutAsync("clients/" + Id(web), changedWeb)).Should().Be(HttpStatusCode.NoContent);
            await AssertRefusesWithoutWriteAsync(fixture, admin, "secret_conflict");
            changedWeb["secret"] = KeycloakFixture.WebClientSecret;
            (await admin.PutAsync("clients/" + Id(web), changedWeb)).Should().Be(HttpStatusCode.NoContent);
            await AssertRefusesWithoutWriteAsync(fixture, admin, "identity_conflict", "duplicate_lookup");

            var clean = await KeycloakReconciliationDriver.RunAsync(fixture);
            clean.Success.Should().BeTrue("removing each planted conflict restores convergence (closed outcome: {0}/{1})", clean.Stage, clean.Reason);
            clean.Mutations.Should().BeGreaterThan(0);
            (await admin.GetAsync()).GetProperty("accessTokenLifespan").GetInt32().Should().Be(900);
            await AssertLoginAsync(fixture, identity, KeycloakFixture.DemoPassword);
        }

        // Under email-as-username, the imported distinct email produces a conflicting
        // provider username projection. Preserve that account and refuse before writes.
        await using var inconsistent = await KeycloakFixture.StartAsync(await LegacyRealmAsync(registrationEmailAsUsername: true));
        using var inconsistentAdmin = await inconsistent.CreateAdminClientAsync();
        (await inconsistentAdmin.GetAsync()).GetProperty("accessTokenLifespan").GetInt32().Should().Be(1800,
            "the negative control has a legitimate pending owned change");
        var existing = await inconsistentAdmin.UserAsync(identity.Username);
        (existing.GetProperty("username").GetString() == identity.Username).Should().BeFalse(
            "the negative control exercises an actual provider username disagreement");
        var stableId = Id(existing);
        await AssertRefusesWithoutWriteAsync(inconsistent, inconsistentAdmin, "identity_conflict");
        (Id(await inconsistentAdmin.UserAsync(identity.Username)) == stableId).Should().BeTrue();
        (await inconsistentAdmin.GetAsync()).GetProperty("registrationEmailAsUsername").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData("unmanaged_disabled")]
    [InlineData("managed_admin_hidden")]
    public Task Hidden_stored_identity_refuses_before_ordinary_writes_and_preserves_the_conflicting_actor(string visibility) =>
        AssertUnsafeIdentityProfileRefusesAsync(visibility, hiddenConflict: true);

    [Theory]
    [InlineData("managed_selector")]
    [InlineData("managed_default")]
    public Task Projected_identity_profile_configuration_refuses_before_any_pending_write(string visibility) =>
        AssertUnsafeIdentityProfileRefusesAsync(visibility, hiddenConflict: false);

    private static async Task AssertUnsafeIdentityProfileRefusesAsync(string visibility, bool hiddenConflict)
    {
        var identity = SeedData.IdentityUsers[0];
        var storedActor = hiddenConflict ? Guid.NewGuid().ToString("D") : identity.UserId.Value.ToString("D");
        var realm = JsonNode.Parse(await File.ReadAllTextAsync(KeycloakFixture.RealmPath("learnstack")))!.AsObject();
        realm["accessTokenLifespan"] = 1800;
        var profileValue = realm["components"]!["org.keycloak.userprofile.UserProfileProvider"]![0]!["config"]!["kc.user.profile.config"]!;
        var profile = JsonNode.Parse(profileValue[0]!.GetValue<string>())!.AsObject();
        var definitions = profile["attributes"]!.AsArray();
        var identityDefinitions = definitions.Where(value => new[] { "user_id", "tenant_id", "organization_id" }
            .Contains(value!["name"]!.GetValue<string>(), StringComparer.Ordinal)).Select(value => value!.DeepClone()).ToArray();
        if (visibility == "unmanaged_disabled")
        {
            foreach (var definition in identityDefinitions)
                definitions.Remove(definitions.Single(value => value!["name"]!.GetValue<string>() == definition["name"]!.GetValue<string>()));
            profile["unmanagedAttributePolicy"] = null;
        }
        else if (visibility == "managed_admin_hidden")
        {
            foreach (var definition in definitions.Where(value => identityDefinitions.Any(identityDefinition =>
                identityDefinition["name"]!.GetValue<string>() == value!["name"]!.GetValue<string>())))
                definition!["permissions"] = new JsonObject
                {
                    ["view"] = new JsonArray("user"),
                    ["edit"] = new JsonArray("user"),
                };
            profile["unmanagedAttributePolicy"] = "ENABLED";
        }
        else
        {
            var actorDefinition = definitions.Single(value => value!["name"]!.GetValue<string>() == "user_id")!;
            if (visibility == "managed_selector")
                actorDefinition["selector"] = new JsonObject { ["scopes"] = new JsonArray("profile") };
            else actorDefinition["defaultValue"] = storedActor;
        }
        profileValue[0] = profile.ToJsonString();
        var importedUser = realm["users"]!.AsArray().Single(value => value!["username"]!.GetValue<string>() == identity.Username)!;
        importedUser["attributes"]!["user_id"] = new JsonArray(storedActor);

        await using var fixture = await KeycloakFixture.StartAsync(realm.ToJsonString());
        using var admin = await fixture.CreateAdminClientAsync();
        (await admin.GetAsync()).GetProperty("accessTokenLifespan").GetInt32().Should().Be(1800,
            "a legitimate owned change remains pending behind the identity visibility guard");
        var hidden = await UserDetailsAsync(admin, identity.Username);
        var stableId = Id(hidden);
        var actorVisible = hidden.TryGetProperty("attributes", out var hiddenAttributes) && hiddenAttributes.TryGetProperty("user_id", out _);
        if (hiddenConflict) actorVisible.Should().BeFalse("the pinned provider actually hides the stored conflicting identity");
        else
        {
            actorVisible.Should().BeTrue("a correct visible actor isolates the unsupported identity profile configuration");
            (hiddenAttributes.GetProperty("user_id")[0].GetString() == storedActor).Should().BeTrue();
        }
        var preparationExpected = visibility == "unmanaged_disabled";
        var profileBefore = await admin.GetAsync("users/profile");
        var ordinaryBefore = preparationExpected ? await PreparationOrdinaryStateAsync(admin) : null;
        var before = await StateAsync(admin);
        var outcome = await KeycloakReconciliationDriver.RunAsync(fixture);
        var after = await StateAsync(admin);
        if (preparationExpected)
        {
            AssertPreparationProfile(await AppendedPreparationProfileAsync(profileBefore), await admin.GetAsync("users/profile"));
            (await PreparationOrdinaryStateAsync(admin) == ordinaryBefore).Should().BeTrue(
                "the fresh-plan actor conflict allows only the exceptional metadata preparation");
        }

        // Test-owned inspection starts only after the reconciler outcome and its state snapshot.
        var visibleProfile = Object(await admin.GetAsync("users/profile"));
        var visibleDefinitions = visibleProfile["attributes"]!.AsArray();
        foreach (var definition in identityDefinitions)
        {
            var existing = visibleDefinitions.SingleOrDefault(value => value!["name"]!.GetValue<string>() == definition["name"]!.GetValue<string>());
            if (existing is not null) visibleDefinitions.Remove(existing);
            visibleDefinitions.Add(definition.DeepClone());
        }
        (await admin.PutAsync("users/profile", visibleProfile)).Should().Be(HttpStatusCode.OK);
        var revealed = await admin.GetAsync("users/" + stableId);
        var storedActorPreserved = revealed.GetProperty("attributes").GetProperty("user_id")[0].GetString() == storedActor;
        outcome.Success.Should().BeFalse("closed outcome: {0}/{1} after {2} mutating calls; stored actor preserved: {3}",
            outcome.Stage ?? "success", outcome.Reason ?? "none", outcome.Mutations, storedActorPreserved);
        outcome.Mutations.Should().Be(preparationExpected ? 1 : 0, "closed outcome: {0}/{1}; stored actor preserved: {2}",
            outcome.Stage ?? "success", outcome.Reason ?? "none", storedActorPreserved);
        outcome.PreparationDispatched.Should().Be(preparationExpected);
        outcome.Stage.Should().Be("plan");
        outcome.Reason.Should().Be("identity_conflict");
        if (!preparationExpected)
            (after == before).Should().BeTrue("the independent snapshot precedes the test-owned visibility change");
        storedActorPreserved.Should().BeTrue("the stored actor is never overwritten");
        (Id(revealed) == stableId).Should().BeTrue();
    }

    [Fact]
    public async Task Readback_failure_is_nonzero_and_a_new_explicit_pass_verifies_the_committed_result()
    {
        await using var fixture = await KeycloakFixture.StartAsync();
        using var admin = await fixture.CreateAdminClientAsync();
        (await admin.PutAsync("", new { accessTokenLifespan = 1800 })).Should().Be(HttpStatusCode.NoContent);
        var failed = await KeycloakReconciliationDriver.RunAsync(fixture, "readback_failure");
        AssertFailure(failed, "readback", "transport");
        failed.Mutations.Should().BeGreaterThan(0);
        (await admin.GetAsync()).GetProperty("accessTokenLifespan").GetInt32().Should().Be(900);
        var explicitRetry = await KeycloakReconciliationDriver.RunAsync(fixture);
        explicitRetry.Success.Should().BeTrue("closed outcome: {0}/{1}", explicitRetry.Stage, explicitRetry.Reason);
        explicitRetry.Mutations.Should().Be(0);
    }

    private static async Task AssertRefusesWithoutWriteAsync(KeycloakFixture fixture, KeycloakAdminClient admin,
        string reason, string scenario = "normal")
    {
        var before = await StateAsync(admin);
        var outcome = await KeycloakReconciliationDriver.RunAsync(fixture, scenario);
        AssertFailure(outcome, "plan", reason);
        outcome.Mutations.Should().Be(0, "the complete bounded plan refuses conflicting state before any write");
        (await StateAsync(admin) == before).Should().BeTrue("independent real-provider snapshots witness zero changes");
    }

    private static void AssertFailure(ReconciliationOutcome outcome, string stage, string reason)
    {
        outcome.Success.Should().BeFalse();
        outcome.Stage.Should().Be(stage, "the closed failure reason is {0}", outcome.Reason);
        outcome.Reason.Should().Be(reason);
    }

    private static async Task AssertStoredIdentityAsync(KeycloakAdminClient admin, SeedIdentity identity)
    {
        var user = await admin.UserAsync(identity.Username);
        var attributes = user.GetProperty("attributes");
        var expected = new Dictionary<string, string>
        {
            ["user_id"] = identity.UserId.Value.ToString("D"),
            ["tenant_id"] = identity.Tenant.TenantId.Value.ToString("D"),
        };
        if (identity.Tenant.MapHostToDefaultOrganization)
            expected["organization_id"] = identity.Tenant.DefaultOrganization.OrganizationId.Value.ToString("D");
        foreach (var (name, value) in expected)
        {
            var stored = attributes.GetProperty(name);
            (stored.ValueKind == JsonValueKind.Array && stored.GetArrayLength() == 1
                && stored[0].ValueKind == JsonValueKind.String && stored[0].GetString() == value).Should().BeTrue(
                "each migrated persona stores its independently declared typed authority");
        }
        if (!expected.ContainsKey("organization_id")) attributes.TryGetProperty("organization_id", out _).Should().BeFalse();
    }

    private static async Task AssertOwnedScopeAndMapperRepairAsync(KeycloakAdminClient admin, KeycloakFixture fixture)
    {
        var scope = await ScopeAsync(admin, "learnstack-tenant-api");
        var path = "client-scopes/" + Id(scope);
        var changedScope = Object(scope);
        changedScope["attributes"]!["include.in.token.scope"] = "false";
        (await admin.PutAsync(path, changedScope)).Should().Be(HttpStatusCode.NoContent);
        var mapper = (await admin.GetAsync(path + "/protocol-mappers/models")).EnumerateArray()
            .Single(value => value.GetProperty("name").GetString() == "user_id");
        var changedMapper = Object(mapper);
        changedMapper["config"]!["id.token.claim"] = "false";
        var mapperPath = path + "/protocol-mappers/models/" + Id(mapper);
        (await admin.PutAsync(mapperPath, changedMapper)).Should().Be(HttpStatusCode.NoContent);
        var result = await KeycloakReconciliationDriver.RunAsync(fixture);
        result.Success.Should().BeTrue("closed owned-repair outcome: {0}/{1}", result.Stage, result.Reason);
        result.Mutations.Should().Be(2, "one real update_scope and one real update_mapper repair are necessary");
        AssertSame(scope, await admin.GetAsync(path), "the owned scope repair restores the original complete scope representation");
        AssertSame(mapper, await admin.GetAsync(mapperPath), "the owned mapper repair restores the original complete mapper representation");
    }

    private static async Task AssertLoginAsync(KeycloakFixture fixture, SeedIdentity identity, string password, string? subject = null)
    {
        using var browser = fixture.Browser();
        var authorization = await browser.AuthorizeAsync(identity.Username, password, Callback(identity));
        authorization.Authorized.Should().BeTrue("the preserved/new account completes real code/S256 authorization (closed outcome: {0})",
            authorization.Error ?? "no_error_code");
        var exchange = await browser.RedeemAsync(authorization);
        exchange.StatusCode.Should().Be(HttpStatusCode.OK);
        var access = await KeycloakTokenVerifier.ValidateAsync(fixture, exchange.AccessToken, "learnstack-api");
        (access.GetProperty("user_id").GetString() == identity.UserId.Value.ToString("D")).Should().BeTrue();
        (access.GetProperty("tenant_id").GetString() == identity.Tenant.TenantId.Value.ToString("D")).Should().BeTrue();
        if (subject is not null) (access.GetProperty("sub").GetString() == subject).Should().BeTrue("the existing provider subject is preserved");
        if (identity.Tenant.MapHostToDefaultOrganization)
            (access.GetProperty("organization_id").GetString() == identity.Tenant.DefaultOrganization.OrganizationId.Value.ToString("D"))
                .Should().BeTrue();
        else access.TryGetProperty("organization_id", out _).Should().BeFalse();
        exchange.Window.AcceptsFresh(access, 900).Should().BeTrue();
    }

    private static async Task AssertManagedConfigurationAsync(KeycloakAdminClient admin)
    {
        (await admin.GetAsync()).GetProperty("accessTokenLifespan").GetInt32().Should().Be(900);
        foreach (var clientId in new[] { "learnstack-web", "learnstack-api" })
        {
            var client = await admin.ClientAsync(clientId);
            client.GetProperty("publicClient").GetBoolean().Should().BeFalse();
            (client.GetProperty("clientAuthenticatorType").GetString() == "client-secret").Should().BeTrue();
            client.GetProperty("standardFlowEnabled").GetBoolean().Should().Be(clientId == "learnstack-web");
            var expectedRedirects = clientId == "learnstack-web"
                ? SeedData.All.Select(tenant => "https://" + tenant.Host + ":3000/api/auth/callback/keycloak") : [];
            client.GetProperty("redirectUris").EnumerateArray().Select(value => value.GetString())
                .Should().BeEquivalentTo(expectedRedirects);
            if (clientId == "learnstack-web")
                client.GetProperty("attributes").GetProperty("post.logout.redirect.uris").GetString()!.Split("##")
                    .Should().BeEquivalentTo(SeedData.All.Select(tenant => "https://" + tenant.Host + ":3000/"));
            client.GetProperty("webOrigins").GetArrayLength().Should().Be(0,
                "the declared empty origin list survives redirect changes without provider-derived origins");
            client.GetProperty("attributes").TryGetProperty("access.token.lifespan", out _).Should().BeFalse();
            var defaults = await admin.GetAsync("clients/" + Id(client) + "/default-client-scopes");
            defaults.EnumerateArray().Select(value => value.GetProperty("name").GetString())
                .Should().Contain(["basic", "learnstack-tenant-api", "unrelated-fixture-scope"]);
        }
        var profile = await admin.GetAsync("users/profile");
        foreach (var name in new[] { "user_id", "tenant_id", "organization_id" })
        {
            var definition = profile.GetProperty("attributes").EnumerateArray().Single(value => value.GetProperty("name").GetString() == name);
            definition.GetProperty("multivalued").GetBoolean().Should().BeFalse();
            foreach (var permission in new[] { "view", "edit" })
                definition.GetProperty("permissions").GetProperty(permission).EnumerateArray().Select(value => value.GetString())
                    .Should().Equal("admin");
        }
    }

    private static async Task AssertUnrelatedConfigurationAsync(KeycloakAdminClient admin, JsonElement profileBefore)
    {
        var realm = await admin.GetAsync();
        realm.GetProperty("registrationEmailAsUsername").GetBoolean().Should().BeFalse(
            "reconciliation preserves the unrelated warm realm username policy");
        (realm.GetProperty("displayName").GetString() == "Preserved warm-realm name").Should().BeTrue();
        realm.GetProperty("ssoSessionMaxLifespan").GetInt32().Should().Be(43200);
        var profile = await admin.GetAsync("users/profile");
        AssertSame(profileBefore.GetProperty("groups"), profile.GetProperty("groups"), "unrelated profile groups survive merge");
        (profile.GetProperty("unmanagedAttributePolicy").GetString() == profileBefore.GetProperty("unmanagedAttributePolicy").GetString())
            .Should().BeTrue();
        var oldAttribute = profileBefore.GetProperty("attributes").EnumerateArray().Single(value => value.GetProperty("name").GetString() == "legacy_note");
        var attribute = profile.GetProperty("attributes").EnumerateArray().Single(value => value.GetProperty("name").GetString() == "legacy_note");
        AssertSame(oldAttribute, attribute, "unrelated profile definitions survive merge");
        foreach (var clientId in new[] { "learnstack-web", "learnstack-api" })
        {
            var client = await admin.ClientAsync(clientId);
            (client.GetProperty("attributes").GetProperty("unrelated.fixture.key").GetString() == "preserved").Should().BeTrue();
            (client.GetProperty("name").GetString() == "Preserved " + clientId).Should().BeTrue();
        }
    }

    private static async Task AssertOnlyAccountGrantAddedAsync(KeycloakAdminClient admin, string userId, JsonElement before)
    {
        var account = await admin.ClientAsync("account");
        var basePath = "users/" + userId + "/role-mappings/clients/" + Id(account);
        var direct = await admin.GetAsync(basePath);
        direct.EnumerateArray().Select(role => role.GetProperty("name").GetString()).Should().Equal("manage-account");
        var role = await admin.GetAsync("clients/" + Id(account) + "/roles/manage-account");
        (Id(direct[0]) == Id(role)).Should().BeTrue("only the qualified built-in capability is added");
        var effective = await admin.GetAsync(basePath + "/composite");
        effective.EnumerateArray().Select(value => value.GetProperty("name").GetString()).Should()
            .BeEquivalentTo(AccountSelfServiceRoles);
        var after = Object(await admin.GetAsync("users/" + userId + "/role-mappings"));
        after["clientMappings"]!.AsObject().Remove("account");
        if (after["clientMappings"]!.AsObject().Count == 0) after.Remove("clientMappings");
        JsonNode.DeepEquals(JsonNode.Parse(before.GetRawText()), after).Should().BeTrue(
            "every prior realm and client mapping survives the single approved addition");
    }

    private static void AssertSamePreservedUser(JsonElement before, JsonElement after)
    {
        var old = Object(before);
        var current = Object(after);
        foreach (var user in new[] { old, current })
            foreach (var name in new[] { "user_id", "tenant_id", "organization_id" }) user["attributes"]?.AsObject().Remove(name);
        JsonNode.DeepEquals(old, current).Should().BeTrue("only the three declared identity attributes may change on an existing account");
    }

    private static async Task<string> CreateGroupAsync(KeycloakAdminClient admin, string name,
        IReadOnlyDictionary<string, string[]> attributes, string? parentId = null)
    {
        var path = parentId is null ? "groups" : "groups/" + parentId + "/children";
        (await admin.PostAsync(path, new { name, attributes })).Should().Be(HttpStatusCode.Created);
        var groups = await admin.GetAsync(path + "?briefRepresentation=false&max=100");
        return Id(groups.EnumerateArray().Single(value => value.GetProperty("name").GetString() == name));
    }

    // Lookup responses include realm-wide profile metadata; detail responses hold the account state being preserved.
    private static async Task<JsonElement> UserDetailsAsync(KeycloakAdminClient admin, string username) =>
        await admin.GetAsync("users/" + Id(await admin.UserAsync(username)));

    private static void AssertSame(JsonElement before, JsonElement after, string because) =>
        JsonNode.DeepEquals(JsonNode.Parse(before.GetRawText()), JsonNode.Parse(after.GetRawText())).Should().BeTrue(because);

    private static async Task<string> StateAsync(KeycloakAdminClient admin)
    {
        var state = new JsonObject { ["realm"] = JsonNode.Parse((await admin.GetAsync()).GetRawText()) };
        foreach (var path in new[] { "users?max=100&briefRepresentation=false", "clients", "client-scopes", "users/profile", "roles",
            "groups?max=100&briefRepresentation=false", "default-groups" })
            state[path] = JsonNode.Parse((await admin.GetAsync(path)).GetRawText());
        foreach (var user in state["users?max=100&briefRepresentation=false"]!.AsArray())
        {
            var id = user!["id"]!.GetValue<string>();
            state["user/" + id] = JsonNode.Parse((await admin.GetAsync("users/" + id)).GetRawText());
            state["groups/" + id] = JsonNode.Parse((await admin.GetAsync("users/" + id + "/groups?max=100&briefRepresentation=false")).GetRawText());
            state["credentials/" + id] = JsonNode.Parse((await admin.GetAsync("users/" + id + "/credentials")).GetRawText());
            state["roles/" + id] = JsonNode.Parse((await admin.GetAsync("users/" + id + "/role-mappings")).GetRawText());
        }
        foreach (var client in state["clients"]!.AsArray())
        {
            var id = client!["id"]!.GetValue<string>();
            foreach (var path in new[] { "default-client-scopes", "optional-client-scopes", "protocol-mappers/models" })
                state["clients/" + id + "/" + path] = JsonNode.Parse((await admin.GetAsync("clients/" + id + "/" + path)).GetRawText());
            if (client["clientId"]!.GetValue<string>() is "learnstack-web" or "learnstack-api")
                state["clients/" + id + "/client-secret"] = JsonNode.Parse((await admin.GetAsync("clients/" + id + "/client-secret")).GetRawText());
        }
        foreach (var scope in state["client-scopes"]!.AsArray())
        {
            var id = scope!["id"]!.GetValue<string>();
            state["mappers/" + id] = JsonNode.Parse((await admin.GetAsync("client-scopes/" + id + "/protocol-mappers/models")).GetRawText());
        }
        return Canonical(state).ToJsonString();
    }

    private static JsonNode Canonical(JsonNode? node, bool preserveArrayOrder = false) => node switch
    {
        JsonObject value => new JsonObject(value.OrderBy(property => property.Key, StringComparer.Ordinal)
            .Select(property => new KeyValuePair<string, JsonNode?>(property.Key, property.Value is null ? null :
                Canonical(property.Value, preserveArrayOrder || property.Key is "attributes" or "requiredActions")))),
        JsonArray value => new JsonArray((preserveArrayOrder
            ? value.Select(item => item is null ? null : Canonical(item, true))
            : value.Select(item => item is null ? null : Canonical(item))
                .OrderBy(item => item?.ToJsonString(), StringComparer.Ordinal)).ToArray()),
        _ => node!.DeepClone(),
    };

    private static async Task<JsonElement> ScopeAsync(KeycloakAdminClient admin, string name)
    {
        var scope = (await admin.GetAsync("client-scopes")).EnumerateArray().Single(value => value.GetProperty("name").GetString() == name);
        return await admin.GetAsync("client-scopes/" + Id(scope));
    }

    private static string Id(JsonElement value) => value.GetProperty("id").GetString()
        ?? throw new InvalidOperationException("Keycloak fixture expected a resource ID.");
    private static JsonObject Object(JsonElement value) => JsonNode.Parse(value.GetRawText())!.AsObject();
    private static string Callback(SeedIdentity identity) => "https://" + identity.Tenant.Host + ":3000/api/auth/callback/keycloak";

    // Controlled legacy import is derived from the committed desired state, without a runtime git dependency.
    private static async Task<string> LegacyRealmAsync(bool registrationEmailAsUsername = false)
    {
        var realm = JsonNode.Parse(await File.ReadAllTextAsync(KeycloakFixture.RealmPath("learnstack")))!.AsObject();
        realm["accessTokenLifespan"] = 1800;
        realm["registrationEmailAsUsername"] = registrationEmailAsUsername;
        realm["displayName"] = "Preserved warm-realm name";
        realm["ssoSessionMaxLifespan"] = 43200;
        var scopes = realm["clientScopes"]!.AsArray();
        scopes.Remove(scopes.Single(value => value!["name"]!.GetValue<string>() == "learnstack-tenant-api"));
        scopes.Add(new JsonObject
        {
            ["name"] = "unrelated-fixture-scope",
            ["protocol"] = "openid-connect",
            ["description"] = "Preserved unrelated scope",
            ["attributes"] = new JsonObject { ["include.in.token.scope"] = "true" },
            ["protocolMappers"] = new JsonArray(new JsonObject
            {
                ["name"] = "preserved-note",
                ["protocol"] = "openid-connect",
                ["protocolMapper"] = "oidc-usermodel-attribute-mapper",
                ["config"] = new JsonObject
                {
                    ["claim.name"] = "fixture_note",
                    ["user.attribute"] = "legacy_note",
                    ["jsonType.label"] = "String",
                    ["access.token.claim"] = "true",
                    ["multivalued"] = "false"
                },
            })
        });
        realm["roles"]!["realm"]!.AsArray().Add(new JsonObject { ["name"] = "fixture-retained-role" });
        realm["roles"]!["client"]!["learnstack-web"] =
            new JsonArray(new JsonObject { ["name"] = "fixture-client-role", ["clientRole"] = true });
        foreach (var client in realm["clients"]!.AsArray())
        {
            var name = client!["clientId"]!.GetValue<string>();
            if (name is not ("learnstack-web" or "learnstack-api")) continue;
            client["name"] = "Preserved " + name;
            client["attributes"]!["access.token.lifespan"] = "1800";
            client["attributes"]!["unrelated.fixture.key"] = "preserved";
            var defaults = client["defaultClientScopes"]!.AsArray();
            defaults.Remove(defaults.Single(value => value!.GetValue<string>() == "learnstack-tenant-api"));
            defaults.Add("unrelated-fixture-scope");
            if (name == "learnstack-web")
            {
                client["publicClient"] = true;
                client.AsObject().Remove("secret");
                client["redirectUris"] = new JsonArray("http://localhost:3000/*");
            }
            else
            {
                defaults.Add("service_account");
                client["authorizationServicesEnabled"] = true;
                client["authorizationSettings"] = new JsonObject
                {
                    ["allowRemoteResourceManagement"] = false,
                    ["policyEnforcementMode"] = "PERMISSIVE",
                    ["decisionStrategy"] = "AFFIRMATIVE",
                    ["resources"] = new JsonArray(),
                    ["scopes"] = new JsonArray(),
                    ["policies"] = new JsonArray(),
                };
                client["standardFlowEnabled"] = true;
                client["redirectUris"] = new JsonArray("http://localhost:5000/*");
            }
        }
        var console = realm["clients"]!.AsArray().Single(value => value!["clientId"]!.GetValue<string>() == "account-console")!;
        var consoleMappers = console["protocolMappers"]!.AsArray();
        consoleMappers.Remove(consoleMappers.Single(value => value!["name"]!.GetValue<string>() == "learnstack-account-self-service-roles"));
        var users = realm["users"]!.AsArray();
        foreach (var user in users) user!.AsObject().Remove("clientRoles");
        while (users.Count > 2) users.RemoveAt(users.Count - 1);
        foreach (var user in users)
            user!["attributes"] = new JsonObject { ["tenant_id"] = new JsonArray("tenant-a"), ["legacy_note"] = new JsonArray("retain-me") };
        users[0]!["firstName"] = "Preserved";
        users[0]!["lastName"] = "Identity";
        users[0]!["email"] = "changed-account-email@fixture.test";
        users[0]!["credentials"]![0]!["value"] = ChangedPassword;
        users[0]!["realmRoles"]!.AsArray().Add("fixture-retained-role");
        users[0]!["clientRoles"] = new JsonObject { ["learnstack-web"] = new JsonArray("fixture-client-role") };
        users[1]!["enabled"] = false;
        users[1]!["requiredActions"] = new JsonArray("UPDATE_PASSWORD");
        users[1]!["credentials"]!.AsArray().Add(new JsonObject
        {
            ["type"] = "otp",
            ["userLabel"] = "Preserved fixture MFA",
            ["secretData"] = "{\"value\":\"GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ\"}",
            ["credentialData"] = "{\"subType\":\"totp\",\"digits\":6,\"counter\":0,\"period\":30,\"algorithm\":\"HmacSHA1\"}",
        });
        users.Add(new JsonObject
        {
            ["username"] = ExtraUsername,
            ["email"] = ExtraUsername,
            ["emailVerified"] = true,
            ["enabled"] = true,
            ["firstName"] = "Unmanaged",
            ["lastName"] = "Account",
            ["realmRoles"] = new JsonArray("tenant-instructor"),
            ["attributes"] = new JsonObject { ["legacy_note"] = new JsonArray("extra-account-retained") },
            ["credentials"] = new JsonArray(new JsonObject { ["type"] = "password", ["value"] = ChangedPassword, ["temporary"] = false }),
        });
        var profileValue = realm["components"]!["org.keycloak.userprofile.UserProfileProvider"]![0]!["config"]!["kc.user.profile.config"]!;
        var profile = JsonNode.Parse(profileValue[0]!.GetValue<string>())!.AsObject();
        var attributes = profile["attributes"]!.AsArray();
        foreach (var value in attributes.Where(value => new[] { "user_id", "tenant_id", "organization_id" }
            .Contains(value!["name"]!.GetValue<string>(), StringComparer.Ordinal)).ToArray()) attributes.Remove(value);
        profile["unmanagedAttributePolicy"] = "ENABLED";
        profile["groups"]!.AsArray().Add(new JsonObject { ["name"] = "unrelated-fixture-group", ["displayHeader"] = "Preserved metadata" });
        attributes.Add(new JsonObject
        {
            ["name"] = "legacy_note",
            ["multivalued"] = true,
            ["group"] = "unrelated-fixture-group",
            ["permissions"] = new JsonObject
            { ["view"] = new JsonArray("admin"), ["edit"] = new JsonArray("admin") }
        });
        profileValue[0] = profile.ToJsonString();
        return realm.ToJsonString();
    }
}
