using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.Modules.Tenancy.Application.Contracts.PublicReads;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tests.Integration.Database;
using LearnStack.Tools.Seeder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>Real warm provider → native BFF → real application-role API; browser proof is separate.</summary>
[Collection(PublicReadTestGroup.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class TenantBffSessionTests(PublicReadFixture database, ITestOutputHelper output)
{
    [Fact]
    public async Task Parent_EOF_during_held_build_joins_acquisition_and_removes_its_private_tree()
    {
        await AssertStartupFailureSurvivesCleanupAsync(observerThrows: false);
        await AssertStartupFailureSurvivesCleanupAsync(observerThrows: true);
        await using var unavailable = new UnavailableTenantIssuer();
        using var authority = new KeycloakTls();
        await using var native = await BffNativeDriver.StartAsync(RepositoryRoot(), NoNetworkConfiguration(unavailable, authority), heldBuild: true);
        var root = native.TemporaryRoot;
        Directory.EnumerateFileSystemEntries(root).Should().NotBeEmpty("the owned build is held before native readiness");
        await native.DisposeAsync();
        Directory.Exists(root).Should().BeFalse("EOF must cancel acquisition without waiting for the suspended command iterator");
        unavailable.Connections.Should().Be(0);
    }

    private static async Task AssertStartupFailureSurvivesCleanupAsync(bool observerThrows)
    {
        var root = Directory.CreateTempSubdirectory("learnstack-bff-start-counter-");
        var receiptPath = Path.Combine(root.FullName, "owned.json");
        string? retained = null;
        var childAbsenceConfirmed = false;
        try
        {
            var scripts = Path.Combine(root.FullName, "frontend/apps/web/scripts");
            Directory.CreateDirectory(scripts);
            await File.WriteAllTextAsync(Path.Combine(scripts, "verify-bff-session.mjs"), $$"""
                import { mkdirSync, writeFileSync } from 'node:fs';
                import { join } from 'node:path';
                process.stdin.once('data', () => {
                  mkdirSync(join(process.env.TMPDIR, 'retained'));
                  writeFileSync({{JsonSerializer.Serialize(receiptPath)}}, JSON.stringify({ pid: process.pid, temporary: process.env.TMPDIR }));
                  process.stderr.write('BFF session fixture failed: compile\nBFF session cleanup failed.\n');
                  process.stdin.destroy();
                  process.exitCode = 1;
                });
                """);
            var observed = 0;
            Exception? failure = null;
            try
            {
                await BffNativeDriver.StartAsync(root.FullName, new { protocol = 1 },
                    secondaryCleanupFailure: () =>
                    {
                        observed++;
                        if (observerThrows) throw new InvalidOperationException("Deliberate closed observer failure.");
                    });
            }
            catch (Exception error) when (error.GetType() != typeof(OutOfMemoryException)
                && error.GetType() != typeof(StackOverflowException))
            { failure = error; }
            using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(receiptPath));
            var candidate = receipt.RootElement.GetProperty("temporary").GetString()!;
            Path.GetDirectoryName(candidate).Should().Be(Path.TrimEndingDirectorySeparator(Path.GetTempPath()));
            Path.GetFileName(candidate).Should().StartWith("learnstack-bff-driver-");
            retained = candidate;
            Directory.Exists(Path.Combine(retained, "retained")).Should().BeTrue();
            var absent = false;
            try
            {
                using var child = Process.GetProcessById(receipt.RootElement.GetProperty("pid").GetInt32());
                absent = child.HasExited;
            }
            catch (ArgumentException) { absent = true; }
            childAbsenceConfirmed = absent;
            absent.Should().BeTrue("the failed driver still joins its actual owned child before returning the original error");
            failure.Should().BeOfType<InvalidOperationException>().Which.Message.Should()
                .Be("BFF native fixture did not reach the closed ready/compile_cleanup_failed phase.");
            observed.Should().Be(1, "the actual retained-tree disposal error must be separately observed");
        }
        finally
        {
            // Only this counter's independently witnessed retained tree is removed.
            // Its presence must first make actual driver disposal fail.
            if (childAbsenceConfirmed)
            {
                if (retained is not null && Directory.Exists(retained)) Directory.Delete(retained, recursive: true);
                root.Delete(recursive: true);
            }
        }
    }

    [Fact]
    public async Task Early_parent_disposal_joins_and_removes_its_private_native_tree()
    {
        await using var unavailable = new UnavailableTenantIssuer();
        using var authority = new KeycloakTls();
        await using var native = await BffNativeDriver.StartAsync(RepositoryRoot(), NoNetworkConfiguration(unavailable, authority));
        var root = native.TemporaryRoot;
        Directory.Exists(root).Should().BeTrue();
        await native.DisposeAsync();
        Directory.Exists(root).Should().BeFalse("closed parent input must let Node join and remove its actual private tree");
        unavailable.Connections.Should().Be(0);
    }

    [Fact]
    public async Task Nonzero_native_shutdown_is_rejected_instead_of_publishing_success()
    {
        await using var unavailable = new UnavailableTenantIssuer();
        using var authority = new KeycloakTls();
        await using var native = await BffNativeDriver.StartAsync(RepositoryRoot(), NoNetworkConfiguration(unavailable, authority), shutdownCounter: true);
        var root = native.TemporaryRoot;
        await FluentActions.Awaiting(native.CompleteAsync).Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("BFF native fixture did not reach the closed complete/native-stop phase.");
        await native.DisposeAsync();
        Directory.Exists(root).Should().BeFalse();
        unavailable.Connections.Should().Be(0);
    }

    [Fact]
    public async Task Enabled_native_startup_and_shutdown_make_no_provider_or_API_connection()
    {
        await using var unavailable = new UnavailableTenantIssuer();
        using var authority = new KeycloakTls();
        await using var native = await BffNativeDriver.StartAsync(RepositoryRoot(), new
        {
            protocol = 1,
            mode = "real",
            issuer = unavailable.Issuer,
            clientSecret = KeycloakFixture.WebClientSecret,
            authority = authority.RootCertificate.ExportCertificatePem(),
            apiOrigin = new UriBuilder("http", "127.0.0.1", new Uri(unavailable.Issuer).Port).Uri.GetLeftPart(UriPartial.Authority),
            secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(),
            sites = SeedData.All.Select(site => new
            {
                host = site.Host + ":3000",
                displayName = site.DisplayName,
                locale = site.Curriculum!.Locales.Single(locale => locale.IsDefault).Locale
            }),
        });
        await native.CompleteAsync();
        unavailable.Connections.Should().Be(0, "enabled native construction and successful joined stop must not discover the issuer or bootstrap an unsolicited request");
    }

    /// <summary>Catalogue: Bff_Host_Cookies_And_History_Are_Isolated (ADR-0070).</summary>
    [Fact]
    public async Task Native_https_browser_stores_host_cookies_and_revalidates_logout_history_with_a_live_counter_control()
    {
        async Task<string?> ClosedErrorAsync(string text)
        {
            using var bytes = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
            using var reader = new StreamReader(bytes);
            return await BffNativeDriver.ReadErrorsAsync(reader, CancellationToken.None);
        }
        (await ClosedErrorAsync("BFF browser fixture failed: browser-launch\n")).Should().Be("browser-launch");
        (await ClosedErrorAsync("BFF browser fixture failed: browser-launch\nBFF browser cleanup failed.\n"))
            .Should().Be("browser-launch_cleanup_failed");
        foreach (var stage in new[] { "browser-executable", "browser-spawn", "browser-devtools", "browser-devtools-inactive",
                     "browser-devtools-unreadable", "browser-devtools-missing", "browser-devtools-empty", "browser-devtools-malformed",
                     "browser-not-live", "browser-connect", "browser-context" })
        {
            (await ClosedErrorAsync("BFF browser fixture failed: " + stage + "\n")).Should().Be(stage);
            (await ClosedErrorAsync("BFF browser fixture failed: " + stage + "\nBFF browser cleanup failed.\n"))
                .Should().Be(stage + "_cleanup_failed");
        }
        (await ClosedErrorAsync("BFF browser fixture failed: browser-launch\nPRIVATE-CANARY\n"))
            .Should().Be("browser-launch_unexpected_output");
        (await ClosedErrorAsync("BFF session fixture failed: native-stop\nBFF session cleanup failed.\n"))
            .Should().Be("native-stop_cleanup_failed");
        (await ClosedErrorAsync("BFF session fixture failed: native-stop\nBFF browser cleanup failed.\n"))
            .Should().Be("native-stop_unexpected_output");
        var input = new
        {
            protocol = 1,
            sites = SeedData.All.Select(site => new
            {
                host = site.Host + ":3000",
                displayName = site.DisplayName,
                locale = site.Curriculum!.Locales.Single(locale => locale.IsDefault).Locale,
            })
        };
        var timer = Stopwatch.StartNew();
        async Task<BffBrowserReceipt> JourneyAsync(bool counter)
        {
            var journey = counter ? "counter" : "positive";
            try
            {
                return await BffNativeDriver.BrowserAsync(RepositoryRoot(), input, counter,
                    () => output.WriteLine("BFF browser cleanup;journey=" + journey + ";state=failed."));
            }
            catch (Exception error) when (error.GetType() != typeof(OutOfMemoryException)
                && error.GetType() != typeof(StackOverflowException))
            {
                try { output.WriteLine("BFF browser failure;journey=" + journey + "."); }
                catch (Exception observation) when (observation.GetType() != typeof(OutOfMemoryException)
                    && observation.GetType() != typeof(StackOverflowException))
                { }
                throw;
            }
        }
        var positive = await JourneyAsync(counter: false);
        var counter = await JourneyAsync(counter: true);
        counter.Version.Should().Be(positive.Version);
        output.WriteLine("Actual native HTTPS browser: stock Chromium={0}; hosts=2; cookie/send/isolation/keyboard/extension/back=true; natural persisted restores={1}; restoration counter killed at {2}; total including joined cleanup={3:F3}s.",
            positive.Version, positive.NaturalRestores, counter.CounterWitness, timer.Elapsed.TotalSeconds);
        output.WriteLine("This is fake-grant/API browser proof, separate from the actual warm-provider/application-role HTTP chain; the explicit event is not labelled a natural BFCache restore.");
    }

    [Fact]
    public async Task Reconciled_warm_realm_serves_both_native_hosts_with_real_refresh_logout_and_persisted_events()
    {
        var root = RepositoryRoot();
        var historical = JsonNode.Parse(await TenantRealmStockBaselineTests.HistoricalImportAsync(root))!.AsObject();
        var passwords = historical["users"]!.AsArray().ToDictionary(user => user!["username"]!.GetValue<string>(),
            user => user!["credentials"]![0]!["value"]!.GetValue<string>(), StringComparer.Ordinal);
        var journey = Stopwatch.StartNew();
        await using var provider = await KeycloakFixture.StartAsync(historical.ToJsonString());
        var resources = await provider.StockProviderResourcesAsync();
        try
        {
            var reconciled = await KeycloakReconciliationDriver.RunAsync(provider);
            reconciled.Success.Should().BeTrue("closed warm qualification: {0}/{1}", reconciled.Stage, reconciled.Reason);
            reconciled.PreparationDispatched.Should().BeTrue();
            reconciled.Mutations.Should().BeGreaterThan(0);
            var repeated = await KeycloakReconciliationDriver.RunAsync(provider);
            repeated.Success.Should().BeTrue();
            repeated.Mutations.Should().Be(0);
            using var admin = await provider.CreateAdminClientAsync();
            var events = await admin.GetAsync("events/config");
            events.GetProperty("eventsEnabled").GetBoolean().Should().BeTrue();
            events.GetProperty("eventsExpiration").GetInt64().Should().Be(604800);
            var auditBefore = await AuditCountsAsync();
            foreach (var tenant in SeedData.All)
            {
                var identity = SeedData.IdentityUsers.First(user => user.Tenant == tenant);
                var password = passwords.GetValueOrDefault(identity.Username) ?? KeycloakFixture.DemoPassword;
                var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
                await using var api = StartApi(secret, provider);
                var readsBefore = database.Observation.Reads;
                var started = Stopwatch.StartNew();
                await using var native = await BffNativeDriver.StartAsync(root, new
                {
                    protocol = 1,
                    mode = "real",
                    issuer = provider.Issuer,
                    clientSecret = KeycloakFixture.WebClientSecret,
                    authority = provider.RootCertificate.ExportCertificatePem(),
                    apiOrigin = api.Origin,
                    secret,
                    sites = SeedData.All.Select(site => new
                    {
                        host = site.Host + ":3000",
                        displayName = site.DisplayName,
                        locale = site.Curriculum!.Locales.Single(locale => locale.IsDefault).Locale
                    }),
                });
                using var browser = provider.Browser();
                var user = await admin.UserAsync(identity.Username);
                var userPath = "users/" + user.GetProperty("id").GetString();
                var cookies = new BffWireCookies();
                var session = await LoginAsync(native, browser, identity, password, cookies, api.Observed);
                session.GetProperty("expiresAt").GetDouble().Should().BeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeMilliseconds(),
                    "the ordinary configured provider idle is used before the separate clamp variant");
                var admitted = await native.RequestAsync(Host(tenant), "/studio", cookies.Header);
                admitted.Status.Should().Be(200);
                admitted.Rendered.Should().BeTrue();
                admitted.PrivateFree.Should().BeTrue();
                admitted.Cache.Should().Contain("no-store");
                var priorReads = api.Observed.Reads.Count(row => row.Actor is not null);
                var other = SeedData.All.Single(site => site != tenant);
                var cross = await native.RequestAsync(Host(other), "/studio", cookies.Header);
                cross.Status.Should().Be(303);
                cross.Location.Should().Be("https://" + Host(other) + "/api/auth/signin?returnTo=%2Fstudio");
                cross.Cookies.Should().BeEmpty("a copied host cookie cannot authorize edits on the other host");
                cross.PrivateFree.Should().BeTrue();
                cross.Cache.Should().Contain("no-store");
                api.Observed.Reads.Count(row => row.Actor is not null).Should().Be(priorReads,
                    "copied HTTP cookies on another host cannot reach an authenticated AccountSite reader");
                var initialProviderSession = await admin.GetAsync(userPath + "/sessions");
                initialProviderSession.GetArrayLength().Should().Be(1);
                await WaitForProviderClockAsync(provider, initialProviderSession[0].GetProperty("lastAccess").GetInt64() + 1000);
                var extended = await ExtendAsync(native, tenant, cookies, session, api.Observed);
                extended.GetProperty("expiresAt").GetDouble().Should().BeGreaterThan(session.GetProperty("expiresAt").GetDouble(),
                    "an explicit fresh-access extension must actually renew provider idle");
                var beforeLogoutCookies = cookies.Header;
                var logout = await native.RequestAsync(Host(tenant), "/api/auth/signout", beforeLogoutCookies, "POST",
                    Form("csrfToken", extended.GetProperty("csrfToken").GetString()!));
                logout.Status.Should().Be(303);
                await AnonymousAsync(native, tenant, beforeLogoutCookies);
                cookies.Apply(logout);
                var returned = await browser.CompleteBffLogoutAsync(PrivateLocation(logout));
                var completion = await native.RequestAsync(Host(tenant), returned.PathAndQuery, cookies.Header);
                completion.Status.Should().Be(303);
                cookies.Apply(completion);
                await AnonymousAsync(native, tenant, cookies);
                (await admin.GetAsync(userPath + "/sessions")).GetArrayLength().Should().Be(0,
                    "actual provider logout ends the corresponding provider SSO session");

                // Kill only this fixture user's provider session; BFF alone retains
                // its refresh credential and must encounter the actual provider error.
                using var failedBrowser = provider.Browser();
                var failedCookies = new BffWireCookies();
                var failedSession = await LoginAsync(native, failedBrowser, identity, password, failedCookies, api.Observed);
                var beforeFailedRefreshCookies = failedCookies.Header;
                (await admin.PostAsync(userPath + "/logout", new { })).Should().Be(HttpStatusCode.NoContent);
                var failed = await native.RequestAsync(Host(tenant), "/api/auth/extend", failedCookies.Header, "POST",
                    Form("csrfToken", failedSession.GetProperty("csrfToken").GetString()!));
                failed.Status.Should().Be(400);
                failed.PrivateFree.Should().BeTrue();
                await AnonymousAsync(native, tenant, beforeFailedRefreshCookies);
                failedCookies.Apply(failed);
                await AnonymousAsync(native, tenant, failedCookies);

                // Case-owned real provider policy compresses time, not a fake BFF
                // clock or transport. Two actual refreshes would exceed max without
                // Keycloak's clamp. The committed 1800/36000 settings stay unchanged.
                (await admin.PutAsync("", new { ssoSessionIdleTimeout = 15, ssoSessionMaxLifespan = 30 }))
                    .Should().Be(HttpStatusCode.NoContent);
                using var maximumBrowser = provider.Browser();
                var maximumCookies = new BffWireCookies();
                var maximumSession = await LoginAsync(native, maximumBrowser, identity, password, maximumCookies, api.Observed);
                var providerSessions = await admin.GetAsync(userPath + "/sessions");
                providerSessions.GetArrayLength().Should().Be(1);
                var maximum = providerSessions[0].GetProperty("start").GetInt64() + 30_000;
                for (var round = 0; round < 2; ++round)
                {
                    await WaitForProviderClockAsync(provider, maximum - 30_000 + (round + 1) * 10_000);
                    var next = await ExtendAsync(native, tenant, maximumCookies, maximumSession, api.Observed);
                    next.GetProperty("expiresAt").GetDouble().Should().BeLessThanOrEqualTo(maximum + 1000,
                        "real refresh never widens the actual provider SSO maximum (integer-second rounding only)");
                    next.GetProperty("expiresAt").GetDouble().Should().BeGreaterThan(maximumSession.GetProperty("expiresAt").GetDouble());
                    maximumSession = next;
                }
                (await admin.PutAsync("", new { ssoSessionIdleTimeout = 1800, ssoSessionMaxLifespan = 36000 }))
                    .Should().Be(HttpStatusCode.NoContent);
                (await admin.PostAsync(userPath + "/logout", new { })).Should().Be(HttpStatusCode.NoContent);
                var opposite = SeedData.IdentityUsers.First(user => user.Tenant == other);
                using var oppositeBrowser = provider.Browser();
                var oppositeCookies = new BffWireCookies();
                var beforeOpposite = await AuditCountsAsync();
                var beforeOppositeReads = api.Observed.Reads.Count(row => row.Actor is not null);
                var oppositeCsrf = await native.RequestAsync(Host(tenant), "/api/auth/csrf", oppositeCookies.Header);
                oppositeCsrf.Status.Should().Be(200);
                oppositeCookies.Apply(oppositeCsrf);
                var oppositeBegin = await native.RequestAsync(Host(tenant), "/api/auth/signin/keycloak", oppositeCookies.Header, "POST",
                    Form("csrfToken", oppositeCsrf.Data.GetProperty("csrfToken").GetString()!));
                oppositeBegin.Status.Should().Be(303);
                oppositeCookies.Apply(oppositeBegin);
                var oppositeCallback = await oppositeBrowser.CompleteBffLoginAsync(PrivateLocation(oppositeBegin), opposite.Username,
                    passwords.GetValueOrDefault(opposite.Username) ?? KeycloakFixture.DemoPassword);
                var oppositeCompleted = await native.RequestAsync(Host(tenant), oppositeCallback.PathAndQuery, oppositeCookies.Header);
                oppositeCompleted.Status.Should().Be(400);
                oppositeCompleted.Data.ValueKind.Should().Be(JsonValueKind.Null,
                    "the real browser request asks for HTML; closed JSON reason mapping is covered separately");
                oppositeCompleted.Rendered.Should().BeFalse("refusal cannot render the admitted institution view");
                oppositeCompleted.Cache.Should().Contain("no-store");
                oppositeCompleted.Cookies.Should().NotContain(value => value.StartsWith("__Host-ls-session=", StringComparison.Ordinal),
                    "the mismatched grant cannot publish a new session handle");
                oppositeCompleted.PrivateFree.Should().BeTrue();
                await AnonymousAsync(native, tenant, oppositeCookies.Header);
                api.Observed.Reads.Count(row => row.Actor is not null).Should().Be(beforeOppositeReads,
                    "a real opposite-tenant persona is refused before the authenticated AccountSite reader and publication");
                (await AuditCountsAsync()).Should().Equal(SeedData.All.Select((site, index) => beforeOpposite[index] + (site == tenant ? 1 : 0)),
                    "the authenticated JWT mismatch writes exactly one security audit under the defended host tenant");
                var oppositeUser = await admin.UserAsync(opposite.Username);
                (await admin.PostAsync("users/" + oppositeUser.GetProperty("id").GetString() + "/logout", new { }))
                    .Should().Be(HttpStatusCode.NoContent);
                var authenticatedReads = api.Observed.Reads.Where(row => row.Actor is not null).ToArray();
                authenticatedReads.Length.Should().Be(7, "three actual grants, three successful refreshes and the protected document read each require their own AccountSite witness");
                authenticatedReads.All(row => row.Actor == identity.UserId.Value && row.Tenant == tenant.TenantId.Value
                    && row.HostTenant == tenant.TenantId.Value
                    && row.Organization == (tenant.MapHostToDefaultOrganization ? tenant.DefaultOrganization.OrganizationId.Value : null))
                    .Should().BeTrue("every BFF witness uses actual JWT actor and independently resolved host/organization");
                database.Observation.Reads.Should().BeGreaterThan(readsBefore,
                    "the retained physical ReadOnly application-role observer actually ran");
                await native.CompleteAsync();
                output.WriteLine("Real warm BFF host completed: locale={0}; authenticated witnesses={1}; native lifecycle={2:F3}s.",
                    tenant.Curriculum!.Locales.Single(locale => locale.IsDefault).Locale, authenticatedReads.Length, started.Elapsed.TotalSeconds);
            }
            foreach (var type in new[] { "LOGIN", "CODE_TO_TOKEN", "REFRESH_TOKEN", "REFRESH_TOKEN_ERROR", "LOGOUT" })
            {
                var stored = await admin.GetAsync("events?max=100&type=" + type);
                stored.EnumerateArray().Count(row => row.GetProperty("type").GetString() == type).Should().BeGreaterThan(0,
                    "the actual BFF journey persists provider event type {0}", type);
            }
            (await AuditCountsAsync()).Should().Equal(auditBefore.Select(count => count + 1),
                "only the one deliberately mismatched persona per host adds a security audit; query Off and provider-event ownership add none");
            output.WriteLine("Real warm BFF proof: hosts=2; real refresh/maximum/logout/error/events=true; zero-write rerun=true; fixture={0:F3}s; journey={1:F3}s.",
                provider.StartupElapsed.TotalSeconds, journey.Elapsed.TotalSeconds);
        }
        finally
        {
            var cleanup = Stopwatch.StartNew();
            await provider.DisposeAsync();
            await KeycloakFixture.AssertStockResourcesAbsentAsync(resources);
            output.WriteLine("BFF provider joined cleanup={0:F3}s; two owned containers and network independently absent=true.", cleanup.Elapsed.TotalSeconds);
        }
    }

    private static async Task<JsonElement> LoginAsync(BffNativeDriver native, KeycloakBrowserFlow browser,
        SeedIdentity identity, string password, BffWireCookies cookies, TenantRealmApiObservation observation)
    {
        var before = observation.Reads.Count(row => row.Actor is not null);
        var host = Host(identity.Tenant);
        var csrf = await native.RequestAsync(host, "/api/auth/csrf", cookies.Header);
        csrf.Status.Should().Be(200);
        cookies.Apply(csrf);
        var begin = await native.RequestAsync(host, "/api/auth/signin/keycloak", cookies.Header, "POST",
            Form("csrfToken", csrf.Data.GetProperty("csrfToken").GetString()!) + "&returnTo=%2Fstudio");
        begin.Status.Should().Be(303);
        cookies.Apply(begin);
        var callback = await browser.CompleteBffLoginAsync(PrivateLocation(begin), identity.Username, password);
        var completed = await native.RequestAsync(host, callback.PathAndQuery, cookies.Header);
        completed.Status.Should().Be(303);
        completed.PrivateFree.Should().BeTrue();
        cookies.Apply(completed);
        var session = await native.RequestAsync(host, "/api/auth/session", cookies.Header);
        session.Status.Should().Be(200);
        session.PrivateFree.Should().BeTrue();
        (session.Data.GetProperty("state").GetString() == "authenticated").Should().BeTrue();
        observation.Reads.Count(row => row.Actor is not null).Should().Be(before + 1, "each actual grant obtains a new API authority witness before publication");
        return session.Data;
    }

    private static async Task<JsonElement> ExtendAsync(BffNativeDriver native, SeedTenant tenant, BffWireCookies cookies, JsonElement session, TenantRealmApiObservation observation)
    {
        var before = observation.Reads.Count(row => row.Actor is not null);
        var response = await native.RequestAsync(Host(tenant), "/api/auth/extend", cookies.Header, "POST",
            Form("csrfToken", session.GetProperty("csrfToken").GetString()!));
        response.Status.Should().Be(200);
        response.PrivateFree.Should().BeTrue();
        cookies.Apply(response);
        (response.Data.GetProperty("state").GetString() == "authenticated").Should().BeTrue();
        observation.Reads.Count(row => row.Actor is not null).Should().Be(before + 1, "each actual refresh revalidates API authority before publishing its revision");
        return response.Data;
    }

    private static async Task AnonymousAsync(BffNativeDriver native, SeedTenant tenant, BffWireCookies cookies)
        => await AnonymousAsync(native, tenant, cookies.Header);

    private static async Task AnonymousAsync(BffNativeDriver native, SeedTenant tenant, string originalCookies)
    {
        var response = await native.RequestAsync(Host(tenant), "/api/auth/session", originalCookies);
        response.Status.Should().Be(200);
        response.PrivateFree.Should().BeTrue();
        (response.Data.GetProperty("state").GetString() == "anonymous").Should().BeTrue();
    }

    private static async Task WaitForProviderClockAsync(KeycloakFixture provider, long targetMilliseconds)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            if (await provider.ObserveProviderUtcAsync(deadline.Token) >= targetMilliseconds) return;
            await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token);
        }
    }

    private ApiHost StartApi(string secret, KeycloakFixture provider)
    {
        var observation = new TenantRealmApiObservation();
        var factory = database.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Tenancy:TrustedHop:Networks:0", "127.0.0.1/32");
            builder.UseSetting("Tenancy:TrustedHop:Secrets:0", secret);
            builder.UseSetting(TenantAuthenticationOptions.IssuerConfigurationKey, provider.Issuer);
            builder.ConfigureTestServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton(_ => new TenantAuthenticationBackchannel(KeycloakHttp.CreateHandler(provider.RootCertificate))));
                services.AddScoped<ObservedPublicConfigurationReader>();
                services.AddScoped<IPublicTenantConfigurationReader>(scope => new TenantRealmContextReader(
                    scope.GetRequiredService<ObservedPublicConfigurationReader>(), scope.GetRequiredService<ITenantContext>(),
                    scope.GetRequiredService<IUnitOfWork>(), observation));
            });
        });
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var address = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new(factory, client, address, observation);
    }

    private async Task<long[]> AuditCountsAsync() => [await database.AuditCountAsync(SeedData.English), await database.AuditCountAsync(SeedData.Yoga)];
    private static object NoNetworkConfiguration(UnavailableTenantIssuer unavailable, KeycloakTls authority) => new
    {
        protocol = 1,
        mode = "real",
        issuer = unavailable.Issuer,
        clientSecret = KeycloakFixture.WebClientSecret,
        authority = authority.RootCertificate.ExportCertificatePem(),
        apiOrigin = new UriBuilder("http", "127.0.0.1", new Uri(unavailable.Issuer).Port).Uri.GetLeftPart(UriPartial.Authority),
        secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(),
        sites = SeedData.All.Select(site => new
        {
            host = site.Host + ":3000",
            displayName = site.DisplayName,
            locale = site.Curriculum!.Locales.Single(locale => locale.IsDefault).Locale
        }),
    };
    private static string Host(SeedTenant tenant) => tenant.Host + ":3000";
    private static string Form(string key, string value) => key + "=" + Uri.EscapeDataString(value);
    private static Uri PrivateLocation(BffWireResponse response) => Uri.TryCreate(response.Location, UriKind.Absolute, out var uri)
        ? uri : throw new InvalidOperationException("BFF fixture lacks its expected private redirect.");
    internal static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend/pnpm-lock.yaml"))) return directory.FullName;
        throw new InvalidOperationException("Cannot find BFF session fixture source.");
    }
    private sealed record ApiHost(WebApplicationFactory<Program> Factory, HttpClient Client, string Origin,
        TenantRealmApiObservation Observed) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { Client.Dispose(); await Factory.DisposeAsync(); }
    }
}
