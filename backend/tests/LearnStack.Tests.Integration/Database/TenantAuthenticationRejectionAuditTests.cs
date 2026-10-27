using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Api.Tenancy;
using LearnStack.Infrastructure.Audit;
using LearnStack.Modules.Tenancy.Application.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Resolver-to-recorder writes and omissions through real app-role PostgreSQL.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class TenantAuthenticationRejectionAuditTests(SchemaFixture schema)
{
    [Theory]
    [InlineData(7)]
    [InlineData(10)]
    [InlineData(14)]
    public async Task Real_token_membership_denials_persist_only_row_7_without_announcing_an_unconfirmed_tenant(int row)
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var dataSource = NpgsqlDataSource.Create(schema.Postgres.AppConnectionString);
        var opens = 0;
        var health = new AuditHealth();
        var lazySource = new Lazy<NpgsqlDataSource>(() =>
        {
            Interlocked.Increment(ref opens);
            return dataSource;
        });
        await using var host = await TenantAuthenticationReconciliationHost.StartAsync(issuer,
            recorder: services => Recorder(services, lazySource, health));

        try
        {
            using var response = await host.SendAsync(issuer.Token(row == 10
                ? issuer.Without("organization_id") : issuer.Payload()), row switch
                {
                    7 => TenantAuthenticationReconciliationHost.TenantHost,
                    10 => TenantAuthenticationReconciliationHost.OrganizationHost,
                    _ => TenantAuthenticationReconciliationHost.PlatformHost,
                }, tenantAssertion: TenantAuthenticationReconciliationHost.OtherTenant);
            await TenantAuthenticationReconciliationTests.AssertProblem(response, HttpStatusCode.NotFound, "not_found");
            var correlation = TenantAuthenticationReconciliationTests.Correlation(response);
            var persisted = await ReadAsync(correlation, TenantAuthenticationIssuer.Tenant);

            if (row == 7)
            {
                var entry = persisted.Should().ContainSingle().Subject;
                AssertRow(entry, correlation, null, "assertedOrganizationId", TenantAuthenticationIssuer.Organization);
                opens.Should().Be(1, "a real standalone write opened the app-role source");
                (await ReadAsync(correlation, TenantAuthenticationReconciliationHost.OtherTenant)).Should().BeEmpty(
                    "the security event belongs to the defended host tenant");
            }
            else
            {
                persisted.Should().BeEmpty();
                opens.Should().Be(0, "rows 10 and 14 must not acquire the store's data source");
                host.Recorder.Rejections.Should().BeEmpty();
                host.Recorder.NoTenantRefusals.Should().HaveCount(row == 14 ? 1 : 0);
            }
            host.Ports.OrganizationChecks.Should().BeEmpty();
            health.IsHealthy.Should().BeTrue();
        }
        finally { await DeleteRecordedRowsAsync(host); }
    }

    [Fact]
    public async Task Organization_host_disagreement_persists_host_scope_as_metadata_in_a_tenant_wide_row()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var dataSource = NpgsqlDataSource.Create(schema.Postgres.AppConnectionString);
        await using var host = await TenantAuthenticationReconciliationHost.StartAsync(issuer,
            recorder: services => Recorder(services, new Lazy<NpgsqlDataSource>(() => dataSource), new AuditHealth()));
        try
        {
            using var response = await host.SendAsync(issuer.Token(issuer.Payload(
                    ("organization_id", TenantAuthenticationReconciliationHost.OtherOrganization.ToString("D")))),
                TenantAuthenticationReconciliationHost.OrganizationHost,
                organizationAssertion: TenantAuthenticationReconciliationHost.OtherOrganization);
            await TenantAuthenticationReconciliationTests.AssertProblem(response, HttpStatusCode.NotFound, "tenant_mismatch");
            var correlation = TenantAuthenticationReconciliationTests.Correlation(response);
            var entry = (await ReadAsync(correlation, TenantAuthenticationIssuer.Tenant)).Should().ContainSingle().Subject;
            AssertRow(entry, correlation, TenantAuthenticationIssuer.Organization,
                "assertedOrganizationId", TenantAuthenticationReconciliationHost.OtherOrganization);
            host.ResolvedRequests.Should().BeEmpty("record attribution does not install a rejected context");
        }
        finally { await DeleteRecordedRowsAsync(host); }
    }

    [Theory]
    [InlineData(7)]
    [InlineData(14)]
    public async Task An_unavailable_audit_source_preserves_refusal_and_row_14_never_touches_it(int row)
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        var attempts = 0;
        var health = new AuditHealth();
        var source = new Lazy<NpgsqlDataSource>(() =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("test-only unavailable audit source");
        });
        await using var host = await TenantAuthenticationReconciliationHost.StartAsync(issuer,
            recorder: services => Recorder(services, source, health));
        using var response = await host.SendAsync(issuer.Token(), row == 7
            ? TenantAuthenticationReconciliationHost.TenantHost : TenantAuthenticationReconciliationHost.PlatformHost);

        await TenantAuthenticationReconciliationTests.AssertProblem(response, HttpStatusCode.NotFound, "not_found");
        attempts.Should().Be(row == 7 ? 1 : 0);
        health.IsHealthy.Should().Be(row != 7);
        var critical = host.Logs.Entries.Where(entry => entry.Category == typeof(PostgresAuditStore).FullName
            && entry.Level == LogLevel.Critical && entry.Event.Id == 3).ToArray();
        critical.Should().HaveCount(row == 7 ? 1 : 0);
        if (row == 7) health.FailingOperation.Should().Be(AuditingTenantAssertionRecorder.RejectOperation);
        host.Ports.OrganizationChecks.Should().BeEmpty();
        (await ReadAsync(TenantAuthenticationReconciliationTests.Correlation(response), TenantAuthenticationIssuer.Tenant))
            .Should().BeEmpty();
    }

    private static AuditingTenantAssertionRecorder Recorder(IServiceProvider services,
        Lazy<NpgsqlDataSource> dataSource, AuditHealth health)
    {
        var catalog = new AuditCatalog([new TenancyAuditCatalogSource()]);
        var store = new PostgresAuditStore(new AuditStateCapture(), dataSource,
            services.GetRequiredService<ILogger<PostgresAuditStore>>(),
            services.GetRequiredService<IMeterFactory>(), health, catalog,
            services.GetRequiredService<ITenantContextAccessor>(), new LearnStack.Infrastructure.Persistence.GlobalIdentityEnrollment());
        return new AuditingTenantAssertionRecorder(services.GetRequiredService<LoggingTenantAssertionRecorder>(),
            catalog, store, new TenantAssertionBurstDetector(Options.Create(new AssertionBurstOptions()), new SystemClock()),
            services.GetRequiredService<TenantAssertionAuditBudget>(),
            new SystemClock(), new SystemGuidFactory(), services.GetRequiredService<ILogger<AuditingTenantAssertionRecorder>>());
    }

    private static void AssertRow(PersistedRejection entry, string correlation, Guid? trustedOrganization,
        string assertedKey, Guid assertedValue)
    {
        entry.Tenant.Should().Be(TenantAuthenticationIssuer.Tenant);
        entry.Organization.Should().BeNull("security rows remain tenant-wide regardless of host scope");
        entry.Actor.Should().Be(TenantAuthenticationIssuer.Actor);
        entry.Operation.Should().Be(AuditingTenantAssertionRecorder.RejectOperation);
        entry.Outcome.Should().Be("denied");
        entry.Correlation.Should().Be(correlation);
        using var metadata = JsonDocument.Parse(entry.Metadata);
        metadata.RootElement.GetProperty("assertionSource").GetString().Should().Be("jwt-claim");
        metadata.RootElement.GetProperty(assertedKey).GetGuid().Should().Be(assertedValue);
        var trusted = metadata.RootElement.GetProperty("trustedOrganizationId");
        if (trustedOrganization is { } organization) trusted.GetGuid().Should().Be(organization);
        else trusted.ValueKind.Should().Be(JsonValueKind.Null);
    }

    private async Task<List<PersistedRejection>> ReadAsync(string correlation, Guid tenant)
    {
        await using var connection = await PostgresFixture.OpenAsync(schema.Postgres.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, tenant);
        await SchemaQueries.AssertApplicationRoleAsync(connection, transaction);
        await using var command = new NpgsqlCommand("""
            SELECT tenant_id, organization_id, actor_user_id, operation, outcome, correlation_id, metadata::text
            FROM audit_log WHERE correlation_id = @correlation
            """, (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        command.Parameters.AddWithValue("correlation", correlation);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<PersistedRejection>();
        while (await reader.ReadAsync())
            rows.Add(new PersistedRejection(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.GetString(6)));
        return rows;
    }

    private async Task DeleteRecordedRowsAsync(TenantAuthenticationReconciliationHost host)
    {
        // Only cleanup uses the append-only administrator role. Every assertion
        // and the production store itself use learnstack_app with FORCE RLS.
        foreach (var correlation in host.Recorder.Rejections.Select(rejection => rejection.CorrelationId).Distinct())
        {
            if (correlation is null) continue;
            await using var connection = await PostgresFixture.OpenAsync(schema.Postgres.PlatformConnectionString);
            await using var command = new NpgsqlCommand("DELETE FROM audit_log WHERE correlation_id = @correlation",
                (NpgsqlConnection)connection);
            command.Parameters.AddWithValue("correlation", correlation);
            await command.ExecuteNonQueryAsync();
        }
    }

    private sealed record PersistedRejection(Guid Tenant, Guid? Organization, Guid? Actor,
        string Operation, string Outcome, string Correlation, string Metadata);
}
