using FluentAssertions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobStorageMigrationTests
{
    [Theory]
    [InlineData("background_jobs", "tg_background_jobs_transition", "plan = '{\"replacement\":true}'", "checkpoints are write-once")]
    [InlineData("background_job_attempts", "tg_background_job_attempts_guard", "execute_intent_at = execute_intent_at + interval '1 microsecond'", "recorded evidence are immutable")]
    [InlineData("background_job_attempts", "tg_background_job_attempts_guard", "outcome = '{\"replacement\":true}'", "recorded evidence are immutable")]
    [InlineData("background_job_attempts", "tg_background_job_attempts_guard", "quiescence_id = uuidv7()", "recorded evidence are immutable")]
    public async Task External_plan_intent_outcome_and_quiescence_are_write_once_with_removed_guard_controls(
        string table, string trigger, string assignment, string message)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var job = await NewExternalOutcomeAsync(database);
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        var sql = $"UPDATE {table} SET {assignment} WHERE id = @id";
        var id = table == "background_jobs" ? job.JobId : job.AttemptId;
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            var replacement = async () => await SchemaQueries.ExecuteAsync(app, transaction, sql, ("id", id));
            var refusal = (await replacement.Should().ThrowAsync<PostgresException>()).Which;
            refusal.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
            refusal.MessageText.Should().Contain(message);
        }
        await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
            await SchemaQueries.ExecuteAsync(owner, null, $"DROP TRIGGER {trigger} ON {table}");
        await using var dirty = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, dirty, SchemaFixture.TenantA, null);
        await SchemaQueries.ExecuteAsync(app, dirty, sql, ("id", id));
        await SchemaQueries.ExecuteAsync(app, dirty, "SET CONSTRAINTS ALL IMMEDIATE");
    }

    [Theory]
    [InlineData("background_job_recurrences", "tg_background_job_recurrences_guard", "manifest_hash", "manifest_hash = repeat('b',64)")]
    [InlineData("background_job_occurrences", "tg_background_job_occurrences_guard", "manifest_hash", "manifest_hash = repeat('b',64)")]
    [InlineData("background_job_recipients", "tg_background_job_recipients_guard", "target_definition_key", "target_definition_key = 'fixture.changed.v1'")]
    public async Task Control_manifests_and_recipient_routes_remain_immutable_below_the_acl(
        string table, string trigger, string column, string assignment)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await BackgroundJobSchemaSeed.SeedRecipientsAsync(database.OutboxConnectionString);
        await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await SchemaQueries.ExecuteAsync(owner, null, $"GRANT UPDATE ({column}) ON {table} TO learnstack_outbox_admin");
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await using (var transaction = await dispatcher.BeginTransactionAsync())
        {
            var rewrite = async () => await SchemaQueries.ExecuteAsync(dispatcher, transaction, $"UPDATE {table} SET {assignment}");
            (await rewrite.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        }
        await SchemaQueries.ExecuteAsync(owner, null, $"DROP TRIGGER {trigger} ON {table}");
        await using var dirty = await dispatcher.BeginTransactionAsync();
        await SchemaQueries.ExecuteAsync(dispatcher, dirty, $"UPDATE {table} SET {assignment}");
        await SchemaQueries.ExecuteAsync(dispatcher, dirty, "SET CONSTRAINTS ALL IMMEDIATE");
    }

    [Fact]
    public async Task Prepared_plan_cap_counts_utf8_bytes_and_preserves_the_inclusive_boundary()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var job = await NewPendingJobAsync(database, "external");
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        await BackgroundJobSchemaSeed.AdmitAsync(app, transaction, job.JobId, job.AttemptId, job.Token,
            SchemaFixture.TenantA, null, "external");
        await SchemaQueries.ExecuteAsync(app, transaction, """
            INSERT INTO background_job_capacity (tenant_id, capacity_group) VALUES (@tenant, 'fixture.resource');
            INSERT INTO background_job_reservations (tenant_id, capacity_group, job_id, attempt_id)
                VALUES (@tenant, 'fixture.resource', @job, @attempt);
            UPDATE background_jobs SET phase = 'prepared', plan = @plan, plan_fingerprint = repeat('a',64),
                operation_id = @operation, prepared_at = clock_timestamp() WHERE id = @job;
            """, ("plan", "{\"id\":\"" + new string('x', 65527) + "\"}"),
            ("operation", Guid.NewGuid()), ("job", job.JobId), ("attempt", job.AttemptId), ("tenant", SchemaFixture.TenantA));
        (await SchemaQueries.CountAsync(app,
            "SELECT octet_length(plan)::bigint FROM background_jobs", transaction)).Should().Be(65536L);
    }

    [Theory]
    [InlineData("{\"id\":1,\"id\":2}")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task Payloads_are_objects_with_unique_keys_before_any_runtime_deserializer(string payload)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        var insert = async () => await BackgroundJobSchemaSeed.InsertJobAsync(app, transaction,
            Guid.CreateVersion7(), SchemaFixture.TenantA, null, parameters: payload);
        (await insert.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }

    private static async Task<PendingJob> NewExternalOutcomeAsync(DisposableSchemaDatabase database, bool recordOutcome = true)
    {
        var job = await NewPendingJobAsync(database, "external");
        var operation = Guid.NewGuid();
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        await BackgroundJobSchemaSeed.AdmitAsync(app, transaction, job.JobId, job.AttemptId, job.Token,
            SchemaFixture.TenantA, null, "external");
        await SchemaQueries.ExecuteAsync(app, transaction, """
            INSERT INTO background_job_capacity (tenant_id, capacity_group) VALUES (@tenant, 'fixture.resource');
            INSERT INTO background_job_reservations (tenant_id, capacity_group, job_id, attempt_id)
                VALUES (@tenant, 'fixture.resource', @job, @attempt);
            UPDATE background_jobs SET plan = '{}', plan_fingerprint = repeat('a',64), prepared_at = clock_timestamp(),
                operation_id = @operation, phase = 'prepared' WHERE id = @job;
            UPDATE background_job_attempts SET plan_fingerprint = repeat('a',64), operation_id = @operation,
                execute_intent_at = clock_timestamp() WHERE id = @attempt;
            UPDATE background_jobs SET phase = 'executing' WHERE id = @job;
            """, ("tenant", SchemaFixture.TenantA), ("job", job.JobId), ("attempt", job.AttemptId),
            ("operation", operation));
        if (recordOutcome)
            await SchemaQueries.ExecuteAsync(app, transaction, """
                UPDATE background_job_attempts SET outcome = '{}', outcome_kind = 'succeeded', outcome_at = clock_timestamp(),
                    quiescence_id = @quiescence, quiescence_kind = 'provider_fenced', quiescent_at = clock_timestamp()
                    WHERE id = @attempt;
                UPDATE background_jobs SET final_outcome_attempt_id = @attempt, finalization_status = 'succeeded',
                    finalization_reason = 'none', phase = 'finalize_pending' WHERE id = @job;
                """, ("job", job.JobId), ("attempt", job.AttemptId), ("quiescence", Guid.NewGuid()));
        await transaction.CommitAsync();
        return job;
    }
}
