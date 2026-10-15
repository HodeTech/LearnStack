using System.Text.Json;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobStorageMigrationTests
{
    private sealed record Eligibility(string Index, string Predicate, string Order);
    private static readonly Eligibility[] EligibilityQueries =
    [
        new("ix_background_jobs_pending",
            "phase = 'pending' AND processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NULL "
            + "AND normal_attempts < 6 AND ownership_token IS NULL AND available_after <= statement_timestamp()",
            "available_after, created_at, id"),
        new("ix_background_jobs_recovery",
            "ownership_token IS NOT NULL AND processed_at IS NULL AND dead_lettered_at IS NULL "
            + "AND terminal_reason IS NULL AND phase <> 'quarantined' AND ownership_until <= statement_timestamp()",
            "ownership_until, created_at, id"),
        new("ix_background_jobs_terminal",
            "phase = 'terminal_pending' AND terminal_reason IS NOT NULL AND processed_at IS NULL "
            + "AND dead_lettered_at IS NULL AND available_after <= statement_timestamp()",
            "available_after, created_at, id"),
        new("ix_background_jobs_bridge",
            "phase IN ('pending','finalize_pending') AND processed_at IS NULL AND dead_lettered_at IS NULL "
            + "AND terminal_reason IS NULL AND ownership_token IS NULL AND handoff_check_at <= statement_timestamp()",
            "handoff_check_at, created_at, id"),
    ];

    [Fact]
    public async Task Actual_eligibility_plans_seek_partial_indexes_through_completed_and_future_backlog_and_detect_removal()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await SeedIndexBacklogAsync(database);
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        (await SchemaQueries.CountAsync(dispatcher, "SELECT count(*) FROM background_jobs WHERE processed_at IS NOT NULL"))
            .Should().Be(3000L, "completed history must dominate useful candidates");
        (await SchemaQueries.CountAsync(dispatcher,
            "SELECT count(*) FROM background_jobs WHERE available_after > clock_timestamp()"))
            .Should().Be(6000L, "future timers must dominate today's eligible work");
        await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await SchemaQueries.ExecuteAsync(owner, null, "ANALYZE background_jobs");
        foreach (var query in EligibilityQueries)
        {
            var sql = $"SELECT id FROM background_jobs WHERE {query.Predicate} ORDER BY {query.Order} LIMIT 10 FOR UPDATE SKIP LOCKED";
            var clean = await ExplainAsync(dispatcher, sql);
            Indexes(clean).Should().Contain(query.Index,
                $"the ordinary planner must seek {query.Index} for its actual ordered locking query");
            IndexConditions(clean).Should().Contain(condition =>
                condition.Contains(query.Order.Split(',')[0] + " <= statement_timestamp()", StringComparison.Ordinal),
                "using the index for ordering alone does not bound future-row scans");
            MaximumRowsRemoved(clean).Should().BeLessThan(100L,
                "a small due quantum must not filter thousands of completed or future rows");
            (await SchemaQueries.CountAsync(dispatcher,
                $"SELECT count(*) FROM background_jobs WHERE {query.Predicate}")).Should().BeGreaterThan(0L,
                "an empty candidate set cannot prove that the query finds eligible work");
            var indexDefinition = (await SchemaQueries.ReadStringsAsync(owner,
                $"SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' AND indexname = '{query.Index}'")).Single();
            await SchemaQueries.ExecuteAsync(owner, null, $"DROP INDEX {query.Index}");
            var dirty = await ExplainAsync(dispatcher, sql);
            Indexes(dirty).Should().NotContain(query.Index);
            MaximumRowsRemoved(dirty).Should().BeGreaterThanOrEqualTo(1000L,
                "the same query must expose completed/future filtering after its partial index is removed");
            await SchemaQueries.ExecuteAsync(owner, null, indexDefinition);
            Indexes(await ExplainAsync(dispatcher, sql)).Should().Contain(query.Index,
                "the repaired index must restore the same normal-planner seek");
        }
    }

    [Theory]
    [InlineData("ix_background_jobs_pending")]
    [InlineData("ix_background_jobs_recovery")]
    [InlineData("ix_background_jobs_terminal")]
    [InlineData("ix_background_jobs_bridge")]
    public async Task Idle_and_partial_quantums_seek_past_future_backlog_and_detect_a_volatile_clock(string index)
    {
        var query = EligibilityQueries.Single(candidate => candidate.Index == index);
        var clockColumn = query.Order.Split(',')[0];
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await SeedFutureEligibilityAsync(database, query);
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        foreach (var dueCount in new[] { 0, 5 })
        {
            if (dueCount != 0)
                await SchemaQueries.ExecuteAsync(dispatcher, null, $"""
                    WITH due AS (SELECT id FROM background_jobs ORDER BY id LIMIT 5)
                    UPDATE background_jobs j SET {clockColumn} = clock_timestamp() - interval '1 minute'
                    FROM due WHERE j.id = due.id;
                    """);
            await SchemaQueries.ExecuteAsync(owner, null, "ANALYZE background_jobs");
            (await SchemaQueries.CountAsync(dispatcher,
                $"SELECT count(*) FROM background_jobs WHERE {query.Predicate}")).Should().Be(dueCount);
            var sql = $"SELECT id FROM background_jobs WHERE {query.Predicate} ORDER BY {query.Order} LIMIT 10 FOR UPDATE SKIP LOCKED";
            var clean = await ExplainAsync(dispatcher, sql);
            Indexes(clean).Should().Contain(query.Index);
            IndexConditions(clean).Should().Contain(condition =>
                condition.Contains(clockColumn + " <= statement_timestamp()", StringComparison.Ordinal));
            MaximumRowsRemoved(clean).Should().BeLessThan(100L,
                "an idle or short page must stop before the future backlog even when LIMIT is not satisfied");

            var dirty = await ExplainAsync(dispatcher,
                sql.Replace("statement_timestamp()", "clock_timestamp()", StringComparison.Ordinal));
            IndexConditions(dirty).Should().NotContain(condition => condition.Contains(clockColumn, StringComparison.Ordinal));
            MaximumRowsRemoved(dirty).Should().BeGreaterThanOrEqualTo(6000L,
                "the same detector must expose the unbounded future scan caused by a volatile candidate cutoff");
        }
    }

    private static async Task SeedFutureEligibilityAsync(DisposableSchemaDatabase database, Eligibility query)
    {
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            await SchemaQueries.ExecuteAsync(app, transaction, """
                INSERT INTO background_jobs (id, tenant_id, definition_key, profile, queue, parameters, original_due_at, traceparent)
                SELECT uuidv7(), @tenant, 'fixture.backlog.v1', 'transactional', 'fixture', '{}',
                    clock_timestamp() - interval '1 minute', @trace FROM generate_series(1,6005);
                """, ("tenant", SchemaFixture.TenantA), ("trace", BackgroundJobSchemaSeed.Traceparent));
            await transaction.CommitAsync();
        }
        var assignment = query.Index switch
        {
            "ix_background_jobs_pending" => "available_after = clock_timestamp() + interval '1 day'",
            "ix_background_jobs_recovery" => "ownership_token = uuidv7(), ownership_kind = 'routing', "
                + "ownership_until = clock_timestamp() + interval '9 minutes'",
            "ix_background_jobs_terminal" => "phase = 'terminal_pending', terminal_reason = 'retired_definition', "
                + "available_after = clock_timestamp() + interval '1 day'",
            "ix_background_jobs_bridge" => "handoff_check_at = clock_timestamp() + interval '1 day'",
            _ => throw new ArgumentOutOfRangeException(nameof(query)),
        };
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await SchemaQueries.ExecuteAsync(dispatcher, null, $"UPDATE background_jobs SET {assignment}");
    }

    private static async Task SeedIndexBacklogAsync(DisposableSchemaDatabase database)
    {
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            await SchemaQueries.ExecuteAsync(app, transaction, """
                INSERT INTO background_jobs (id, tenant_id, definition_key, profile, queue, parameters, original_due_at, traceparent)
                SELECT uuidv7(), @tenant, 'fixture.backlog.v1', 'transactional', 'fixture', '{}',
                    CASE WHEN n <= 3000 THEN '2025-01-01T00:00:00Z'::timestamptz
                         WHEN n <= 9000 THEN clock_timestamp() + interval '1 day'
                         ELSE clock_timestamp() - interval '1 minute' END, @trace
                FROM generate_series(1,9040) n;
                """, ("tenant", SchemaFixture.TenantA), ("trace", BackgroundJobSchemaSeed.Traceparent));
            await transaction.CommitAsync();
        }
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await SchemaQueries.ExecuteAsync(dispatcher, null, """
            UPDATE background_jobs SET ownership_token = uuidv7(), ownership_kind = 'routing',
                ownership_until = clock_timestamp() + interval '4 minutes'
            WHERE original_due_at = '2025-01-01T00:00:00Z';
            UPDATE background_jobs SET handoff_check_at = original_due_at + interval '1 minute'
            WHERE original_due_at > clock_timestamp();
            """);
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            await SchemaQueries.ExecuteAsync(app, transaction, """
                INSERT INTO background_job_attempts (id, tenant_id, job_id, stage, ordinal, admission_token, deadline)
                SELECT uuidv7(), tenant_id, id, 'normal', 1, ownership_token, clock_timestamp() + interval '4 minutes'
                FROM background_jobs WHERE original_due_at = '2025-01-01T00:00:00Z';
                UPDATE background_jobs j SET normal_attempts = 1, current_attempt_id = a.id,
                    attempt_deadline = a.deadline, phase = 'executing'
                FROM background_job_attempts a WHERE a.tenant_id = j.tenant_id AND a.job_id = j.id;
                UPDATE background_jobs SET phase = 'completed', processed_at = clock_timestamp()
                WHERE original_due_at = '2025-01-01T00:00:00Z';
                """);
            await transaction.CommitAsync();
        }
        await SchemaQueries.ExecuteAsync(dispatcher, null, """
            WITH candidates AS (SELECT id FROM background_jobs WHERE phase = 'pending' AND available_after <= clock_timestamp()
                ORDER BY id LIMIT 5)
            UPDATE background_jobs j SET phase = 'terminal_pending', terminal_reason = 'retired_definition'
            FROM candidates c WHERE j.id = c.id;
            WITH candidates AS (SELECT id FROM background_jobs WHERE phase = 'pending' AND available_after <= clock_timestamp()
                AND ownership_token IS NULL ORDER BY id LIMIT 5)
            UPDATE background_jobs j SET ownership_token = uuidv7(), ownership_kind = 'routing',
                ownership_until = clock_timestamp() + interval '1 minute' FROM candidates c WHERE j.id = c.id;
            UPDATE background_jobs SET ownership_until = clock_timestamp() - interval '1 second'
            WHERE ownership_token IS NOT NULL AND phase = 'pending';
            """);
    }

    private static async Task<string> ExplainAsync(System.Data.Common.DbConnection connection, string sql)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + sql,
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static string[] Indexes(string json)
    {
        using var document = JsonDocument.Parse(json);
        return WalkPlan(document.RootElement[0].GetProperty("Plan"))
            .Where(node => node.TryGetProperty("Index Name", out _))
            .Select(node => node.GetProperty("Index Name").GetString()!).ToArray();
    }

    private static string[] IndexConditions(string json)
    {
        using var document = JsonDocument.Parse(json);
        return WalkPlan(document.RootElement[0].GetProperty("Plan"))
            .Where(node => node.TryGetProperty("Index Cond", out _))
            .Select(node => node.GetProperty("Index Cond").GetString()!).ToArray();
    }

    private static long MaximumRowsRemoved(string json)
    {
        using var document = JsonDocument.Parse(json);
        return WalkPlan(document.RootElement[0].GetProperty("Plan"))
            .Where(node => node.TryGetProperty("Rows Removed by Filter", out _))
            .Select(node => node.GetProperty("Rows Removed by Filter").GetInt64()).DefaultIfEmpty().Max();
    }

    private static IEnumerable<JsonElement> WalkPlan(JsonElement node)
    {
        yield return node;
        if (node.TryGetProperty("Plans", out var children))
            foreach (var child in children.EnumerateArray())
                foreach (var descendant in WalkPlan(child))
                    yield return descendant;
    }
}
