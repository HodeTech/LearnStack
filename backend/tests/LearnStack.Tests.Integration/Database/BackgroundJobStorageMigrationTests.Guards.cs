using System.Data.Common;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobStorageMigrationTests
{
    [Theory]
    [InlineData("phase, processed_at", "'completed', clock_timestamp()")]
    [InlineData("phase, quarantine_reason", "'quarantined', 'unknown_outcome'")]
    [InlineData("wake_generation", "2")]
    [InlineData("last_error", "'unexpected_fault'")]
    public async Task Initial_state_forgery_is_refused_even_for_the_owner_and_the_removed_guard_control_accepts_it(
        string extraColumns, string extraValues)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var sql = $$"""
            INSERT INTO background_jobs (id, tenant_id, definition_key, profile, queue, parameters,
                original_due_at, traceparent, {{extraColumns}})
            VALUES (uuidv7(), @tenant, 'fixture.run.v1', 'transactional', 'fixture', '{}', now(), @trace, {{extraValues}})
            """;
        await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using (var transaction = await owner.BeginTransactionAsync())
        {
            await EducationSchemaSeed.AnnounceAsync(owner, transaction, SchemaFixture.TenantA, null);
            var forged = async () => await SchemaQueries.ExecuteAsync(owner, transaction, sql,
                ("tenant", SchemaFixture.TenantA), ("trace", BackgroundJobSchemaSeed.Traceparent));
            (await forged.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        }
        await SchemaQueries.ExecuteAsync(owner, null, "DROP TRIGGER tg_background_jobs_envelope ON background_jobs");
        await using var dirty = await owner.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(owner, dirty, SchemaFixture.TenantA, null);
        await SchemaQueries.ExecuteAsync(owner, dirty, sql,
            ("tenant", SchemaFixture.TenantA), ("trace", BackgroundJobSchemaSeed.Traceparent));
        await SchemaQueries.ExecuteAsync(owner, dirty, "SET CONSTRAINTS ALL IMMEDIATE");
    }

    [Fact]
    public async Task Attempt_ledger_and_summary_commit_together_and_missing_deferred_guards_are_detected()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var job = await NewPendingJobAsync(database);
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            await InsertAttemptOnlyAsync(app, transaction, job);
            var commitCheck = async () => await SchemaQueries.ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
            var error = (await commitCheck.Should().ThrowAsync<PostgresException>()).Which;
            error.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
            error.MessageText.Should().Contain("counts and durable attempt receipts");
        }
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            await BackgroundJobSchemaSeed.AdmitAsync(app, transaction, job.JobId, job.AttemptId, job.Token,
                SchemaFixture.TenantA, null);
            await SchemaQueries.ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
            (await SchemaQueries.CountAsync(app, "SELECT count(*) FROM background_job_attempts", transaction)).Should().Be(1L);
        }
        await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
            await SchemaQueries.ExecuteAsync(owner, null, """
                DROP TRIGGER ct_background_jobs_attempt_counts ON background_jobs;
                DROP TRIGGER ct_background_job_attempts_counts ON background_job_attempts;
                """);
        await using var dirty = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, dirty, SchemaFixture.TenantA, null);
        await InsertAttemptOnlyAsync(app, dirty, job);
        await SchemaQueries.ExecuteAsync(app, dirty, "SET CONSTRAINTS ALL IMMEDIATE");
    }

    [Fact]
    public async Task Child_job_and_reciprocal_recipient_receipt_are_deferred_atomic_and_missing_guards_are_detected()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await BackgroundJobSchemaSeed.SeedRecipientsAsync(database.OutboxConnectionString);
        var route = BackgroundJobSchemaSeed.Find(SchemaFixture.TenantA, null);
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await EducationSchemaSeed.AnnounceAsync(app, transaction, route.TenantId, null);
            await BackgroundJobSchemaSeed.InsertJobAsync(app, transaction, route.JobId, route.TenantId, null,
                recipientId: route.RecipientId);
            var commitCheck = async () => await SchemaQueries.ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
            var error = (await commitCheck.Should().ThrowAsync<PostgresException>()).Which;
            error.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
            error.MessageText.Should().Contain("reciprocal recipient receipt");
        }
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await EducationSchemaSeed.AnnounceAsync(app, transaction, route.TenantId, null);
            await BackgroundJobSchemaSeed.InsertJobAsync(app, transaction, route.JobId, route.TenantId, null,
                recipientId: route.RecipientId);
            await SchemaQueries.ExecuteAsync(app, transaction,
                "UPDATE background_job_recipients SET accepted_job_id = @job, accepted_at = clock_timestamp() WHERE id = @recipient",
                ("job", route.JobId), ("recipient", route.RecipientId));
            await SchemaQueries.ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
        }
        await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
            await SchemaQueries.ExecuteAsync(owner, null, """
                DROP TRIGGER ct_background_jobs_recipient ON background_jobs;
                DROP TRIGGER ct_background_job_recipients_receipt ON background_job_recipients;
                """);
        await using var dirty = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, dirty, route.TenantId, null);
        await BackgroundJobSchemaSeed.InsertJobAsync(app, dirty, route.JobId, route.TenantId, null,
            recipientId: route.RecipientId);
        await SchemaQueries.ExecuteAsync(app, dirty, "SET CONSTRAINTS ALL IMMEDIATE");
    }

    [Theory]
    [InlineData("background_jobs", "tg_background_jobs_envelope", "parameters = '{\"changed\":true}'")]
    [InlineData("background_job_attempts", "tg_background_job_attempts_guard", "deadline = deadline + interval '1 second'")]
    [InlineData("background_job_capacity", "tg_background_job_capacity_immutable", "created_at = created_at + interval '1 second'")]
    [InlineData("background_job_reservations", "tg_background_job_reservations_guard", "reserved_at = reserved_at + interval '1 second'")]
    public async Task Immutable_facts_are_owner_enforced_and_the_same_attack_succeeds_with_only_its_guard_removed(
        string table, string trigger, string assignment)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedAsync(database.AppConnectionString, database.OutboxConnectionString);
        await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        var sql = $"UPDATE {table} SET {assignment} WHERE tenant_id = @tenant";
        await using (var transaction = await owner.BeginTransactionAsync())
        {
            await EducationSchemaSeed.AnnounceAsync(owner, transaction, SchemaFixture.TenantA, null);
            var rewrite = async () => await SchemaQueries.ExecuteAsync(owner, transaction, sql, ("tenant", SchemaFixture.TenantA));
            (await rewrite.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        }
        await SchemaQueries.ExecuteAsync(owner, null, $"DROP TRIGGER {trigger} ON {table}");
        await using var dirty = await owner.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(owner, dirty, SchemaFixture.TenantA, null);
        await SchemaQueries.ExecuteAsync(owner, dirty, sql, ("tenant", SchemaFixture.TenantA));
        await SchemaQueries.ExecuteAsync(owner, dirty, "SET CONSTRAINTS ALL IMMEDIATE");
    }

    private sealed record PendingJob(Guid JobId, Guid AttemptId, Guid Token);
    private static async Task<PendingJob> NewPendingJobAsync(DisposableSchemaDatabase database, string profile = "transactional")
    {
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = new PendingJob(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.NewGuid());
        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            await BackgroundJobSchemaSeed.InsertJobAsync(app, transaction, job.JobId, SchemaFixture.TenantA, null, profile,
                capacityGroup: profile == "external" ? BackgroundJobSchemaSeed.CapacityGroup : null);
            await transaction.CommitAsync();
        }
        await BackgroundJobSchemaSeed.ClaimAsync(database.OutboxConnectionString, job.JobId, job.Token);
        return job;
    }

    private static Task InsertAttemptOnlyAsync(DbConnection connection, DbTransaction transaction, PendingJob job) =>
        SchemaQueries.ExecuteAsync(connection, transaction, """
            INSERT INTO background_job_attempts (id, tenant_id, job_id, stage, ordinal, admission_token, deadline)
            VALUES (@attempt, @tenant, @job, 'normal', 1, @token, clock_timestamp() + interval '30 seconds')
            """, ("attempt", job.AttemptId), ("tenant", SchemaFixture.TenantA), ("job", job.JobId), ("token", job.Token));
}
