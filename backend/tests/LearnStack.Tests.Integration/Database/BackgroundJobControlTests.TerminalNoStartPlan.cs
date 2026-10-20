using System.Text.Json;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Jobs;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobControlTests
{
    private const string TerminalPlanDefinition = "fixture.terminalplan.v1";
    private const string OccupiedPlanDefinition = "fixture.occupiedplan.v1";
    private const int TerminalPlanBacklog = 6_000;

    [Fact]
    public async Task Actual_terminal_no_start_selection_seeks_future_audit_backlog_and_detects_volatile_cutoff()
    {
        await using var database = await DatabaseAsync();
        await SeedTerminalNoStartPlanBacklogAsync(database);
        await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        (await SchemaQueries.ReadStringsAsync(dispatcher, "SELECT current_user"))
            .Should().Equal("learnstack_outbox_admin");
        (await CountTerminalPlanAsync((NpgsqlConnection)dispatcher, """
            SELECT count(*) FROM background_jobs j
            JOIN background_job_attempts a ON a.tenant_id=j.tenant_id AND a.job_id=j.id AND a.id=j.current_attempt_id
            JOIN background_job_reservations r ON r.tenant_id=a.tenant_id AND r.job_id=a.job_id AND r.attempt_id=a.id
            WHERE j.definition_key=@definition AND j.phase='terminal_pending' AND j.processed_at IS NULL
                AND j.dead_lettered_at IS NULL AND j.available_after>clock_timestamp()
                AND j.ownership_token IS NULL AND j.last_error='audit_unavailable'
                AND a.no_start_at IS NOT NULL AND a.execute_intent_at IS NULL
                AND r.released_at IS NOT NULL AND r.release_reason='never_started'
            """, ("definition", TerminalPlanDefinition))).Should().Be(TerminalPlanBacklog,
                "the future subjects retain real guarded NoStart/release history, not invented summary counts");

        var sql = PostgresBackgroundJobControlStore.TerminalNoStartSelectionSql;
        BackgroundJobId? due = null;
        for (var candidates = 0; candidates <= 1; candidates++)
        {
            if (candidates == 1) due = await SeedTerminalNoStartPlanCandidateAsync(database);
            // Guarded seed updates leave obsolete index entries whose bitmap
            // TIDs are counted by EXPLAIN even when no live row is due. Make
            // maintenance explicit before measuring the live future backlog;
            // retain the normal planner and the same dirty/clean visit bounds.
            foreach (var maintenance in new[] { "VACUUM (ANALYZE) background_jobs",
                         "VACUUM (ANALYZE) background_job_attempts", "VACUUM (ANALYZE) background_job_reservations" })
                await SchemaQueries.ExecuteAsync(owner, null, maintenance);
            var cleanJson = await ExplainControlAsync((NpgsqlConnection)dispatcher, sql);
            var clean = PlanNodes(cleanJson);
            clean.Where(node => node.TryGetProperty("Index Cond", out _))
                .Select(node => node.GetProperty("Index Cond").GetString())
                .Should().Contain(condition => condition!.Contains("available_after <= statement_timestamp()", StringComparison.Ordinal),
                    "the exact runtime candidate query must bound future eligibility with the normal planner; visited={0}; plan={1}",
                    VisitedTerminalPlanRows(clean), cleanJson);
            clean[0].GetProperty("Actual Rows").GetDouble().Should().Be(candidates);
            VisitedTerminalPlanRows(clean).Should().BeLessThan(256,
                "an idle or one-row cleanup page must stop before the future backlog and unrelated occupied work; plan={0}", cleanJson);

            if (candidates == 0)
            {
                // Change only the available-after range bound. LIMIT 1 may
                // legitimately stop on a due hit, so the dirty scan is proved
                // on the idle page; both clean cardinalities remain covered.
                var dirtySql = sql.Replace("j.available_after<=statement_timestamp()",
                    "j.available_after<=clock_timestamp()", StringComparison.Ordinal);
                dirtySql.Should().NotBe(sql);
                var dirty = PlanNodes(await ExplainControlAsync((NpgsqlConnection)dispatcher, dirtySql));
                VisitedTerminalPlanRows(dirty).Should().BeGreaterThanOrEqualTo(TerminalPlanBacklog,
                    "the same real-query detector must expose work proportional to future/occupied population when only the cutoff becomes volatile");
            }
        }

        // EXPLAIN ANALYZE executes the SELECT, including its row lock, inside a
        // transaction the shared helper disposes/rolls back. It must not admit
        // recovery, spend another attempt, write evidence, or release occupancy.
        due.Should().NotBeNull();
        var candidate = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, due!.Value))!;
        candidate.Phase.Should().Be("terminal_pending");
        candidate.NormalAttempts.Should().Be(1);
        candidate.OwnershipToken.Should().BeNull();
        (await CountTerminalPlanAsync((NpgsqlConnection)dispatcher, """
            SELECT count(*) FROM background_job_reservations
            WHERE job_id=@job AND released_at IS NULL
            """, ("job", due!.Value.Value))).Should().Be(1);
    }

    private static async Task SeedTerminalNoStartPlanBacklogAsync(DisposableSchemaDatabase database)
    {
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            await SchemaQueries.ExecuteAsync(app, transaction, """
                INSERT INTO background_jobs (id,tenant_id,definition_key,profile,queue,capacity_group,
                    parameters,original_due_at,traceparent)
                SELECT uuidv7(),@tenant,kind.definition,'external','external',
                    kind.capacity_prefix||((n-1)/1000)::text,'{}',clock_timestamp()-interval '1 minute',@trace
                FROM generate_series(1,@count) AS series(n)
                CROSS JOIN (VALUES (@terminal::text,'fixture.resource_'::text),
                    (@occupied::text,'fixture.occupied_'::text)) AS kind(definition,capacity_prefix)
                """, ("tenant", SchemaFixture.TenantA), ("trace", BackgroundJobSchemaSeed.Traceparent),
                ("count", TerminalPlanBacklog), ("terminal", TerminalPlanDefinition), ("occupied", OccupiedPlanDefinition));
            await transaction.CommitAsync();
        }
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await SchemaQueries.ExecuteAsync(dispatcher, null, """
            UPDATE background_jobs SET ownership_token=uuidv7(),ownership_kind='routing',
                ownership_until=clock_timestamp()+interval '9 minutes'
            """);
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            await SchemaQueries.ExecuteAsync(app, transaction, """
                INSERT INTO background_job_capacity (tenant_id,capacity_group)
                    SELECT DISTINCT tenant_id,capacity_group FROM background_jobs WHERE tenant_id=@tenant;
                INSERT INTO background_job_attempts (id,tenant_id,job_id,stage,ordinal,admission_token,deadline)
                    SELECT uuidv7(),tenant_id,id,'normal',1,ownership_token,clock_timestamp()+interval '4 minutes'
                    FROM background_jobs WHERE tenant_id=@tenant;
                UPDATE background_jobs j SET normal_attempts=1,current_attempt_id=a.id,
                    attempt_deadline=a.deadline,phase='preparing'
                FROM background_job_attempts a WHERE a.tenant_id=j.tenant_id AND a.job_id=j.id AND j.tenant_id=@tenant;
                INSERT INTO background_job_reservations (tenant_id,capacity_group,job_id,attempt_id)
                    SELECT tenant_id,capacity_group,id,current_attempt_id FROM background_jobs WHERE tenant_id=@tenant;
                """, ("tenant", SchemaFixture.TenantA));
            await transaction.CommitAsync();
        }
        await SchemaQueries.ExecuteAsync(dispatcher, null, """
            UPDATE background_jobs SET ownership_until=clock_timestamp()-interval '1 microsecond'
                WHERE definition_key=@definition;
            UPDATE background_jobs SET phase='terminal_pending',terminal_reason='permanent_failure'
                WHERE definition_key=@definition;
            UPDATE background_jobs SET ownership_token=uuidv7(),ownership_kind='recovery',
                ownership_until=clock_timestamp()+interval '9 minutes' WHERE definition_key=@definition;
            """, ("definition", TerminalPlanDefinition));
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            await SchemaQueries.ExecuteAsync(app, transaction, """
                UPDATE background_job_attempts a SET no_start_at=clock_timestamp()
                FROM background_jobs j WHERE j.tenant_id=a.tenant_id AND j.id=a.job_id
                    AND a.id=j.current_attempt_id AND j.tenant_id=@tenant AND j.definition_key=@definition;
                UPDATE background_job_reservations r SET released_at=clock_timestamp(),release_reason='never_started'
                FROM background_jobs j WHERE j.tenant_id=r.tenant_id AND j.id=r.job_id
                    AND r.attempt_id=j.current_attempt_id AND j.tenant_id=@tenant AND j.definition_key=@definition;
                """, ("tenant", SchemaFixture.TenantA), ("definition", TerminalPlanDefinition));
            await transaction.CommitAsync();
        }
        // Same granted, guarded durable shape as known terminal-audit rollback
        // backoff. The long future interval makes the planner observation stable;
        // this fixture does not claim it executed 6,000 audit writer failures.
        await SchemaQueries.ExecuteAsync(dispatcher, null, """
            UPDATE background_jobs SET available_after=clock_timestamp()+interval '1 day',last_error='audit_unavailable',
                ownership_token=NULL,ownership_kind=NULL,ownership_until=NULL WHERE definition_key=@definition
            """, ("definition", TerminalPlanDefinition));
    }

    private static async Task<BackgroundJobId> SeedTerminalNoStartPlanCandidateAsync(DisposableSchemaDatabase database)
    {
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            TerminalPlanDefinition, parameters: "{}", capacityGroup: "fixture.resource_0", profile: "external");
        var token = Guid.NewGuid();
        await BackgroundJobSchemaSeed.ClaimAsync(database.OutboxConnectionString, job.Value, token);
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            var attempt = Guid.CreateVersion7();
            await BackgroundJobSchemaSeed.AdmitAsync(app, transaction, job.Value, attempt, token,
                SchemaFixture.TenantA, null, "external");
            await SchemaQueries.ExecuteAsync(app, transaction, """
                INSERT INTO background_job_reservations (tenant_id,capacity_group,job_id,attempt_id)
                    VALUES (@tenant,'fixture.resource_0',@job,@attempt)
                """, ("tenant", SchemaFixture.TenantA), ("job", job.Value), ("attempt", attempt));
            await transaction.CommitAsync();
        }
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await SchemaQueries.ExecuteAsync(dispatcher, null, """
            UPDATE background_jobs SET ownership_until=clock_timestamp()-interval '1 microsecond' WHERE id=@job;
            UPDATE background_jobs SET phase='terminal_pending',terminal_reason='permanent_failure' WHERE id=@job;
            """, ("job", job.Value));
        return job;
    }

    private static double VisitedTerminalPlanRows(IEnumerable<JsonElement> nodes) => nodes.Sum(node =>
        (PlanNumber(node, "Actual Rows") + PlanNumber(node, "Rows Removed by Filter")
            + PlanNumber(node, "Rows Removed by Join Filter")) * PlanNumber(node, "Actual Loops"));

    private static double PlanNumber(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) ? value.GetDouble() : 0;

    private static async Task<long> CountTerminalPlanAsync(NpgsqlConnection connection, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
