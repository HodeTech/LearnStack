using System.Net;
using System.Reflection;
using FluentAssertions;
using LearnStack.Infrastructure.Identity.ProviderEvents;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Resilience;
using LearnStack.SharedKernel.Secrets;
using LearnStack.Tools.Seeder;
using LearnStack.Tests.Integration.Database;
using Polly;
using Xunit;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>Actual pinned TLS provider, actual owned realm tool and the production read-only adapter.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed partial class ProviderEventAdapterTests(KeycloakFixture fixture) : IClassFixture<KeycloakFixture>
{
    [Fact]
    public async Task Separate_reader_reads_real_login_history_refuses_admin_writes_and_observes_rotation_and_disable()
    {
        var prepared = await KeycloakReconciliationDriver.RunAsync(fixture, includeEventReader: true);
        prepared.Success.Should().BeTrue("reader setup must qualify: {0}/{1}, completed writes {2}", prepared.Stage, prepared.Reason, prepared.Mutations);
        var repeated = await KeycloakReconciliationDriver.RunAsync(fixture, includeEventReader: true);
        repeated.Success.Should().BeTrue(); repeated.Mutations.Should().Be(0);
        using var admin = await fixture.CreateAdminClientAsync();
        var realm = await admin.GetAsync();
        var realmId = realm.GetProperty("id").GetString()!;
        var authority = IdentityAuthorityId.From(Guid.CreateVersion7());
        var secrets = new Secrets(KeycloakFixture.EventReaderSecret);
        var provider = Create(authority, realmId, secrets);
        await using var retained = (IAsyncDisposable)provider;
        (await provider.ReadPolicyAsync()).RetentionSeconds.Should().Be(604800);
        var from = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var browser = fixture.Browser();
        var user = SeedData.IdentityUsers[0];
        var authorization = await browser.AuthorizeAsync(user.Username, KeycloakFixture.DemoPassword,
            "https://" + user.Tenant.Host + ":3000/api/auth/callback/keycloak");
        authorization.Authorized.Should().BeTrue();
        var exchanged = await browser.RedeemAsync(authorization);
        exchanged.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await provider.ReadAsync(new(from, DateTimeOffset.UtcNow, 0));
        page.Issue.Should().BeNull();
        var subject = (await admin.UserAsync(user.Username)).GetProperty("id").GetString()!;
        page.Items.Should().Contain(item => item.Fact != null && item.Fact.Kind == IdentityProviderEventKind.Login
            && item.Fact.SubjectKey != null && item.Fact.SubjectKey.Equals(IdentitySubjectKey.Compute(authority, subject)));
        page.Items.Should().OnlyContain(item => item.Disposition != IdentityEventItemDisposition.Malformed);
        var actualLogin = page.Items.Select(item => item.Fact).First(item => item?.Kind == IdentityProviderEventKind.Login
            && item.SubjectKey?.Equals(IdentitySubjectKey.Compute(authority, subject)) == true)!;
        await AssertActualMirrorAsync(admin, realmId, secrets, user.UserId, actualLogin.SourceEventId);

        using (var machine = KeycloakHttp.Create(fixture.BaseAddress, fixture.RootCertificate))
        {
            using var tokenResponse = await KeycloakHttp.SendAsync(machine, HttpMethod.Post, "realms/learnstack/protocol/openid-connect/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                { ["grant_type"] = "client_credentials", ["client_id"] = "learnstack-event-reader", ["client_secret"] = secrets.Value }));
            tokenResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            using var token = await KeycloakHttp.ReadJsonAsync(tokenResponse, CancellationToken.None);
            machine.DefaultRequestHeaders.Authorization = new("Bearer", token.RootElement.GetProperty("access_token").GetString());
            foreach (var (method, path) in new[] { (HttpMethod.Get, "admin/realms/learnstack/users"),
                         (HttpMethod.Delete, "admin/realms/learnstack/events"), (HttpMethod.Get, "admin/realms/learnstack-hub/events") })
            {
                using var denied = await KeycloakHttp.SendAsync(machine, method, path);
                denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            }
            using var deniedPolicy = await KeycloakHttp.SendAsync(machine, HttpMethod.Put, "admin/realms/learnstack/events/config",
                System.Net.Http.Json.JsonContent.Create(new { eventsEnabled = false }));
            deniedPolicy.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        var client = await admin.ClientAsync("learnstack-event-reader");
        var id = client.GetProperty("id").GetString()!;
        (await admin.PostAsync("clients/" + id + "/client-secret", new { })).Should().Be(HttpStatusCode.OK);
        await Assert.ThrowsAsync<IdentityEventProviderException>(() => provider.ReadPolicyAsync());
        secrets.Value = (await admin.GetAsync("clients/" + id + "/client-secret")).GetProperty("value").GetString()!;
        (await provider.ReadPolicyAsync()).RetentionSeconds.Should().Be(604800);
        var disabled = System.Text.Json.Nodes.JsonNode.Parse(client.GetRawText())!.AsObject();
        disabled["enabled"] = false;
        (await admin.PutAsync("clients/" + id, disabled)).Should().Be(HttpStatusCode.NoContent);
        await Assert.ThrowsAsync<IdentityEventProviderException>(() => provider.ReadPolicyAsync());
    }

    private IIdentityEventProvider Create(IdentityAuthorityId authority, string realm, ISecretProvider secrets)
    {
        var type = typeof(IdentityEventProviderOptions).Assembly.GetType("LearnStack.Infrastructure.Identity.ProviderEvents.KeycloakIdentityEventProvider", true)!;
        return (IIdentityEventProvider)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
            [new IdentityEventProviderOptions(authority, fixture.Issuer, realm), secrets, new Resilience(), KeycloakHttp.CreateHandler(fixture.RootCertificate)], null)!;
    }
    private sealed class Secrets(string value) : ISecretProvider
    {
        internal string Value { get; set; } = value;
        public string? GetSecret(string name) => name == "IdentityEvents:ClientSecret" ? Value : null;
    }
    private sealed class Resilience : IProviderResilience<IIdentityEventProvider>
    {
        public string PortName => "identityEvents";
        public ResiliencePipeline Pipeline { get; } = ResiliencePipeline.Empty;
    }
}
