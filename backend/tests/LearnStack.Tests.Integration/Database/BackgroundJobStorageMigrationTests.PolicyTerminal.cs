using FluentAssertions;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobStorageMigrationTests
{
    [Theory]
    [InlineData("failed", "permanent", 1, "external_failed", "permanent_failure", "retry_exhausted")]
    [InlineData("failed", "transient_not_repeatable", 1, "external_failed", "external_retry_forbidden", "permanent_failure")]
    [InlineData("cancelled", "transient_not_repeatable", 1, "external_cancelled", "external_retry_forbidden", "retry_exhausted")]
    [InlineData("failed", "transient_repeatable", 6, "normal_attempts_exhausted", "retry_exhausted", "external_retry_forbidden")]
    public async Task Ordinary_terminal_policy_requires_actual_app_finalization_and_rejects_reason_substitution(
        string kind, string policy, int normalStarts, string selectionReason, string terminalReason, string wrongReason)
    {
        // This isolates the storage boundary, not the business finalizer or audit witness.
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var job = await SelectedPolicyFailureAsync(database, kind, policy, normalStarts, selectionReason);
        await AdmitCountedStageAsync(database, job, Guid.CreateVersion7(), "finalize", 1, job.AttemptId, default);
        const string TerminalSql = """
            UPDATE background_jobs SET phase='terminal_pending', terminal_reason=@reason, dead_lettered_at=clock_timestamp()
                WHERE id=@job
            """;
        await using (var control = await PostgresFixture.OpenAsync(database.OutboxConnectionString))
        {
            (await SchemaQueries.ReadStringsAsync(control, "SELECT current_user")).Should().Equal("learnstack_outbox_admin");
            var bypass = async () => await SchemaQueries.ExecuteAsync(control, null, TerminalSql,
                ("job", job.JobId), ("reason", terminalReason));
            var refused = (await bypass.Should().ThrowAsync<PostgresException>()).Which;
            refused.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
            refused.MessageText.Should().Be("ordinary terminal policy requires live app failure finalization");
        }
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            var substitute = async () => await SchemaQueries.ExecuteAsync(app, transaction, TerminalSql,
                ("job", job.JobId), ("reason", wrongReason));
            var refused = (await substitute.Should().ThrowAsync<PostgresException>()).Which;
            refused.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
            refused.MessageText.Should().Be("ordinary terminal policy requires live app failure finalization");
        }
        await using (var positive = await app.BeginTransactionAsync())
        {
            await SchemaQueries.AssertApplicationRoleAsync(app, positive);
            await EducationSchemaSeed.AnnounceAsync(app, positive, SchemaFixture.TenantA, null);
            await SchemaQueries.ExecuteAsync(app, positive, TerminalSql, ("job", job.JobId), ("reason", terminalReason));
            await positive.CommitAsync();
        }
        await using var read = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, read, SchemaFixture.TenantA, null);
        (await PolicyCountAsync(app, """
            SELECT count(*) FROM background_jobs WHERE id=@job AND normal_attempts=@normal AND finalize_attempts=1
                AND terminal_reason=@reason AND dead_lettered_at IS NOT NULL AND NOT business_finalization_incomplete
            """, read, ("job", job.JobId), ("normal", normalStarts), ("reason", terminalReason))).Should().Be(1,
            "the same exact selected facts reach their intended terminal projection under live app finalization");
    }

    [Fact]
    public async Task A_pending_bypass_reason_cannot_be_rewritten_into_ordinary_completed_failure()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var job = await SelectedPolicyFailureAsync(database, "failed", "permanent", 1, "external_failed");
        await AdmitCountedStageAsync(database, job, Guid.CreateVersion7(), "finalize", 1, job.AttemptId, default);
        await using var control = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await SchemaQueries.ExecuteAsync(control, null, """
            UPDATE background_jobs SET phase='terminal_pending', terminal_reason='finalize_refused' WHERE id=@job;
            UPDATE background_jobs SET ownership_token=uuidv7(), ownership_kind='recovery',
                ownership_until=clock_timestamp()+interval '1 minute' WHERE id=@job
            """, ("job", job.JobId));
        var rewrite = async () => await SchemaQueries.ExecuteAsync(control, null, """
            UPDATE background_jobs SET terminal_reason='permanent_failure', dead_lettered_at=clock_timestamp() WHERE id=@job
            """, ("job", job.JobId));
        var refused = (await rewrite.Should().ThrowAsync<PostgresException>()).Which;
        refused.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        refused.MessageText.Should().Be("a terminal request retains its original reason");
        await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
            await SchemaQueries.ExecuteAsync(owner, null, "DROP TRIGGER tg_background_jobs_transition ON background_jobs");
        await SchemaQueries.ExecuteAsync(control, null, """
            UPDATE background_jobs SET terminal_reason='permanent_failure', dead_lettered_at=clock_timestamp(),
                ownership_token=NULL, ownership_kind=NULL, ownership_until=NULL WHERE id=@job
            """, ("job", job.JobId));
        (await SchemaQueries.CountAsync(control, """
            SELECT count(*) FROM background_jobs WHERE terminal_reason='permanent_failure' AND dead_lettered_at IS NOT NULL
            """)).Should().Be(1, "removing only the transition guard exposes the same otherwise well-shaped control substitution");
    }

    [Theory]
    [InlineData("permanent")]
    [InlineData("transient_repeatable")]
    [InlineData("transient_not_repeatable")]
    public async Task Policy_down_refuses_every_governed_failure_and_preserves_grants_and_FORCE(string policy)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var job = await PolicyOutcomeAsync(database, "failed", policy, 1);
        await using var context = Platform(database);
        var reverse = async () => await context.GetService<IMigrator>().MigrateAsync(BeforeExternalPolicy);
        var refused = (await reverse.Should().ThrowAsync<PostgresException>()).Which;
        refused.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        refused.MessageText.Should().Be("external failure-policy history cannot be discarded by reversal");
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var read = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, read);
        await EducationSchemaSeed.AnnounceAsync(app, read, SchemaFixture.TenantA, null);
        (await PolicyCountAsync(app, "SELECT count(*) FROM background_job_attempts WHERE id=@attempt AND failure_policy=@policy",
            read, ("attempt", job.AttemptId), ("policy", policy))).Should().Be(1);
        (await PolicyCountAsync(app, """
            SELECT count(*) FROM pg_class WHERE relname IN ('background_jobs','background_job_attempts') AND relforcerowsecurity
            """, read)).Should().Be(2);
        (await PolicyCountAsync(app, """
            SELECT count(*) WHERE has_column_privilege('learnstack_app','background_job_attempts','failure_policy','UPDATE')
                AND has_column_privilege('learnstack_outbox_admin','background_job_attempts','failure_policy','SELECT')
            """, read)).Should().Be(1, "a refused reverse cannot partially revoke the policy projection or checkpoint grant");
    }

    private static async Task<PendingJob> SelectedPolicyFailureAsync(DisposableSchemaDatabase database,
        string kind, string policy, int normalStarts, string reason)
    {
        var job = await PolicyOutcomeAsync(database, kind, policy, normalStarts);
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        await SchemaQueries.ExecuteAsync(app, transaction, """
            UPDATE background_jobs SET final_outcome_attempt_id=@attempt, finalization_status='failed',
                finalization_reason=@reason, phase='finalize_pending' WHERE id=@job
            """, ("job", job.JobId), ("attempt", job.AttemptId), ("reason", reason));
        await transaction.CommitAsync();
        return job;
    }
}
