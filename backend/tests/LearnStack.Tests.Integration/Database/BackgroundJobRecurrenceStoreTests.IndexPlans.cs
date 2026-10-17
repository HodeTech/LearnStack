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
