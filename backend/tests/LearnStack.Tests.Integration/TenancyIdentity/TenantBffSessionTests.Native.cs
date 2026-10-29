using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tests.Integration.Database;
using LearnStack.Tools.Seeder;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;
using Npgsql;

namespace LearnStack.Tests.Integration.TenancyIdentity;

public sealed partial class TenantBffSessionTests
{
    [Fact]
    public async Task Actorless_creation_with_lost_response_converges_without_tenant_session_and_logs_out_entry()
    {
        await using var provider = await KeycloakFixture.StartAsync();
        using var admin = await provider.CreateAdminClientAsync();
        await using var database = await KeycloakIdentityDatabase.CreateAsync(publicDatabase, provider);
        await SeedAdministrationOriginsAsync(database);
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var nativeSourceSecret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        await using var api = StartApi(database, secret, nativeSourceSecret, provider);
        await using var native = await BffNativeDriver.StartAsync(RepositoryRoot(),
            NativeConfiguration(provider, database, api, secret, nativeSourceSecret));
        await AssertActorlessUnknownCreationAsync(database, provider, admin, native, api, SeedData.English);
        await native.CompleteAsync();
    }

    [Fact]
    public async Task One_provider_SSO_keeps_two_native_host_contexts_independent_across_refresh()
    {
        await using var provider = await KeycloakFixture.StartAsync();
        using var admin = await provider.CreateAdminClientAsync();
        await using var database = await KeycloakIdentityDatabase.CreateAsync(publicDatabase, provider);
        await SeedAdministrationOriginsAsync(database);
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var nativeSourceSecret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        await using var api = StartApi(database, secret, nativeSourceSecret, provider);
        await using var native = await BffNativeDriver.StartAsync(RepositoryRoot(),
            NativeConfiguration(provider, database, api, secret, nativeSourceSecret));
        using var browser = provider.Browser();
        var tenant = SeedData.English;
        var identity = SeedData.IdentityUsers.First(person => person.Tenant == tenant);
        var firstHost = Host(tenant);
        var secondHost = tenant.Host + ":3000";
        var firstCookies = new BffWireCookies();
        var secondCookies = new BffWireCookies();
        var first = await LoginAsync(native, browser, identity, KeycloakFixture.DemoPassword, firstCookies, api.Observed,
            atHost: firstHost);
        browser.BffFormCallbacks.Should().Be(1);
        browser.BffSsoCallbacks.Should().Be(0);
        var user = await admin.UserAsync(identity.Username);
        var sessionsPath = "users/" + user.GetProperty("id").GetString() + "/sessions";
        var originalProviderSession = await admin.GetAsync(sessionsPath);
        originalProviderSession.GetArrayLength().Should().Be(1);
        var originalProviderSessionId = originalProviderSession[0].GetProperty("id").GetString();
        var second = await LoginAsync(native, browser, identity, KeycloakFixture.DemoPassword, secondCookies, api.Observed,
            atHost: secondHost);
        browser.BffFormCallbacks.Should().Be(1, "the same provider jar performs no second password login");
        browser.BffSsoCallbacks.Should().Be(1, "the actual provider redirects the second host through existing SSO");
        var afterSecond = await admin.GetAsync(sessionsPath);
        afterSecond.GetArrayLength().Should().Be(1);
        (afterSecond[0].GetProperty("id").GetString() == originalProviderSessionId).Should().BeTrue();
        (firstCookies.Header != secondCookies.Header).Should().BeTrue("native cookies are separate on the two hosts");
        var branch = tenant.DefaultOrganization.OrganizationId.Value;
        var originalSecondCookies = secondCookies.Header;
        var switched = await native.RequestAsync(secondHost, "/api/auth/switch", secondCookies.Header, "POST",
            Form("csrfToken", second.GetProperty("csrfToken").GetString()!) + "&"
            + Form("tenantId", tenant.TenantId.Value.ToString("D")) + "&" + Form("organizationId", branch.ToString("D")));
        switched.Status.Should().Be(303);
        secondCookies.Apply(switched);
        (secondCookies.Header != originalSecondCookies).Should().BeTrue();
        var currentSecond = await native.RequestAsync(secondHost, "/api/auth/session", secondCookies.Header);
        currentSecond.Data.GetProperty("state").GetString().Should().Be("authenticated");
        second = currentSecond.Data;
        var retainedFirst = await native.RequestAsync(firstHost, "/api/auth/session", firstCookies.Header);
        (retainedFirst.Data.GetProperty("csrfToken").GetString() == first.GetProperty("csrfToken").GetString())
            .Should().BeTrue("the second host's scope epoch does not revoke the first host owner");
        var beforeNullAudit = await AuditCountsAsync(database);
        var beforeBranchAudit = await ScopeAdmissionCountAsync(database, tenant, branch);
        first = await ExtendAsync(native, tenant, firstCookies, first, api.Observed, firstHost);
        second = await ExtendAsync(native, tenant, secondCookies, second, api.Observed, secondHost);
        first = await ExtendAsync(native, tenant, firstCookies, first, api.Observed, firstHost);
        (await AuditCountsAsync(database)).Should().Equal(beforeNullAudit);
        (await ScopeAdmissionCountAsync(database, tenant, branch)).Should().Be(beforeBranchAudit,
            "three real refreshes never dispatch another admission security event");
        var witnesses = api.Observed.Reads.Where(row => row.Actor is not null).ToArray();
        witnesses.Select(row => row.Organization).Should().Equal([null, null, branch, null, branch, null]);
        witnesses.Should().OnlyContain(row => row.Actor == identity.UserId.Value && row.Tenant == tenant.TenantId.Value
            && row.HostTenant == tenant.TenantId.Value && row.HostOrganization == null
            && row.Origin == TenantContextOrigin.HostAndNativeSessionScope);
        var copied = await native.RequestAsync(secondHost, "/api/auth/session", firstCookies.Header);
        copied.Data.GetProperty("state").GetString().Should().Be("anonymous");
        var stillFirst = await native.RequestAsync(firstHost, "/api/auth/session", firstCookies.Header);
        var stillSecond = await native.RequestAsync(secondHost, "/api/auth/session", secondCookies.Header);
        (stillFirst.Data.GetProperty("csrfToken").GetString() == first.GetProperty("csrfToken").GetString()).Should().BeTrue();
        (stillSecond.Data.GetProperty("csrfToken").GetString() == second.GetProperty("csrfToken").GetString()).Should().BeTrue();
        var afterRefreshes = await admin.GetAsync(sessionsPath);
        afterRefreshes.GetArrayLength().Should().Be(1);
        (afterRefreshes[0].GetProperty("id").GetString() == originalProviderSessionId).Should().BeTrue();
        await native.CompleteAsync();
    }

