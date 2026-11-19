using System.Net;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.Tests.Integration.Database;
using LearnStack.Tools.IdentityAuthorityRebind;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Xunit;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>
/// The tool's reader of the new realm against the pinned Keycloak (ADR-0104 § 6): its discovery issuer, its key set,
/// the user count, the paging past one page, and that a user's <c>id</c> is the <c>sub</c> its tokens carry.
/// </summary>
/// <remarks>
/// <see cref="KeycloakRealmContinuitySourceTests"/> drives the failure modes on purpose; this is the other half, the
/// shape and the arithmetic of the real administration API, which a fake only imitates.
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class KeycloakEnumerationTests : IAsyncLifetime
{
    private const string ReaderClient = "rebind-view-users-reader";
    private const string ReaderSecret = "view-users-reader-secret-of-the-test";
    private const int ExtraUsers = 130;

    private KeycloakFixture _keycloak = null!;

    public async Task InitializeAsync() => _keycloak = await KeycloakFixture.StartAsync();

    public async Task DisposeAsync() => await _keycloak.DisposeAsync();

    [Fact]
    public async Task The_Pinned_Realm_Is_Enumerated_Completely_Across_Pages_And_Its_Ids_Are_The_Token_Subjects()
    {
        using var admin = await _keycloak.CreateAdminClientAsync();
        var readerAccount = await CreateViewUsersReaderAsync(admin);
        var created = await CreateUsersAsync(admin, ExtraUsers);

        using var client = KeycloakRealmContinuitySource.CreateClient(KeycloakHttp.CreateHandler(_keycloak.RootCertificate));
        var snapshot = await new KeycloakRealmContinuitySource(client, ReaderClient, ReaderSecret).ReadAsync(_keycloak.Issuer);

        snapshot.DiscoveredIssuer.Should().Be(_keycloak.Issuer, "the realm names its own issuer, which the proof requires to equal the target");
        snapshot.SigningKeyCount.Should().BeGreaterThan(0);
        snapshot.Complete.Should().BeTrue("the pages add up to the count, and the count did not move while they were read");

        // The independent read: every account the administration API lists in one request, by the test's own admin client.
        var listed = await admin.GetAsync("users?first=0&max=1000&briefRepresentation=true");
        var expected = listed.EnumerateArray().Select(user => user.GetProperty("id").GetString()!).ToList();
        expected.Count.Should().BeGreaterThan(ExtraUsers, "the shipped realm's own users and the created ones");
        snapshot.Subjects.Should().BeEquivalentTo(expected);
        snapshot.Subjects.Count.Should().BeGreaterThan(KeycloakRealmContinuitySource.PageSize,
            "the enumeration crossed at least one page boundary");
        snapshot.Subjects.Should().Contain(created);
        snapshot.Subjects.Should().NotContain(readerAccount, "a service account is not a person of the realm");

        // The id is the sub: the service account's own token carries its user id as the subject.
        var token = await AccessTokenAsync(client);
        new JsonWebToken(token).Subject.Should().Be(readerAccount);

        // A credential without the role cannot read the realm: the administration API refuses it.
        await CreateReaderClientAsync(admin, "rebind-no-role-reader", assignRole: false);
        var refused = async () => await new KeycloakRealmContinuitySource(client, "rebind-no-role-reader", ReaderSecret)
            .ReadAsync(_keycloak.Issuer);
        await refused.Should().ThrowAsync<NewRealmUnavailableException>();
    }

    /// <summary>The credential the runbook has the operator create: a confidential client with the one view-users role.</summary>
    private static async Task<string> CreateViewUsersReaderAsync(KeycloakAdminClient admin) =>
        await CreateReaderClientAsync(admin, ReaderClient, assignRole: true);

    private static async Task<string> CreateReaderClientAsync(KeycloakAdminClient admin, string clientId, bool assignRole)
    {
        (await admin.PostAsync("clients", new
        {
            clientId,
            protocol = "openid-connect",
            publicClient = false,
            serviceAccountsEnabled = true,
            standardFlowEnabled = false,
            directAccessGrantsEnabled = false,
            secret = ReaderSecret,
        })).Should().Be(HttpStatusCode.Created);

        var client = await admin.ClientAsync(clientId);
        var account = (await admin.GetAsync("clients/" + client.GetProperty("id").GetString() + "/service-account-user"))
            .GetProperty("id").GetString()!;
        if (assignRole)
        {
            var management = await admin.ClientAsync("realm-management");
            var role = await admin.GetAsync("clients/" + management.GetProperty("id").GetString() + "/roles/view-users");
            (await admin.PostAsync(
                "users/" + account + "/role-mappings/clients/" + management.GetProperty("id").GetString(),
                new[] { role })).Should().Be(HttpStatusCode.NoContent);
        }

        return account;
    }

    private static async Task<List<string>> CreateUsersAsync(KeycloakAdminClient admin, int count)
    {
        for (var index = 0; index < count; index++)
        {
            (await admin.PostAsync("users", new
            {
                username = $"enumeration-user-{index:D3}",
                enabled = true,
                email = $"enumeration-user-{index:D3}@example.test",
                firstName = "Enumeration",
                lastName = $"User{index:D3}",
            }))
                .Should().Be(HttpStatusCode.Created);
        }

        // The realm may use the e-mail address as the username, so the created accounts are found by search.
        var found = await admin.GetAsync("users?search=Enumeration&first=0&max=1000&briefRepresentation=true");
        var ids = found.EnumerateArray().Select(user => user.GetProperty("id").GetString()!).ToList();
        ids.Count.Should().Be(count, "every created account is found");
        return ids;
    }

    private async Task<string> AccessTokenAsync(HttpClient client)
    {
        using var response = await client.PostAsync(
            new Uri(_keycloak.BaseAddress, "realms/learnstack/protocol/openid-connect/token"),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = ReaderClient,
                ["client_secret"] = ReaderSecret,
            }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("access_token").GetString()!;
    }
}
