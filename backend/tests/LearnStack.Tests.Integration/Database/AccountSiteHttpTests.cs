using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.Api.Common;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.Tools.Seeder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Signed credentials through the production route, real host mappings and application-role reads.</summary>
[Collection(PublicReadTestGroup.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed partial class AccountSiteHttpTests(PublicReadFixture parent)
{
    [Fact]
    public async Task Actual_account_HTTP_reads_English_and_Yoga_row_9_under_ReadOnly_without_query_audits()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var fixture = await IdentityPublicReadFixture.CreateWithMembershipsAsync(parent, issuer.Issuer,
            new Dictionary<UserId, string> { [UserId.From(TenantAuthenticationIssuer.Actor)] = "opaque-keycloak-subject" });
        using var client = Client(fixture, issuer, SeedData.English.Host);
        var englishAudits = await fixture.AuditCountAsync(SeedData.English);
        var yogaAudits = await fixture.AuditCountAsync(SeedData.Yoga);
        var reads = fixture.Observation.Reads;

        using var englishResponse = await Send(client, SeedData.English, Token(issuer, SeedData.English));
        var english = await Account(englishResponse, SeedData.English, organization: false);
        using var yogaResponse = await Send(client, SeedData.Yoga, Token(issuer, SeedData.Yoga, organization: true));
        var yoga = await Account(yogaResponse, SeedData.Yoga, organization: true);

        yoga.GetProperty("site").GetProperty("theme").GetRawText().Should().NotBe(
            english.GetProperty("site").GetProperty("theme").GetRawText(),
            "contrasting stored tenant themes expose a widened tenant read");
        fixture.Observation.Reads.Should().Be(reads + 2,
            "the production reader checks transaction_read_only and the non-superuser learnstack_app role");
        (await fixture.AuditCountAsync(SeedData.English)).Should().Be(englishAudits);
        (await fixture.AuditCountAsync(SeedData.Yoga)).Should().Be(yogaAudits);
        issuer.MetadataRequests.Should().Be(1);
        issuer.JwksRequests.Should().Be(1);
    }

    [Fact]
    public async Task Anonymous_public_site_survives_an_unavailable_issuer_and_failed_credentials_never_fall_back()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var fixture = await IdentityPublicReadFixture.CreateWithMembershipsAsync(parent, issuer.Issuer,
            new Dictionary<UserId, string> { [UserId.From(TenantAuthenticationIssuer.Actor)] = "opaque-keycloak-subject" });
        issuer.DiscoveryStatusCode = 503;
        using var client = Client(fixture, issuer, SeedData.English.Host);
        using var anonymous = await TenantAuthenticationHttpRequest.Send(client, SeedData.English.Host,
            TenantAuthenticationHttpRequest.PublicPath);
        anonymous.StatusCode.Should().Be(HttpStatusCode.OK);
        TenantAuthenticationHttpTests.NoStore(anonymous);
        issuer.MetadataRequests.Should().Be(0);

        var reads = fixture.Observation.Reads;
        using var invalid = await TenantAuthenticationHttpRequest.Send(client, SeedData.English.Host,
            TenantAuthenticationHttpRequest.PublicPath, Token(issuer, SeedData.English));
        await TenantAuthenticationHttpTests.Problem(invalid, HttpStatusCode.Unauthorized, "unauthorized");
        TenantAuthenticationHttpTests.NoStore(invalid);
        invalid.Headers.WwwAuthenticate.Should().ContainSingle().Which.ToString().Should().Be("Bearer");
        fixture.Observation.Reads.Should().Be(reads);
        issuer.MetadataRequests.Should().BeGreaterThan(0);

        using var stillAnonymous = await TenantAuthenticationHttpRequest.Send(client, SeedData.English.Host,
            TenantAuthenticationHttpRequest.PublicPath);
        stillAnonymous.StatusCode.Should().Be(HttpStatusCode.OK);
        TenantAuthenticationHttpTests.NoStore(stillAnonymous);
        fixture.Observation.Reads.Should().Be(reads + 1);
    }

    [Fact]
    public async Task Claimless_signed_profiles_keep_the_public_ceiling_and_emit_one_fixed_Debug_event_per_admitted_request()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var fixture = await IdentityPublicReadFixture.CreateWithMembershipsAsync(parent, issuer.Issuer,
            new Dictionary<UserId, string> { [UserId.From(TenantAuthenticationIssuer.Actor)] = "opaque-keycloak-subject" });
        var logs = new AccountHttpLogObservation();
        using var client = Client(fixture, issuer, SeedData.Yoga.Host, services => services.AddSingleton<ILogEventSink>(logs));
        var token = issuer.Token(issuer.Without("tenant_id", "organization_id"));
        var audits = await fixture.AuditCountAsync(SeedData.Yoga);
        using var publicResponse = await TenantAuthenticationHttpRequest.Send(client, SeedData.Yoga.Host,
            TenantAuthenticationHttpRequest.PublicPath, token);
        publicResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        TenantAuthenticationHttpTests.NoStore(publicResponse);
        var reads = fixture.Observation.Reads;
        using var protectedResponse = await Send(client, SeedData.Yoga, token);
        await TenantAuthenticationHttpTests.Problem(protectedResponse, HttpStatusCode.NotFound, "not_found");
        TenantAuthenticationHttpTests.NoStore(protectedResponse);

        fixture.Observation.Reads.Should().Be(reads, "a claimless actor has no authority to invoke the protected read");
        (await fixture.AuditCountAsync(SeedData.Yoga)).Should().Be(audits);
        logs.AssertClaimlessEvents(2);
    }

    [Fact]
    public async Task Actual_account_HTTP_rechecks_a_suspended_mapped_organization_after_host_cache_warmup()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var fixture = await IdentityPublicReadFixture.CreateWithMembershipsAsync(parent, issuer.Issuer,
            new Dictionary<UserId, string> { [UserId.From(TenantAuthenticationIssuer.Actor)] = "opaque-keycloak-subject" });
        using var client = Client(fixture, issuer, SeedData.Yoga.Host);
        var token = Token(issuer, SeedData.Yoga, organization: true);
        using var warm = await Send(client, SeedData.Yoga, token);
        await Account(warm, SeedData.Yoga, organization: true);
        await fixture.ExecuteAsync(SeedData.Yoga,
            "UPDATE organizations SET status='Suspended' WHERE tenant_id=@tenant AND id=@organization");
        try
        {
            using var refused = await Send(client, SeedData.Yoga, token);
            await TenantAuthenticationHttpTests.Problem(refused, HttpStatusCode.NotFound, "not_found");
            TenantAuthenticationHttpTests.NoStore(refused);
            (await AccountHttpAudit.Read(fixture, SeedData.Yoga,
                TenantAuthenticationReconciliationTests.Correlation(refused), SeedData.Yoga.DefaultOrganization.OrganizationId))
                .Should().ContainSingle("live lifecycle refusal has one MUST event under the resolved branch scope")
                .Which.Operation.Should().Be("identity.authorization.deny");
        }
        finally
        {
            await fixture.ExecuteAsync(SeedData.Yoga,
                "UPDATE organizations SET status='Active' WHERE tenant_id=@tenant AND id=@organization");
        }
        using var restored = await Send(client, SeedData.Yoga, token);
        await Account(restored, SeedData.Yoga, organization: true);
    }

    [Fact]
    public async Task Real_platform_membership_success_still_masks_AccountSite_before_configuration_access()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var fixture = await IdentityPublicReadFixture.CreateWithMembershipsAsync(parent, issuer.Issuer,
            new Dictionary<UserId, string> { [UserId.From(TenantAuthenticationIssuer.Actor)] = "opaque-keycloak-subject" });
        using var client = Client(fixture, issuer, "localhost");
        var reads = fixture.Observation.Reads;
        var audits = await fixture.AuditCountAsync(SeedData.Yoga);
        using var response = await TenantAuthenticationHttpRequest.Send(client, "localhost",
            token: Token(issuer, SeedData.Yoga, organization: true));

        await TenantAuthenticationHttpTests.Problem(response, HttpStatusCode.NotFound, "not_found");
        TenantAuthenticationHttpTests.NoStore(response);
        fixture.Observation.Reads.Should().Be(reads, "ClaimAndMembership supplies no independently classified institution host");
        (await fixture.AuditCountAsync(SeedData.Yoga)).Should().Be(audits);
    }

    [Theory]
    [InlineData("HEAD")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("OPTIONS")]
    public async Task Actual_account_HTTP_admits_only_the_enumerated_GET(string method)
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var fixture = await IdentityPublicReadFixture.CreateWithMembershipsAsync(parent, issuer.Issuer,
            new Dictionary<UserId, string> { [UserId.From(TenantAuthenticationIssuer.Actor)] = "opaque-keycloak-subject" });
        using var client = Client(fixture, issuer, SeedData.English.Host);
        var reads = fixture.Observation.Reads;
        using var response = await TenantAuthenticationHttpRequest.Send(client, SeedData.English.Host,
            token: Token(issuer, SeedData.English), method: method);

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        response.Content.Headers.Allow.Should().BeEquivalentTo(["GET"]);
        TenantAuthenticationHttpTests.NoStore(response);
        fixture.Observation.Reads.Should().Be(reads);
        if (method == "HEAD") (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
        else await TenantAuthenticationHttpTests.Problem(response, HttpStatusCode.MethodNotAllowed, "method_not_allowed");

        using var absent = await TenantAuthenticationHttpRequest.Send(client, SeedData.English.Host, method: method);
        absent.StatusCode.Should().Be(HttpStatusCode.NotFound, "credential admission precedes method disclosure");
        TenantAuthenticationHttpTests.NoStore(absent);
        absent.Content.Headers.Allow.Should().BeEmpty();
        absent.Headers.WwwAuthenticate.Should().BeEmpty();
        if (method == "HEAD") (await absent.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
        else await TenantAuthenticationHttpTests.Problem(absent, HttpStatusCode.NotFound, "not_found");
        fixture.Observation.Reads.Should().Be(reads);
    }

    [Theory]
    [InlineData(TenantAuthenticationHttpRequest.AccountPath)]
    [InlineData(TenantAuthenticationHttpRequest.PublicPath)]
    public async Task Application_body_limit_refusals_keep_no_store_before_credential_admission(string path)
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var fixture = await IdentityPublicReadFixture.CreateWithMembershipsAsync(parent, issuer.Issuer,
            new Dictionary<UserId, string> { [UserId.From(TenantAuthenticationIssuer.Actor)] = "opaque-keycloak-subject" });
        using var client = Client(fixture, issuer, SeedData.English.Host);
        var reads = fixture.Observation.Reads;
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer invalid");
        request.Content = new ByteArrayContent(new byte[checked((int)RequestBodyLimit.MaxBytes + 1)]);
        using var response = await client.SendAsync(request);
        await TenantAuthenticationHttpTests.Problem(response, HttpStatusCode.RequestEntityTooLarge, "payload_too_large");
        TenantAuthenticationHttpTests.NoStore(response);
        fixture.Observation.Reads.Should().Be(reads);
        issuer.MetadataRequests.Should().Be(0);
    }

    [Fact]
    public async Task Account_query_parameters_are_rejected_before_configuration_and_stay_no_store()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var fixture = await IdentityPublicReadFixture.CreateWithMembershipsAsync(parent, issuer.Issuer,
            new Dictionary<UserId, string> { [UserId.From(TenantAuthenticationIssuer.Actor)] = "opaque-keycloak-subject" });
        using var client = Client(fixture, issuer, SeedData.English.Host);
        var reads = fixture.Observation.Reads;
        using var response = await TenantAuthenticationHttpRequest.Send(client, SeedData.English.Host,
            TenantAuthenticationHttpRequest.AccountPath + "?tenantId=" + SeedData.Yoga.TenantId.Value.ToString("D"),
            Token(issuer, SeedData.English));

        await TenantAuthenticationHttpTests.Problem(response, HttpStatusCode.BadRequest, "validation_failed");
        TenantAuthenticationHttpTests.NoStore(response);
        fixture.Observation.Reads.Should().Be(reads);
    }

    private static HttpClient Client(PublicReadFixture fixture, TenantAuthenticationIssuer issuer, string host, Action<IServiceCollection>? configure = null) =>
        fixture.ClientFor(host, configure, builder =>
        {
            builder.UseSetting(TenantAuthenticationOptions.IssuerConfigurationKey, issuer.Issuer);
            builder.UseSetting("Serilog:MinimumLevel:Default", "Debug");
        });

    internal static string Token(TenantAuthenticationIssuer issuer, SeedTenant tenant, bool organization = false,
        Guid? claimedTenant = null, Guid? claimedOrganization = null, string? subject = null,
        string? tokenId = null, Guid? actor = null)
    {
        var payload = issuer.Payload(
            ("tenant_id", (claimedTenant ?? tenant.TenantId.Value).ToString("D")),
            ("organization_id", (claimedOrganization ?? tenant.DefaultOrganization.OrganizationId.Value).ToString("D")),
            ("sub", subject ?? "opaque-keycloak-subject"),
            ("jti", tokenId ?? "account-http-token"),
            ("user_id", (actor ?? TenantAuthenticationIssuer.Actor).ToString("D")));
        if (!organization)
        {
            using var document = JsonDocument.Parse(payload);
            payload = "{" + string.Join(",", document.RootElement.EnumerateObject()
                .Where(property => !property.NameEquals("organization_id"))
                .Select(property => JsonSerializer.Serialize(property.Name) + ":" + property.Value.GetRawText())) + "}";
        }
        return issuer.Token(payload);
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, SeedTenant tenant, string token) =>
        TenantAuthenticationHttpRequest.Send(client, tenant.Host, token: token);

    private static async Task<JsonElement> Account(HttpResponseMessage response, SeedTenant tenant, bool organization)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the actual production route must execute successfully before negative controls count");
        TenantAuthenticationHttpTests.NoStore(response);
        var account = await response.Content.ReadFromJsonAsync<JsonElement>();
        account.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            ["actorId", "tenantId", "organizationId", "site"]);
        account.GetProperty("actorId").GetGuid().Should().Be(TenantAuthenticationIssuer.Actor);
        account.GetProperty("tenantId").GetGuid().Should().Be(tenant.TenantId.Value);
        if (organization) account.GetProperty("organizationId").GetGuid().Should().Be(tenant.DefaultOrganization.OrganizationId.Value);
        else account.GetProperty("organizationId").ValueKind.Should().Be(JsonValueKind.Null);
        var site = account.GetProperty("site");
        site.GetProperty("displayName").GetString().Should().Be(tenant.DisplayName);
        site.GetProperty("defaultLocale").GetString().Should().Be(tenant.Curriculum!.Locales.Single(locale => locale.IsDefault).Locale);
        site.GetProperty("enabledLocales").EnumerateArray().Select(locale => locale.GetString()).Should().Equal(
            tenant.Curriculum!.Locales.Where(locale => locale.IsEnabled).OrderBy(locale => locale.Sort).Select(locale => locale.Locale));
        using var theme = JsonDocument.Parse(tenant.Curriculum!.Theme.Value);
        site.GetProperty("theme").GetProperty("primary").GetString().Should().Be(theme.RootElement.GetProperty("primary").GetString());
        site.GetProperty("showPlatformAttribution").GetBoolean().Should().BeFalse();
        return account;
    }

    private sealed class AccountHttpLogObservation : ILogEventSink
    {
        internal ConcurrentQueue<LogEvent> Entries { get; } = new();
        public void Emit(LogEvent logEvent) => Entries.Enqueue(logEvent);
        internal void AssertClaimlessEvents(int count)
        {
            var events = Entries.Where(entry => entry.MessageTemplate.Text == "Tenant authentication outcome tenant_claim_absent.").ToArray();
            events.Should().HaveCount(count);
            foreach (var entry in events)
            {
                entry.Level.Should().Be(LogEventLevel.Debug);
                entry.Exception.Should().BeNull();
                entry.Properties.Should().NotContainKey("tenant_id").And.NotContainKey("sub");
            }
        }
    }
}
