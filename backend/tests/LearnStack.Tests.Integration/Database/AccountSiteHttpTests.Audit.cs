using System.Net;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Api.Tenancy;
using LearnStack.Tools.Seeder;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class AccountSiteHttpTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(7)]
    [InlineData(10)]
    public async Task Actual_account_route_preserves_claim_and_membership_codes_and_durable_host_attribution(int row)
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        var tenant = row is 7 or 8 ? SeedData.English : SeedData.Yoga;
        using var client = Client(issuer, tenant.Host);
        var token = row switch
        {
            8 => Token(issuer, tenant, claimedTenant: SeedData.Yoga.TenantId.Value),
            11 => Token(issuer, tenant, organization: true,
                claimedOrganization: SeedData.English.DefaultOrganization.OrganizationId.Value),
            12 => Token(issuer, tenant, organization: true, claimedTenant: SeedData.English.TenantId.Value,
                claimedOrganization: SeedData.English.DefaultOrganization.OrganizationId.Value),
            7 => Token(issuer, tenant, organization: true),
            _ => Token(issuer, tenant),
        };
        var reads = fixture.Observation.Reads;
        using var response = await TenantAuthenticationHttpRequest.Send(client, tenant.Host, token: token,
            tenantAssertion: SeedData.Yoga.TenantId.Value.ToString("D"), organizationAssertion: "malformed-later-header");

        await TenantAuthenticationHttpTests.Problem(response, HttpStatusCode.NotFound,
            row is 7 or 10 ? "not_found" : "tenant_mismatch");
        fixture.Observation.Reads.Should().Be(reads, "claim/membership refusal precedes controller dispatch and header assertions");
        var correlation = TenantAuthenticationReconciliationTests.Correlation(response);
        var records = await AccountHttpAudit.Read(fixture, tenant, correlation);
        if (row == 10)
        {
            records.Should().BeEmpty("a tenant-only token on an organization host has no rejection audit");
            return;
        }
        var assertionKey = row is 7 or 11 ? "assertedOrganizationId" : "assertedTenantId";
        var asserted = row switch
        {
            7 => SeedData.English.DefaultOrganization.OrganizationId.Value,
            8 => SeedData.Yoga.TenantId.Value,
            11 => SeedData.English.DefaultOrganization.OrganizationId.Value,
            _ => SeedData.English.TenantId.Value,
        };
        AccountHttpAudit.Assert(records.Should().ContainSingle().Subject, tenant, correlation, "jwt-claim",
            row is 7 or 8 ? null : SeedData.Yoga.DefaultOrganization.OrganizationId.Value, assertionKey, asserted);
        var foreign = tenant == SeedData.English ? SeedData.Yoga : SeedData.English;
        (await AccountHttpAudit.Read(fixture, foreign, correlation)).Should().BeEmpty(
            "the durable event belongs to the defended host, never a token-selected tenant");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_authenticated_header_rejection_preserves_actor_correlation_and_trusted_organization(bool claimless)
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        using var client = Client(issuer, SeedData.Yoga.Host);
        var token = claimless ? issuer.Token(issuer.Without("tenant_id", "organization_id"))
            : Token(issuer, SeedData.Yoga, organization: true);
        var reads = fixture.Observation.Reads;
        using var response = await TenantAuthenticationHttpRequest.Send(client, SeedData.Yoga.Host,
            claimless ? TenantAuthenticationHttpRequest.PublicPath : TenantAuthenticationHttpRequest.AccountPath,
            token, organizationAssertion: SeedData.English.DefaultOrganization.OrganizationId.Value.ToString("D"));

        await TenantAuthenticationHttpTests.Problem(response, HttpStatusCode.NotFound, "tenant_mismatch");
        fixture.Observation.Reads.Should().Be(reads);
        var correlation = TenantAuthenticationReconciliationTests.Correlation(response);
        var record = (await AccountHttpAudit.Read(fixture, SeedData.Yoga, correlation)).Should().ContainSingle().Subject;
        AccountHttpAudit.Assert(record, SeedData.Yoga, correlation, "header",
            SeedData.Yoga.DefaultOrganization.OrganizationId.Value, "assertedOrganizationId",
            SeedData.English.DefaultOrganization.OrganizationId.Value);
    }

    [Fact]
    public async Task Anonymous_header_mismatch_and_malformed_assertions_keep_their_distinct_errors_without_query_reads()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        using var client = Client(issuer, SeedData.English.Host);
        var reads = fixture.Observation.Reads;
        using var anonymous = await TenantAuthenticationHttpRequest.Send(client, SeedData.English.Host,
            TenantAuthenticationHttpRequest.PublicPath, tenantAssertion: SeedData.Yoga.TenantId.Value.ToString("D"));
        await TenantAuthenticationHttpTests.Problem(anonymous, HttpStatusCode.NotFound, "not_found");
        using var malformed = await TenantAuthenticationHttpRequest.Send(client, SeedData.English.Host,
            token: Token(issuer, SeedData.English), tenantAssertion: "not-a-guid");
        await TenantAuthenticationHttpTests.Problem(malformed, HttpStatusCode.BadRequest, "validation_failed");
        using var repeated = await TenantAuthenticationHttpRequest.Send(client, SeedData.English.Host,
            token: Token(issuer, SeedData.English), tenantAssertion:
            SeedData.English.TenantId.Value.ToString("D") + ", " + SeedData.English.TenantId.Value.ToString("D"));
        await TenantAuthenticationHttpTests.Problem(repeated, HttpStatusCode.BadRequest, "validation_failed");

        fixture.Observation.Reads.Should().Be(reads);
        foreach (var response in new[] { anonymous, malformed, repeated })
            (await AccountHttpAudit.Read(fixture, SeedData.English,
                TenantAuthenticationReconciliationTests.Correlation(response))).Should().BeEmpty();
    }
}

