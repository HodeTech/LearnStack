using System.Data.Common;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobStorageMigrationTests
{
    [Theory]
    [InlineData("succeeded", null)]
    [InlineData("failed", "permanent")]
    [InlineData("failed", "transient_repeatable")]
    [InlineData("failed", "transient_not_repeatable")]
    [InlineData("cancelled", "transient_repeatable")]
    [InlineData("no_start", null)]
    public async Task External_evidence_requires_exact_atomic_release_and_removing_only_the_deferred_guard_exposes_the_half_write(
        string kind, string? policy)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var job = kind == "no_start" ? await PreparedNoStartAsync(database) : await NewExternalOutcomeAsync(database, recordOutcome: false);
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            await WriteAtomicEvidenceAsync(app, transaction, job, kind, policy, release: false);
            var force = async () => await SchemaQueries.ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
            var refused = (await force.Should().ThrowAsync<PostgresException>()).Which;
            refused.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
            refused.MessageText.Should().Be("external evidence and exact reservation release must commit together");
        }
        string definition;
        await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
        {
            definition = (await SchemaQueries.ReadStringsAsync(owner, """
                SELECT pg_get_triggerdef(oid) FROM pg_trigger WHERE tgrelid='background_job_attempts'::regclass
                    AND tgname='ct_background_job_attempts_counts'
                """)).Single();
            await SchemaQueries.ExecuteAsync(owner, null,
                "DROP TRIGGER ct_background_job_attempts_counts ON background_job_attempts");
        }
        await using (var dirty = await app.BeginTransactionAsync())
        {
            await SchemaQueries.AssertApplicationRoleAsync(app, dirty);
            await EducationSchemaSeed.AnnounceAsync(app, dirty, SchemaFixture.TenantA, null);
            await WriteAtomicEvidenceAsync(app, dirty, job, kind, policy, release: false);
            await SchemaQueries.ExecuteAsync(app, dirty, "SET CONSTRAINTS ALL IMMEDIATE");
            (await PolicyCountAsync(app, """
                SELECT count(*) FROM background_job_attempts a JOIN background_job_reservations r
                    ON r.tenant_id=a.tenant_id AND r.job_id=a.job_id AND r.attempt_id=a.id
                WHERE a.id=@attempt AND (a.outcome IS NOT NULL OR a.no_start_at IS NOT NULL) AND r.released_at IS NULL
                """, dirty, ("attempt", job.AttemptId))).Should().Be(1,
                "removing only the attempt's deferred guard exposes the same otherwise valid half checkpoint");
        }
        await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
            await SchemaQueries.ExecuteAsync(owner, null, definition);
        await using (var positive = await app.BeginTransactionAsync())
        {
            await SchemaQueries.AssertApplicationRoleAsync(app, positive);
            await EducationSchemaSeed.AnnounceAsync(app, positive, SchemaFixture.TenantA, null);
            await WriteAtomicEvidenceAsync(app, positive, job, kind, policy, release: true);
            await positive.CommitAsync();
        }
        await using var read = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, read, SchemaFixture.TenantA, null);
        (await PolicyCountAsync(app, """
            SELECT count(*) FROM background_job_attempts a JOIN background_job_reservations r
                ON r.tenant_id=a.tenant_id AND r.job_id=a.job_id AND r.attempt_id=a.id
            WHERE a.id=@attempt AND (a.outcome IS NOT NULL OR a.no_start_at IS NOT NULL) AND r.released_at IS NOT NULL
                AND r.release_reason=@reason
            """, read, ("attempt", job.AttemptId), ("reason", kind == "no_start" ? "never_started" : "quiescent"))).Should().Be(1,
            "restored enforcement accepts the complete evidence/release pair under the app role");
    }

    private static async Task<PendingJob> PreparedNoStartAsync(DisposableSchemaDatabase database)
    {
        var job = await NewPendingJobAsync(database, "external");
        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            await BackgroundJobSchemaSeed.AdmitAsync(app, transaction, job.JobId, job.AttemptId, job.Token,
                SchemaFixture.TenantA, null, "external");
            await SchemaQueries.ExecuteAsync(app, transaction, """
                INSERT INTO background_job_capacity (tenant_id, capacity_group) VALUES (@tenant,'fixture.resource');
                INSERT INTO background_job_reservations (tenant_id,capacity_group,job_id,attempt_id)
                    VALUES (@tenant,'fixture.resource',@job,@attempt);
                UPDATE background_jobs SET plan='{}',plan_fingerprint=repeat('a',64),prepared_at=clock_timestamp(),
                    operation_id=uuidv7(),phase='prepared' WHERE id=@job;
                """, ("tenant", SchemaFixture.TenantA), ("job", job.JobId), ("attempt", job.AttemptId));
            await transaction.CommitAsync();
        }
        await using var control = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await SchemaQueries.ExecuteAsync(control, null, """
            UPDATE background_jobs SET ownership_until=clock_timestamp()-interval '1 second' WHERE id=@job;
            UPDATE background_jobs SET phase='reconciling',ownership_kind='recovery',ownership_token=uuidv7(),
                ownership_until=clock_timestamp()+interval '1 minute' WHERE id=@job;
            """, ("job", job.JobId));
        return job;
    }

    private static Task WriteAtomicEvidenceAsync(DbConnection app, DbTransaction transaction, PendingJob job,
        string kind, string? policy, bool release) => SchemaQueries.ExecuteAsync(app, transaction, """
        UPDATE background_job_attempts SET
            outcome=CASE WHEN @kind='no_start' THEN NULL ELSE '{}' END,
            outcome_kind=CASE WHEN @kind='no_start' THEN NULL ELSE @kind END,
            outcome_at=CASE WHEN @kind='no_start' THEN NULL ELSE clock_timestamp() END, failure_policy=@policy,
            quiescence_id=CASE WHEN @kind='no_start' THEN NULL ELSE uuidv7() END,
            quiescence_kind=CASE WHEN @kind='no_start' THEN NULL ELSE 'provider_fenced' END,
            quiescent_at=CASE WHEN @kind='no_start' THEN NULL ELSE clock_timestamp() END,
            no_start_at=CASE WHEN @kind='no_start' THEN clock_timestamp() ELSE NULL END WHERE id=@attempt;
        UPDATE background_job_reservations SET released_at=clock_timestamp(),
            release_reason=CASE WHEN @kind='no_start' THEN 'never_started' ELSE 'quiescent' END
            WHERE job_id=@job AND attempt_id=@attempt AND @release;
        """, ("job", job.JobId), ("attempt", job.AttemptId), ("kind", kind),
        ("policy", (object?)policy ?? DBNull.Value), ("release", release));
}
