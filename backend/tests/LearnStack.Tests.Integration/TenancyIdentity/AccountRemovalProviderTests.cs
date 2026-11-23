using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Infrastructure.Identity.AccountRemoval;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Resilience;
using LearnStack.SharedKernel.Secrets;
using LearnStack.Tests.Integration.Database;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Polly;
using Xunit;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>One owned provider for ADR-0107 § 7: the committed realm, with the account-removal client its import declares.</summary>
public sealed class AccountRemovalKeycloakFixture : IAsyncLifetime
{
    internal KeycloakFixture Keycloak { get; private set; } = null!;

    public async Task InitializeAsync() => Keycloak = await KeycloakFixture.StartAsync();

    public async Task DisposeAsync() => await Keycloak.DisposeAsync();
}

/// <summary>
/// ADR-0107 § 7 real-provider controls: the confined adapter deletes a real Keycloak user through the committed realm's
/// account-removal client, and the client's authority is exactly what its role grants, no more and no less. Each case
/// carries its dirty control.
/// </summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class AccountRemovalProviderTests(AccountRemovalKeycloakFixture provider) : IClassFixture<AccountRemovalKeycloakFixture>
{
    private KeycloakFixture Keycloak => provider.Keycloak;

    [Fact]
    public async Task The_adapter_deletes_a_real_user_once_and_reports_absent_afterwards_without_touching_another_account()
    {
        using var admin = await Keycloak.CreateAdminClientAsync();
        var doomed = await CreateUserAsync(admin, "closing-person");
        var bystander = await CreateUserAsync(admin, "bystander-person");
        using var adapter = Adapter();

        (await adapter.RemoveAsync(doomed)).Should().Be(AccountRemovalOutcome.Removed,
            "the real client's token passes the adapter's narrowing and Keycloak accepts the deletion");
        await AssertGoneAsync(admin, doomed);
        (await adapter.RemoveAsync(doomed)).Should().Be(AccountRemovalOutcome.Absent, "a missing account is success");
        (await admin.GetAsync("users/" + bystander)).GetProperty("id").GetString().Should().Be(bystander,
            "dirty control: the deletion named one identifier and removed that account only");
        (await adapter.RemoveAsync(Guid.CreateVersion7().ToString("D"))).Should().Be(AccountRemovalOutcome.Absent,
            "an identifier the provider never issued is absent");
    }

    [Fact]
    public async Task Keycloaks_own_not_found_for_a_deletion_is_json_which_is_what_the_adapter_reads_as_absence()
    {
        // The adapter treats a 404 as absence only when it carries the provider's own JSON type (it never reads the body). That
        // rule rests on this observation of the pinned image: a missing user, in any spelling of its identifier, is answered 404
        // with application/json. A proxy that hides the admin path answers in another type, and the unit cases plant it.
        using var client = KeycloakHttp.Create(Keycloak.BaseAddress, Keycloak.RootCertificate);
        var admin = AdminClient(await TokenAsync(client, "learnstack-account-removal", KeycloakFixture.AccountRemovalSecret));
        var unknown = Guid.CreateVersion7().ToString("D");

        foreach (var path in new[] { "users/" + unknown, "users/" + unknown.ToUpperInvariant(), "users/not-a-uuid" })
        {
            using var response = await KeycloakHttp.SendAsync(admin, HttpMethod.Delete, path);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, path);
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/json",
                "the provider's own not-found answer carries its JSON type: " + path);
            (await KeycloakHttp.ReadBodyAsync(response)).Should().Contain("User not found");
        }

        // Dirty control: a path the provider does not route answers 404 as well, and also in JSON. The adapter never builds such a
        // path (the architecture rules pin its two routes), so this is why the type alone is not asked to prove more than the
        // deletion route's own answer, and why the runbook has the operator exercise that route after provisioning.
        using var unrouted = await KeycloakHttp.SendAsync(admin, HttpMethod.Delete, "unrouted/" + unknown);
        unrouted.StatusCode.Should().Be(HttpStatusCode.NotFound);
        unrouted.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        using var adapter = Adapter();
        (await adapter.RemoveAsync(unknown)).Should().Be(AccountRemovalOutcome.Absent);
    }

    [Fact]
    public async Task A_wrong_client_secret_is_refused_by_the_provider_and_changes_nothing()
    {
        using var admin = await Keycloak.CreateAdminClientAsync();
        var kept = await CreateUserAsync(admin, "kept-person");
        using var adapter = Adapter(secret: "a-secret-the-provider-never-issued");
        (await adapter.RemoveAsync(kept)).Should().Be(AccountRemovalOutcome.Refused);
        (await admin.GetAsync("users/" + kept)).GetProperty("id").GetString().Should().Be(kept);
    }

    [Fact]
    public async Task The_client_token_carries_manage_users_alone_and_reaches_neither_federation_nor_client_administration()
    {
        using var client = KeycloakHttp.Create(Keycloak.BaseAddress, Keycloak.RootCertificate);
        var access = await TokenAsync(client, "learnstack-account-removal", KeycloakFixture.AccountRemovalSecret);
        using var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(access.Split('.')[1]));
        payload.RootElement.GetProperty("azp").GetString().Should().Be("learnstack-account-removal");
        payload.RootElement.GetProperty("resource_access").EnumerateObject().Select(value => value.Name).Should().Equal("realm-management");
        payload.RootElement.GetProperty("resource_access").GetProperty("realm-management").GetProperty("roles").EnumerateArray()
            .Select(value => value.GetString()).Should().Equal("manage-users");
        (payload.RootElement.TryGetProperty("realm_access", out var realm) && realm.GetProperty("roles").GetArrayLength() != 0)
            .Should().BeFalse("no realm role");
        var admin = AdminClient(access);

        // What manage-users does not reach: identity-provider brokers, clients and realm settings.
        (await SendAsync(admin, HttpMethod.Get, "identity-provider/instances")).Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(admin, HttpMethod.Get, "clients")).Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(admin, HttpMethod.Put, "", JsonContent.Create(new { realm = "learnstack" }))).Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(admin, HttpMethod.Get, "authentication/flows")).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_client_can_also_create_update_and_reset_credentials_which_is_why_the_runbook_names_its_blast_radius()
    {
        // The accepted risk of ADR-0107 § 7, shown rather than asserted: manage-users is not deletion-only.
        using var client = KeycloakHttp.Create(Keycloak.BaseAddress, Keycloak.RootCertificate);
        var admin = AdminClient(await TokenAsync(client, "learnstack-account-removal", KeycloakFixture.AccountRemovalSecret));
        using var create = await KeycloakHttp.SendAsync(admin, HttpMethod.Post, "users", JsonContent.Create(new
        {
            username = "created-by-the-removal-client",
            enabled = true,
            email = "created@removal.test",
            emailVerified = true,
        }));
        create.StatusCode.Should().Be(HttpStatusCode.Created, "manage-users creates users in the shared realm");
        var id = create.Headers.Location!.AbsolutePath.Split('/').Last();
        (await SendAsync(admin, HttpMethod.Put, "users/" + id + "/reset-password",
            JsonContent.Create(new { type = "password", value = "an-attacker-chosen-password-1", temporary = false })))
            .Should().Be(HttpStatusCode.NoContent, "and resets credentials");
        (await SendAsync(admin, HttpMethod.Put, "users/" + id, JsonContent.Create(new { enabled = false })))
            .Should().Be(HttpStatusCode.NoContent, "and updates them");
        using var adapter = Adapter();
        (await adapter.RemoveAsync(id)).Should().Be(AccountRemovalOutcome.Removed, "the adapter cleans up what the control created");
    }

    [Fact]
    public async Task The_federation_client_cannot_delete_a_user_so_the_two_confined_clients_do_not_overlap()
    {
        using var admin = await Keycloak.CreateAdminClientAsync();
        var user = await CreateUserAsync(admin, "federation-cannot-delete");
        using var client = KeycloakHttp.Create(Keycloak.BaseAddress, Keycloak.RootCertificate);
        var federation = AdminClient(await TokenAsync(client, "learnstack-federation-admin", KeycloakFixture.FederationAdminSecret));
        (await SendAsync(federation, HttpMethod.Delete, "users/" + user)).Should().Be(HttpStatusCode.Forbidden);
        (await admin.GetAsync("users/" + user)).GetProperty("id").GetString().Should().Be(user, "nothing was deleted");
        // Dirty control: the removal client's own deletion of the same account succeeds.
        using var adapter = Adapter();
        (await adapter.RemoveAsync(user)).Should().Be(AccountRemovalOutcome.Removed);
    }

    private KeycloakAccountRemoval Adapter(string secret = KeycloakFixture.AccountRemovalSecret) =>
        new(new KeycloakAccountRemovalOptions(Keycloak.Issuer), new Secrets(secret), new Resilience(), NullLogger.Instance,
            KeycloakHttp.CreateHandler(Keycloak.RootCertificate));

    private HttpClient AdminClient(string access)
    {
        var client = KeycloakHttp.Create(new Uri(Keycloak.BaseAddress, "admin/realms/learnstack/"), Keycloak.RootCertificate);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
        return client;
    }

    private static async Task<HttpStatusCode> SendAsync(HttpClient client, HttpMethod method, string path, HttpContent? content = null)
    {
        using var response = await KeycloakHttp.SendAsync(client, method, path, content);
        return response.StatusCode;
    }

    private static async Task<string> TokenAsync(HttpClient client, string clientId, string secret)
    {
        using var issued = await KeycloakHttp.JsonAsync(client, HttpMethod.Post, "realms/learnstack/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = secret,
            }));
        return issued.RootElement.GetProperty("access_token").GetString()!;
    }

    private static async Task<string> CreateUserAsync(KeycloakAdminClient admin, string username)
    {
        // The realm uses the e-mail address as the user name, so the name the provider stores is the address.
        var address = username + "@removal.test";
        (await admin.PostAsync("users", new { username = address, enabled = true, email = address, emailVerified = true }))
            .Should().Be(HttpStatusCode.Created);
        return (await admin.UserAsync(address)).GetProperty("id").GetString()!;
    }

    private static async Task AssertGoneAsync(KeycloakAdminClient admin, string id)
    {
        var act = () => admin.GetAsync("users/" + id);
        await act.Should().ThrowAsync<InvalidOperationException>("the provider answers 404 for a deleted user");
    }

    private sealed class Secrets(string value) : ISecretProvider
    {
        public string? GetSecret(string key) => key == "AccountRemoval:ClientSecret" ? value : null;
    }

    private sealed class Resilience : IProviderResilience<IIdentityProviderAccountRemoval>
    {
        public ResiliencePipeline Pipeline => ResiliencePipeline.Empty;
        public string PortName => "accountRemoval";
    }
}
