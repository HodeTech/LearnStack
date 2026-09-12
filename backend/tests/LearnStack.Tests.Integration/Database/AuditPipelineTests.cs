using FluentAssertions;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Audit;
using LearnStack.Tools.Seeder;
using MediatR;
using LearnStack.Infrastructure.Caching;
using LearnStack.SharedKernel.Caching;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Time;
using System.Diagnostics.Metrics;
using LearnStack.Infrastructure.Audit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// A real command through the real pipeline, and the row it leaves in <c>audit_log</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything else in this suite proves a part.</b> The store writes rows against the
/// real table; the interceptor fills the capture; the behaviour classifies and reconciles
/// against doubles. None of that says a request produces a row — and until it does, every
/// one of those parts could be right while the thing they exist for never happens.
/// </para>
/// <para>
/// It runs the seeder's composition root because that is a real one: the same
/// <c>AddModuleDbContext</c>, the same <c>NpgsqlUnitOfWork</c>, the same catalogue merged
/// from the same module sources, and the same eight-step MediatR pipeline. A hand-built
/// provider would test the arrangement this case exists to doubt.
/// </para>
/// <para>
/// It connects as <c>learnstack_app</c>, which is the point: a non-owning
/// <c>NOBYPASSRLS</c> role. Connecting as the owner would pass against inert policies and
/// prove nothing about the half of the claim that is Row Level Security.
/// </para>
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class AuditPipelineTests : IAsyncLifetime
{
    private readonly SchemaFixture _schema;

    public AuditPipelineTests(SchemaFixture schema) => _schema = schema;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => CleanUpAsync();

    /// <summary>
    /// Provisioning a tenant writes the two rows its matrix promises, on the business
    /// transaction.
    /// </summary>
    /// <remarks>
    /// Named for the catalogue rather than for the scenario: this is
    /// <see href="../../../../docs/standards/21-architecture-tests-catalogue.md">Standards
    /// 21</see>'s canonical rule, and a second spelling is the drift that document exists
    /// to prevent.
    /// </remarks>
    [Fact]
    public async Task MustClass_Audit_Writes_Share_The_Business_Transaction()
    {
        // ONE command, TWO rows — the case ADR-0033 Amendment 2 § 1 exists for.
        // ProvisionTenantCommand writes two aggregate roots on one transaction and the
        // Tenancy matrix classifies both MUST, so a singular reading of "one row per
        // request" would silently never write the Organization row the matrix promises.
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);

        var exitCode = await Runner(dataSource).RunAsync(CancellationToken.None, [SeedData.English]);

        exitCode.Should().Be(0);

        var rows = await RowsAsync(SeedData.English.TenantId.Value);

        // The whole seed for one tenant, every operation its module's matrix classifies
        // MUST. The pair is the point — ProvisionTenantCommand alone accounts for the
        // first two — and the rest are here because a case asserting only the pair would
        // pass while every other command audited nothing.
        rows.Select(row => row.Operation).Should().BeEquivalentTo(
            "tenancy.tenant.create",
            "tenancy.organization.create",
            "tenancy.organization.create",
            "tenancy.hostmapping.write",
            "customization.content_type.register",
            "customization.content_type.publish",
            "customization.level_taxonomy.register",
            "customization.level_taxonomy.publish");

        rows.Count(row => row.Operation == "tenancy.organization.create").Should().Be(2,
            "the seed creates a second organization with CreateOrganizationCommand, and "
            + "one slug registered from two request types is one matrix row and two rows "
            + "in the log");
    }

    [Fact]
    public async Task A_MUST_row_that_cannot_be_written_takes_its_business_write_down_with_it()
    {
        // The rule's second clause, against the real pipeline: its catalogue entry said
        // Packet 9 implemented it here, and nothing here forced a failure (the fifth review
        // of Packet 9). A trigger refuses the audit insert for one host only, so the
        // command's MUST row fails at the commit boundary — and the mapping it describes must
        // not commit without it. The reconcile's standalone attempt carries the same key and
        // is refused the same way, which is the one state that reports on the health check.
        //
        // Atomicity is the proof because `xmin` cannot be — measured: EF Core wraps each
        // SaveChanges inside an open transaction in a savepoint, so the business rows carry
        // subtransaction ids while the audit row, raw SQL at the top level, carries the
        // parent's. Rows on one transaction show different `xmin`s.
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        (await Runner(dataSource).RunAsync(CancellationToken.None, [SeedData.English])).Should().Be(0);

        const string Host = "unauditable.example";

        await ExecuteAsOwnerAsync(
            $$"""
            CREATE FUNCTION refuse_probe_audit() RETURNS trigger LANGUAGE plpgsql AS $fn$
            BEGIN
                IF NEW.entity_id = '{{Host}}' THEN
                    RAISE EXCEPTION 'forced audit failure';
                END IF;
                RETURN NEW;
            END
            $fn$;
            CREATE TRIGGER refuse_probe_audit BEFORE INSERT ON audit_log
                FOR EACH ROW EXECUTE FUNCTION refuse_probe_audit();
            """);

        try
        {
            await using var provider = SeedComposition.Build(
                dataSource,
                new SeedTenantContext(SeedData.English.TenantId, SeedData.English.DefaultOrganization.OrganizationId),
                NullLoggerFactory.Instance);

            var act = async () =>
            {
                await using var scope = provider.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ISender>().Send(new MapHostToTenantCommand(Host));
            };

            (await act.Should().ThrowAsync<AuditWriteFailedException>()).Which.Error.Code
                .Should().Be("audit_unavailable", "the edge answers it 503");

            (await CountAsync("SELECT count(*) FROM platform_host_to_tenant WHERE host = @key", Host))
                .Should().Be(0, "a mapping whose MUST row could not be written did not happen");
            (await CountAsync("SELECT count(*) FROM audit_log WHERE entity_id = @key", Host))
                .Should().Be(0, "the premise: the trigger refused both attempts");

            provider.GetRequiredService<IAuditHealth>().IsHealthy.Should().BeFalse(
                "the standalone attempt failed too, and that is what the check reports");
        }
        finally
        {
            await ExecuteAsOwnerAsync(
                """
                DROP TRIGGER refuse_probe_audit ON audit_log;
                DROP FUNCTION refuse_probe_audit();
                """);
        }
    }

    [Fact]
    public async Task Audit_Classification_Does_Not_Read_The_Database_On_The_Request_Path()
    {
        // AuditLogBehavior classifies at step 3, before TransactionBehavior announces the tenant
        // at step 6 — and audit_config carries ENABLE + FORCE row level security. A
        // classification query there would return ZERO ROWS SILENTLY, which is
        // indistinguishable from "this tenant has no overrides", so no catch could ever fire
        // (ADR-0033 § Decision). The in-process catalogue is what answers, and this is the case
        // that can tell the difference: with SELECT revoked the read would THROW rather than
        // filter, so a request that still completes and still writes its rows is a request that
        // never made it.
        await ExecuteAsOwnerAsync("REVOKE SELECT ON audit_config FROM learnstack_app;");

        try
        {
            await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);

            var exitCode = await Runner(dataSource).RunAsync(CancellationToken.None, [SeedData.English]);

            exitCode.Should().Be(0,
                "classification reads the in-process catalogue, and the tenant override read is "
                + "the one failure ADR-0033 does not reject the operation for");

            var rows = await RowsAsync(SeedData.English.TenantId.Value);

            rows.Select(row => row.Operation).Should().Contain("tenancy.tenant.create",
                "the MUST rows are written at the classification the catalogue carries");
            rows.Should().OnlyContain(row => row.Outcome == "success");

            // The half the command alone cannot show. A MUST short-circuits before any read, so
            // a seed of MUST operations passes whether the classifier reads or not; a SHOULD is
            // the operation that reaches the loader. Under the same REVOKE its read FAILS — and
            // the declared tier still comes back, which is ADR-0033's one non-rejecting failure.
            var classifier = Classifier(dataSource);

            (await classifier.ClassifyAsync(
                    TenantId.From(SchemaFixture.TenantA),
                    Operation("tenancy.tenant.create", OperationClass.Should),
                    CancellationToken.None))
                .Should().Be(AuditClassification.Should,
                    "a tenant-override read failure falls back to the declared tier rather than "
                    + "rejecting the operation (ADR-0033 § Decision)");

            // And a MUST answers without asking at all: a data source that cannot connect is
            // proof the floor is decided in process.
            await using var unreachable = NpgsqlDataSource.Create(
                "Host=127.0.0.1;Port=1;Username=nobody;Database=nothing;Timeout=1");

            (await Classifier(unreachable).ClassifyAsync(
                    TenantId.From(SchemaFixture.TenantA),
                    Operation("tenancy.tenant.create", OperationClass.Must),
                    CancellationToken.None))
                .Should().Be(AuditClassification.Must,
                    "the floor is not overridable, so there is nothing to read for one");
        }
        finally
        {
            await ExecuteAsOwnerAsync("GRANT SELECT ON audit_config TO learnstack_app;");
        }
    }

    [Fact]
    public async Task The_row_carries_the_tenant_the_transaction_announced()
    {
        // The one value the row's own WITH CHECK accepts. A provisioning command runs
        // under an UNRESOLVED context by construction — the tenant does not exist yet — so
        // reading the context would give the all-zero tenant and the policy would refuse
        // the insert. Measured: it did, with 42501, before the behaviour read
        // IProvisionsTenant.ProvisioningTenantId instead (ADR-0044 § 2).
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);

        (await Runner(dataSource).RunAsync(CancellationToken.None, [SeedData.English])).Should().Be(0);

        var rows = await RowsAsync(SeedData.English.TenantId.Value);

        rows.Should().NotBeEmpty();
        rows.Should().OnlyContain(row => row.TenantId == SeedData.English.TenantId.Value);
        rows.Should().OnlyContain(row => row.Outcome == "success",
            "a clean seed refuses nothing");
    }

    [Fact]
    public async Task The_row_carries_the_snapshot_the_interceptor_captured()
    {
        // The whole seam, end to end: the interceptor captured the aggregate on
        // SaveChanges, the buffer held it across three flushes, and the store composed one
        // row from it on the business transaction. A snapshot that arrived empty here
        // would mean the interceptor never attached — which is exactly the failure that
        // reports success everywhere else.
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);

        (await Runner(dataSource).RunAsync(CancellationToken.None, [SeedData.English])).Should().Be(0);

        var tenantRow = (await RowsAsync(SeedData.English.TenantId.Value))
            .Single(row => row.Operation == "tenancy.tenant.create");

        tenantRow.EntityType.Should().Be("Tenant");
        tenantRow.EntityId.Should().Be(SeedData.English.TenantId.Value.ToString());

        // The EARLIEST capture's before state, and the tenant is created in this request —
        // so there is no prior state, and a non-null one would mean the merge walked past
        // the insert.
        tenantRow.BeforeState.Should().BeNull();

        tenantRow.AfterState.Should().NotBeNull();
        tenantRow.AfterState.Should().Contain(SeedData.English.Slug);

        // The LATEST capture's after state. ProvisionTenantCommand saves three times and
        // assigns the default organization on the third, so a merge that kept the first
        // capture would record a tenant that never had one.
        tenantRow.AfterState.Should().NotContain("\"DefaultOrganizationId\":null");
    }

    [Fact]
    public async Task The_host_mapping_row_carries_the_state_it_changed()
    {
        // The entity the first capture predicate would have missed. PlatformHostMapping is a
        // keyless projection row rather than an aggregate root, and a capture that only walked
        // roots wrote `tenancy.hostmapping.write` with no before, no after and no changes — a
        // row that records that something happened and not what.
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);

        (await Runner(dataSource).RunAsync(CancellationToken.None, [SeedData.English])).Should().Be(0);

        var mapping = (await RowsAsync(SeedData.English.TenantId.Value))
            .Single(row => row.Operation == "tenancy.hostmapping.write");

        mapping.EntityType.Should().Be("PlatformHostMapping");
        mapping.EntityId.Should().Be(SeedData.English.Host);

        mapping.BeforeState.Should().BeNull("the host is mapped for the first time here");
        mapping.AfterState.Should().NotBeNull().And.Contain(SeedData.English.Host,
            "the row says which host now points where");
        mapping.AfterState.Should().Contain("IsPubliclyLive",
            "and the flags that decide whether the host serves anything");

        // The tenant is the row's own column rather than a snapshot field: every row in
        // audit_log carries one, and repeating it inside the state would be a second place for
        // it to be wrong.
        mapping.TenantId.Should().Be(SeedData.English.TenantId.Value);
    }

    /// <summary>
    /// A refused second run leaves the first run's rows alone and puts its own refusal on
    /// the record, written standalone after the transaction went away.
    /// </summary>
    /// <remarks>
    /// The catalogue's canonical name. The fresh-instant half of the rule — the
    /// commit-in-doubt pair under one id, and the <c>23505</c> that is positive evidence
    /// rather than a failure — is asserted against the real table by
    /// <c>AuditStoreTests.The_indeterminate_pair_is_two_rows_under_one_id</c> and
    /// <c>A_duplicate_on_the_standalone_re_write_is_positive_evidence_and_is_swallowed</c>.
    /// </remarks>
    [Fact]
    public async Task Audit_Survives_Transaction_Rollback()
    {
        // The seed is idempotent, so the second run refuses before it writes — and a
        // refusal that produced a success row would be worse than no row at all.
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);

        (await Runner(dataSource).RunAsync(CancellationToken.None, [SeedData.English])).Should().Be(0);
        (await Runner(dataSource).RunAsync(CancellationToken.None, [SeedData.English])).Should().Be(0);

        var rows = await RowsAsync(SeedData.English.TenantId.Value);

        // The second run REFUSES, and every refusal is recorded. That is the point rather
        // than an inconvenience: a repeated provisioning attempt is exactly the shape a
        // probe takes, and Audit Coverage justifies the whole `denied` class with it.
        rows.Count(row => row.Outcome == "success").Should().Be(8,
            "the first run's rows are untouched");

        rows.Where(row => row.Outcome != "success").Should().NotBeEmpty(
            "the refused second run is on the record too");

        rows.Where(row => row.Outcome != "success")
            .Should().OnlyContain(row => row.Outcome == "failed" || row.Outcome == "denied");

        // INTENTS ARE PLURAL, and a refusal is where that is easiest to get wrong: the refused
        // provisioning declares two — the tenant and its default organization — and both are
        // reconciled, so the refusal is two rows rather than one (ADR-0033 Amendment 2 § 1).
        rows.Where(row => row.Outcome != "success").Select(row => row.Operation)
            .Should().Contain("tenancy.tenant.create")
            .And.Contain("tenancy.organization.create",
                "ProvisionTenantCommand declares two intents and the refusal reconciles both — a "
                + "singular reading would record the tenant's half and drop the organization's");

        // And the refused run wrote no business row: the counts are the first run's, exactly.
        (await ScalarAsync("SELECT count(*) FROM tenants WHERE id = @tenant",
            SeedData.English.TenantId.Value)).Should().Be(1L,
            "a refused run leaves the row the first run committed and adds none");
        (await ScalarAsync("SELECT count(*) FROM organizations WHERE tenant_id = @tenant",
            SeedData.English.TenantId.Value)).Should().Be(2L,
            "the seed's two organizations, and the refused run added neither");
    }

    /// <summary>Reads one number as <c>learnstack_platform</c>, which the rows outlive a context.</summary>
    private async Task<long> ScalarAsync(string sql, Guid tenant)
    {
        await using var connection = await PostgresFixture.OpenAsync(
            _schema.Postgres.PlatformConnectionString);
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)connection);
        command.Parameters.AddWithValue("tenant", tenant);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed record Row(
        Guid TenantId,
        string Operation,
        string Outcome,
        string? EntityType,
        string? EntityId,
        string? BeforeState,
        string? AfterState);

    private static SeedRunner Runner(NpgsqlDataSource dataSource) =>
        new(context => SeedComposition.Build(dataSource, context, NullLoggerFactory.Instance),
            NullLogger<SeedRunner>.Instance);

    /// <summary>
    /// Reads the rows as <c>learnstack_platform</c>.
    /// </summary>
    /// <remarks>
    /// The READ is the bypass, and only the read: every row asserted here was written by
    /// <c>learnstack_app</c> under the policy. A test that also wrote as the platform role
    /// would prove nothing about the insert, which is the half that matters.
    /// </remarks>
    private async Task<IReadOnlyList<Row>> RowsAsync(Guid tenantId)
    {
        await using var connection = await PostgresFixture.OpenAsync(
            _schema.Postgres.PlatformConnectionString);

        await using var command = new NpgsqlCommand(
            """
            SELECT tenant_id, operation, outcome, entity_type, entity_id,
                   before_state::text, after_state::text
            FROM audit_log
            WHERE tenant_id = @tenant
            ORDER BY operation
            """, (NpgsqlConnection)connection);
        command.Parameters.AddWithValue("tenant", tenantId);

        var rows = new List<Row>();

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            rows.Add(new Row(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return rows;
    }

    private async Task<long> CountAsync(string sql, object key)
    {
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.PlatformConnectionString);
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)connection);
        command.Parameters.AddWithValue("key", key);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>DDL on the shared schema, as its owner — the only role that may alter it.</summary>
    /// <summary>The real classifier, with the cache implementation the composition roots pick.</summary>
    private static AuditConfigService Classifier(NpgsqlDataSource dataSource)
    {
        var meterFactory = new ServiceCollection().AddMetrics()
            .BuildServiceProvider().GetRequiredService<IMeterFactory>();

        return new AuditConfigService(
            new InMemoryCacheService(new SystemClock(), meterFactory),
            new Lazy<NpgsqlDataSource>(() => dataSource),
            NullLogger<AuditConfigService>.Instance);
    }

    private static AuditCatalogEntry Operation(string operation, OperationClass operationClass) =>
        new("tenancy", operation, OperationType.Create, operationClass, typeof(object));

    private async Task ExecuteAsOwnerAsync(string sql)
    {
        await using var owner = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)owner);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Removes what a case seeded, so the shared fixture's counts do not move.</summary>
    /// <remarks>
    /// Three roles, and each is the only one that can do its part. <c>audit_log</c> and the
    /// tenancy tables go as <c>learnstack_platform</c> — the audit rows because it is the
    /// only role holding <c>DELETE</c> on that table, the tenancy rows because they belong
    /// to tenants with no context left to announce. The customization tables go as the
    /// OWNER with the tenant announced, because <c>learnstack_platform</c> holds only
    /// <c>SELECT</c> on them.
    /// </remarks>
    private async Task CleanUpAsync()
    {
        var ids = SeedData.All.Select(tenant => tenant.TenantId.Value).ToArray();

        await using (var platform = await PostgresFixture.OpenAsync(
            _schema.Postgres.PlatformConnectionString))
        {
            foreach (var statement in new[]
            {
                "DELETE FROM audit_log WHERE tenant_id = ANY(@ids)",
                "DELETE FROM platform_host_to_tenant WHERE tenant_id = ANY(@ids)",
                "UPDATE tenants SET default_organization_id = NULL WHERE id = ANY(@ids)",
                "DELETE FROM organizations WHERE tenant_id = ANY(@ids)",
                "DELETE FROM tenants WHERE id = ANY(@ids)",
            })
            {
                await using var cleanup = new NpgsqlCommand(statement, (NpgsqlConnection)platform);
                cleanup.Parameters.AddWithValue("ids", ids);
                await cleanup.ExecuteNonQueryAsync();
            }
        }

        await using var owner = await PostgresFixture.OpenAsync(
            _schema.Postgres.MigrationConnectionString);

        foreach (var tenant in SeedData.All)
        {
            await using var transaction = await owner.BeginTransactionAsync();
            await SchemaQueries.SetTenantAsync(owner, transaction, tenant.TenantId.Value);

            foreach (var statement in new[]
            {
                "DELETE FROM tenant_level_taxonomy_items WHERE tenant_id = @tenant",
                "DELETE FROM tenant_level_taxonomies WHERE tenant_id = @tenant",
                "DELETE FROM tenant_content_types WHERE tenant_id = @tenant",
                "DELETE FROM customization_generations WHERE tenant_id = @tenant",
                "DELETE FROM tenant_domains WHERE tenant_id = @tenant",
                "DELETE FROM tenant_locales WHERE tenant_id = @tenant",
            })
            {
                await SchemaQueries.ExecuteAsync(owner, transaction, statement,
                    ("tenant", tenant.TenantId.Value));
            }

            await transaction.CommitAsync();
        }
    }
}
