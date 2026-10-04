using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Api.Tenancy;
using LearnStack.Modules.Tenancy.Application.Contracts.PublicReads;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using LearnStack.Tools.Seeder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Collection(PublicReadTestGroup.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class PublicSiteHttpTests(PublicReadFixture fixture)
{
    private static SeedTenant English => SeedData.English;
    private static SeedTenant Yoga => SeedData.Yoga;
    private const string Path = "/api/v1/public/site";

    [Fact]
    public async Task Site_is_allowlisted_host_resolved_and_read_only_without_audit_rows()
    {
        var before = await fixture.AuditCountAsync(English);
        using var client = fixture.ClientFor(English.Host);
        using var response = await client.GetAsync(Path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        NoStore(response);
        var site = await response.Content.ReadFromJsonAsync<JsonElement>();
        site.EnumerateObject().Select(member => member.Name).Should().BeEquivalentTo(
            ["displayName", "enabledLocales", "defaultLocale", "theme", "showPlatformAttribution"]);
        site.GetProperty("displayName").GetString().Should().Be(English.DisplayName);
        site.GetProperty("enabledLocales").EnumerateArray().Select(locale => locale.GetString()).Should().Equal("en");
        site.GetProperty("defaultLocale").GetString().Should().Be("en");
        site.GetProperty("theme").EnumerateObject().Select(member => member.Name).Should().BeEquivalentTo(
            ["primary", "background", "foreground", "muted"]);
        site.GetProperty("showPlatformAttribution").GetBoolean().Should().BeFalse("the registered Null provider enables every feature");
        (await fixture.AuditCountAsync(English)).Should().Be(before);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain(English.TenantId.Value.ToString("D")).And.NotContain("branding.theme");
        using var yoga = await client.GetAsync(new Uri($"http://{Yoga.Host}{Path}"));
        yoga.StatusCode.Should().Be(HttpStatusCode.OK);
        var other = await yoga.Content.ReadFromJsonAsync<JsonElement>();
        other.GetProperty("displayName").GetString().Should().Be(Yoga.DisplayName);
        other.GetProperty("enabledLocales").EnumerateArray().Select(locale => locale.GetString()).Should().Equal("tr-TR", "en");
        other.GetProperty("theme").GetRawText().Should().NotBe(site.GetProperty("theme").GetRawText());
    }

    [Fact]
    public async Task Locale_display_order_uses_sort_then_ordinal_canonical_tag()
    {
        using var client = fixture.ClientFor(Yoga.Host);
        await fixture.ExecuteAsync(Yoga, "UPDATE tenant_locales SET sort=0 WHERE tenant_id=@tenant AND locale='en'");
        try
        {
            using var response = await client.GetAsync(Path);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var site = await response.Content.ReadFromJsonAsync<JsonElement>();
            site.GetProperty("enabledLocales").EnumerateArray().Select(locale => locale.GetString()).Should().Equal("en", "tr-TR");
            site.GetProperty("defaultLocale").GetString().Should().Be("tr-TR");
        }
        finally { await fixture.ExecuteAsync(Yoga, "UPDATE tenant_locales SET sort=1 WHERE tenant_id=@tenant AND locale='en'"); }
    }

    [Theory]
    [InlineData("?locale=en")]
    [InlineData("?tenantId=x")]
    [InlineData("?locale=en&locale=tr")]
    public async Task Site_rejects_every_query_parameter_before_loading_configuration(string query)
    {
        using var client = fixture.ClientFor(English.Host);
        var before = fixture.Observation.Reads;
        using var response = await client.GetAsync(Path + query);
        await Error(response, HttpStatusCode.BadRequest, "validation_failed");
        fixture.Observation.Reads.Should().Be(before);
    }

    [Theory]
    [InlineData("GET", "missing.learnstack.local", "not_found")]
    [InlineData("GET", "localhost", "tenant_mismatch")]
    [InlineData("HEAD", "missing.learnstack.local", "not_found")]
    [InlineData("HEAD", "localhost", "tenant_mismatch")]
    public async Task Unknown_and_platform_hosts_keep_their_generic_refusals(string method, string host, string code)
    {
        using var client = fixture.ClientFor(host);
        using var request = new HttpRequestMessage(new HttpMethod(method), Path);
        using var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        NoStore(response);
        if (method == "HEAD") (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
        else await Error(response, HttpStatusCode.NotFound, code);
    }

    [Theory]
    [InlineData("Suspended")]
    [InlineData("Archived")]
    [InlineData("deleted")]
    public async Task Live_mapping_does_not_serve_an_inactive_tenant(string state)
    {
        using var client = fixture.ClientFor(English.Host);
        (await client.GetAsync(Path)).StatusCode.Should().Be(HttpStatusCode.OK, "warm the host mapping before withdrawing lifecycle access");
        await fixture.ExecuteAsync(English, state == "deleted"
            ? "UPDATE tenants SET deleted_at=now(), deleted_by=@actor WHERE id=@tenant"
            : $"UPDATE tenants SET status='{state}' WHERE id=@tenant");
        try { await GetAndHeadError(client, HttpStatusCode.NotFound, "not_found"); }
        finally { await fixture.ExecuteAsync(English, "UPDATE tenants SET status='Trial', deleted_at=NULL, deleted_by=NULL WHERE id=@tenant"); }
    }

    [Theory]
    [InlineData("Suspended")]
    [InlineData("Archived")]
    [InlineData("deleted")]
    public async Task Organization_host_checks_the_mapped_organization_live_state(string state)
    {
        using var client = fixture.ClientFor(Yoga.Host);
        (await client.GetAsync(Path)).StatusCode.Should().Be(HttpStatusCode.OK);
        await fixture.ExecuteAsync(Yoga, state == "deleted"
            ? "UPDATE organizations SET deleted_at=now(), deleted_by=@actor WHERE tenant_id=@tenant AND id=@organization"
            : $"UPDATE organizations SET status='{state}' WHERE tenant_id=@tenant AND id=@organization");
        try { await GetAndHeadError(client, HttpStatusCode.NotFound, "not_found"); }
        finally { await fixture.ExecuteAsync(Yoga, "UPDATE organizations SET status='Active', deleted_at=NULL, deleted_by=NULL WHERE tenant_id=@tenant AND id=@organization"); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Valid_empty_or_all_disabled_configuration_hides_site(bool delete)
    {
        using var client = fixture.ClientFor(English.Host);
        using var warm = await client.GetAsync(Path);
        warm.StatusCode.Should().Be(HttpStatusCode.OK);
        await fixture.ExecuteAsync(English, delete
            ? "DELETE FROM tenant_locales WHERE tenant_id=@tenant"
            : "UPDATE tenant_locales SET is_default=false,is_enabled=false WHERE tenant_id=@tenant");
        try { await GetAndHeadError(client, HttpStatusCode.NotFound, "not_found"); }
        finally
        {
            await fixture.ExecuteAsync(English, "DELETE FROM tenant_locales WHERE tenant_id=@tenant; INSERT INTO tenant_locales(tenant_id,locale,is_default,is_enabled,sort) VALUES(@tenant,'en',true,true,0)");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_default_or_stored_tag_is_dependency_unavailable_without_default_guessing(bool tag)
    {
        using var client = fixture.ClientFor(English.Host);
        using var warm = await client.GetAsync(Path);
        warm.StatusCode.Should().Be(HttpStatusCode.OK);
        await fixture.ExecuteAsync(English, tag
            ? "INSERT INTO tenant_locales(tenant_id,locale,is_default,is_enabled,sort) VALUES(@tenant,'bad_tag',false,false,9)"
            : "UPDATE tenant_locales SET is_default=false WHERE tenant_id=@tenant");
        try { await GetAndHeadError(client, HttpStatusCode.ServiceUnavailable, "dependency_unavailable"); }
        finally
        {
            await fixture.ExecuteAsync(English, "DELETE FROM tenant_locales WHERE tenant_id=@tenant AND locale='bad_tag'; UPDATE tenant_locales SET is_default=true WHERE tenant_id=@tenant AND locale='en'");
        }
    }

    [Theory]
    [InlineData("'[]'::jsonb")]
    [InlineData("'{}'::jsonb")]
    [InlineData("NULL")]
    public async Task Invalid_deleted_or_absent_theme_uses_null_whole_value(string value)
    {
        using var client = fixture.ClientFor(English.Host);
        using var warm = await client.GetAsync(Path);
        warm.StatusCode.Should().Be(HttpStatusCode.OK);
        await fixture.ExecuteAsync(English, value == "NULL"
            ? "UPDATE tenant_settings SET deleted_at=now(),deleted_by=@actor WHERE tenant_id=@tenant AND key='branding.theme'"
            : $"UPDATE tenant_settings SET value={value} WHERE tenant_id=@tenant AND key='branding.theme'");
        try
        {
            using var response = await client.GetAsync(Path);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("theme").ValueKind.Should().Be(JsonValueKind.Null);
        }
        finally
        {
            var json = English.Curriculum!.Theme.Value.Replace("'", "''", StringComparison.Ordinal);
            await fixture.ExecuteAsync(English, $"UPDATE tenant_settings SET value='{json}'::jsonb,deleted_at=NULL,deleted_by=NULL WHERE tenant_id=@tenant AND key='branding.theme'");
        }
    }

    [Theory]
    [InlineData("granted", false)]
    [InlineData("denied", true)]
    [InlineData("omitted", true)]
    [InlineData("degraded", true)]
    public async Task Effective_entitlement_changes_attribution_only(string mode, bool expected)
    {
        using var client = fixture.ClientFor(English.Host, services => services.AddSingleton<IEntitlementProvider>(new ProjectionProvider(mode)));
        using var response = await client.GetAsync(Path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var site = await response.Content.ReadFromJsonAsync<JsonElement>();
        site.GetProperty("showPlatformAttribution").GetBoolean().Should().Be(expected);
        using var theme = JsonDocument.Parse(English.Curriculum!.Theme.Value);
        site.GetProperty("theme").GetProperty("primary").GetString().Should().Be(
            theme.RootElement.GetProperty("primary").GetString());
    }

    [Fact]
    public async Task Head_success_validation_routing_and_exception_responses_are_bodyless_no_store()
    {
        using var client = fixture.ClientFor(English.Host);
        foreach (var path in new[] { Path, Path + "?x=1", "/api/v1/public/missing" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, path);
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(path == Path ? HttpStatusCode.OK : path.Contains('?', StringComparison.Ordinal) ? HttpStatusCode.BadRequest : HttpStatusCode.NotFound);
            NoStore(response);
            (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
        }
        using var faultClient = fixture.ClientFor(English.Host, services => services.AddScoped<IPublicTenantConfigurationReader, FaultReader>());
        await GetAndHeadError(faultClient, HttpStatusCode.InternalServerError, "internal_error");
    }

    [Fact]
    public async Task Method_errors_rate_limits_and_conditional_requests_keep_no_store()
    {
        using var client = fixture.ClientFor(English.Host);
        using var post = await client.PostAsync(Path, null);
        await Error(post, HttpStatusCode.MethodNotAllowed, "method_not_allowed");
        using var conditional = new HttpRequestMessage(HttpMethod.Get, Path);
        conditional.Headers.TryAddWithoutValidation("If-None-Match", "*");
        using var unchanged = await client.SendAsync(conditional);
        unchanged.StatusCode.Should().Be(HttpStatusCode.OK);
        NoStore(unchanged);
        using var limited = fixture.ClientFor(English.Host);
        for (var index = 0; index < RateLimitingExtensions.AnonymousPermitPerWindow; index++)
            (await limited.GetAsync("/healthz")).StatusCode.Should().Be(HttpStatusCode.OK);
        using var rejection = await limited.GetAsync(Path);
        await Error(rejection, HttpStatusCode.TooManyRequests, "rate_limited");
        using var head = await limited.SendAsync(new HttpRequestMessage(HttpMethod.Head, Path));
        head.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        NoStore(head);
        (await head.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
    }

    [Fact]
    public void Production_public_endpoint_inventory_is_exact_get_and_head()
    {
        var endpoints = fixture.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => string.Equals(endpoint.RoutePattern.RawText, "api/v1/public/site", StringComparison.OrdinalIgnoreCase)).ToArray();
        endpoints.Should().HaveCount(2);
        endpoints.Select(endpoint => endpoint.RoutePattern.RawText).Should().OnlyContain(path => path == "api/v1/public/site");
        endpoints.SelectMany(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods).Should().BeEquivalentTo(["GET", "HEAD"]);
    }

    private static async Task GetAndHeadError(HttpClient client, HttpStatusCode status, string code)
    {
        using var get = await client.GetAsync(Path);
        await Error(get, status, code);
        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, Path));
        head.StatusCode.Should().Be(status);
        NoStore(head);
        (await head.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
    }

    private static async Task Error(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        response.StatusCode.Should().Be(status);
        NoStore(response);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be(code);
    }

    private static void NoStore(HttpResponseMessage response)
    {
        response.Headers.CacheControl?.NoStore.Should().BeTrue();
        response.Headers.ETag.Should().BeNull();
        response.Content.Headers.LastModified.Should().BeNull();
    }

    private sealed class ProjectionProvider(string mode) : IEntitlementProvider
    {
        public Task<EntitlementProjection> GetAsync(TenantId tenantId, CancellationToken ct = default)
        {
            return Task.FromResult(new EntitlementProjection(tenantId, "proof",
                mode == "degraded" ? FeatureKeys.All.Keys.ToDictionary(key => key.Value, _ => false, StringComparer.Ordinal)
                    : mode == "omitted" ? new Dictionary<string, bool>() : new Dictionary<string, bool> { [FeatureKeys.WhiteLabelBranding.Value] = mode == "granted" },
                mode == "degraded" ? LimitKeys.All.Keys.ToDictionary(key => key.Value, _ => 0L, StringComparer.Ordinal) : new Dictionary<string, long>(),
                ComplianceCaps.None, null, null, 1));
        }
        public Task<EntitlementRefreshOutcome> RefreshAsync(EntitlementProjection projection, CancellationToken ct = default) =>
            Task.FromResult(EntitlementRefreshOutcome.IgnoredAsStale);
    }
    private sealed class FaultReader : IPublicTenantConfigurationReader
    {
        public Task<Result<PublicTenantConfiguration>> ReadAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Synthetic public read failure");
    }
}
