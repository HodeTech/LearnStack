using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Modules.Tenancy.Infrastructure.Jobs;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRecurrenceStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Conflicting_same_revision_binary_cannot_materialize_or_fan_out_the_stored_manifest(bool occurrenceAlreadyExists)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
            await SeedLifecycleTenantAsync(app, SchemaFixture.TenantA, SchemaFixture.OrgA1, "Active", false, true);
        var today = await TodayAsync(database);
        var stored = Fixture.Manifest(today.AddDays(-2), today.AddDays(-2));
        var conflicting = Fixture.Manifest(stored.Facts.ActivatesAt, stored.Facts.Anchor, target: "jobsrecurrencefixture.run.v2");
        conflicting.ManifestHash.Should().NotBe(stored.ManifestHash);
        await using var source = Source(database);
        var store = Store(source);
        await AssertRegistrationAsync(store, stored, BackgroundJobRecurrenceRegistrationResult.Registered);
        if (occurrenceAlreadyExists)
        {
            var materialized = await store.MaterializeRecurrenceAsync(Fixture.Recurrences(stored));
            materialized.ThrowIfFailed();
            materialized.Value.Should().NotBeNull();
            var page = await store.MaterializeRecipientPageAsync(Fixture.Recurrences(conflicting), BackgroundJobLifecycleProjectionDeclaration.Create());
            page.ThrowIfFailed();
            page.Value.Should().BeNull("a conflicting binary must not fan out the same revision's different frozen target");
        }
        else
        {
            var materialized = await store.MaterializeRecurrenceAsync(Fixture.Recurrences(conflicting));
            materialized.ThrowIfFailed();
            materialized.Value.Should().BeNull("a conflicting binary must not materialize the same revision's different frozen target");
        }
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        (await ReadAsync<long>(dispatcher, "SELECT count(*) FROM background_job_recipients")).Should().Be(0);
        if (occurrenceAlreadyExists)
            (await ReadAsync<bool>(dispatcher, """
                SELECT bounds_frozen_at IS NULL AND after_tenant_id IS NULL AND last_error='deployment_mismatch'
                    AND quarantined_at IS NULL FROM background_job_occurrences
                """)).Should().BeTrue();
        else
            (await ReadAsync<bool>(dispatcher, """
                SELECT next_scheduled_at=@next AND last_error='deployment_mismatch' AND quarantined_at IS NULL
                FROM background_job_recurrences
                """, ("next", stored.Facts.ActivatesAt))).Should().BeTrue();
    }

    [Theory]
    [InlineData(BackgroundJobRecipientPolicy.ServingTenants)]
    [InlineData(BackgroundJobRecipientPolicy.ProvisionedNonArchivedTenants)]
    public async Task Tenant_pages_bound_examined_keys_preserve_the_cohort_and_apply_the_declared_policy(BackgroundJobRecipientPolicy policy)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var eligible = new List<Guid>();
        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
            for (var index = 1; index <= 123; index++)
            {
                var tenant = RouteId(0x30000000 + index);
                var status = new[] { "Trial", "Active", "Suspended", "Archived" }[index % 4];
                var deleted = index % 7 == 0;
                var provisioned = index % 11 != 0;
                await SeedLifecycleTenantAsync(app, tenant, RouteId(0x40000000 + index), status, deleted, provisioned);
                if (!deleted && provisioned && (status is "Trial" or "Active"
                        || policy == BackgroundJobRecipientPolicy.ProvisionedNonArchivedTenants && status == "Suspended"))
                    eligible.Add(tenant);
            }
        await using var source = Source(database);
        var store = Store(source);
        var manifest = await PrepareOccurrenceAsync(database, store, policy);
        var registry = Fixture.Recurrences(manifest);
        var projection = BackgroundJobLifecycleProjectionDeclaration.Create();
        var first = await store.MaterializeRecipientPageAsync(registry, projection);
        first.ThrowIfFailed();
        first.Value!.ExaminedRoutes.Should().Be(100, "the bound counts raw routes, including ineligible and deleted tenants");
        first.Value.Completed.Should().BeFalse();
        first.CommitAcknowledged.Should().BeTrue();
        first.CleanupConfirmed.Should().BeTrue();

        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
            await SeedLifecycleTenantAsync(app, RouteId(0x50000000), RouteId(0x60000000), "Active", false, true);
        var second = await store.MaterializeRecipientPageAsync(registry, projection);
        second.ThrowIfFailed();
        second.Value!.ExaminedRoutes.Should().Be(23, "a route above the frozen upper key waits for the next occurrence");
        second.Value.Completed.Should().BeTrue();
        var duplicate = await store.MaterializeRecipientPageAsync(registry, projection);
        duplicate.ThrowIfFailed();
        duplicate.Value.Should().BeNull();

        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await using var command = new NpgsqlCommand("""
            SELECT tenant_id,organization_id,due_at,scheduled_at,id FROM background_job_recipients ORDER BY tenant_id
            """, (NpgsqlConnection)dispatcher) { CommandTimeout = 5 };
        var actual = new List<Guid>();
        await using (var rows = await command.ExecuteReaderAsync())
            while (await rows.ReadAsync())
            {
                actual.Add(rows.GetGuid(0));
                rows.IsDBNull(1).Should().BeTrue();
                rows.GetFieldValue<DateTimeOffset>(2).Should().BeOnOrAfter(rows.GetFieldValue<DateTimeOffset>(3));
                rows.GetFieldValue<DateTimeOffset>(2).Should().BeOnOrBefore(rows.GetFieldValue<DateTimeOffset>(3) + manifest.Facts.Jitter);
                rows.GetGuid(4).Version.Should().Be(7);
            }
        actual.Should().BeEquivalentTo(eligible);
        (first.Value.InsertedRecipients + second.Value.InsertedRecipients).Should().Be(eligible.Count);
        (await ReadAsync<bool>(dispatcher, """
            SELECT completed_at IS NOT NULL AND ownership_token IS NULL AND ownership_until IS NULL
                AND upper_tenant_id=@upper AND after_tenant_id=@upper FROM background_job_occurrences
            """, ("upper", RouteId(0x30000000 + 123)))).Should().BeTrue();
    }

    [Fact]
    public async Task Organization_pages_filter_only_after_raw_seek_and_recheck_the_parent_lifecycle()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
        {
            await CompleteTenantAsync(app, SchemaFixture.TenantA, SchemaFixture.OrgA1, "Active");
            await CompleteTenantAsync(app, SchemaFixture.TenantB, SchemaFixture.OrgB1, "Suspended");
        }
        var eligible = new List<Guid> { SchemaFixture.OrgA1, SchemaFixture.OrgA2 };
        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
        {
            await using var transaction = await app.BeginTransactionAsync();
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            for (var index = 1; index <= 126; index++)
            {
                var organization = RouteId(0x40000000 + index);
                var deleted = index % 7 == 0;
                var status = index % 5 == 0 ? "Suspended" : "Active";
                await SchemaQueries.ExecuteAsync(app, transaction, """
                    INSERT INTO organizations (id,tenant_id,slug,display_name,status,created_at,created_by,row_version,deleted_at,deleted_by)
                    VALUES (@id,@tenant,@slug,'Lifecycle fixture',@status,clock_timestamp(),@actor,0,
                        CASE WHEN @deleted THEN clock_timestamp() ELSE NULL END,CASE WHEN @deleted THEN @actor ELSE NULL END)
                    """, ("id", organization), ("tenant", SchemaFixture.TenantA), ("slug", "org" + index),
                    ("status", status), ("actor", SchemaFixture.Actor), ("deleted", deleted));
                if (!deleted && status == "Active") eligible.Add(organization);
            }
            await transaction.CommitAsync();
        }
        await using var source = Source(database);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Definitions(), new() { FanoutPageSize = 37 }, new SystemGuidFactory());
        var manifest = await PrepareOccurrenceAsync(database, store, BackgroundJobRecipientPolicy.ServingOrganizations);
        var projection = BackgroundJobLifecycleProjectionDeclaration.Create();
        var examined = 0;
        var inserted = 0;
        var completed = false;
        for (var page = 0; page < 8 && !completed; page++)
        {
            var result = await store.MaterializeRecipientPageAsync(Fixture.Recurrences(manifest), projection);
            result.ThrowIfFailed();
            result.Value.Should().NotBeNull();
            result.Value!.ExaminedRoutes.Should().BeInRange(1, 37);
            examined += result.Value.ExaminedRoutes;
            inserted += result.Value.InsertedRecipients;
            completed = result.Value.Completed;
        }
        completed.Should().BeTrue();
        examined.Should().Be(129);
        inserted.Should().Be(eligible.Count);
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        var actual = await SchemaQueries.ReadStringsAsync(dispatcher,
            "SELECT organization_id::text FROM background_job_recipients");
        actual.Should().BeEquivalentTo(eligible.Select(id => id.ToString("D")));
        (await ReadAsync<long>(dispatcher, "SELECT count(*) FROM background_job_recipients WHERE tenant_id=@tenant",
            ("tenant", SchemaFixture.TenantB))).Should().Be(0, "an Active organization under a suspended tenant is not serving");
    }

    [Fact]
    public async Task Recipient_insert_fault_rolls_back_bounds_claim_and_checkpoint_before_a_clean_page_retry()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
            await SeedLifecycleTenantAsync(app, SchemaFixture.TenantA, SchemaFixture.OrgA1, "Active", false, true);
        await using var source = Source(database);
        var store = Store(source);
        var manifest = await PrepareOccurrenceAsync(database, store, BackgroundJobRecipientPolicy.ServingTenants);
        await using var migration = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await SchemaQueries.ExecuteAsync(migration, null, """
            REVOKE INSERT (id,occurrence_id,tenant_id,organization_id,recurrence_key,revision,manifest_hash,
                target_definition_key,recipient_policy,scheduled_at,due_at) ON background_job_recipients FROM learnstack_outbox_admin
            """);
        try
        {
            var failed = await store.MaterializeRecipientPageAsync(Fixture.Recurrences(manifest), BackgroundJobLifecycleProjectionDeclaration.Create());
            var failure = () => failed.ThrowIfFailed();
            failure.Should().Throw<PostgresException>().Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
            failed.KnownRollback.Should().BeTrue();
            await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
            (await ReadAsync<bool>(dispatcher, """
                SELECT bounds_frozen_at IS NULL AND upper_tenant_id IS NULL AND after_tenant_id IS NULL
                    AND ownership_token IS NULL AND ownership_until IS NULL AND completed_at IS NULL FROM background_job_occurrences
                """)).Should().BeTrue();
            (await ReadAsync<long>(dispatcher, "SELECT count(*) FROM background_job_recipients")).Should().Be(0);
        }
        finally
        {
            await SchemaQueries.ExecuteAsync(migration, null, """
                GRANT INSERT (id,occurrence_id,tenant_id,organization_id,recurrence_key,revision,manifest_hash,
                    target_definition_key,recipient_policy,scheduled_at,due_at) ON background_job_recipients TO learnstack_outbox_admin
                """);
        }
        var retry = await store.MaterializeRecipientPageAsync(Fixture.Recurrences(manifest), BackgroundJobLifecycleProjectionDeclaration.Create());
        retry.ThrowIfFailed();
        retry.Value!.ExaminedRoutes.Should().Be(1);
        retry.Value.InsertedRecipients.Should().Be(1);
        retry.Value.Completed.Should().BeTrue();
    }

    [Fact]
    public async Task Empty_inventory_completes_a_frozen_empty_cohort_without_fabricating_a_recipient()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = Source(database);
        var store = Store(source);
        var manifest = await PrepareOccurrenceAsync(database, store, BackgroundJobRecipientPolicy.ServingTenants);
        var result = await store.MaterializeRecipientPageAsync(Fixture.Recurrences(manifest), BackgroundJobLifecycleProjectionDeclaration.Create());
        result.ThrowIfFailed();
        result.Value.Should().BeEquivalentTo(new { ExaminedRoutes = 0, InsertedRecipients = 0, Completed = true });
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        (await ReadAsync<bool>(dispatcher, """
            SELECT bounds_frozen_at IS NOT NULL AND upper_tenant_id IS NULL AND after_tenant_id IS NULL
                AND completed_at IS NOT NULL AND ownership_token IS NULL FROM background_job_occurrences
            """)).Should().BeTrue();
    }

    private static async Task<BackgroundJobRecurrenceDescriptor> PrepareOccurrenceAsync(DisposableSchemaDatabase database,
        PostgresBackgroundJobControlStore store, BackgroundJobRecipientPolicy policy)
    {
        var today = await TodayAsync(database);
        var manifest = Fixture.Manifest(today.AddDays(-2), today.AddDays(-2), policy: policy,
            target: policy == BackgroundJobRecipientPolicy.ServingOrganizations ? "jobsrecurrencefixture.organization.v1" : "jobsrecurrencefixture.run.v1",
            jitter: TimeSpan.FromMinutes(17));
        await AssertRegistrationAsync(store, manifest, BackgroundJobRecurrenceRegistrationResult.Registered);
        var occurrence = await store.MaterializeRecurrenceAsync(Fixture.Recurrences(manifest));
        occurrence.ThrowIfFailed();
        occurrence.Value.Should().NotBeNull();
        return manifest;
    }

    private static Guid RouteId(int value) => Guid.Parse(value.ToString("x8", System.Globalization.CultureInfo.InvariantCulture)
        + "-0000-7000-8000-000000000001");

    private static async Task SeedLifecycleTenantAsync(System.Data.Common.DbConnection app, Guid tenant, Guid organization,
        string status, bool deleted, bool provisioned)
    {
        await using var transaction = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, transaction, tenant, null);
        await SchemaQueries.ExecuteAsync(app, transaction, """
            INSERT INTO tenants (id,slug,display_name,status,created_at,created_by,row_version,deleted_at,deleted_by)
            VALUES (@tenant,@slug,'Lifecycle fixture',@status,clock_timestamp(),@actor,0,
                CASE WHEN @deleted THEN clock_timestamp() ELSE NULL END,CASE WHEN @deleted THEN @actor ELSE NULL END);
            INSERT INTO organizations (id,tenant_id,slug,display_name,status,created_at,created_by,row_version)
            VALUES (@organization,@tenant,'default','Lifecycle fixture','Active',clock_timestamp(),@actor,0);
            UPDATE tenants SET default_organization_id=CASE WHEN @provisioned THEN @organization ELSE NULL END WHERE id=@tenant;
            """, ("tenant", tenant), ("organization", organization), ("slug", "tenant" + tenant.ToString("N")),
            ("status", status), ("actor", SchemaFixture.Actor), ("deleted", deleted), ("provisioned", provisioned));
        await transaction.CommitAsync();
    }

    private static async Task CompleteTenantAsync(System.Data.Common.DbConnection app, Guid tenant, Guid organization, string status)
    {
        await using var transaction = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, transaction, tenant, null);
        await TenancyLifecycleFixture.LockAsync(app, transaction, tenant);
        await SchemaQueries.ExecuteAsync(app, transaction,
            "UPDATE tenants SET default_organization_id=@organization,status=@status WHERE id=@tenant",
            ("organization", organization), ("status", status), ("tenant", tenant));
        await transaction.CommitAsync();
    }
}
