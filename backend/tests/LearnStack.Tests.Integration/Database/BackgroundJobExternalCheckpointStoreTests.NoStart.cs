using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobExternalCheckpointStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sixth_reused_plan_admission_stopped_before_arm_commits_no_start_release_and_failure_selection(bool alreadyRecorded)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external");
        await using var host = RecoveryHost(database, preparing: true);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        for (var ordinal = 1; ordinal <= 6; ordinal++)
        {
            var admitted = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
            admitted.NormalAttemptOrdinal.Should().Be(ordinal);
            if (ordinal == 1)
            {
                var prepared = await host.Services.GetRequiredService<JobExecutor>().ExecuteBusinessAsync(admitted,
                    admitted.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare));
                prepared.CommitAcknowledged.Should().BeTrue();
            }
            else
            {
                await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
                await using var transaction = await app.BeginTransactionAsync();
                await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
                await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
                await using var command = new NpgsqlCommand("""
                    SELECT plan_fingerprint IS NULL AND operation_id IS NULL AND execute_intent_at IS NULL
                        AND no_start_at IS NULL AND outcome IS NULL
                    FROM background_job_attempts WHERE id=@attempt
                    """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction);
                command.Parameters.AddWithValue("attempt", admitted.AttemptId.Value);
                (await command.ExecuteScalarAsync()).Should().Be(true, "reuse admission must not invent attempt checkpoints");
            }
            admitted.Run.Close();
            await using (var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString))
                await SchemaQueries.ExecuteAsync(dispatcher, null, """
                    UPDATE background_jobs SET ownership_until=clock_timestamp() WHERE id=@job;
                    UPDATE background_jobs SET phase='reconciling',ownership_token=NULL,ownership_kind=NULL,ownership_until=NULL,probe_after=clock_timestamp()
                        WHERE id=@job AND ownership_until<=clock_timestamp()
                    """, ("job", job.Value));
            var acquired = await store.AcquireExternalRecoveryAsync(job, await RecoveryGenerationAsync(database, job),
                admitted.Envelope.Queue, CancellationToken.None);
            acquired.ThrowIfFailed();
            var recovery = BindRecovery(acquired, runtime);
            var snapshot = await ReadRecoveryAsync(host.Services, recovery);
            snapshot.PlanJson.Should().NotBeNull();
            snapshot.ExecuteIntentRecorded.Should().BeFalse();
            if (alreadyRecorded && ordinal == 6)
            {
                // Legal retained history from the prior no-start writer: the
                // immutable job plan exists, but this attempt was never stamped.
                await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
                await using var transaction = await app.BeginTransactionAsync();
                await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
                await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
                await SchemaQueries.ExecuteAsync(app, transaction, """
                    UPDATE background_job_attempts SET no_start_at=clock_timestamp() WHERE id=@attempt;
                    UPDATE background_job_reservations SET released_at=clock_timestamp(),release_reason='never_started'
                        WHERE attempt_id=@attempt AND released_at IS NULL
                    """, ("attempt", admitted.AttemptId.Value));
                await transaction.CommitAsync();
            }
            var gate = recovery.Run.OpenBoundary(BackgroundJobCheckpointKind.NoStart);
            var commandNoStart = RecordBackgroundJobOutcomeCommand.ForNoStart(recovery, gate);
            (await SendRecoveryAsync(host.Services, recovery, commandNoStart, gate)).IsSuccess.Should().BeTrue();
            gate.CommitAcknowledged.Should().BeTrue();
            await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
            await using (var transaction = await app.BeginTransactionAsync())
            {
                await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
                await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
                await using var command = new NpgsqlCommand("""
                    SELECT j.phase,j.normal_attempts,j.finalize_attempts,j.final_outcome_attempt_id,j.finalization_status,
                        j.finalization_reason,a.no_start_at IS NOT NULL,r.released_at IS NOT NULL,r.release_reason,
                        a.execute_intent_at IS NULL AND a.outcome IS NULL AND a.failure_policy IS NULL,
                        a.plan_fingerprint=j.plan_fingerprint AND a.operation_id=j.operation_id,
                        (SELECT count(*) FROM background_job_attempts all_attempts WHERE all_attempts.job_id=j.id),
                        j.ownership_token IS NULL AND j.ownership_kind IS NULL AND j.ownership_until IS NULL,
                        j.wake_generation
                    FROM background_jobs j JOIN background_job_attempts a ON a.id=j.current_attempt_id
                        JOIN background_job_reservations r ON r.attempt_id=a.id WHERE j.id=@job
                    """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction);
                command.Parameters.AddWithValue("job", job.Value);
                await using var row = await command.ExecuteReaderAsync();
                (await row.ReadAsync()).Should().BeTrue();
                row.GetString(0).Should().Be(ordinal == 6 ? "finalize_pending" : "reconciling");
                row.GetInt32(1).Should().Be(ordinal);
                row.GetInt32(2).Should().Be(0);
                if (ordinal == 6)
                {
                    row.GetGuid(3).Should().Be(admitted.AttemptId.Value);
                    row.GetString(4).Should().Be("failed");
                    row.GetString(5).Should().Be("normal_attempts_exhausted");
                }
                else row.IsDBNull(3).Should().BeTrue();
                row.GetBoolean(6).Should().BeTrue();
                row.GetBoolean(7).Should().BeTrue();
                row.GetString(8).Should().Be("never_started");
                row.GetBoolean(9).Should().BeTrue("no provider fact or ExecuteIntent is fabricated");
                if (ordinal == 6) row.GetBoolean(10).Should().BeTrue("the selected attempt must carry the immutable plan identity");
                row.GetInt64(11).Should().Be(ordinal);
                if (ordinal == 6)
                {
                    row.GetBoolean(12).Should().BeTrue("finalization eligibility revokes the non-executing recovery token");
                    row.GetInt64(13).Should().Be(acquired.Value!.WakeGeneration + 1);
                }
            }
            if (ordinal == 6)
            {
                var selected = await ReadStoredSelectionAsync(database, admitted);
                selected.SelectedNormalAttemptId.Should().Be(admitted.AttemptId);
                selected.FinalizationStatus.Should().Be(BackgroundJobFinalizationStatus.Failed);
                selected.FinalizationReason.Should().Be(BackgroundJobFinalizationReason.NormalAttemptsExhausted);
                selected.NoStartRecorded.Should().BeTrue();
                selected.OutcomeState.Should().BeNull();
                selected.FailurePolicy.Should().BeNull();
                var permanent = await ReadStoredSelectionAsync(database, admitted,
                    (admitted.AttemptId.Value, "failed", "permanent_failure"));
                permanent.FinalizationReason.Should().Be(BackgroundJobFinalizationReason.PermanentFailure,
                    "a stored no-start permanent selection remains distinct from budget exhaustion even at six");
            }
            if (ordinal < 6)
            {
                await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
                await SchemaQueries.ExecuteAsync(dispatcher, null, """
                    UPDATE background_jobs SET phase='pending',ownership_kind=NULL,ownership_token=NULL,ownership_until=NULL
                        WHERE id=@job AND phase='reconciling'
                    """, ("job", job.Value));
            }
        }
    }
}
