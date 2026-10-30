using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobStorageMigrationTests
{
    [Theory]
    [InlineData("succeeded", "none", true, true)]
    [InlineData("failed", "permanent_failure", true, false)]
    [InlineData("failed", "normal_attempts_exhausted", true, false)]
    [InlineData("succeeded", "none", false, false)]
    public async Task Policy_upgrade_validates_actual_predecessor_selection_and_atomic_release_without_rewriting_history(
        string status, string reason, bool release, bool compatible)
    {
        await using var database = await DisposableSchemaDatabase.CreateAtPlatformMigrationAsync(schema.Postgres,
            BeforeExternalPolicy);
        await using var context = Platform(database);
        await context.GetService<IMigrator>().MigrateAsync(BeforeExternalPolicy);
        var job = await NewExternalOutcomeAsync(database, recordOutcome: false);
        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            await SchemaQueries.ExecuteAsync(app, transaction, """
                UPDATE background_job_attempts SET outcome='{}', outcome_kind='succeeded', outcome_at=clock_timestamp(),
                    quiescence_id=uuidv7(), quiescence_kind='provider_fenced', quiescent_at=clock_timestamp() WHERE id=@attempt;
                UPDATE background_jobs SET final_outcome_attempt_id=@attempt, finalization_status=@status,
                    finalization_reason=@reason, phase='finalize_pending' WHERE id=@job;
                UPDATE background_job_reservations SET released_at=clock_timestamp(), release_reason='quiescent'
                    WHERE job_id=@job AND attempt_id=@attempt AND @release;
                """, ("job", job.JobId), ("attempt", job.AttemptId), ("status", status), ("reason", reason), ("release", release));
            await transaction.CommitAsync();
        }
        var upgrade = async () => await context.Database.MigrateAsync();
        if (compatible) await upgrade();
        else
        {
            var refusal = (await upgrade.Should().ThrowAsync<PostgresException>()).Which;
            refusal.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
            refusal.MessageText.Should().Be("external retained history requires an explicit recovery design");
        }
        await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
        {
            (await SchemaQueries.CountAsync(owner, """
                SELECT count(*) FROM pg_attribute WHERE attrelid='background_job_attempts'::regclass
                    AND attname='failure_policy' AND NOT attisdropped
                """)).Should().Be(compatible ? 1 : 0);
            (await SchemaQueries.CountAsync(owner, """
                SELECT count(*) FROM pg_class WHERE relname IN ('background_jobs','background_job_attempts','background_job_reservations')
                    AND relrowsecurity AND relforcerowsecurity
                """)).Should().Be(3, "even a refused migration must restore every temporarily relaxed FORCE policy");
        }
        await using var verify = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var read = await verify.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(verify, read);
        await EducationSchemaSeed.AnnounceAsync(verify, read, SchemaFixture.TenantA, null);
        (await PolicyCountAsync(verify, """
            SELECT count(*) FROM background_jobs j JOIN background_job_attempts a
                ON a.tenant_id=j.tenant_id AND a.job_id=j.id AND a.id=j.final_outcome_attempt_id
            JOIN background_job_reservations r ON r.tenant_id=a.tenant_id AND r.job_id=a.job_id AND r.attempt_id=a.id
            WHERE j.id=@job AND a.id=@attempt AND j.finalization_status=@status AND j.finalization_reason=@reason
                AND a.outcome_kind='succeeded' AND a.outcome='{}' AND (r.released_at IS NOT NULL)=@release
            """, read, ("job", job.JobId), ("attempt", job.AttemptId), ("status", status), ("reason", reason),
            ("release", release))).Should().Be(1, "the independently applied predecessor accepted this exact history; upgrade may only refuse it");
    }

    [Theory]
    [InlineData("retired_definition", false, 0)]
    [InlineData("invalid_envelope", false, 0)]
    [InlineData("retired_definition", true, 1)]
    [InlineData("invalid_envelope", true, 1)]
    [InlineData("finalize_refused", true, 1)]
    [InlineData("finalize_exhausted", true, 6)]
    public async Task Prepared_control_terminal_request_and_claim_leave_incomplete_false_until_actual_terminal_write(
        string reason, bool select, int finalizeStarts)
    {
        // Storage-state proof only; the runtime suite separately owns the sixth
        // MUST audit and its witness. All decisive writes use the granted roles.
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var job = await PolicyOutcomeAsync(database, "succeeded", null, 1);
        if (select)
        {
            await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
            await using var transaction = await app.BeginTransactionAsync();
            await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            await SchemaQueries.ExecuteAsync(app, transaction, """
                UPDATE background_jobs SET final_outcome_attempt_id=@attempt, finalization_status='succeeded',
                    finalization_reason='none', phase='finalize_pending' WHERE id=@job
                """, ("job", job.JobId), ("attempt", job.AttemptId));
            await transaction.CommitAsync();
        }
        for (var ordinal = 1; ordinal <= finalizeStarts; ordinal++)
        {
            await AdmitCountedStageAsync(database, job, Guid.CreateVersion7(), "finalize", ordinal, job.AttemptId, default);
            if (ordinal != finalizeStarts) await RecoverStageAsync(database, job.JobId, "finalize_pending");
        }
        await using var control = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        (await SchemaQueries.ReadStringsAsync(control, "SELECT current_user")).Should().Equal("learnstack_outbox_admin");
        await SchemaQueries.ExecuteAsync(control, null, """
            UPDATE background_jobs SET phase='terminal_pending', terminal_reason=@reason WHERE id=@job;
            """, ("job", job.JobId), ("reason", reason));
        (await SchemaQueries.CountAsync(control, """
            SELECT count(*) FROM background_jobs WHERE phase='terminal_pending' AND dead_lettered_at IS NULL
                AND NOT business_finalization_incomplete AND ownership_token IS NULL
            """)).Should().Be(1, "a request is neither completed dead-letter state nor an incomplete-business audit claim");
        await SchemaQueries.ExecuteAsync(control, null, """
            UPDATE background_jobs SET ownership_token=uuidv7(), ownership_kind='recovery',
                ownership_until=clock_timestamp()+interval '1 minute' WHERE id=@job;
            """, ("job", job.JobId));
        (await SchemaQueries.CountAsync(control, """
            SELECT count(*) FROM background_jobs WHERE dead_lettered_at IS NULL AND NOT business_finalization_incomplete
                AND ownership_kind='recovery' AND ownership_token IS NOT NULL
            """)).Should().Be(1, "terminal claim must remain reachable under the existing state CHECK");
        await SchemaQueries.ExecuteAsync(control, null, """
            UPDATE background_jobs SET dead_lettered_at=clock_timestamp() WHERE id=@job;
            """, ("job", job.JobId));
        (await SchemaQueries.CountAsync(control, """
            SELECT count(*) FROM background_jobs WHERE dead_lettered_at IS NOT NULL AND business_finalization_incomplete
                AND ownership_token IS NULL
            """)).Should().Be(1, "the actual bypass terminal transition stamps incomplete together with its durable terminal timestamp");
    }
}
