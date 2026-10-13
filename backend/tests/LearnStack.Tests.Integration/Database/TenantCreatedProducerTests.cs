using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Application.Messaging;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Modules.Tenancy.Application.Contracts.IntegrationEvents;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tools.Seeder;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Completed provisioning produces one pending fact and two MUST rows, on both real roots.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class TenantCreatedProducerTests(SchemaFixture schema)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Provisioning_business_outbox_and_both_MUST_rows_share_one_commit(bool api, bool refuseAudit)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        if (refuseAudit)
        {
            await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
            await using var revoke = new NpgsqlCommand("REVOKE INSERT ON audit_log FROM learnstack_app", (NpgsqlConnection)owner);
            await revoke.ExecuteNonQueryAsync();
        }

        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var seed = SeedComposition.Build(source, null, NullLoggerFactory.Instance);
        await using var host = new ConstructionOnlyApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Default", database.AppConnectionString);
            builder.ConfigureServices(services => services.AddSingleton<ITenantContextAccessor>(
                new StaticTenantContextAccessor(null)));
        });
        await using var scope = (api ? host.Services : seed).CreateAsyncScope();
        var services = scope.ServiceProvider;
        var tenant = TenantId.From(Guid.CreateVersion7());
        var organization = OrganizationId.From(Guid.CreateVersion7());
        var command = new ProvisionTenantCommand(tenant, "producer-probe", "Producer probe", organization, "hq", "HQ");
        services.GetRequiredService<IOutbox>().Should().BeOfType<PostgresOutbox>();
        services.GetRequiredService<ITenantContext>().IsResolved.Should().BeFalse();

        var send = () => services.GetRequiredService<ISender>().Send(command);
        if (refuseAudit)
        {
            (await send.Should().ThrowAsync<AuditWriteFailedException>()).Which.Error.Code.Should().Be("audit_unavailable");
        }
        else
        {
            var result = await send();
            result.IsSuccess.Should().BeTrue();
            result.Value!.DefaultOrganizationId.Should().Be(organization);
        }

        services.GetRequiredService<IUnitOfWork>().Transaction.Should().BeNull();
        services.GetRequiredService<ProducerExecution>().HasInvocation.Should().BeFalse();
        services.GetRequiredService<IAuditStateCapture>().Intents.Should().BeEmpty();
        var tracked = services.GetRequiredService<TenancyDbContext>().ChangeTracker.Entries()
            .Select(entry => entry.Entity).OfType<LearnStack.SharedKernel.Domain.IHasDomainEvents>().ToList();
        tracked.Should().NotBeEmpty("the real writer tracked the tenant and organization");
        tracked.Should().OnlyContain(entity => entity.DomainEvents.Count == 0,
            "owner cleanup clears saved facts after either terminal");

        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, tenant.Value);
        (await ScalarAsync(connection, transaction, "SELECT current_user")).Should().Be("learnstack_app");
        (await ScalarAsync(connection, transaction,
            "SELECT (rolbypassrls OR rolsuper)::text FROM pg_roles WHERE rolname = current_user")).Should().Be("false");
        var expected = refuseAudit ? 0L : 1L;
        (await ScalarAsync(connection, transaction, "SELECT count(*) FROM tenants WHERE default_organization_id IS NOT NULL"))
            .Should().Be(expected);
        (await ScalarAsync(connection, transaction, "SELECT count(*) FROM organizations")).Should().Be(expected);
        (await ScalarAsync(connection, transaction, "SELECT count(*) FROM outbox_messages")).Should().Be(expected);

        if (refuseAudit) return;

        await using var read = new NpgsqlCommand(
            """
            SELECT id, tenant_id, organization_id, correlation_id, causation_id, actor_user_id,
                   type, topic, partition_key, payload::text, metadata IS NULL,
                   processed_at, attempts, last_error, occurred_at
            FROM outbox_messages
            """, connection, transaction);
        await using (var row = await read.ExecuteReaderAsync())
        {
            (await row.ReadAsync()).Should().BeTrue();
            var eventId = row.GetGuid(0);
            var factId = row.GetGuid(4);
            eventId.Should().NotBeEmpty().And.NotBe(factId, "translation mints an integration identity separate from causation");
            factId.Should().NotBeEmpty();
            row.GetGuid(1).Should().Be(tenant.Value);
            row.IsDBNull(2).Should().BeTrue("provisioning announces no organization");
            ActivityContext.TryParse(row.GetString(3), null, out _).Should().BeTrue();
            row.IsDBNull(5).Should().BeTrue("unresolved provisioning invents no causal human");
            row.GetString(6).Should().Be(typeof(TenantCreatedIntegrationEventV1).AssemblyQualifiedName);
            row.GetString(7).Should().Be("learnstack.tenancy.tenant");
            row.GetString(8).Should().Be(tenant.Value.ToString("D"));
            using var payload = JsonDocument.Parse(row.GetString(9));
            payload.RootElement.EnumerateObject().Select(property => property.Name)
                .Should().BeEquivalentTo(["EventId", "TenantId", "OccurredAt", "Topic", "PartitionKey"]);
            var decoded = JsonSerializer.Deserialize<TenantCreatedIntegrationEventV1>(
                row.GetString(9), IntegrationEventBase.PayloadJsonOptions)!;
            decoded.EventId.Should().Be(eventId);
            decoded.TenantId.Should().Be(tenant.Value);
            decoded.OccurredAt.Should().BeCloseTo(row.GetFieldValue<DateTimeOffset>(14), TimeSpan.FromMicroseconds(1),
                "PostgreSQL timestamps have microsecond precision");
            row.GetBoolean(10).Should().BeTrue();
            row.IsDBNull(11).Should().BeTrue();
            row.GetInt32(12).Should().Be(0);
            row.IsDBNull(13).Should().BeTrue();
            (await row.ReadAsync()).Should().BeFalse("one completed provisioning yields one pending integration event");
        }

        (await ScalarAsync(connection, transaction,
            "SELECT count(*) FROM audit_log WHERE operation_class = 'Must' AND outcome = 'success'"))
            .Should().Be(2L);
        (await ScalarAsync(connection, transaction,
            "SELECT count(DISTINCT operation) FROM audit_log WHERE operation IN ('tenancy.tenant.create', 'tenancy.organization.create')"))
            .Should().Be(2L);
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return await command.ExecuteScalarAsync();
    }
}