    private static object NativeConfiguration(KeycloakFixture provider, IdentityPublicReadFixture database,
        ApiHost api, string secret, string nativeSourceSecret) => new
        {
            protocol = 1,
            mode = "real",
            issuer = provider.Issuer,
            clientSecret = KeycloakFixture.WebClientSecret,
            authority = provider.RootCertificate.ExportCertificatePem(),
            apiOrigin = api.Origin,
            secret,
            authorityId = database.Registration.AuthorityId.Value.ToString("D"),
            nativeSourceSecret,
            sites = NativeSites(),
        };

    private static async Task AssertActorlessUnknownCreationAsync(IdentityPublicReadFixture database,
        KeycloakFixture provider, KeycloakAdminClient admin, BffNativeDriver native, ApiHost api, SeedTenant tenant)
    {
        var username = "native-entry-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant() + "@example.test";
        (await admin.PostAsync("users", new
        {
            username,
            enabled = true,
            email = username,
            emailVerified = false,
            firstName = "Entry",
            lastName = "Fixture",
            credentials = new[] { new { type = "password", value = KeycloakFixture.DemoPassword, temporary = false } },
        })).Should().Be(HttpStatusCode.Created);
        var user = await admin.UserAsync(username);
        var subject = user.GetProperty("id").GetString()!;
        using var browser = provider.Browser();
        var cookies = new BffWireCookies();
        var host = Host(tenant);
        var csrf = await native.RequestAsync(host, "/api/auth/csrf", cookies.Header);
        cookies.Apply(csrf);
        var begin = await native.RequestAsync(host, "/api/auth/signin/keycloak", cookies.Header, "POST",
            Form("csrfToken", csrf.Data.GetProperty("csrfToken").GetString()!));
        begin.Status.Should().Be(303);
        cookies.Apply(begin);
        var callback = await browser.CompleteBffLoginAsync(PrivateLocation(begin), username, KeycloakFixture.DemoPassword);
        var completed = await native.RequestAsync(host, callback.PathAndQuery, cookies.Header);
        completed.Status.Should().Be(303);
        completed.Cookies.Should().NotContain(value => value.StartsWith("__Host-ls-session=", StringComparison.Ordinal));
        cookies.Apply(completed);
        var entry = await native.RequestAsync(host, "/api/auth/entry", cookies.Header);
        entry.Status.Should().Be(200);
        entry.Data.GetProperty("identityState").GetString().Should().Be("missing");
        entry.Data.GetProperty("contexts").ValueKind.Should().Be(JsonValueKind.Null);
        var body = Form("csrfToken", entry.Data.GetProperty("csrfToken").GetString()!);
        var before = api.Faults.BootstrapRequests;
        var suppressed = api.Faults.Suppressed;
        api.Faults.Arm("/api/v1/identity/self/bootstrap");
        var unknown = await native.RequestAsync(host, "/api/auth/entry/create", cookies.Header, "POST", body);
        unknown.Status.Should().Be(503, "the real creation committed, but its response was deliberately withheld");
        unknown.Cookies.Should().BeEmpty();
        unknown.PrivateFree.Should().BeTrue();
        api.Faults.Suppressed.Should().Be(suppressed + 1);
        api.Faults.BootstrapRequests.Should().Be(before + 1);
        var recovered = await native.RequestAsync(host, "/api/auth/entry", cookies.Header);
        recovered.Status.Should().Be(200);
        recovered.Data.GetProperty("identityState").GetString().Should().Be("active");
        recovered.Data.GetProperty("contexts").GetProperty("items").GetArrayLength().Should().Be(0,
            "creating global identity grants no Membership or tenant session");
        var deliberate = await native.RequestAsync(host, "/api/auth/entry/create", cookies.Header, "POST", body);
        deliberate.Status.Should().Be(303);
        api.Faults.BootstrapRequests.Should().Be(before + 1,
            "the deliberate retry first observes the existing binding instead of automatically repeating POST");
        await using var owner = new NpgsqlConnection(database.MigrationConnectionString);
        await owner.OpenAsync();
        await using var count = new NpgsqlCommand("SELECT count(*) FROM public.users WHERE authority_id=@authority AND subject_key=@subject", owner);
        count.Parameters.AddWithValue("authority", database.Registration.AuthorityId.Value);
        count.Parameters.AddWithValue("subject", Convert.FromHexString(IdentitySubjectKey.Compute(database.Registration.AuthorityId, subject).Hex));
        Convert.ToInt64(await count.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture).Should().Be(1);
        await AnonymousAsync(native, tenant, cookies);
        var discard = await native.RequestAsync(host, "/api/auth/signout", cookies.Header, "POST", body);
        discard.Status.Should().Be(303);
        discard.Cookies.Should().Contain(value => value.StartsWith("__Host-ls-entry=;", StringComparison.Ordinal));
        cookies.Apply(discard);
        var returned = await browser.CompleteBffLogoutAsync(PrivateLocation(discard));
        var finished = await native.RequestAsync(host, returned.PathAndQuery, cookies.Header);
        finished.Status.Should().Be(303);
        cookies.Apply(finished);
        (await admin.GetAsync("users/" + subject + "/sessions")).GetArrayLength().Should().Be(0,
            "entry recovery actually ends its provider SSO session instead of immediately recycling it");
    }

