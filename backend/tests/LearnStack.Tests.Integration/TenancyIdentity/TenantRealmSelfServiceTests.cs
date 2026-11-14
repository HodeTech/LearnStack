using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Tests.Integration.Database;
using LearnStack.Tools.Seeder;
using Xunit;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>
/// The account self-service boundary after ADR-0099 § Realm invariants retired it: the console issues no code, no persona
/// or registration default carries manage-account, and a retired grant carried by a group refuses reconciliation before
/// any write. The bounded console capability these tests proved before P03-8 Step 3 is retired, not repaired.
/// </summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class TenantRealmSelfServiceTests
{
    private static readonly string[] Retired = ["manage-account", "manage-account-links"];

    [Fact]
    public async Task The_committed_import_retires_account_self_service_for_personas_and_new_registrations()
    {
        await using var fixture = await KeycloakFixture.StartAsync();
        using var admin = await fixture.CreateAdminClientAsync();
        foreach (var identity in SeedData.IdentityUsers)
        {
            using var console = fixture.Browser();
            (await console.AuthorizeAsync(identity.Username, KeycloakFixture.DemoPassword,
                new Uri(fixture.BaseAddress, "realms/learnstack/account/").AbsoluteUri, clientId: "account-console", scope: "openid"))
                .Authorized.Should().BeFalse("the disabled console issues no code");
        }
        // A new registration inherits only the default role, which no longer carries account management.
        var username = "retired-console-" + Guid.NewGuid().ToString("N") + "@provider.test";
        using (var registration = fixture.Browser())
            (await registration.RegisterAsync(username, KeycloakFixture.DemoPassword,
                "https://" + SeedData.English.Host + ":3000/api/auth/callback/keycloak", new Dictionary<string, string>()))
                .Authorized.Should().BeTrue("ordinary registration is unchanged");
        var user = await admin.UserAsync(username);
        var account = await admin.ClientAsync("account");
        var effective = await admin.GetAsync("users/" + user.GetProperty("id").GetString() + "/role-mappings/clients/"
            + account.GetProperty("id").GetString() + "/composite");
        effective.EnumerateArray().Select(role => role.GetProperty("name").GetString()).Should().NotContain(Retired);
        (await admin.DeleteAsync("users/" + user.GetProperty("id").GetString())).Should().Be(HttpStatusCode.NoContent);
        var rerun = await KeycloakReconciliationDriver.RunAsync(fixture);
        rerun.Success.Should().BeTrue("closed outcome: {0}/{1}", rerun.Stage, rerun.Reason);
        rerun.Mutations.Should().Be(0);
    }

    [Fact]
    public async Task A_retired_grant_carried_by_a_group_refuses_before_any_write_and_keeps_the_group()
    {
        await using var fixture = await KeycloakFixture.StartAsync();
        using var admin = await fixture.CreateAdminClientAsync();
        var account = await admin.ClientAsync("account");
        var accountId = account.GetProperty("id").GetString();
        var role = await admin.GetAsync("clients/" + accountId + "/roles/manage-account");
        (await admin.PostAsync("groups", new { name = "legacy-console-group" })).Should().Be(HttpStatusCode.Created);
        var group = (await admin.GetAsync("groups?search=legacy-console-group&exact=true")).EnumerateArray().Single();
        var groupId = group.GetProperty("id").GetString();
        (await admin.PostAsync("groups/" + groupId + "/role-mappings/clients/" + accountId, new[] { JsonNode.Parse(role.GetRawText()) }))
            .Should().Be(HttpStatusCode.NoContent);
        var persona = await admin.UserAsync(SeedData.IdentityUsers[0].Username);
        (await admin.PutAsync("users/" + persona.GetProperty("id").GetString() + "/groups/" + groupId, new { })).Should().Be(HttpStatusCode.NoContent);
        var result = await KeycloakReconciliationDriver.RunAsync(fixture);
        result.Success.Should().BeFalse("a retired capability from an unowned source is refused, not silently left in force");
        result.Stage.Should().Be("plan");
        result.Reason.Should().Be("unsupported");
        result.Mutations.Should().Be(0);
        (await admin.GetAsync("groups/" + groupId + "/role-mappings/clients/" + accountId)).EnumerateArray()
            .Select(value => value.GetProperty("name").GetString()).Should().Equal(["manage-account"], "the group is never repaired");
    }
}
