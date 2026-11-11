using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Infrastructure.Identity.ProviderEvents;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Resilience;
using LearnStack.SharedKernel.Secrets;
using LearnStack.Tests.Integration.Database;
using LearnStack.Tools.Seeder;
using Microsoft.AspNetCore.WebUtilities;
using Polly;
using Xunit;

namespace LearnStack.Tests.Integration.TenancyIdentity;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class ProviderEventActionTests
{
    [Fact]
    public async Task Actual_reset_credential_MFA_logout_and_lockout_history_crosses_the_selected_read_only_port()
    {
        await using var fixture = await KeycloakFixture.StartWithInvitationMailAsync();
        var setup = await KeycloakReconciliationDriver.RunAsync(fixture, includeEventReader: true);
        setup.Success.Should().BeTrue("owned reader setup: {0}/{1}", setup.Stage, setup.Reason);
        using var admin = await fixture.CreateAdminClientAsync();
        var realm = await admin.GetAsync();
        var authority = IdentityAuthorityId.From(Guid.CreateVersion7());
        var provider = Create(fixture, authority, realm.GetProperty("id").GetString()!);
        await using var retained = (IAsyncDisposable)provider;
        var from = DateTimeOffset.UtcNow.AddMinutes(-1);
        const string email = "owned-provider-actions@example.test";
        var original = Guid.NewGuid().ToString("N") + "aA9!";
        var changed = Guid.NewGuid().ToString("N") + "bB8!";
        await CreateUserAsync(admin, email, original);
        var callback = "https://" + SeedData.English.Host + ":3000/api/auth/callback/keycloak";
        using (var reset = fixture.Browser())
        {
            await reset.RequestProviderResetAsync(email, callback);
            using var mailDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var link = await fixture.ReadInvitationMailAsync(email, "/realms/learnstack/login-actions/action-token", mailDeadline.Token);
            await reset.CompleteProviderResetAsync(link, changed);
        }
        using (var resetProof = fixture.Browser())
            (await resetProof.AuthorizeAsync(email, changed, callback)).Authorized.Should().BeTrue("the actual email-reset path changed the credential");
        using (var actions = fixture.Browser())
        {
            await actions.ChangeProviderPasswordAsync(email, changed, original, callback);
            await actions.ConfigureProviderTotpAsync(email, original, callback);
        }
        var actionUser = await admin.UserAsync(email);
        var credentials = await admin.GetAsync("users/" + actionUser.GetProperty("id").GetString() + "/credentials");
        credentials.EnumerateArray().Count(value => value.GetProperty("type").GetString() == "otp").Should().Be(1);
        using (var browser = fixture.Browser())
        {
            var identity = SeedData.IdentityUsers[0];
            var auth = await browser.AuthorizeAsync(identity.Username, KeycloakFixture.DemoPassword, callback);
            auth.Authorized.Should().BeTrue();
            var tokens = await browser.RedeemAsync(auth);
            var logout = QueryHelpers.AddQueryString(new Uri(fixture.BaseAddress, "realms/learnstack/protocol/openid-connect/logout").AbsoluteUri,
                new Dictionary<string, string?> { ["id_token_hint"] = tokens.IdToken, ["post_logout_redirect_uri"] = "https://" + identity.Tenant.Host + ":3000/", ["state"] = "owned-event-logout" });
            _ = await browser.CompleteBffLogoutAsync(new Uri(logout));
        }

        const string lockedEmail = "owned-provider-lockout@example.test";
        await CreateUserAsync(admin, lockedEmail, original);
        var lockPolicy = JsonNode.Parse(realm.GetRawText())!.AsObject();
        lockPolicy["failureFactor"] = 1; lockPolicy["permanentLockout"] = false;
        lockPolicy["minimumQuickLoginWaitSeconds"] = 60; lockPolicy["waitIncrementSeconds"] = 60;
        (await admin.PutAsync("", lockPolicy)).Should().Be(HttpStatusCode.NoContent);
        using (var invalid = fixture.Browser())
            (await invalid.AuthorizeAsync(lockedEmail, changed, callback)).Authorized.Should().BeFalse();
        var seen = new HashSet<IdentityProviderEventKind>();
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            var page = await provider.ReadAsync(new(from, DateTimeOffset.UtcNow, 0));
            page.Issue.Should().BeNull();
            page.Items.Should().OnlyContain(item => item.Disposition != IdentityEventItemDisposition.Malformed);
            foreach (var item in page.Items) if (item.Fact is { } fact) seen.Add(fact.Kind);
            if (seen.Contains(IdentityProviderEventKind.LoginFailure) && seen.Contains(IdentityProviderEventKind.Logout)
                && seen.Contains(IdentityProviderEventKind.PasswordResetRequested)
                && seen.Contains(IdentityProviderEventKind.TemporaryLockout)
                && seen.Contains(IdentityProviderEventKind.PasswordUpdated) && seen.Contains(IdentityProviderEventKind.CredentialUpdated)
                && seen.Contains(IdentityProviderEventKind.TotpUpdated)) break;
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(10))
                throw new InvalidOperationException("Owned provider actions did not yield all required selected event families: " + string.Join(',', seen));
            await Task.Delay(50);
        }
        // This fixture owns all deliberate account/policy mutations; ingestion itself has only view-events.
        // Pinned ResetPassword sets UPDATE_PASSWORD; this real journey emits its
        // credential/password facts, rather than inventing a RESET_PASSWORD row.
    }

    private static async Task CreateUserAsync(KeycloakAdminClient admin, string email, string password) =>
        (await admin.PostAsync("users", new
        {
            username = email,
            email,
            emailVerified = true,
            enabled = true,
            firstName = "Owned",
            lastName = "Event proof",
            credentials = new[] { new { type = "password", value = password, temporary = false } }
        }))
        .Should().Be(HttpStatusCode.Created);

    private static IIdentityEventProvider Create(KeycloakFixture fixture, IdentityAuthorityId authority, string realm)
    {
        var type = typeof(IdentityEventProviderOptions).Assembly.GetType("LearnStack.Infrastructure.Identity.ProviderEvents.KeycloakIdentityEventProvider", true)!;
        return (IIdentityEventProvider)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
            [new IdentityEventProviderOptions(authority, fixture.Issuer, realm), new Secret(), new Resilience(), KeycloakHttp.CreateHandler(fixture.RootCertificate)], null)!;
    }
    private sealed class Secret : ISecretProvider
    { public string? GetSecret(string name) => name == "IdentityEvents:ClientSecret" ? KeycloakFixture.EventReaderSecret : null; }
    private sealed class Resilience : IProviderResilience<IIdentityEventProvider>
    { public string PortName => "identityEvents"; public ResiliencePipeline Pipeline { get; } = ResiliencePipeline.Empty; }
}