    private static async Task<long> ScopeAdmissionCountAsync(IdentityPublicReadFixture database, SeedTenant tenant, Guid organization)
    {
        await using var app = new NpgsqlConnection(database.AppConnectionString);
        await app.OpenAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, transaction, tenant.TenantId.Value, organization);
        await using var count = new NpgsqlCommand("""
            SELECT count(*) FROM public.audit_log
            WHERE tenant_id=@tenant AND organization_id=@organization AND operation='identity.membership.admit_session_scope'
            """, app, transaction);
        count.Parameters.AddWithValue("tenant", tenant.TenantId.Value);
        count.Parameters.AddWithValue("organization", organization);
        return Convert.ToInt64(await count.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Owns one exact post-COMMIT response fault, without changing authentication, business or audit code.</summary>
    private sealed class NativeResponseFaults : IStartupFilter
    {
        private string? _armed;
        private int _suppressed;
        private int _bootstrapRequests;
        internal int Suppressed => Volatile.Read(ref _suppressed);
        internal int BootstrapRequests => Volatile.Read(ref _bootstrapRequests);
        internal void Arm(string path)
        {
            if (path is not ("/api/v1/identity/self/bootstrap" or "/api/v1/identity/me/profile")
                || Interlocked.CompareExchange(ref _armed, path, null) is not null)
                throw new InvalidOperationException("Native fixture response fault is invalid.");
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (http, continuation) =>
            {
                if (http.Request.Path == "/api/v1/identity/self/bootstrap" && http.Request.Method == "POST")
                    Interlocked.Increment(ref _bootstrapRequests);
                var armed = Volatile.Read(ref _armed);
                if (armed is null || http.Request.Path != armed || http.Request.Method is not ("POST" or "PUT")
                    || Interlocked.CompareExchange(ref _armed, null, armed) != armed)
                { await continuation(); return; }
                var actual = http.Response.Body;
                await using var withheld = new MemoryStream();
                http.Response.Body = withheld;
                try
                {
                    await continuation();
                    if (http.Response.StatusCode is >= 200 and < 300)
                    {
                        Interlocked.Increment(ref _suppressed);
                        http.Abort();
                    }
                    else
                    {
                        withheld.Position = 0;
                        await withheld.CopyToAsync(actual, http.RequestAborted);
                    }
                }
                finally { http.Response.Body = actual; }
            });
            next(app);
        };
    }
}

