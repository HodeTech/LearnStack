using System.Text.Json;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobControlTests
{
    private static readonly string[] ControlBacklogKeys = ["fixture.backlog.v1"];
    [Theory]
    [InlineData("recovery", "ix_background_jobs_recovery", "ownership_until")]
    [InlineData("exhaustion", "ix_background_jobs_exhausted", "available_after")]
    [InlineData("retirement", "ix_background_jobs_pending", "available_after")]
    [InlineData("terminal", "ix_background_jobs_terminal", "available_after")]
    public async Task Actual_control_queries_seek_future_backlog_for_idle_and_partial_quanta_and_detect_volatile_cutoff(
        string lane, string index, string clock)
    {
        await using var database = await DatabaseAsync();
        await SeedControlFutureAsync(database, lane);
        await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        var sql = lane switch
        {
            "recovery" => PostgresBackgroundJobControlStore.SelectionSql(BackgroundJobControlLane.Recovery),
            "exhaustion" => PostgresBackgroundJobControlStore.SelectionSql(BackgroundJobControlLane.Exhaustion),
            "retirement" => PostgresBackgroundJobControlStore.SelectionSql(BackgroundJobControlLane.Retirement),
            _ => PostgresBackgroundJobTerminalStore.ClaimSelectionSql,
        };
        foreach (var due in new[] { 0, 1 })
        {
            if (due != 0)
                await SchemaQueries.ExecuteAsync(dispatcher, null, $"""
                    WITH due AS (SELECT id FROM background_jobs ORDER BY id LIMIT 1)
                    UPDATE background_jobs j SET {clock}=clock_timestamp()-interval '1 minute'
                    FROM due WHERE j.id=due.id
                    """);
            await SchemaQueries.ExecuteAsync(owner, null, "ANALYZE background_jobs");
            var clean = await ExplainControlAsync((NpgsqlConnection)dispatcher, sql);
            var nodes = PlanNodes(clean).ToArray();
            nodes.Where(node => node.TryGetProperty("Index Name", out _)).Select(node => node.GetProperty("Index Name").GetString())
                .Should().Contain(index, "the actual runtime query must seek its matching partial index with the normal planner");
            nodes.Where(node => node.TryGetProperty("Index Cond", out _)).Select(node => node.GetProperty("Index Cond").GetString())
                .Should().Contain(condition => condition!.Contains(clock + " <= statement_timestamp()", StringComparison.Ordinal));
            Removed(nodes).Should().BeLessThan(100, "an empty or short due page must stop before six thousand future rows");
            if (lane != "terminal" || due == 0)
            {
                var dirty = await ExplainControlAsync((NpgsqlConnection)dispatcher,
                    sql.Replace("statement_timestamp()", "clock_timestamp()", StringComparison.Ordinal));
                Removed(PlanNodes(dirty)).Should().BeGreaterThanOrEqualTo(6000,
                    "the same actual-query detector must expose a future-row scan when its cutoff becomes volatile");
            }
        }
    }

    private static async Task SeedControlFutureAsync(DisposableSchemaDatabase database, string lane)
    {
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            await SchemaQueries.ExecuteAsync(app, transaction, """
                INSERT INTO background_jobs (id, tenant_id, definition_key, profile, queue, parameters, original_due_at, traceparent)
                SELECT uuidv7(), @tenant, 'fixture.backlog.v1', 'transactional', 'transactional', '{}',
                    clock_timestamp()-interval '1 minute', @trace FROM generate_series(1,6001)
                """, ("tenant", SchemaFixture.TenantA), ("trace", BackgroundJobSchemaSeed.Traceparent));
            await transaction.CommitAsync();
        }
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        if (lane == "exhaustion")
        {
            // Populate actual six-start histories through the existing app grants
            // and transition trigger; fixture setup does not disable a guard.
            for (var ordinal = 1; ordinal <= 6; ordinal++)
            {
                await SchemaQueries.ExecuteAsync(dispatcher, null, """
                    UPDATE background_jobs SET ownership_token=uuidv7(), ownership_kind='routing',
                        ownership_until=clock_timestamp()+interval '4 minutes'
                    """);
                await using (var transaction = await app.BeginTransactionAsync())
                {
                    await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
                    await SchemaQueries.ExecuteAsync(app, transaction, """
                        INSERT INTO background_job_attempts (id, tenant_id, job_id, stage, ordinal, admission_token, deadline)
                        SELECT uuidv7(), tenant_id, id, 'normal', @ordinal, ownership_token,
                            clock_timestamp()+interval '4 minutes' FROM background_jobs;
                        UPDATE background_jobs j SET normal_attempts=@ordinal, current_attempt_id=a.id,
                            attempt_deadline=a.deadline, phase='executing'
                        FROM background_job_attempts a WHERE a.job_id=j.id AND a.tenant_id=j.tenant_id AND a.ordinal=@ordinal
                        """, ("ordinal", ordinal));
                    await transaction.CommitAsync();
                }
                await SchemaQueries.ExecuteAsync(dispatcher, null, """
                    UPDATE background_jobs SET ownership_until=clock_timestamp()-interval '1 microsecond';
                    UPDATE background_jobs SET phase='reconciling', ownership_token=uuidv7(), ownership_kind='recovery',
                        ownership_until=clock_timestamp()+interval '4 minutes';
                    UPDATE background_jobs SET phase='pending', available_after=clock_timestamp(),
                        ownership_token=NULL, ownership_kind=NULL, ownership_until=NULL
                    """);
            }
        }
        var assignment = lane switch
        {
            "recovery" => "ownership_token=uuidv7(), ownership_kind='routing', ownership_until=clock_timestamp()+interval '9 minutes'",
            "terminal" => "phase='terminal_pending', terminal_reason='retired_definition', available_after=clock_timestamp()+interval '1 day'",
            _ => "available_after=clock_timestamp()+interval '1 day'",
        };
        await SchemaQueries.ExecuteAsync(dispatcher, null, "UPDATE background_jobs SET " + assignment);
    }

    private static async Task<string> ExplainControlAsync(NpgsqlConnection dispatcher, string sql)
    {
        await using var transaction = await dispatcher.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + sql, dispatcher, transaction);
        command.Parameters.AddWithValue("blocked", Array.Empty<Guid>());
        command.Parameters.AddWithValue("compatible", ControlBacklogKeys);
        command.Parameters.AddWithValue("retired", ControlBacklogKeys);
        command.Parameters.AddWithValue("batch", 100);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static JsonElement[] PlanNodes(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Descendants(document.RootElement[0].GetProperty("Plan")).Select(node => node.Clone()).ToArray();
    }

    private static IEnumerable<JsonElement> Descendants(JsonElement node)
    {
        yield return node;
        if (node.TryGetProperty("Plans", out var children))
            foreach (var child in children.EnumerateArray())
                foreach (var descendant in Descendants(child)) yield return descendant;
    }

    private static long Removed(IEnumerable<JsonElement> nodes) => nodes.Where(node => node.TryGetProperty("Rows Removed by Filter", out _))
        .Select(node => node.GetProperty("Rows Removed by Filter").GetInt64()).DefaultIfEmpty().Max();
}