/// <summary>Every observed audit read runs as learnstack_app with the defended tenant announced.</summary>
internal static class AccountHttpAudit
{
    internal static async Task<List<AccountHttpAuditRow>> Read(PublicReadFixture fixture, SeedTenant tenant, string correlation)
    {
        await using var connection = await PostgresFixture.OpenAsync(fixture.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, tenant.TenantId.Value);
        await SchemaQueries.AssertApplicationRoleAsync(connection, transaction);
        await using var command = new NpgsqlCommand("""
            SELECT tenant_id, organization_id, actor_user_id, operation, outcome, correlation_id, metadata::text
            FROM audit_log WHERE correlation_id=@correlation
            """, (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        command.Parameters.AddWithValue("correlation", correlation);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<AccountHttpAuditRow>();
        while (await reader.ReadAsync())
            rows.Add(new AccountHttpAuditRow(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.GetString(6)));
        return rows;
    }

    internal static void Assert(AccountHttpAuditRow row, SeedTenant tenant, string correlation, string source,
        Guid? trustedOrganization, string assertedKey, Guid asserted)
    {
        row.Tenant.Should().Be(tenant.TenantId.Value);
        row.Organization.Should().BeNull("security audit visibility remains tenant-wide");
        row.Actor.Should().Be(TenantAuthenticationIssuer.Actor);
        row.Operation.Should().Be(AuditingTenantAssertionRecorder.RejectOperation);
        row.Outcome.Should().Be("denied");
        row.Correlation.Should().Be(correlation);
        using var metadata = JsonDocument.Parse(row.Metadata);
        metadata.RootElement.GetProperty("assertionSource").GetString().Should().Be(source);
        metadata.RootElement.GetProperty(assertedKey).GetGuid().Should().Be(asserted);
        if (trustedOrganization is { } organization)
            metadata.RootElement.GetProperty("trustedOrganizationId").GetGuid().Should().Be(organization);
        else metadata.RootElement.GetProperty("trustedOrganizationId").ValueKind.Should().Be(JsonValueKind.Null);
    }
}

internal sealed record AccountHttpAuditRow(Guid Tenant, Guid? Organization, Guid? Actor, string Operation,
    string Outcome, string Correlation, string Metadata);
