using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using LearnStack.Tests.Integration.Database;
using LearnStack.Tools.Seeder;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Npgsql;

namespace LearnStack.Tests.Integration.TenancyIdentity;

public sealed partial class TenantBffSessionTests
{
    private static async Task AssertLearnerMembershipRevocationAsync(IdentityPublicReadFixture database,
        KeycloakFixture provider, KeycloakAdminClient admin, BffNativeDriver native, ApiHost api,
        SeedFoundationMembership membership, string password)
    {
        membership.IsFirstAdmin.Should().BeFalse("the existing administrator and its last-admin invariant remain intact");
        var identity = membership.Identity;
        var tenant = identity.Tenant;
        using var browser = provider.Browser();
        var cookies = new BffWireCookies();
        var beforeLogin = api.Observed.Reads.Count(row => row.Actor is not null);
        var beforeResponses = api.AccountResponses.Responses.Count;
        await LoginAsync(native, browser, identity, password, cookies, api.Observed, "/portal");
        var positive = await native.RequestAsync(Host(tenant), "/portal", cookies.Header);
        positive.Status.Should().Be(200);
        positive.Rendered.Should().BeTrue();
        positive.PrivateFree.Should().BeTrue();
        positive.Cache.Should().Contain("no-store");
        api.AccountResponses.Responses.Skip(beforeResponses).Select(row => row.Status).Should().Equal([200, 200],
            "both explicit selection and protected learner document obtain actual AccountSite success");
        var learnerReads = api.Observed.Reads.Where(row => row.Actor is not null).Skip(beforeLogin).ToArray();
        learnerReads.Should().HaveCount(2);
        learnerReads.Should().OnlyContain(row => row.Actor == identity.UserId.Value
            && row.Tenant == tenant.TenantId.Value && row.HostTenant == tenant.TenantId.Value
            && row.Organization == (tenant.MapHostToDefaultOrganization ? tenant.DefaultOrganization.OrganizationId.Value : null));

        var user = await admin.UserAsync(identity.Username);
        var sessionsPath = "users/" + user.GetProperty("id").GetString() + "/sessions";
        var providerSession = await admin.GetAsync(sessionsPath);
        providerSession.GetArrayLength().Should().Be(1);
        var providerSessionId = providerSession[0].GetProperty("id").GetString();
        await RevokeLearnerMembershipAsync(database, membership);
        var passive = await native.RequestAsync(Host(tenant), "/api/auth/session", cookies.Header);
        passive.Data.GetProperty("state").GetString().Should().Be("authenticated",
            "the local session was live before its next actual API authority check");

        var originalCookies = cookies.Header;
        var beforeRefusal = api.AccountResponses.Responses.Count;
        var beforeRefusedRead = api.Observed.Reads.Count(row => row.Actor is not null);
        var refused = await native.RequestAsync(Host(tenant), "/portal", originalCookies);
        refused.Status.Should().Be(303);
        refused.Location.Should().Be("https://" + Host(tenant) + "/api/auth/signin?returnTo=%2Fportal");
        refused.Rendered.Should().BeFalse();
        refused.PrivateFree.Should().BeTrue();
        refused.Cache.Should().Contain("no-store");
        var apiRefusal = api.AccountResponses.Responses.Skip(beforeRefusal).Should().ContainSingle().Subject;
        apiRefusal.Status.Should().Be(404, "the actual application membership refusal must reach the native AccountSite caller");
        apiRefusal.Cache.Should().Contain("no-store");
        api.Observed.Reads.Count(row => row.Actor is not null).Should().Be(beforeRefusedRead,
            "revocation refuses before protected configuration disclosure");
        // ADR-0078 confirms the native candidate before publishing a tenant
        // context. Unlike the legacy resolved policy refusal, revoked coverage
        // stops in the resolver: ADR-0077 forbids inventing a generic 404 audit.
        // AccountSiteHttpTests separately retains resolved-policy MUST evidence.
        (await AccountHttpAudit.Read(database, tenant, apiRefusal.Correlation,
            membership.OrganizationId)).Should().BeEmpty(
                "native coverage refused before context publication and protected-policy invocation");

        await AnonymousAsync(native, tenant, originalCookies);
        cookies.Apply(refused);
        await AnonymousAsync(native, tenant, cookies);
        api.AccountResponses.Responses.Count.Should().Be(beforeRefusal + 1,
            "passive projections cannot replay API authority checks after local revocation");
        var retainedProviderSession = await admin.GetAsync(sessionsPath);
        retainedProviderSession.GetArrayLength().Should().Be(1);
        retainedProviderSession[0].GetProperty("id").GetString().Should().Be(providerSessionId,
            "membership revocation, not provider logout or expiry, ended local authority");
        (await admin.PostAsync("users/" + user.GetProperty("id").GetString() + "/logout", new { }))
            .Should().Be(HttpStatusCode.NoContent);
    }

    private static async Task RevokeLearnerMembershipAsync(IdentityPublicReadFixture database, SeedFoundationMembership membership)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, transaction, membership.Identity.Tenant.TenantId.Value,
            membership.OrganizationId?.Value);
        await IdentityMembershipFixture.LockAsync(app, transaction, membership.Identity.Tenant.TenantId.Value,
            membership.Identity.UserId.Value, exclusive: true);
        await using var update = new NpgsqlCommand("""
            UPDATE public.memberships SET status='Revoked', deleted_at=clock_timestamp(), deleted_by=@actor
            WHERE id=@membership AND tenant_id=@tenant AND user_id=@actor AND status='Active'
            """, app, transaction);
        update.Parameters.AddWithValue("actor", membership.Identity.UserId.Value);
        update.Parameters.AddWithValue("membership", membership.MembershipId);
        update.Parameters.AddWithValue("tenant", membership.Identity.Tenant.TenantId.Value);
        (await update.ExecuteNonQueryAsync()).Should().Be(1, "the exact committed learner membership must actually change");
        await transaction.CommitAsync();
        // Revocation is terminal. This case owns the database through disposal; no lifecycle rollback is invented.
    }

    /// <summary>Observes actual AccountSite response status and public headers without altering admission or payload.</summary>
    private sealed class AccountResponseObservation : IStartupFilter
    {
        internal ConcurrentQueue<AccountResponse> Responses { get; } = new();
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                if (context.Request.Path == TenantAuthenticationHttpRequest.AccountPath)
                    context.Response.OnStarting(() =>
                    {
                        Responses.Enqueue(new(context.Response.StatusCode,
                            context.Response.Headers["X-Correlation-Id"].ToString(),
                            context.Response.Headers.CacheControl.ToString()));
                        return Task.CompletedTask;
                    });
                await continuation();
            });
            next(app);
        };
    }

    private sealed record AccountResponse(int Status, string Correlation, string Cache);
}
