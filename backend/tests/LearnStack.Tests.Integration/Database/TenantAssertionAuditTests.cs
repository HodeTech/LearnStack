using System.Diagnostics.Metrics;
using FluentAssertions;
using LearnStack.Api.Tenancy;
using LearnStack.Infrastructure.Audit;
using LearnStack.Modules.Tenancy.Application.Audit;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The rejected-assertion row against the real table, under the real policy.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the unit suite cannot say.</b> It proves which rows are written and what they
/// carry, against a store double. It cannot prove PostgreSQL accepts one: <c>audit_log</c>
/// is organization-scoped with <c>ENABLE</c> + <c>FORCE ROW LEVEL SECURITY</c>, so a draft
/// whose <c>organization_id</c> and announced GUC disagree fails <c>WITH CHECK</c> — and
/// this is the one writer whose rows have no business transaction to ride and no
/// organization to name.
/// </para>
/// <para>
/// It connects as <c>learnstack_app</c>, which is the half that matters: a non-owning
/// <c>NOBYPASSRLS</c> role. Written as the owner it would pass against inert policies.
/// </para>
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class TenantAssertionAuditTests
{
    private readonly SchemaFixture _schema;

    public TenantAssertionAuditTests(SchemaFixture schema) => _schema = schema;

    [Fact]
    public async Task An_authenticated_mismatch_lands_a_denied_row_the_tenant_can_read()
    {
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);

        var store = new PostgresAuditStore(
            new AuditStateCapture(),
            new Lazy<NpgsqlDataSource>(() => dataSource),
            NullLogger<PostgresAuditStore>.Instance,
            MeterFactory,
            new AuditHealth());

        var recorder = Recorder(store, threshold: 10);

        try
        {
            // Inside the try, unlike the first draft. This suite shares a schema with
            // TenancySchemaTests, which compares EXACT per-table row counts — so a write
            // that succeeded and then failed an assertion would leave a row behind and
            // fail an unrelated class, in an order xUnit does not contract.
            await recorder.RecordRejectionAsync(new TenantAssertionRejection(
                SchemaFixture.TenantA,
                TenantAssertionDimension.Tenant,
                SchemaFixture.TenantB,
                IsAuthenticated: true));

            var row = await ReadAsync();

            row.Should().NotBeNull("learnstack_app wrote it under the resolved tenant's policy");
            row!.Value.Operation.Should().Be(AuditingTenantAssertionRecorder.RejectOperation);
            row.Value.Outcome.Should().Be("denied");
            row.Value.TenantId.Should().Be(SchemaFixture.TenantA);
            row.Value.OrganizationId.Should().BeNull();
            row.Value.Metadata.Should().Contain(SchemaFixture.TenantB.ToString(),
                "the asserted value is metadata, never the row's tenant");

            // Round-tripped through PostgreSQL, not merely handed to a double. The column
            // is varchar(100) with a filtered index on it, so a value the writer composes
            // but the column will not hold is a defect only a real write can show.
            row.Value.CorrelationId.Should().Be(FixtureContext.Correlation,
                "the row joins to its own trace and to the Warning line beside it");
        }
        finally
        {
            await DeleteAsync();
        }
    }

    [Fact]
    public async Task An_anonymous_burst_lands_one_row_however_long_the_run_is()
    {
        // Fifty occurrences, one row. The row an anonymous caller can cause is the one
        // thing this design bounds, and a fixture that wrote fifty would be the flood the
        // burst event exists to replace — visible here as a count against the real table.
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);

        var store = new PostgresAuditStore(
            new AuditStateCapture(),
            new Lazy<NpgsqlDataSource>(() => dataSource),
            NullLogger<PostgresAuditStore>.Instance,
            MeterFactory,
            new AuditHealth());

        var recorder = Recorder(store, threshold: 3);

        try
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                await recorder.RecordRejectionAsync(new TenantAssertionRejection(
                    SchemaFixture.TenantA,
                    TenantAssertionDimension.Organization,
                    SchemaFixture.TenantB,
                    IsAuthenticated: false));
            }

            var row = await ReadAsync();

            row.Should().NotBeNull();
            row!.Value.Operation.Should().Be(AuditingTenantAssertionRecorder.BurstOperation);
            row.Value.Metadata.Should().Contain("assertedOrganizationId");
        }
        finally
        {
            await DeleteAsync();
        }
    }

    /// <summary>
    /// Reads the assertion rows as tenant A, through the policy rather than around it.
    /// </summary>
    /// <remarks>
    /// No <c>app.organization_id</c> is announced, deliberately: a tenant-wide row must be
    /// visible to a tenant-scoped read, and that is the property that makes this event
    /// readable by the admin whose boundary was defended.
    /// </remarks>
    private async Task<(Guid TenantId, Guid? OrganizationId, string Operation, string Outcome,
        string? Metadata, string? CorrelationId)?> ReadAsync()
    {
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();

        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);

        await using var command = new NpgsqlCommand(
            """
            SELECT tenant_id, organization_id, operation, outcome, metadata::text,
                   correlation_id
            FROM audit_log
            WHERE operation LIKE 'tenancy.tenant_assertion.%'
            """,
            (NpgsqlConnection)connection,
            (NpgsqlTransaction)transaction);

        await using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return null;
        }

        var row = (
            reader.GetGuid(0),
            reader.IsDBNull(1) ? (Guid?)null : reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));

        (await reader.ReadAsync()).Should().BeFalse("each case writes exactly one row");

        return row;
    }

    private async Task DeleteAsync()
    {
        await using var connection = await PostgresFixture.OpenAsync(
            _schema.Postgres.PlatformConnectionString);
        await using var command = new NpgsqlCommand(
            "DELETE FROM audit_log WHERE operation LIKE 'tenancy.tenant_assertion.%'",
            (NpgsqlConnection)connection);

        await command.ExecuteNonQueryAsync();
    }

    private static AuditingTenantAssertionRecorder Recorder(IAuditStore store, int threshold) =>
        new(new LoggingTenantAssertionRecorder(
                NullLogger<LoggingTenantAssertionRecorder>.Instance, MeterFactory),
            new AuditCatalog([new TenancyAuditCatalogSource()]),
            store,
            new TenantAssertionBurstDetector(
                Options.Create(new AssertionBurstOptions
                {
                    Threshold = threshold,
                    Window = TimeSpan.FromMinutes(5),
                }),
                new SystemClock()),
            new FixtureContext(),
            new SystemClock(),
            new LearnStack.SharedKernel.Identifiers.SystemGuidFactory(),
            NullLogger<AuditingTenantAssertionRecorder>.Instance);

    /// <summary>Tenant A, resolved, as the resolver would have left it.</summary>
    private sealed class FixtureContext : LearnStack.SharedKernel.Tenancy.ITenantContext
    {
        public bool IsResolved => true;

        public LearnStack.SharedKernel.Identifiers.TenantId TenantId =>
            LearnStack.SharedKernel.Identifiers.TenantId.From(SchemaFixture.TenantA);

        public LearnStack.SharedKernel.Identifiers.OrganizationId? OrganizationId => null;

        public LearnStack.SharedKernel.Identifiers.UserId? UserId => null;

        public const string Correlation = "00-assertion-fixture-01";

        public string? CorrelationId => Correlation;

        public string? ModuleName => "tenancy";
    }

    private static readonly IMeterFactory MeterFactory =
        new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();
}
