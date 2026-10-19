using FluentAssertions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobStorageMigrationTests
{
    [Theory]
    [InlineData("parent", false)]
    [InlineData("parent", true)]
    [InlineData("recipient", false)]
    [InlineData("recipient", true)]
    public async Task Job_envelopes_refuse_foreign_tenant_and_mismatched_organization_backreferences(string kind, bool foreignTenant)
    {
        await using var app = await PostgresFixture.OpenAsync(schema.Postgres.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null, tenantReadHatch: true);
        var target = BackgroundJobSchemaSeed.Find(foreignTenant ? SchemaFixture.TenantB : SchemaFixture.TenantA,
            foreignTenant ? null : SchemaFixture.OrgA1);
        var insert = async () => await BackgroundJobSchemaSeed.InsertJobAsync(app, transaction, Guid.CreateVersion7(),
            SchemaFixture.TenantA, null, parentId: kind == "parent" ? target.JobId : null,
            recipientId: kind == "recipient" ? target.RecipientId : null);
        var refusal = (await insert.Should().ThrowAsync<PostgresException>()).Which;
        refusal.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        refusal.MessageText.Should().Contain(kind == "parent" ? "parent scope" : "recipient");
    }

    [Theory]
    [InlineData("background_jobs", "tg_background_jobs_envelope")]
    [InlineData("background_job_attempts", "tg_background_job_attempts_guard")]
    [InlineData("background_job_reservations", "tg_background_job_reservations_guard")]
    public async Task Composite_foreign_keys_independently_refuse_cross_tenant_parent_attempt_and_reservation_links(
        string table, string trigger)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedAsync(database.AppConnectionString, database.OutboxConnectionString);
        await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
            await SchemaQueries.ExecuteAsync(owner, null, $"DROP TRIGGER {trigger} ON {table}");
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        var foreign = BackgroundJobSchemaSeed.Find(SchemaFixture.TenantB, null);
        var sql = table switch
        {
            "background_jobs" => """
                INSERT INTO background_jobs (id, tenant_id, definition_key, profile, queue, parameters, original_due_at, traceparent, parent_job_id)
                VALUES (uuidv7(), @tenant, 'fixture.run.v1', 'transactional', 'fixture', '{}', now(), @trace, @job)
                """,
            "background_job_attempts" => """
                INSERT INTO background_job_attempts (id, tenant_id, job_id, stage, ordinal, admission_token, deadline)
                VALUES (uuidv7(), @tenant, @job, 'normal', 2, uuidv7(), now() + interval '30 seconds')
                """,
            "background_job_reservations" => """
                INSERT INTO background_job_reservations (tenant_id, capacity_group, job_id, attempt_id)
                VALUES (@tenant, 'fixture.resource', @job, @attempt)
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(table)),
        };
        var link = async () => await SchemaQueries.ExecuteAsync(app, transaction, sql,
            ("tenant", SchemaFixture.TenantA), ("trace", BackgroundJobSchemaSeed.Traceparent),
            ("job", foreign.JobId), ("attempt", foreign.AttemptId));
        (await link.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task Attempt_organization_must_mirror_its_job_even_when_the_tenant_read_hatch_reveals_the_parent()
    {
        await using var app = await PostgresFixture.OpenAsync(schema.Postgres.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null, tenantReadHatch: true);
        var parent = BackgroundJobSchemaSeed.Find(SchemaFixture.TenantA, SchemaFixture.OrgA1);
        var insert = async () => await SchemaQueries.ExecuteAsync(app, transaction, """
            INSERT INTO background_job_attempts (id, tenant_id, job_id, stage, ordinal, admission_token, deadline)
            VALUES (uuidv7(), @tenant, @job, 'normal', 2, uuidv7(), now() + interval '30 seconds')
            """, ("tenant", SchemaFixture.TenantA), ("job", parent.JobId));
        var refusal = (await insert.Should().ThrowAsync<PostgresException>()).Which;
        refusal.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        refusal.MessageText.Should().Contain("parent scope");
    }

    [Fact]
    public async Task Recurrence_and_logical_slots_preserve_database_microsecond_precision()
    {
        await using var dispatcher = await PostgresFixture.OpenAsync(schema.Postgres.OutboxConnectionString);
        await using var transaction = await dispatcher.BeginTransactionAsync();
        await SchemaQueries.ExecuteAsync(dispatcher, transaction, """
            INSERT INTO background_job_recurrences (recurrence_key, revision, manifest_hash, target_definition_key,
                schedule, activates_at, anchor_at, period_microseconds, jitter_microseconds, recipient_policy, next_scheduled_at)
            VALUES ('fixture.precision', 1, repeat('a',64), 'fixture.run.v1', 'interval',
                '2026-01-01T00:00:00.000001Z', '2026-01-01T00:00:00.000001Z', 60000001, 1,
                'serving_tenants', '2026-01-01T00:01:00.000002Z');
            INSERT INTO background_job_occurrences (id, recurrence_key, revision, manifest_hash, target_definition_key,
                recipient_policy, scheduled_at, jitter_microseconds, skipped_slots)
            VALUES (uuidv7(), 'fixture.precision', 1, repeat('a',64), 'fixture.run.v1', 'serving_tenants',
                '2026-01-01T00:01:00.000002Z', 1, 0);
            """);
        (await SchemaQueries.CountAsync(dispatcher, """
            SELECT count(*) FROM background_job_occurrences o JOIN background_job_recurrences r USING (recurrence_key, revision)
            WHERE o.recurrence_key = 'fixture.precision' AND r.period_microseconds = 60000001 AND r.jitter_microseconds = 1
              AND r.anchor_at = '2026-01-01T00:00:00.000001Z'::timestamptz
              AND o.scheduled_at = r.anchor_at + r.period_microseconds * interval '1 microsecond'
            """, transaction)).Should().Be(1L);
    }
}
