using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Tools.Seeder;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace LearnStack.Tests.Integration.TenancyIdentity;

public sealed partial class TenantBffSessionTests
{
    [Fact]
    public async Task Provider_backchannel_closes_exact_SSO_across_hosts_and_requires_actual_TLS_bridge()
    {
        await using var provider = await KeycloakFixture.StartAsync(callbackTrust: false);
        using var admin = await provider.CreateAdminClientAsync();
        await using var database = await KeycloakIdentityDatabase.CreateAsync(publicDatabase, provider);
        await SeedAdministrationOriginsAsync(database);
        var identity = SeedData.IdentityMemberships.Single(value => value.Identity.Tenant == SeedData.English && value.IsFirstAdmin).Identity;
        var user = await admin.UserAsync(identity.Username);
        var userPath = "users/" + user.GetProperty("id").GetString();
        var callbackPort = UnusedCallbackPort();
        var callbackUrl = "https://host.docker.internal:" + callbackPort + "/api/auth/backchannel/keycloak";
        var configured = await KeycloakReconciliationDriver.RunAsync(provider, backchannelUrl: callbackUrl);
        configured.Success.Should().BeTrue();
        var repeated = await KeycloakReconciliationDriver.RunAsync(provider, backchannelUrl: callbackUrl);
        repeated.Success.Should().BeTrue();
        repeated.Mutations.Should().Be(0, "matching provider callback configuration is a zero-write rerun");
        var userId = user.GetProperty("id").GetString();
        var client = await admin.ClientAsync("learnstack-web");
        var clientId = client.GetProperty("id").GetString();
        client.GetProperty("frontchannelLogout").GetBoolean().Should().BeFalse();
        var attributes = client.GetProperty("attributes");
        (attributes.GetProperty("backchannel.logout.url").GetString() == callbackUrl).Should().BeTrue();
        attributes.GetProperty("backchannel.logout.session.required").GetString().Should().Be("true");
        attributes.GetProperty("backchannel.logout.revoke.offline.tokens").GetString().Should().Be("false");

        foreach (var mode in new[] { "disabled", "untrusted", "trusted" })
        {
            if (mode == "trusted")
            {
                await provider.RecreateWithCallbackTrustAsync();
                ((await admin.UserAsync(identity.Username)).GetProperty("id").GetString() == userId).Should().BeTrue(
                    "the owned provider restart preserves the existing administrator identity");
                ((await admin.ClientAsync("learnstack-web")).GetProperty("id").GetString() == clientId).Should().BeTrue(
                    "the owned provider restart preserves the client registration");
                var afterRestart = await KeycloakReconciliationDriver.RunAsync(provider, backchannelUrl: callbackUrl);
                afterRestart.Success.Should().BeTrue();
                afterRestart.Mutations.Should().Be(0, "trust recovery must not rewrite provider configuration");
            }
            var enabled = mode != "disabled";
            var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            var sourceSecret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            await using var api = StartApi(database, secret, sourceSecret, provider);
            var configuration = JsonSerializer.SerializeToNode(NativeConfiguration(provider, database, api, secret, sourceSecret))!.AsObject();
            if (enabled) configuration["backchannel"] = JsonSerializer.SerializeToNode(new
            {
                url = callbackUrl,
                certificate = provider.CallbackCertificate,
                privateKey = provider.CallbackPrivateKey,
                authority = provider.RootCertificate.ExportCertificatePem(),
            });
            await using var native = await BffNativeDriver.StartAsync(RepositoryRoot(), configuration);
            using var originalBrowser = provider.Browser();
            var first = new BffWireCookies();
            var firstSession = await LoginAsync(native, originalBrowser, identity, KeycloakFixture.DemoPassword, first, api.Observed);
            var originalSession = (await admin.GetAsync(userPath + "/sessions")).EnumerateArray()
                .Select(value => value.GetProperty("id").GetString()!).ToArray();
            originalSession.Length.Should().Be(1, "the preceding case retires its actual provider session");

            var second = new BffWireCookies();
            var alternateHost = identity.Tenant.Host + ":3000";
            await LoginAsync(native, originalBrowser, identity, KeycloakFixture.DemoPassword, second, api.Observed, atHost: alternateHost);
            originalBrowser.BffSsoCallbacks.Should().Be(1, "both host grants use the same actual provider SSO session");
            (await admin.GetAsync(userPath + "/sessions")).GetArrayLength().Should().Be(1);
            using var independentBrowser = provider.Browser();
            var independent = new BffWireCookies();
            await LoginAsync(native, independentBrowser, identity, KeycloakFixture.DemoPassword, independent, api.Observed);
            (await admin.GetAsync(userPath + "/sessions")).GetArrayLength().Should().Be(2);
            var readsBefore = api.Observed.Reads.Count;
            var actorReadsBefore = api.Observed.Reads.Count(row => row.Actor is not null);

            Uri? providerReturn = null;
            string? returnCookies = null;
            if (mode == "trusted")
            {
                // Use the already active two-host SSO rather than creating a
                // second journey that consumes the same visitor admission budget.
                var signout = await native.RequestAsync(Host(identity.Tenant), "/api/auth/signout", first.Header, "POST",
                    Form("csrfToken", firstSession.GetProperty("csrfToken").GetString()!));
                signout.Status.Should().Be(303);
                RequireCookiePresent(signout.Cookies, value => value.StartsWith("__Host-ls-logout-", StringComparison.Ordinal)
                    && !value.Contains("=;", StringComparison.Ordinal));
                first.Apply(signout);
                returnCookies = first.Header;
                var callbackReads = api.Observed.Reads.Count;
                providerReturn = await originalBrowser.CompleteBffLogoutAsync(PrivateLocation(signout));
                api.Observed.Reads.Count.Should().Be(callbackReads, "the actual RP back-channel callback performs no API bootstrap or business read");
            }
            else
            {
                // These configurations are counterfactual samples, not proof
                // that a callback was delivered to the native listener.
                (await admin.DeleteAsync("sessions/" + originalSession[0])).Should().Be(HttpStatusCode.NoContent);
                api.Observed.Reads.Count.Should().Be(readsBefore, "the machine callback does not perform API or tenant bootstrap reads");
            }
            foreach (var (host, cookies) in new[] { (Host(identity.Tenant), first), (alternateHost, second) })
            {
                var response = await native.RequestAsync(host, "/api/auth/session", cookies.Header);
                response.Status.Should().Be(200);
                response.Cache.Should().Be("no-store");
                response.PrivateFree.Should().BeTrue();
                response.Data.GetProperty("state").GetString().Should().Be(mode == "trusted" ? "anonymous" : "authenticated",
                    "this immediate observation records the configured counterfactual; it does not establish callback delivery timing");
            }
            var other = await native.RequestAsync(Host(identity.Tenant), "/api/auth/session", independent.Header);
            other.Data.GetProperty("state").GetString().Should().Be("authenticated", "another device's SSO target is preserved");
            api.Observed.Reads.Count(row => row.Actor is not null).Should().Be(actorReadsBefore,
                "passive session observation preserves its ordinary anonymous bootstrap but performs no authenticated business read");
            if (providerReturn is not null)
            {
                var completion = await native.RequestAsync(Host(identity.Tenant), providerReturn.PathAndQuery, returnCookies!);
                completion.Status.Should().Be(303);
                completion.Location.Should().Be("https://" + Host(identity.Tenant) + "/");
                RequireCookiePresent(completion.Cookies, value => value.StartsWith("__Host-ls-logout-", StringComparison.Ordinal)
                    && value.Contains("=;", StringComparison.Ordinal) && value.Contains("Max-Age=0", StringComparison.Ordinal));
                first.Apply(completion);
                var replay = await native.RequestAsync(Host(identity.Tenant), providerReturn.PathAndQuery, returnCookies!);
                replay.Status.Should().Be(400, "the original return proof is single-use even if its cookie is replayed");
                var remaining = (await admin.GetAsync(userPath + "/sessions")).EnumerateArray()
                    .Select(value => value.GetProperty("id").GetString()!).ToArray();
                remaining.Should().NotContain(originalSession[0], "real RP logout ends its exact provider SSO session");
                remaining.Should().HaveCount(1, "the independent provider session remains live");
            }
            using var laterBrowser = provider.Browser();
            var later = new BffWireCookies();
            var beforeLater = (await admin.GetAsync(userPath + "/sessions")).EnumerateArray()
                .Select(value => value.GetProperty("id").GetString()!).ToArray();
            await LoginAsync(native, laterBrowser, identity, KeycloakFixture.DemoPassword, later, api.Observed);
            var laterResponse = await native.RequestAsync(Host(identity.Tenant), "/api/auth/session", later.Header);
            laterResponse.Data.GetProperty("state").GetString().Should().Be("authenticated", "a new provider SSO session is not fenced by an older session's logout");
            if (mode == "trusted")
            {
                var withLater = (await admin.GetAsync(userPath + "/sessions")).EnumerateArray()
                    .Select(value => value.GetProperty("id").GetString()!).ToArray();
                var laterProviderSession = withLater.Except(beforeLater).Should().ContainSingle().Subject;
                (await admin.DeleteAsync("sessions/" + laterProviderSession)).Should().Be(HttpStatusCode.NoContent);
                var afterAdminLogout = await native.RequestAsync(Host(identity.Tenant), "/api/auth/session", later.Header);
                afterAdminLogout.Data.GetProperty("state").GetString().Should().Be("anonymous",
                    "the same trusted bridge also applies actual administrator-initiated logout");
            }
            await native.CompleteAsync();
            (await admin.PostAsync(userPath + "/logout", new { })).Should().Be(HttpStatusCode.NoContent);
            (await admin.GetAsync(userPath + "/sessions")).GetArrayLength().Should().Be(0);
        }
        output.WriteLine("Actual Keycloak logout: trusted callbacks close same-SSO hosts; distinct/later SSO remain independent; RP browser return is single-use; disabled and untrusted configurations are sampled immediately; same-port provider trust recovery and zero-write readback confirmed.");
    }

    private static int UnusedCallbackPort()
    {
        // Choose only a currently unbound port. A competing bind causes startup failure, never process eviction.
        using var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
