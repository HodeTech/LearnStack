using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Api.Tenancy;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tests.Integration.Database;
using LearnStack.Tools.Seeder;
using Npgsql;

namespace LearnStack.Tests.Integration.TenancyIdentity;

public sealed partial class TenantRealmApiTests
{
    private static async Task AccountAsync(HttpResponseMessage response, SeedIdentity identity,
        TenantRealmApiObservation observation)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a real provider access token must reach the existing production AccountSite route");
        TenantAuthenticationHttpTests.NoStore(response);
        var account = await response.Content.ReadFromJsonAsync<JsonElement>();
        account.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            ["actorId", "tenantId", "organizationId", "site"]);
        account.GetProperty("actorId").GetGuid().Should().Be(identity.UserId.Value);
        account.GetProperty("tenantId").GetGuid().Should().Be(identity.Tenant.TenantId.Value);
        var organization = Organization(identity.Tenant);
        if (organization is { } expected) account.GetProperty("organizationId").GetGuid().Should().Be(expected);
        else account.GetProperty("organizationId").ValueKind.Should().Be(JsonValueKind.Null);
        AssertSite(account.GetProperty("site"), identity.Tenant);
        observation.Reads.Should().NotBeEmpty("the real configuration reader must have executed");
        AssertContext(observation.Reads.Last(), identity.Tenant, identity.UserId.Value, TenantContextOrigin.HostAndClaim);
    }

    private static async Task PublicSiteAsync(HttpResponseMessage response, SeedTenant tenant)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        TenantAuthenticationHttpTests.NoStore(response);
        AssertSite(await response.Content.ReadFromJsonAsync<JsonElement>(), tenant);
    }

    private static void AssertSite(JsonElement site, SeedTenant tenant)
    {
        site.GetProperty("displayName").GetString().Should().Be(tenant.DisplayName);
        site.GetProperty("defaultLocale").GetString().Should().Be(
            tenant.Curriculum!.Locales.Single(locale => locale.IsDefault).Locale);
        site.GetProperty("enabledLocales").EnumerateArray().Select(locale => locale.GetString()).Should().Equal(
            tenant.Curriculum!.Locales.Where(locale => locale.IsEnabled).OrderBy(locale => locale.Sort).Select(locale => locale.Locale));
        using var expectedTheme = JsonDocument.Parse(tenant.Curriculum!.Theme.Value);
        JsonElement.DeepEquals(site.GetProperty("theme"), expectedTheme.RootElement).Should().BeTrue(
            "the actual tenant theme must agree with typed SeedData rather than another tenant's configuration");
        site.GetProperty("showPlatformAttribution").GetBoolean().Should().BeFalse();
    }

    private static Guid? Organization(SeedTenant tenant) => tenant.MapHostToDefaultOrganization
        ? tenant.DefaultOrganization.OrganizationId.Value : null;

    private static void AssertContext(TenantRealmReadContext context, SeedTenant tenant, Guid? actor, TenantContextOrigin origin)
    {
        context.Tenant.Should().Be(tenant.TenantId.Value);
        context.Organization.Should().Be(Organization(tenant));
        context.Actor.Should().Be(actor);
        context.Origin.Should().Be(origin);
        context.HostTenant.Should().Be(tenant.TenantId.Value);
        context.HostOrganization.Should().Be(Organization(tenant));
    }

    private static async Task UnauthorizedAsync(HttpResponseMessage response)
    {
        await TenantAuthenticationHttpTests.Problem(response, HttpStatusCode.Unauthorized, "unauthorized");
        response.Headers.WwwAuthenticate.Should().ContainSingle().Which.ToString().Should().Be("Bearer");
    }

    private static bool HasAudience(JsonElement payload, string expected)
    {
        if (!payload.TryGetProperty("aud", out var audience)) return false;
        return audience.ValueKind == JsonValueKind.String ? audience.GetString() == expected
            : audience.ValueKind == JsonValueKind.Array
                && audience.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == expected);
    }

    private async Task AssertRejectionAsync(HttpResponseMessage response, SeedTenant defended, SeedIdentity identity,
        string assertedKey, Guid asserted)
    {
        var correlation = TenantAuthenticationReconciliationTests.Correlation(response);
        var rows = await AccountHttpAudit.Read(_database, defended, correlation);
        var row = rows.Should().ContainSingle().Subject;
        row.Tenant.Should().Be(defended.TenantId.Value);
        row.Organization.Should().BeNull("rejected scope does not narrow security-audit visibility");
        row.Actor.Should().Be(identity.UserId.Value);
        row.Operation.Should().Be(AuditingTenantAssertionRecorder.RejectOperation);
        row.Outcome.Should().Be("denied");
        row.Correlation.Should().Be(correlation);
        using var metadata = JsonDocument.Parse(row.Metadata);
        metadata.RootElement.GetProperty("assertionSource").GetString().Should().Be("jwt-claim");
        metadata.RootElement.GetProperty(assertedKey).GetGuid().Should().Be(asserted);
        if (Organization(defended) is { } trusted)
            metadata.RootElement.GetProperty("trustedOrganizationId").GetGuid().Should().Be(trusted);
        else metadata.RootElement.GetProperty("trustedOrganizationId").ValueKind.Should().Be(JsonValueKind.Null);
        var foreign = defended == SeedData.English ? SeedData.Yoga : SeedData.English;
        (await AccountHttpAudit.Read(_database, foreign, correlation)).Should().BeEmpty(
            "the event is visible only under the defended tenant, never token-selected scope");
    }

    private async Task<long[]> AuditCountsAsync(PublicReadFixture? sourceDatabase = null)
    {
        var counts = new List<long>();
        foreach (var tenant in SeedData.All)
        {
            await using var connection = await PostgresFixture.OpenAsync((sourceDatabase ?? _database).AppConnectionString);
            await using var transaction = await connection.BeginTransactionAsync();
            await SchemaQueries.SetTenantAsync(connection, transaction, tenant.TenantId.Value);
            await SchemaQueries.AssertApplicationRoleAsync(connection, transaction);
            await using var count = new NpgsqlCommand("SELECT count(*) FROM audit_log WHERE tenant_id=@tenant",
                (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
            count.Parameters.AddWithValue("tenant", tenant.TenantId.Value);
            counts.Add((long)(await count.ExecuteScalarAsync())!);
        }
        return counts.ToArray();
    }

    private async Task AssertSingleAdditionalAuditAsync(long[] before, SeedTenant defended)
    {
        var expected = SeedData.All.Select((tenant, index) => before[index] + (tenant == defended ? 1 : 0));
        (await AuditCountsAsync()).Should().Equal(expected,
            "one real MUST rejection audit is durable under exactly the defended tenant");
    }
}