public sealed partial class TenantBffSessionTests
{
    private static async Task AssertUnknownNativeProfileWriteAsync(IdentityPublicReadFixture database,
        BffNativeDriver native, ApiHost api, SeedIdentity identity, BffWireCookies cookies, JsonElement session)
    {
        var membership = SeedData.IdentityMemberships.Single(value => value.Identity == identity);
        var prior = await NativeProfileAsync(database, membership);
        var key = Guid.CreateVersion7().ToString("D");
        var label = "Native committed profile";
        var body = Form("csrfToken", session.GetProperty("csrfToken").GetString()!)
            + "&" + Form("version", prior.Version.ToString(System.Globalization.CultureInfo.InvariantCulture))
            + "&" + Form("idempotencyKey", key) + "&" + Form("displayName", label) + "&preferredLocale=";
        var before = await AuditCountsAsync(database);
        var suppressed = api.Faults.Suppressed;
        api.Faults.Arm("/api/v1/identity/me/profile");
        var unknown = await native.RequestAsync(Host(identity.Tenant), "/api/account/profile", cookies.Header, "POST", body);
        unknown.Status.Should().Be(503);
        unknown.PrivateFree.Should().BeTrue();
        unknown.Cookies.Should().BeEmpty();
        api.Faults.Suppressed.Should().Be(suppressed + 1);
        var committed = await NativeProfileAsync(database, membership);
        committed.Should().Be((prior.Version + 1, label));
        var retry = await native.RequestAsync(Host(identity.Tenant), "/api/account/profile", cookies.Header, "POST", body);
        retry.Status.Should().Be(303);
        retry.Location.Should().Be("https://" + Host(identity.Tenant) + "/portal/profile");
        retry.PrivateFree.Should().BeTrue();
        (await NativeProfileAsync(database, membership)).Should().Be(committed,
            "the deliberate same-key/body retry discloses a protected receipt, never executes a second mutation");
        (await AuditCountsAsync(database)).Should().Equal(SeedData.All.Select((tenant, index) => before[index]
            + (tenant == identity.Tenant ? 2 : 0)), "the original MUST write and separate MUST receipt disclosure both commit");
    }

