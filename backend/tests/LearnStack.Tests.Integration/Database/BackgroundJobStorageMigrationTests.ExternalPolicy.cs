using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobStorageMigrationTests
{
    private const string BeforeExternalPolicy = "20261016210528_add_transactional_job_capacity_cleanup";

    [Theory]
    [InlineData("succeeded", null, true)]
    [InlineData("succeeded", "permanent", false)]
    [InlineData("failed", null, false)]
    [InlineData("failed", "permanent", true)]
    [InlineData("failed", "transient_repeatable", true)]
    [InlineData("failed", "transient_not_repeatable", true)]
    [InlineData("failed", "undefined", false)]
    [InlineData("cancelled", null, false)]
    [InlineData("cancelled", "permanent", false)]
    [InlineData("cancelled", "transient_repeatable", true)]
    [InlineData("cancelled", "transient_not_repeatable", true)]
    [InlineData(null, "permanent", false)]
    public async Task External_failure_policy_shape_is_explicit_and_SQL_NULL_cannot_bypass_it(
        string? kind, string? policy, bool valid)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var job = await NewExternalOutcomeAsync(database, recordOutcome: false);
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        var checkpoint = async () => await WritePolicyOutcomeAsync(app, transaction, job, kind, policy);
        if (!valid)
        {
            var refused = (await checkpoint.Should().ThrowAsync<PostgresException>()).Which;
            refused.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
            refused.ConstraintName.Should().Be("ck_background_job_attempts_failure_policy");
            await transaction.RollbackAsync();
            await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
                await SchemaQueries.ExecuteAsync(owner, null,
                    "ALTER TABLE background_job_attempts DROP CONSTRAINT ck_background_job_attempts_failure_policy");
            await using var dirty = await app.BeginTransactionAsync();
            await SchemaQueries.AssertApplicationRoleAsync(app, dirty);
            await EducationSchemaSeed.AnnounceAsync(app, dirty, SchemaFixture.TenantA, null);
            await WritePolicyOutcomeAsync(app, dirty, job, kind, policy);
            await SchemaQueries.ExecuteAsync(app, dirty, "SET CONSTRAINTS ALL IMMEDIATE");
            (await PolicyCountAsync(app, """
                SELECT count(*) FROM background_job_attempts WHERE id=@attempt
                    AND outcome_kind IS NOT DISTINCT FROM @kind AND failure_policy IS NOT DISTINCT FROM @policy
                """, dirty, ("attempt", job.AttemptId), ("kind", (object?)kind ?? DBNull.Value),
                ("policy", (object?)policy ?? DBNull.Value))).Should().Be(1,
                    "removing only the policy CHECK must expose this same otherwise reachable shape attack");
        }
        else
        {
            await checkpoint();
            await transaction.CommitAsync();
            await using var read = await app.BeginTransactionAsync();
            await EducationSchemaSeed.AnnounceAsync(app, read, SchemaFixture.TenantA, null);
            (await PolicyCountAsync(app, """
                SELECT count(*) FROM background_job_attempts WHERE id=@attempt AND outcome_kind=@kind
                    AND failure_policy IS NOT DISTINCT FROM @policy
                """, read, ("attempt", job.AttemptId), ("kind", kind!), ("policy", (object?)policy ?? DBNull.Value))).Should().Be(1);
            (await PolicyCountAsync(app, """
                SELECT count(*) FROM background_job_reservations WHERE job_id=@job AND attempt_id=@attempt
                    AND released_at IS NOT NULL AND release_reason='quiescent'
                """, read, ("job", job.JobId), ("attempt", job.AttemptId))).Should().Be(1);
        }
    }

    [Theory]
    [InlineData("failed", "permanent", 1, "failed", "external_failed", true)]
    [InlineData("failed", "transient_not_repeatable", 1, "failed", "external_failed", true)]
    [InlineData("cancelled", "transient_not_repeatable", 1, "failed", "external_cancelled", true)]
    [InlineData("failed", "transient_repeatable", 1, "failed", "external_failed", false)]
    [InlineData("failed", "transient_repeatable", 1, "failed", "normal_attempts_exhausted", false)]
    [InlineData("failed", "transient_repeatable", 6, "failed", "normal_attempts_exhausted", true)]
    [InlineData("cancelled", "transient_repeatable", 6, "failed", "normal_attempts_exhausted", true)]
    [InlineData("failed", "permanent", 6, "failed", "external_failed", true)]
    [InlineData("failed", "permanent", 6, "failed", "normal_attempts_exhausted", false)]
    [InlineData("cancelled", "transient_not_repeatable", 6, "failed", "external_cancelled", true)]
    [InlineData("cancelled", "transient_not_repeatable", 6, "failed", "normal_attempts_exhausted", false)]
    [InlineData("succeeded", null, 1, "succeeded", "none", true)]
    [InlineData("succeeded", null, 1, "failed", "external_failed", false)]
    [InlineData("failed", "permanent", 1, "failed", "external_cancelled", false)]
    public async Task External_finalization_selection_matches_actual_normal_ordinal_and_policy(
        string kind, string? policy, int ordinal, string status, string reason, bool valid)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var job = await PolicyOutcomeAsync(database, kind, policy, ordinal);
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        const string SelectionSql = """
            UPDATE background_jobs SET final_outcome_attempt_id=@attempt, finalization_status=@status,
                finalization_reason=@reason, phase='finalize_pending' WHERE id=@job;
            """;
        var select = async () => await SchemaQueries.ExecuteAsync(app, transaction, SelectionSql,
            ("job", job.JobId), ("attempt", job.AttemptId), ("status", status), ("reason", reason));
        if (!valid)
        {
            (await select.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
            await transaction.RollbackAsync();
            await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
                await SchemaQueries.ExecuteAsync(owner, null,
                    "DROP TRIGGER tg_background_jobs_transition ON background_jobs");
            await using var dirty = await app.BeginTransactionAsync();
            await SchemaQueries.AssertApplicationRoleAsync(app, dirty);
            await EducationSchemaSeed.AnnounceAsync(app, dirty, SchemaFixture.TenantA, null);
            await SchemaQueries.ExecuteAsync(app, dirty, SelectionSql, ("job", job.JobId), ("attempt", job.AttemptId),
                ("status", status), ("reason", reason));
            await SchemaQueries.ExecuteAsync(app, dirty, "SET CONSTRAINTS ALL IMMEDIATE");
            (await PolicyCountAsync(app, """
                SELECT count(*) FROM background_jobs WHERE id=@job AND final_outcome_attempt_id=@attempt
                    AND finalization_status=@status AND finalization_reason=@reason
                """, dirty, ("job", job.JobId), ("attempt", job.AttemptId), ("status", status),
                ("reason", reason))).Should().Be(1, "the same policy substitution succeeds only without its transition guard");
        }
        else
        {
            await select();
            await transaction.CommitAsync();
            await using var read = await app.BeginTransactionAsync();
            await EducationSchemaSeed.AnnounceAsync(app, read, SchemaFixture.TenantA, null);
            (await PolicyCountAsync(app, """
                SELECT count(*) FROM background_jobs WHERE id=@job AND final_outcome_attempt_id=@attempt
                    AND finalization_status=@status AND finalization_reason=@reason AND normal_attempts=@ordinal
                """, read, ("job", job.JobId), ("attempt", job.AttemptId), ("status", status),
                ("reason", reason), ("ordinal", ordinal))).Should().Be(1);
        }
    }

    [Theory]
    [InlineData("permanent")]
    [InlineData("transient_not_repeatable")]
    public async Task External_policy_cannot_be_rewritten_but_the_removed_trigger_control_exposes_the_attack(string replacement)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var job = await PolicyOutcomeAsync(database, "failed", "transient_repeatable", 1);
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            var rewrite = async () => await SchemaQueries.ExecuteAsync(app, transaction,
                "UPDATE background_job_attempts SET failure_policy=@policy WHERE id=@attempt",
                ("attempt", job.AttemptId), ("policy", replacement));
            var refusal = (await rewrite.Should().ThrowAsync<PostgresException>()).Which;
            refusal.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
            refusal.MessageText.Should().Be("attempt identity and recorded evidence are immutable");
        }
        await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
            await SchemaQueries.ExecuteAsync(owner, null,
                "DROP TRIGGER tg_background_job_attempts_guard ON background_job_attempts");
        await using var dirty = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, dirty);
        await EducationSchemaSeed.AnnounceAsync(app, dirty, SchemaFixture.TenantA, null);
        await SchemaQueries.ExecuteAsync(app, dirty, "UPDATE background_job_attempts SET failure_policy=@policy WHERE id=@attempt",
            ("attempt", job.AttemptId), ("policy", replacement));
        await SchemaQueries.ExecuteAsync(app, dirty, "SET CONSTRAINTS ALL IMMEDIATE");
        (await PolicyCountAsync(app, "SELECT count(*) FROM background_job_attempts WHERE id=@attempt AND failure_policy=@policy",
            dirty, ("attempt", job.AttemptId), ("policy", replacement))).Should().Be(1);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("succeeded")]
    public async Task External_policy_migration_refuses_legacy_failure_history_without_guessing_and_preserves_success(string kind)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var context = Platform(database);
        await context.GetService<IMigrator>().MigrateAsync(BeforeExternalPolicy);
        var job = await NewExternalOutcomeAsync(database, recordOutcome: false);
        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            await SchemaQueries.ExecuteAsync(app, transaction, """
                UPDATE background_job_attempts SET outcome='{}', outcome_kind=@kind, outcome_at=clock_timestamp(),
                    quiescence_id=uuidv7(), quiescence_kind='provider_fenced', quiescent_at=clock_timestamp() WHERE id=@attempt;
                UPDATE background_job_reservations SET released_at=clock_timestamp(), release_reason='quiescent'
                    WHERE job_id=@job AND attempt_id=@attempt;
                """, ("job", job.JobId), ("attempt", job.AttemptId), ("kind", kind));
            await transaction.CommitAsync();
        }
        var upgrade = async () => await context.Database.MigrateAsync();
        if (kind == "succeeded") await upgrade();
        else
        {
            var refusal = (await upgrade.Should().ThrowAsync<PostgresException>()).Which;
            refusal.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
            refusal.MessageText.Should().Be("external completed-failure history requires an explicit recovery design");
        }
        await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        (await SchemaQueries.CountAsync(owner, """
            SELECT count(*) FROM pg_attribute WHERE attrelid='background_job_attempts'::regclass
                AND attname='failure_policy' AND NOT attisdropped
            """)).Should().Be(kind == "succeeded" ? 1 : 0);
        (await SchemaQueries.ReadStringsAsync(owner, """
            SELECT relforcerowsecurity::text FROM pg_class WHERE oid='background_job_attempts'::regclass
            """)).Should().Equal("true");
        await using var verifyApp = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var verifyTransaction = await verifyApp.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(verifyApp, verifyTransaction);
        await EducationSchemaSeed.AnnounceAsync(verifyApp, verifyTransaction, SchemaFixture.TenantA, null);
        (await PolicyCountAsync(verifyApp, """
            SELECT count(*) FROM background_job_attempts WHERE id=@attempt AND outcome_kind=@kind
                AND outcome='{}' AND quiescent_at IS NOT NULL
            """, verifyTransaction, ("attempt", job.AttemptId), ("kind", kind))).Should().Be(1,
                "a refused upgrade must preserve the exact retained outcome rather than guess or discard history");
    }

    /// <summary>Applied schema fixtures, not executable runtime receipts or provider causality evidence.</summary>
    private static async Task<PendingJob> PolicyOutcomeAsync(DisposableSchemaDatabase database, string kind, string? policy, int ordinal)
    {
        var job = await NewPendingJobAsync(database, "external");
        var operation = Guid.NewGuid();
        for (var number = 1; number <= ordinal; number++)
        {
            var attempt = number == 1 ? job.AttemptId : Guid.CreateVersion7();
            await AdmitCountedStageAsync(database, job, attempt, "normal", number, default, operation,
                recordOutcome: number != ordinal);
            if (number != ordinal) await RecoverStageAsync(database, job.JobId, "pending");
            else job = job with { AttemptId = attempt };
        }
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        await WritePolicyOutcomeAsync(app, transaction, job, kind, policy);
        await transaction.CommitAsync();
        return job;
    }

    private static async Task<long> PolicyCountAsync(DbConnection app, string sql, DbTransaction transaction,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)app, (NpgsqlTransaction)transaction)
        { CommandTimeout = 5 };
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static Task WritePolicyOutcomeAsync(DbConnection app, DbTransaction transaction,
        PendingJob job, string? kind, string? policy) => SchemaQueries.ExecuteAsync(app, transaction, """
        UPDATE background_job_attempts SET outcome=CASE WHEN @kind::text IS NULL THEN NULL ELSE '{}' END,
            outcome_kind=@kind, failure_policy=@policy,
            outcome_at=CASE WHEN @kind::text IS NULL THEN NULL ELSE clock_timestamp() END,
            quiescence_id=CASE WHEN @kind::text IS NULL THEN NULL ELSE uuidv7() END,
            quiescence_kind=CASE WHEN @kind::text IS NULL THEN NULL ELSE 'provider_fenced' END,
            quiescent_at=CASE WHEN @kind::text IS NULL THEN NULL ELSE clock_timestamp() END WHERE id=@attempt;
        UPDATE background_job_reservations SET released_at=clock_timestamp(), release_reason='quiescent'
            WHERE job_id=@job AND attempt_id=@attempt AND @kind::text IS NOT NULL;
        """, ("job", job.JobId), ("attempt", job.AttemptId), ("kind", (object?)kind ?? DBNull.Value),
        ("policy", (object?)policy ?? DBNull.Value));
}
