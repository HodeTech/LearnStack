using FluentAssertions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobStorageMigrationTests
{
    [Fact]
    public async Task Six_quiescent_external_starts_and_six_finalize_starts_retain_twelve_receipts_and_refuse_a_seventh()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var job = await NewPendingJobAsync(database, "external");
        var operation = Guid.NewGuid();
        Guid finalOutcome = default;
        for (var ordinal = 1; ordinal <= 6; ordinal++)
        {
            var attempt = Guid.CreateVersion7();
            await AdmitCountedStageAsync(database, job, attempt, "normal", ordinal, finalOutcome, operation);
            finalOutcome = attempt;
            if (ordinal < 6) await RecoverStageAsync(database, job.JobId, "pending");
        }
        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            await SchemaQueries.ExecuteAsync(app, transaction, """
                UPDATE background_jobs SET final_outcome_attempt_id = @outcome, finalization_status = 'succeeded',
                    finalization_reason = 'none', phase = 'finalize_pending' WHERE id = @job
                """, ("outcome", finalOutcome), ("job", job.JobId));
            await transaction.CommitAsync();
        }
        for (var ordinal = 1; ordinal <= 6; ordinal++)
        {
            await AdmitCountedStageAsync(database, job, Guid.CreateVersion7(), "finalize", ordinal, finalOutcome, operation);
            await RecoverStageAsync(database, job.JobId, "finalize_pending");
        }
        await using var verify = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var verifyTransaction = await verify.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(verify, verifyTransaction, SchemaFixture.TenantA, null);
        (await SchemaQueries.CountAsync(verify, "SELECT count(*) FROM background_job_attempts", verifyTransaction)).Should().Be(12L);
        (await SchemaQueries.CountAsync(verify,
            "SELECT count(*) FROM background_job_attempts WHERE stage = 'normal' AND execute_intent_at IS NOT NULL AND quiescent_at IS NOT NULL",
            verifyTransaction)).Should().Be(6L, "every counted external start has durable positive quiescence");
        (await SchemaQueries.CountAsync(verify,
            "SELECT count(*) FROM background_jobs WHERE normal_attempts = 6 AND finalize_attempts = 6", verifyTransaction)).Should().Be(1L);
        (await SchemaQueries.CountAsync(verify,
            "SELECT count(*) FROM background_job_reservations WHERE released_at IS NOT NULL", verifyTransaction)).Should().Be(6L);
        await verifyTransaction.RollbackAsync();
        await AssertSeventhRefusedAsync(database, job.JobId, "finalize", finalOutcome);
    }

    [Fact]
    public async Task Sixth_normal_start_is_counted_and_a_seventh_cannot_create_a_receipt()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var job = await NewPendingJobAsync(database);
        for (var ordinal = 1; ordinal <= 6; ordinal++)
        {
            await AdmitCountedStageAsync(database, job, Guid.CreateVersion7(), "transactional", ordinal, default, default);
            await RecoverStageAsync(database, job.JobId, "pending");
        }
        await AssertSeventhRefusedAsync(database, job.JobId, "normal", null);
    }

    [Fact]
    public async Task Null_organization_recipient_routes_are_deduplicated_for_the_same_occurrence()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await BackgroundJobSchemaSeed.SeedRecipientsAsync(database.OutboxConnectionString);
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await using var transaction = await dispatcher.BeginTransactionAsync();
        var duplicate = async () => await SchemaQueries.ExecuteAsync(dispatcher, transaction, """
            INSERT INTO background_job_recipients (id, occurrence_id, tenant_id, organization_id, recurrence_key, revision,
                manifest_hash, target_definition_key, recipient_policy, scheduled_at, due_at)
            SELECT uuidv7(), occurrence_id, tenant_id, NULL, recurrence_key, revision, manifest_hash,
                target_definition_key, recipient_policy, scheduled_at, due_at
            FROM background_job_recipients WHERE tenant_id = @tenant AND organization_id IS NULL
            """, ("tenant", SchemaFixture.TenantA));
        var refusal = (await duplicate.Should().ThrowAsync<PostgresException>()).Which;
        refusal.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        refusal.ConstraintName.Should().Be("ux_background_job_recipients_route");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Parameter_utf8_cap_is_inclusive_and_refuses_an_extra_multibyte_character(bool oversized)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        var payload = "{\"id\":\"" + new string('ü', 131067) + (oversized ? "üü" : "x") + "\"}";
        var insert = async () => await BackgroundJobSchemaSeed.InsertJobAsync(app, transaction,
            Guid.CreateVersion7(), SchemaFixture.TenantA, null, parameters: payload);
        if (oversized)
            (await insert.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        else
        {
            await insert();
            (await SchemaQueries.CountAsync(app, "SELECT octet_length(parameters)::bigint FROM background_jobs", transaction))
                .Should().Be(262144L);
        }
    }

    private static async Task AdmitCountedStageAsync(DisposableSchemaDatabase database, PendingJob job, Guid attempt,
        string stage, int ordinal, Guid finalOutcome, Guid operation)
    {
        var token = ordinal == 1 && stage != "finalize" ? job.Token : Guid.NewGuid();
        if (ordinal != 1 || stage == "finalize")
            await BackgroundJobSchemaSeed.ClaimAsync(database.OutboxConnectionString, job.JobId, token);
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        var external = stage == "normal";
        var finalize = stage == "finalize";
        var deadline = BackgroundJobSchemaSeed.Microseconds(DateTimeOffset.UtcNow.AddMinutes(4));
        await SchemaQueries.ExecuteAsync(app, transaction, """
            INSERT INTO background_job_attempts (id, tenant_id, job_id, stage, ordinal, admission_token, deadline, final_outcome_attempt_id)
            VALUES (@attempt, @tenant, @job, @stage, @ordinal, @token, @deadline, @outcome);
            UPDATE background_jobs SET normal_attempts = CASE WHEN @finalize THEN normal_attempts ELSE @ordinal END,
                finalize_attempts = CASE WHEN @finalize THEN @ordinal ELSE finalize_attempts END,
                current_attempt_id = @attempt, attempt_deadline = @deadline, phase = @phase WHERE id = @job;
            """, ("attempt", attempt), ("tenant", SchemaFixture.TenantA), ("job", job.JobId),
            ("stage", finalize ? "finalize" : "normal"), ("ordinal", ordinal), ("token", token), ("deadline", deadline),
            ("outcome", finalize ? finalOutcome : DBNull.Value), ("finalize", finalize),
            ("phase", finalize ? "finalizing" : external ? ordinal == 1 ? "preparing" : "prepared" : "executing"));
        if (external)
        {
            if (ordinal == 1)
                await SchemaQueries.ExecuteAsync(app, transaction, """
                    INSERT INTO background_job_capacity (tenant_id, capacity_group) VALUES (@tenant, 'fixture.resource');
                    UPDATE background_jobs SET plan = '{}', plan_fingerprint = repeat('a',64), prepared_at = clock_timestamp(),
                        operation_id = @operation, phase = 'prepared' WHERE id = @job
                    """, ("tenant", SchemaFixture.TenantA), ("job", job.JobId), ("operation", operation));
            await SchemaQueries.ExecuteAsync(app, transaction, """
                INSERT INTO background_job_reservations (tenant_id, capacity_group, job_id, attempt_id)
                    VALUES (@tenant, 'fixture.resource', @job, @attempt);
                UPDATE background_job_attempts SET plan_fingerprint = repeat('a',64), operation_id = @operation,
                    execute_intent_at = clock_timestamp() WHERE id = @attempt;
                UPDATE background_jobs SET phase = 'executing' WHERE id = @job;
                UPDATE background_job_attempts SET outcome = '{}', outcome_kind = 'succeeded', outcome_at = clock_timestamp(),
                    quiescence_id = uuidv7(), quiescence_kind = 'provider_fenced', quiescent_at = clock_timestamp() WHERE id = @attempt;
                UPDATE background_job_reservations SET released_at = clock_timestamp(), release_reason = 'quiescent'
                    WHERE job_id = @job AND attempt_id = @attempt;
                """, ("tenant", SchemaFixture.TenantA), ("job", job.JobId), ("attempt", attempt), ("operation", operation));
        }
        await transaction.CommitAsync();
    }

    private static async Task RecoverStageAsync(DisposableSchemaDatabase database, Guid job, string nextPhase)
    {
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await SchemaQueries.ExecuteAsync(dispatcher, null, """
            UPDATE background_jobs SET ownership_until = clock_timestamp() - interval '1 second' WHERE id = @job;
            UPDATE background_jobs SET phase = 'reconciling', ownership_kind = 'recovery', ownership_token = uuidv7(),
                ownership_until = clock_timestamp() + interval '1 minute' WHERE id = @job;
            UPDATE background_jobs SET phase = @phase, ownership_kind = NULL, ownership_token = NULL, ownership_until = NULL WHERE id = @job;
            """, ("job", job), ("phase", nextPhase));
    }

    private static async Task AssertSeventhRefusedAsync(DisposableSchemaDatabase database, Guid job, string stage, Guid? outcome)
    {
        var token = Guid.NewGuid();
        await BackgroundJobSchemaSeed.ClaimAsync(database.OutboxConnectionString, job, token);
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        var seventh = async () => await SchemaQueries.ExecuteAsync(app, transaction, """
            INSERT INTO background_job_attempts (id, tenant_id, job_id, stage, ordinal, admission_token, deadline, final_outcome_attempt_id)
            VALUES (uuidv7(), @tenant, @job, @stage, 7, @token, clock_timestamp() + interval '30 seconds', @outcome)
            """, ("tenant", SchemaFixture.TenantA), ("job", job), ("stage", stage), ("token", token),
            ("outcome", (object?)outcome ?? DBNull.Value));
        var refusal = (await seventh.Should().ThrowAsync<PostgresException>()).Which;
        refusal.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        refusal.ConstraintName.Should().Be("ck_background_job_attempts_identity",
            "the real fresh routing receipt must reach the ordinal ceiling rather than fail on an unrelated missing privilege or stale state");
    }
}