    private static async Task<(long Version, string? DisplayName)> NativeProfileAsync(IdentityPublicReadFixture database,
        SeedFoundationMembership membership)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, transaction, membership.Identity.Tenant.TenantId.Value, membership.OrganizationId?.Value);
        await using var read = new NpgsqlCommand("""
            SELECT m.row_version, p.display_name FROM public.memberships m
            JOIN public.membership_profiles p ON p.tenant_id=m.tenant_id AND p.membership_id=m.id
            WHERE m.id=@membership AND m.tenant_id=@tenant
            """, app, transaction);
        read.Parameters.AddWithValue("membership", membership.MembershipId);
        read.Parameters.AddWithValue("tenant", membership.Identity.Tenant.TenantId.Value);
        await using var rows = await read.ExecuteReaderAsync();
        (await rows.ReadAsync()).Should().BeTrue();
        var result = (rows.GetInt64(0), rows.IsDBNull(1) ? null : rows.GetString(1));
        (await rows.ReadAsync()).Should().BeFalse();
        return result;
    }
}

public sealed partial class TenantBffSessionTests
{
    private static async Task AssertDifferentProviderAccountCannotReplaceLiveDestinationAsync(
        KeycloakFixture provider, BffNativeDriver native, ApiHost api, SeedIdentity original,
        SeedIdentity different, string differentPassword, BffWireCookies cookies, JsonElement session,
        IdentityPublicReadFixture database)
    {
        using var otherProviderBrowser = provider.Browser();
        var host = Host(original.Tenant);
        var before = await AuditCountsAsync(database);
        var beforeReads = api.Observed.Reads.Count(row => row.Actor is not null);
        var csrf = await native.RequestAsync(host, "/api/auth/csrf", cookies.Header);
        cookies.Apply(csrf);
        var started = await native.RequestAsync(host, "/api/auth/signin/keycloak", cookies.Header, "POST",
            Form("csrfToken", csrf.Data.GetProperty("csrfToken").GetString()!));
        started.Status.Should().Be(303);
        cookies.Apply(started);
        var callback = await otherProviderBrowser.CompleteBffLoginAsync(PrivateLocation(started), different.Username, differentPassword);
        var completed = await native.RequestAsync(host, callback.PathAndQuery, cookies.Header);
        completed.Status.Should().Be(303);
        completed.Cookies.Should().NotContain(value => value.StartsWith("__Host-ls-session=", StringComparison.Ordinal));
        cookies.Apply(completed);
        var entry = await native.RequestAsync(host, "/api/auth/entry", cookies.Header);
        entry.Status.Should().Be(200);
        entry.Data.GetProperty("identityState").GetString().Should().Be("active");
        var replacement = await native.RequestAsync(host, "/api/auth/entry/select", cookies.Header, "POST",
            Form("csrfToken", entry.Data.GetProperty("csrfToken").GetString()!) + "&"
            + Form("tenantId", different.Tenant.TenantId.Value.ToString("D")));
        replacement.Status.Should().Be(409, "a different verified subject cannot replace the live destination, even with explicit confirmation");
        replacement.PrivateFree.Should().BeTrue();
        replacement.Cookies.Should().BeEmpty();
        var retained = await native.RequestAsync(host, "/api/auth/session", cookies.Header);
        retained.Data.GetProperty("csrfToken").GetString().Should().Be(session.GetProperty("csrfToken").GetString());
        api.Observed.Reads.Count(row => row.Actor is not null).Should().Be(beforeReads);
        (await AuditCountsAsync(database)).Should().Equal(before, "the subject fence refuses before API scope admission");
        var logout = await native.RequestAsync(host, "/api/auth/signout", cookies.Header, "POST",
            Form("csrfToken", entry.Data.GetProperty("csrfToken").GetString()!));
        logout.Status.Should().Be(303);
        logout.Cookies.Should().NotContain(value => value.StartsWith("__Host-ls-session=", StringComparison.Ordinal)
            || value.StartsWith("__Host-ls-browser=", StringComparison.Ordinal));
        cookies.Apply(logout);
        var returned = await otherProviderBrowser.CompleteBffLogoutAsync(PrivateLocation(logout));
        var finish = await native.RequestAsync(host, returned.PathAndQuery, cookies.Header);
        finish.Status.Should().Be(303);
        cookies.Apply(finish);
        var after = await native.RequestAsync(host, "/api/auth/session", cookies.Header);
        after.Data.GetProperty("csrfToken").GetString().Should().Be(session.GetProperty("csrfToken").GetString(),
            "entry provider logout and its late return never rotate the unrelated active native owner");
    }
}
