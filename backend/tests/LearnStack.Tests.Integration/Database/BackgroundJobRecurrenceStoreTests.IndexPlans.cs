using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRecurrenceStoreTests
{
    [Fact]
    public async Task Actual_control_quarantine_observation_seeks_each_partial_index_and_detects_each_missing_index()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
            await SeedLifecycleTenantAsync(app, SchemaFixture.TenantA, SchemaFixture.OrgA1, "Active", false, true);
        var today = await TodayAsync(database);
        var manifest = Fixture.Manifest(today.AddDays(-10), today.AddDays(-10), period: TimeSpan.FromMinutes(1));
        await using var source = Source(database);
        await AssertRegistrationAsync(Store(source), manifest, BackgroundJobRecurrenceRegistrationResult.Registered);
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await using (var transaction = await dispatcher.BeginTransactionAsync())
        {
            // Legal confined-role state populates every queried control table.
            // No quarantine or positive earlier OR arm can hide a backlog scan.
            await SchemaQueries.ExecuteAsync(dispatcher, transaction, """
                INSERT INTO background_job_recurrences (recurrence_key,revision,manifest_hash,target_definition_key,
                    schedule,activates_at,anchor_at,period_microseconds,jitter_microseconds,recipient_policy,next_scheduled_at)
                SELECT 'jobsrecurrencefixture.backlog_'||n,1,manifest_hash,target_definition_key,
                    schedule,activates_at,anchor_at,period_microseconds,jitter_microseconds,recipient_policy,next_scheduled_at
                FROM background_job_recurrences CROSS JOIN generate_series(1,3000) n WHERE recurrence_key=@key;
                INSERT INTO background_job_occurrences (id,recurrence_key,revision,manifest_hash,target_definition_key,
                    recipient_policy,scheduled_at,jitter_microseconds,skipped_slots)
                SELECT uuidv7(),@key,1,@hash,@target,'serving_tenants',@anchor+n*interval '1 minute',0,0
                FROM generate_series(1,3000) n;
                UPDATE background_job_occurrences SET ownership_token=uuidv7(),ownership_until=clock_timestamp()+interval '9 minutes';
                UPDATE background_job_occurrences SET bounds_frozen_at=clock_timestamp(),upper_tenant_id=@tenant;
                INSERT INTO background_job_recipients (id,occurrence_id,tenant_id,organization_id,recurrence_key,revision,
                    manifest_hash,target_definition_key,recipient_policy,scheduled_at,due_at)
                SELECT uuidv7(),id,@tenant,NULL,recurrence_key,revision,manifest_hash,target_definition_key,
                    recipient_policy,scheduled_at,scheduled_at FROM background_job_occurrences;
                """, ("key", manifest.Facts.Key), ("hash", manifest.ManifestHash), ("target", manifest.Facts.Target),
                ("anchor", manifest.Facts.Anchor), ("tenant", SchemaFixture.TenantA));
            await transaction.CommitAsync();
        }
        await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await SchemaQueries.ExecuteAsync(owner, null,
            "ANALYZE background_job_recurrences; ANALYZE background_job_occurrences; ANALYZE background_job_recipients");
        var indexes = new[]
        {
            (Name: "ix_background_job_recurrences_quarantined", Table: "background_job_recurrences", Columns: "recurrence_key,revision"),
            (Name: "ix_background_job_occurrences_quarantined", Table: "background_job_occurrences", Columns: "id"),
            (Name: "ix_background_job_recipients_quarantined", Table: "background_job_recipients", Columns: "id"),
        };
        using (var clean = await ExplainQuarantineAsync()) AssertBoundedControlQuarantinePlan(clean);
        foreach (var index in indexes)
        {
            await SchemaQueries.ExecuteAsync(owner, null, "DROP INDEX " + index.Name);
            try
            {
                using var dirty = await ExplainQuarantineAsync();
                Action sameDetector = () => AssertBoundedControlQuarantinePlan(dirty);
                sameDetector.Should().Throw<Xunit.Sdk.XunitException>();
                var examined = LifecyclePlanNodes(dirty.RootElement[0].GetProperty("Plan"))
                    .Where(node => node.TryGetProperty("Relation Name", out var relation) && relation.GetString() == index.Table)
                    .Sum(ExaminedQuarantineRows);
                examined.Should().BeGreaterThanOrEqualTo(3000,
                    "the exact empty-observation query must scan this nonempty history when its partial index is removed");
            }
            finally
            {
                await SchemaQueries.ExecuteAsync(owner, null, "CREATE INDEX " + index.Name + " ON " + index.Table
                    + "(" + index.Columns + ") WHERE quarantined_at IS NOT NULL");
            }
            using var restored = await ExplainQuarantineAsync();
            AssertBoundedControlQuarantinePlan(restored);
        }

        async Task<JsonDocument> ExplainQuarantineAsync()
        {
            await using var command = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) "
                + PostgresBackgroundJobControlStore.QuarantinePresenceSql, (NpgsqlConnection)dispatcher)
            { CommandTimeout = 5 };
            return JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!);
        }
    }

    private static void AssertBoundedControlQuarantinePlan(JsonDocument plan)
    {
        var nodes = LifecyclePlanNodes(plan.RootElement[0].GetProperty("Plan")).ToArray();
        nodes.Where(node => node.TryGetProperty("Index Name", out _))
            .Select(node => node.GetProperty("Index Name").GetString()).Should().Contain(
                "ix_background_job_recurrences_quarantined", "ix_background_job_occurrences_quarantined", "ix_background_job_recipients_quarantined");
        nodes.Where(node => node.TryGetProperty("Relation Name", out var relation)
                && relation.GetString()!.StartsWith("background_job_", StringComparison.Ordinal))
            .Sum(ExaminedQuarantineRows).Should().BeLessThanOrEqualTo(16);
    }

    private static double ExaminedQuarantineRows(JsonElement node) =>
        (node.GetProperty("Actual Rows").GetDouble()
            + (node.TryGetProperty("Rows Removed by Filter", out var removed) ? removed.GetDouble() : 0))
        * node.GetProperty("Actual Loops").GetDouble();

    [Fact]
    public async Task Recipient_claim_uses_its_pending_index_without_sorting_a_zero_jitter_backlog()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
            await SeedLifecycleTenantAsync(app, SchemaFixture.TenantA, SchemaFixture.OrgA1, "Active", false, true);
        var today = await TodayAsync(database);
        var manifest = Fixture.Manifest(today.AddDays(-10), today.AddDays(-10), period: TimeSpan.FromMinutes(1));
        await using var source = Source(database);
        await AssertRegistrationAsync(Store(source), manifest, BackgroundJobRecurrenceRegistrationResult.Registered);
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await using (var transaction = await dispatcher.BeginTransactionAsync())
        {
            await SchemaQueries.ExecuteAsync(dispatcher, transaction, """
                INSERT INTO background_job_occurrences (id,recurrence_key,revision,manifest_hash,target_definition_key,
                    recipient_policy,scheduled_at,jitter_microseconds,skipped_slots)
                SELECT uuidv7(),@key,1,@hash,@target,'serving_tenants',@anchor+n*interval '1 minute',0,0
                FROM generate_series(1,3000) n;
                UPDATE background_job_occurrences SET ownership_token=uuidv7(),ownership_until=clock_timestamp()+interval '9 minutes';
                UPDATE background_job_occurrences SET bounds_frozen_at=clock_timestamp(),upper_tenant_id=@tenant;
                INSERT INTO background_job_recipients (id,occurrence_id,tenant_id,organization_id,recurrence_key,revision,
                    manifest_hash,target_definition_key,recipient_policy,scheduled_at,due_at)
                SELECT uuidv7(),id,@tenant,NULL,recurrence_key,revision,manifest_hash,target_definition_key,
                    recipient_policy,scheduled_at,scheduled_at FROM background_job_occurrences;
                UPDATE background_job_recipients SET available_after=@available;
                """, ("key", manifest.Facts.Key), ("hash", manifest.ManifestHash), ("target", manifest.Facts.Target),
                ("anchor", manifest.Facts.Anchor), ("tenant", SchemaFixture.TenantA), ("available", today));
            await transaction.CommitAsync();
        }
        await using (var migration = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
            await SchemaQueries.ExecuteAsync(migration, null, "ANALYZE background_job_recipients; ANALYZE background_job_recurrences");
        (await ReadAsync<long>(dispatcher, "SELECT count(*) FROM background_job_recipients")).Should().Be(3000);
        var actualSql = (string)typeof(PostgresBackgroundJobControlStore)
            .GetField("RecipientClaimSelectionSql", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
        using var clean = await ExplainRecipientAsync(actualSql);
        AssertBoundedRecipientPlan(clean);
        var retained = await ReadAsync<Guid[]>(dispatcher,
            "SELECT array_agg(id) FROM (SELECT id FROM background_job_recipients ORDER BY available_after,id LIMIT 4) oldest");
        using (var retainedPlan = await ExplainRecipientAsync(actualSql, retained))
            AssertBoundedRecipientPlan(retainedPlan);
        var obsolete = actualSql.Replace("ORDER BY r.available_after,r.id", "ORDER BY r.available_after,r.scheduled_at,r.id", StringComparison.Ordinal);
        obsolete.Should().NotBe(actualSql, "the dirty control must change the actual production query");
        using var dirty = await ExplainRecipientAsync(obsolete);
        var sameBudget = () => AssertBoundedRecipientPlan(dirty);
        sameBudget.Should().Throw<Xunit.Sdk.XunitException>("the old ordering must fail the same bounded-plan assertion");
        var dirtyRows = LifecyclePlanNodes(dirty.RootElement[0].GetProperty("Plan"))
            .Where(node => node.TryGetProperty("Relation Name", out var relation) && relation.GetString() == "background_job_recipients")
            .Sum(node => node.GetProperty("Actual Rows").GetDouble() * node.GetProperty("Actual Loops").GetDouble());
        dirtyRows.Should().BeGreaterThanOrEqualTo(3000, "the obsolete ordering consumes the equal-availability backlog before LIMIT");

        async Task<JsonDocument> ExplainRecipientAsync(string sql, Guid[]? excluded = null)
        {
            await using var command = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + sql, (NpgsqlConnection)dispatcher)
            { CommandTimeout = 5 };
            command.Parameters.AddWithValue("retained", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid, excluded ?? []);
            return JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!);
        }
    }

    private static void AssertBoundedRecipientPlan(JsonDocument plan)
    {
        var nodes = LifecyclePlanNodes(plan.RootElement[0].GetProperty("Plan")).ToArray();
        nodes.Where(node => node.TryGetProperty("Index Name", out var index)
            && index.GetString() == "ix_background_job_recipients_pending").Should().ContainSingle();
        nodes.Select(node => node.GetProperty("Node Type").GetString()).Should()
            .NotContain(kind => kind!.Contains("Sort", StringComparison.Ordinal));
        nodes.Where(node => node.TryGetProperty("Relation Name", out var relation) && relation.GetString() == "background_job_recipients")
            .Sum(node => node.GetProperty("Actual Rows").GetDouble() * node.GetProperty("Actual Loops").GetDouble())
            .Should().BeLessThanOrEqualTo(8, "the production LIMIT 1 must not consume the same-availability backlog");
        nodes[0].GetProperty("Actual Rows").GetDouble().Should().Be(1);
    }

    [Fact]
    public async Task Actual_lifecycle_page_seeks_past_a_large_ineligible_prefix_without_rescanning_it()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
        {
            await using var transaction = await app.BeginTransactionAsync();
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            // Setup still obeys app-role RLS: evaluating each tuple's ID sets
            // that same tenant's GUC before WITH CHECK examines the tuple. No
            // owner/bypass privilege or disabled policy seeds the tenant rows.
            await using var seed = new NpgsqlCommand("""
                INSERT INTO tenants (id,slug,display_name,status,created_at,created_by,row_version)
                SELECT set_config('app.tenant_id',target.id::text,true)::uuid,'fixture'||target.ordinal,
                    'Lifecycle fixture','Archived',clock_timestamp(),@actor,0
                FROM (SELECT uuidv7() AS id,ordinal FROM generate_series(1,12000) AS ordinal) target
                """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction) { CommandTimeout = 10 };
            seed.Parameters.AddWithValue("actor", SchemaFixture.Actor);
            (await seed.ExecuteNonQueryAsync()).Should().Be(12000);
            await transaction.CommitAsync();
        }
        await using (var migration = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
            await SchemaQueries.ExecuteAsync(migration, null, "ANALYZE tenants");
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        var after = await ReadAsync<Guid>(dispatcher, "SELECT id FROM tenants ORDER BY id OFFSET 5999 LIMIT 1");
        var upper = await ReadAsync<Guid>(dispatcher, "SELECT id FROM tenants ORDER BY id DESC LIMIT 1");
        var actualSql = (string)typeof(PostgresBackgroundJobControlStore)
            .GetField("TenantLifecyclePageSql", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
        await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + actualSql,
            (NpgsqlConnection)dispatcher)
        { CommandTimeout = 5 };
        explain.Parameters.AddWithValue("afterTenant", after);
        explain.Parameters.AddWithValue("upperTenant", upper);
        explain.Parameters.AddWithValue("limit", 100);
        using var plan = JsonDocument.Parse((string)(await explain.ExecuteScalarAsync())!);
        var nodes = LifecyclePlanNodes(plan.RootElement[0].GetProperty("Plan")).ToArray();
        nodes.Where(node => node.TryGetProperty("Relation Name", out var relation) && relation.GetString() == "tenants")
            .Should().ContainSingle().Which.GetProperty("Node Type").GetString().Should().Be("Index Scan");
        nodes.Where(node => node.TryGetProperty("Index Cond", out var condition)
            && condition.GetString()!.Contains("id >", StringComparison.Ordinal)
            && condition.GetString()!.Contains("id <=", StringComparison.Ordinal)).Should().NotBeEmpty();
        var visits = nodes.Sum(node =>
            (node.GetProperty("Actual Rows").GetDouble()
                + (node.TryGetProperty("Rows Removed by Filter", out var removed) ? removed.GetDouble() : 0))
            * node.GetProperty("Actual Loops").GetDouble());
        visits.Should().BeLessThanOrEqualTo(256, "a late raw page must not rescan the 6,000-row ineligible prefix");
        nodes[0].GetProperty("Actual Rows").GetDouble().Should().Be(100);
    }

    private static IEnumerable<JsonElement> LifecyclePlanNodes(JsonElement node)
    {
        yield return node;
        if (!node.TryGetProperty("Plans", out var children)) yield break;
        foreach (var child in children.EnumerateArray())
            foreach (var descendant in LifecyclePlanNodes(child)) yield return descendant;
    }
}
