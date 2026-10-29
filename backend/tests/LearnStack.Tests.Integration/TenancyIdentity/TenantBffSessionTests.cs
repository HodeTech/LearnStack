using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
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
public sealed partial class TenantBffSessionTests(PublicReadFixture publicDatabase, ITestOutputHelper output)
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
            authorityId = Guid.CreateVersion7().ToString("D"),
            nativeSourceSecret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)),
            sites = NativeSites(),
        });
        await native.CompleteAsync();
        unavailable.Connections.Should().Be(0, "enabled native construction and successful joined stop must not discover the issuer or bootstrap an unsolicited request");
    }

    private static void RequirePrivateEqual(string? actual, string? expected, string because = "") =>
        string.Equals(actual, expected, StringComparison.Ordinal).Should().BeTrue(because);

    private static void RequirePrivateDifferent(string? actual, string? expected, string because = "") =>
        string.Equals(actual, expected, StringComparison.Ordinal).Should().BeFalse(because);

    private static void RequireCookiePresent(string[] cookies, Func<string, bool> predicate, string because = "") =>
        cookies.Any(predicate).Should().BeTrue(because);

    private static void RequireCookieAbsent(string[] cookies, Func<string, bool> predicate, string because = "") =>
        cookies.Any(predicate).Should().BeFalse(because);

    private static void RequireCookiesEmpty(string[] cookies, string because = "") =>
        cookies.Length.Should().Be(0, because);

    private static (string Name, bool ExpectedFailure, bool Failed, bool PrivateFree)[] ObservePrivateAssertionOutput()
    {
        var firstToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var secondToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var firstCookie = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var secondCookie = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        string[] canaries = [firstToken, secondToken, firstCookie, secondCookie];
        string[] cookies = ["__Host-ls-session=" + firstCookie + "; Path=/", "__Host-ls-browser=" + secondCookie + "; Path=/"];
        (string Name, bool ExpectedFailure, Action Check)[] controls =
        [
            ("equal_positive", false, () => RequirePrivateEqual(firstToken, firstToken)),
            ("equal_negative", true, () => RequirePrivateEqual(firstToken, secondToken)),
            ("different_positive", false, () => RequirePrivateDifferent(firstToken, secondToken)),
            ("different_negative", true, () => RequirePrivateDifferent(firstToken, firstToken)),
            ("present_positive", false, () => RequireCookiePresent(cookies, value => value.StartsWith("__Host-ls-session=", StringComparison.Ordinal))),
            ("present_negative", true, () => RequireCookiePresent(cookies, value => value.StartsWith("__Host-ls-entry=", StringComparison.Ordinal))),
            ("absent_positive", false, () => RequireCookieAbsent(cookies, value => value.StartsWith("__Host-ls-entry=", StringComparison.Ordinal))),
            ("absent_negative", true, () => RequireCookieAbsent(cookies, value => value.StartsWith("__Host-ls-session=", StringComparison.Ordinal))),
            ("empty_positive", false, () => RequireCookiesEmpty([])),
            ("empty_negative", true, () => RequireCookiesEmpty(cookies)),
        ];
        // Capture before any outer assertion scope: only closed names and Boolean
        // observations reach assertion formatting, never the deliberate canaries.
        return controls.Select(control =>
        {
            var error = Record.Exception(control.Check);
            var diagnostic = error?.ToString() ?? "";
            return (control.Name, control.ExpectedFailure, Failed: error is not null,
                PrivateFree: canaries.All(canary => !diagnostic.Contains(canary, StringComparison.Ordinal)));
        }).ToArray();
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
        var privateAssertions = ObservePrivateAssertionOutput();
        using (new FluentAssertions.Execution.AssertionScope())
        {
            privateAssertions.Should().OnlyContain(control => control.Failed == control.ExpectedFailure && control.PrivateFree,
                "the real fixture assertion helpers must retain their guards without printing private values on failure");
            (await ClosedErrorAsync("BFF browser fixture failed: browser-launch\n")).Should().Be("browser-launch");
            (await ClosedErrorAsync("BFF browser fixture failed: browser-launch\nBFF browser cleanup failed.\n"))
                .Should().Be("browser-launch_cleanup_failed");
            foreach (var stage in new[] { "browser-executable", "browser-spawn", "browser-devtools", "browser-devtools-inactive",
                         "browser-devtools-unreadable", "browser-devtools-missing", "browser-devtools-empty", "browser-devtools-malformed",
                         "browser-not-live", "browser-connect", "browser-context", "browser-entry-confirmation",
                         "browser-administration-pages", "browser-no-script",
                         "browser-no-script-anonymous", "browser-no-script-signin", "browser-no-script-confirm", "browser-no-script-select", "browser-no-script-controls", "browser-no-script-submit", "browser-no-script-origin", "browser-no-script-result", "browser-no-script-cache", "browser-no-script-saved", "browser-no-script-privacy", "browser-no-script-signout", "browser-no-script-headers",
                         "browser-administration-counter-progress", "browser-administration-counter-status", "browser-administration-counter-cache",
                         "browser-administration-counter-wrapper", "browser-administration-counter-heading", "browser-administration-counter-view",
                         "browser-administration-counter-alert", "browser-administration-counter-observer", "browser-administration-counter-private-output" })
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
        }
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
        async Task<BffBrowserReceipt> JourneyAsync(bool counter, bool unavailablePageCounter = false)
        {
            var journey = counter ? "counter" : unavailablePageCounter ? "unavailable_pages" : "positive";
            try
            {
                return await BffNativeDriver.BrowserAsync(RepositoryRoot(), input, counter,
                    () => output.WriteLine("BFF browser cleanup;journey=" + journey + ";state=failed."),
                    unavailablePageCounter: unavailablePageCounter);
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
        var unavailablePages = await JourneyAsync(counter: false, unavailablePageCounter: true);
        counter.Version.Should().Be(positive.Version);
        unavailablePages.Version.Should().Be(positive.Version);
        unavailablePages.CounterWitness.Should().Be("unavailable_view");
        output.WriteLine("Actual native HTTPS browser: stock Chromium={0}; hosts=2; ready-pages=10; no-script-hosts=2; cookie/send/isolation/keyboard/extension/back=true; natural persisted restores={1}; restoration counter killed at {2}; total including joined cleanup={3:F3}s.",
            positive.Version, positive.NaturalRestores, counter.CounterWitness, timer.Elapsed.TotalSeconds);
        output.WriteLine("The unavailable-page counter retains its protected wrapper and h1 but fails the same selected-page ready/content guard.");
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
            await using var database = await KeycloakIdentityDatabase.CreateAsync(publicDatabase, provider);
            await SeedAdministrationOriginsAsync(database);
            var auditBefore = await AuditCountsAsync(database);
            foreach (var tenant in SeedData.All)
            {
                var identity = SeedData.IdentityUsers.First(user => user.Tenant == tenant);
                var password = passwords.GetValueOrDefault(identity.Username) ?? KeycloakFixture.DemoPassword;
                var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
                var nativeSourceSecret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
                await using var api = StartApi(database, secret, nativeSourceSecret, provider);
                var readsBefore = database.Observation.Reads;
                var started = Stopwatch.StartNew();
                await using var native = await BffNativeDriver.StartAsync(root,
                    NativeConfiguration(provider, database, api, secret, nativeSourceSecret));
                var anonymousWindow = Stopwatch.StartNew();
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
                await AssertSelectedNativePagesAsync(database, native, api, identity, cookies);
                var priorReads = api.Observed.Reads.Count(row => row.Actor is not null);
                var other = SeedData.All.Single(site => site != tenant);
                var cross = await native.RequestAsync(Host(other), "/studio", cookies.Header);
                cross.Status.Should().Be(303);
                cross.Location.Should().Be("https://" + Host(other) + "/api/auth/signin?returnTo=%2Fstudio");
                RequireCookiesEmpty(cross.Cookies, "a copied host cookie cannot authorize edits on the other host");
                cross.PrivateFree.Should().BeTrue();
                cross.Cache.Should().Contain("no-store");
                api.Observed.Reads.Count(row => row.Actor is not null).Should().Be(priorReads,
                    "copied HTTP cookies on another host cannot reach an authenticated AccountSite reader");
                var initialProviderSession = await admin.GetAsync(userPath + "/sessions");
                initialProviderSession.GetArrayLength().Should().Be(1);
                await WaitForProviderClockAsync(provider, initialProviderSession[0].GetProperty("lastAccess").GetInt64() + 1000);
                var beforeRefreshAudit = await AuditCountsAsync(database);
                var extended = await ExtendAsync(native, tenant, cookies, session, api.Observed);
                (await AuditCountsAsync(database)).Should().Equal(beforeRefreshAudit,
                    "ordinary refresh uses Off self/AccountSite, never the audited admission POST");
                extended.GetProperty("expiresAt").GetDouble().Should().BeGreaterThan(session.GetProperty("expiresAt").GetDouble(),
                    "an explicit fresh-access extension must actually renew provider idle");
                var otherAccount = SeedData.IdentityUsers.First(person => person.Tenant != tenant);
                await AssertDifferentProviderAccountCannotReplaceLiveDestinationAsync(provider, native, api, identity,
                    otherAccount, passwords.GetValueOrDefault(otherAccount.Username) ?? KeycloakFixture.DemoPassword,
                    cookies, extended, database);
                await AssertUnknownNativeProfileWriteAsync(database, native, api, identity, cookies, extended);
                // Page reads and replacement/profile checks are one bounded
                // journey; start switch/logout/clamp in the next real window.
                await NextAnonymousWindowAsync(anonymousWindow);
                var oldScopeCookie = cookies.Header;
                var beforeSwitchAudit = await AuditCountsAsync(database);
                var branch = tenant.DefaultOrganization.OrganizationId.Value;
                var beforeBranchAdmissions = await ScopeAdmissionCountAsync(database, tenant, branch);
                var switched = await native.RequestAsync(Host(tenant), "/api/auth/switch", cookies.Header, "POST",
                    Form("csrfToken", extended.GetProperty("csrfToken").GetString()!) + "&"
                    + Form("tenantId", tenant.TenantId.Value.ToString("D")) + "&" + Form("organizationId", branch.ToString("D")));
                switched.Status.Should().Be(303);
                switched.PrivateFree.Should().BeTrue();
                cookies.Apply(switched);
                (cookies.Header != oldScopeCookie).Should().BeTrue("scope publication rotates the private handle");
                await AnonymousAsync(native, tenant, oldScopeCookie);
                var switchedSession = await native.RequestAsync(Host(tenant), "/api/auth/session", cookies.Header);
                switchedSession.Data.GetProperty("state").GetString().Should().Be("authenticated");
                RequirePrivateDifferent(switchedSession.Data.GetProperty("csrfToken").GetString(), extended.GetProperty("csrfToken").GetString());
                switchedSession.Data.GetProperty("expiresAt").GetDouble().Should().BeLessThanOrEqualTo(extended.GetProperty("expiresAt").GetDouble());
                extended = switchedSession.Data;
                (await AuditCountsAsync(database)).Should().Equal(beforeSwitchAudit,
                    "the derived branch switch adds no NULL-org admission event");
                (await ScopeAdmissionCountAsync(database, tenant, branch)).Should().Be(beforeBranchAdmissions + 1,
                    "the explicit switch adds exactly one MUST admission at the actual derived branch");
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
                // clock or transport. A 22-second idle leaves room around each
                // 10-second step; the second refresh exceeds the 35-second maximum
                // without Keycloak's clamp. Committed 1800/36000 settings stay intact.
                const int clampIdleSeconds = 22;
                const int clampMaximumSeconds = 35;
                (await admin.PutAsync("", new { ssoSessionIdleTimeout = clampIdleSeconds, ssoSessionMaxLifespan = clampMaximumSeconds }))
                    .Should().Be(HttpStatusCode.NoContent);
                using var maximumBrowser = provider.Browser();
                var maximumCookies = new BffWireCookies();
                var maximumSession = await LoginAsync(native, maximumBrowser, identity, password, maximumCookies, api.Observed);
                var providerSessions = await admin.GetAsync(userPath + "/sessions");
                providerSessions.GetArrayLength().Should().Be(1);
                var providerStart = providerSessions[0].GetProperty("start").GetInt64();
                var maximum = providerStart + clampMaximumSeconds * 1000L;
                for (var round = 0; round < 2; ++round)
                {
                    await WaitForProviderClockAsync(provider, providerStart + (round + 1) * 10_000);
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
                var beforeOpposite = await AuditCountsAsync(database);
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
                oppositeCompleted.Status.Should().Be(303);
                oppositeCompleted.Location.Should().Be("https://" + Host(tenant) + "/api/auth/entry");
                RequireCookieAbsent(oppositeCompleted.Cookies, value => value.StartsWith("__Host-ls-session=", StringComparison.Ordinal),
                    "tenantless identity alone cannot publish a tenant session");
                oppositeCookies.Apply(oppositeCompleted);
                var oppositeEntry = await native.RequestAsync(Host(tenant), "/api/auth/entry", oppositeCookies.Header);
                oppositeEntry.Status.Should().Be(200);
                var ownContext = oppositeEntry.Data.GetProperty("contexts").GetProperty("items").EnumerateArray()
                    .Single(item => item.GetProperty("tenantId").GetString() == other.TenantId.Value.ToString("D")
                        && item.GetProperty("organizationId").ValueKind == JsonValueKind.Null);
                ownContext.GetProperty("canUseCurrentHost").GetBoolean().Should().BeFalse();
                var destination = await native.RequestAsync(Host(tenant), "/api/auth/entry/select", oppositeCookies.Header, "POST",
                    Form("csrfToken", oppositeEntry.Data.GetProperty("csrfToken").GetString()!) + "&"
                    + Form("tenantId", other.TenantId.Value.ToString("D")));
                destination.Status.Should().Be(303);
                var destinationLocation = PrivateLocation(destination);
                var admittedDestination = ownContext.GetProperty("entryOrigin").GetString();
                admittedDestination.Should().BeOneOf("https://" + Host(other), "https://" + other.Host + ":3000");
                destinationLocation.GetLeftPart(UriPartial.Authority).Should().Be(admittedDestination,
                    "navigation preserves the API's independently verified destination instead of choosing a host in the BFF");
                destinationLocation.AbsolutePath.Should().Be("/api/auth/signin");
                var destinationQuery = QueryHelpers.ParseQuery(destinationLocation.Query);
                destinationQuery.Keys.Should().BeEquivalentTo(["returnTo", "contextHint"]);
                destinationQuery["contextHint"].ToString().Should().Be("tenant");
                RequireCookiesEmpty(destination.Cookies, "cross-host entry transfers no cookie or provider credential");
                destination.PrivateFree.Should().BeTrue();
                await AnonymousAsync(native, tenant, oppositeCookies.Header);
                api.Observed.Reads.Count(row => row.Actor is not null).Should().Be(beforeOppositeReads,
                    "own navigation and cross-host recovery grant no scoped AccountSite authority");
                (await AuditCountsAsync(database)).Should().Equal(beforeOpposite,
                    "Off discovery and a fresh destination sign-in link do not dispatch an admission POST");
                var oppositeUser = await admin.UserAsync(opposite.Username);
                (await admin.PostAsync("users/" + oppositeUser.GetProperty("id").GetString() + "/logout", new { }))
                    .Should().Be(HttpStatusCode.NoContent);
                var authenticatedReads = api.Observed.Reads.Where(row => row.Actor is not null).ToArray();
                authenticatedReads.Length.Should().Be(13, "three initial selections, three successful refreshes, one switch, the shell and five selected pages each require their own AccountSite witness");
                authenticatedReads.All(row => row.Actor == identity.UserId.Value && row.Tenant == tenant.TenantId.Value
                    && row.HostTenant == tenant.TenantId.Value)
                    .Should().BeTrue("every BFF witness uses the actual actor and independently resolved tenant host");
                authenticatedReads.Count(row => row.Organization == branch).Should().Be(1,
                    "only the explicit derived-branch switch changes the immutable native scope");
                authenticatedReads.Count(row => row.Organization is null).Should().Be(12);
                database.Observation.Reads.Should().BeGreaterThan(readsBefore,
                    "the retained physical ReadOnly application-role observer actually ran");
                await NextAnonymousWindowAsync(anonymousWindow);
                var learner = SeedData.IdentityMemberships.Single(member => member.Identity.Tenant == tenant && !member.IsFirstAdmin);
                await AssertLearnerMembershipRevocationAsync(database, provider, admin, native, api, learner,
                    passwords.GetValueOrDefault(learner.Identity.Username) ?? KeycloakFixture.DemoPassword);
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
            (await AuditCountsAsync(database)).Should().Equal(SeedData.All.Select((site, index) =>
                auditBefore[index] + 11 + (site.MapHostToDefaultOrganization ? 0 : 1)),
                "NULL-org administrator selections, six selected-page read events and original profile write/receipt emit MUST events; learner admission follows its exact scope and pre-resolution revocation creates no invented policy event");
            output.WriteLine("Real warm BFF proof: hosts=2; real refresh/maximum/logout/error/events/membership-revocation=true; zero-write rerun=true; fixture={0:F3}s; journey={1:F3}s.",
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

    // These independent journeys deliberately exceed the real 60/min visitor
    // budget. Pace their boundaries instead of enlarging or bypassing it.
    private static async Task NextAnonymousWindowAsync(Stopwatch elapsed)
    {
        var remaining = LearnStack.Api.Tenancy.RateLimitingExtensions.Window
            + TimeSpan.FromSeconds(1) - elapsed.Elapsed;
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
        elapsed.Restart();
    }

    private static async Task<JsonElement> LoginAsync(BffNativeDriver native, KeycloakBrowserFlow browser,
        SeedIdentity identity, string password, BffWireCookies cookies, TenantRealmApiObservation observation,
        string returnPath = "/studio", string? atHost = null)
    {
        var before = observation.Reads.Count(row => row.Actor is not null);
        var host = atHost ?? Host(identity.Tenant);
        var csrf = await native.RequestAsync(host, "/api/auth/csrf", cookies.Header);
        csrf.Status.Should().Be(200);
        cookies.Apply(csrf);
        var begin = await native.RequestAsync(host, "/api/auth/signin/keycloak", cookies.Header, "POST",
            Form("csrfToken", csrf.Data.GetProperty("csrfToken").GetString()!) + "&" + Form("returnTo", returnPath));
        begin.Status.Should().Be(303);
        cookies.Apply(begin);
        var callback = await browser.CompleteBffLoginAsync(PrivateLocation(begin), identity.Username, password);
        var completed = await native.RequestAsync(host, callback.PathAndQuery, cookies.Header);
        completed.Status.Should().Be(303);
        completed.PrivateFree.Should().BeTrue();
        cookies.Apply(completed);
        completed.Location.Should().Be("https://" + host + "/api/auth/entry");
        RequireCookieAbsent(completed.Cookies, value => value.StartsWith("__Host-ls-session=", StringComparison.Ordinal),
            "provider callback creates entry custody, never a tenant session");
        var entry = await native.RequestAsync(host, "/api/auth/entry", cookies.Header);
        entry.Status.Should().Be(200);
        entry.Data.GetProperty("state").GetString().Should().Be("entry");
        entry.Data.GetProperty("identityState").GetString().Should().Be("active");
        var membership = SeedData.IdentityMemberships.Single(value => value.Identity == identity);
        var selectionBody = Form("csrfToken", entry.Data.GetProperty("csrfToken").GetString()!)
            + "&" + Form("tenantId", identity.Tenant.TenantId.Value.ToString("D"));
        if (membership.OrganizationId is { } organization)
            selectionBody += "&" + Form("organizationId", organization.Value.ToString("D"));
        var selected = await native.RequestAsync(host, "/api/auth/entry/select", cookies.Header, "POST", selectionBody);
        selected.Status.Should().Be(303);
        selected.Location.Should().Be("https://" + host + returnPath);
        selected.PrivateFree.Should().BeTrue();
        cookies.Apply(selected);
        var session = await native.RequestAsync(host, "/api/auth/session", cookies.Header);
        session.Status.Should().Be(200);
        session.PrivateFree.Should().BeTrue();
        (session.Data.GetProperty("state").GetString() == "authenticated").Should().BeTrue();
        observation.Reads.Count(row => row.Actor is not null).Should().Be(before + 1, "each actual grant obtains a new API authority witness before publication");
        return session.Data;
    }

    private static async Task<JsonElement> ExtendAsync(BffNativeDriver native, SeedTenant tenant, BffWireCookies cookies, JsonElement session, TenantRealmApiObservation observation,
        string? atHost = null)
    {
        var before = observation.Reads.Count(row => row.Actor is not null);
        var response = await native.RequestAsync(atHost ?? Host(tenant), "/api/auth/extend", cookies.Header, "POST",
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

    private static ApiHost StartApi(IdentityPublicReadFixture database, string secret, string nativeSourceSecret, KeycloakFixture provider)
    {
        var observation = new TenantRealmApiObservation();
        var accountResponses = new AccountResponseObservation();
        var faults = new NativeResponseFaults();
        var factory = database.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Tenancy:TrustedHop:Networks:0", "127.0.0.1/32");
            builder.UseSetting("Tenancy:TrustedHop:Secrets:0", secret);
            builder.UseSetting(TenantAuthenticationOptions.IssuerConfigurationKey, provider.Issuer);
            builder.UseSetting("Identity:NativeSession:CurrentSecret", nativeSourceSecret);
            builder.UseSetting("Identity:NativeSession:HttpsPort", "3000");
            builder.UseSetting("Identity:SelfBootstrap:Enabled", "true");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IStartupFilter>(accountResponses);
                services.AddSingleton<IStartupFilter>(faults);
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
        return new(factory, client, address, observation, accountResponses, faults);
    }

    private static async Task<long[]> AuditCountsAsync(PublicReadFixture database) => [await database.AuditCountAsync(SeedData.English), await database.AuditCountAsync(SeedData.Yoga)];
    private static object NoNetworkConfiguration(UnavailableTenantIssuer unavailable, KeycloakTls authority) => new
    {
        protocol = 1,
        mode = "real",
        issuer = unavailable.Issuer,
        clientSecret = KeycloakFixture.WebClientSecret,
        authority = authority.RootCertificate.ExportCertificatePem(),
        apiOrigin = new UriBuilder("http", "127.0.0.1", new Uri(unavailable.Issuer).Port).Uri.GetLeftPart(UriPartial.Authority),
        secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(),
        authorityId = Guid.CreateVersion7().ToString("D"),
        nativeSourceSecret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)),
        sites = NativeSites(),
    };
    private static string Host(SeedTenant tenant) => SeedAdministrationOriginManifest.Find(tenant.TenantId)!.Host + ":3000";
    private static IEnumerable<object> NativeSites() => SeedData.All.SelectMany(site => new[] { site.Host + ":3000", Host(site) }
        .Select(host => new { host, displayName = site.DisplayName, locale = site.Curriculum!.Locales.Single(locale => locale.IsDefault).Locale }));
    private static async Task SeedAdministrationOriginsAsync(IdentityPublicReadFixture database)
    {
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        var runner = new SeedAdministrationOriginRunner(context => SeedComposition.Build(source, context,
            NullLoggerFactory.Instance, identityAuthority: database.Registration,
            seedOriginAdmission: new FixedSeedAdministrationOriginAdmission()));
        (await runner.RunAsync(SeedData.All.Select(tenant => tenant.TenantId))).Should().Be(2);
        (await runner.RunAsync(SeedData.All.Select(tenant => tenant.TenantId))).Should().Be(0);
    }
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
        TenantRealmApiObservation Observed, AccountResponseObservation AccountResponses, NativeResponseFaults Faults) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { Client.Dispose(); await Factory.DisposeAsync(); }
    }
}
