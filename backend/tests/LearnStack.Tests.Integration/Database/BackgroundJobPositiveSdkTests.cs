using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Natural real-store SDK/provider flow. Construction-only host does not claim native Hangfire handoff.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed partial class BackgroundJobPositiveSdkTests(SchemaFixture schema, Xunit.Abstractions.ITestOutputHelper output)
{
    private static BackgroundJobPositiveSdkFixture Fixture => BackgroundJobPositiveSdkFixture.Shared;

    [Theory]
    [InlineData("succeeded", null)]
    [InlineData("permanent", "permanent_failure")]
    [InlineData("not_repeatable", "external_retry_forbidden")]
    [InlineData("cancelled_not_repeatable", "external_retry_forbidden")]
    public async Task Genuine_sdk_and_provider_preserve_one_logical_effect_and_commit_domain_finalization_with_its_real_MUST_audits(
        string mode, string? terminalCode)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        var resource = Guid.CreateVersion7();
        var preparedId = OrganizationId.From(Guid.CreateVersion7());
        var finalizedId = OrganizationId.From(Guid.CreateVersion7());
        await InstallTransactionObserverAsync(database, preparedId, finalizedId);
        await provider.SetModeAsync(resource, mode);
        var construction = new BackgroundJobPositiveProviderConstructionProbe();
        await using var host = Fixture.Host(database, provider.Settings, construction);
        var submitted = await Fixture.SubmitAsync(host.Services, Fixture.Parameters(resource, preparedId, finalizedId));
        submitted.IsSuccess.Should().BeTrue();
        var job = submitted.Value;
        var executor = host.Services.GetRequiredService<JobExecutor>();
        using var metrics = new BackgroundJobStageProbe(host.Services.GetRequiredService<BackgroundJobObservation>());
        var claim = await host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>().TryClaimAsync(job,
            await GenerationAsync(database, job));
        claim.Should().NotBeNull();
        var started = await executor.ExecuteAsync(claim!, BackgroundJobTestRun.Create());
        started.Outcome.Should().Be(JobStartExecutionOutcome.Started);
        started.CleanupFailed.Should().BeFalse();
        var normal = started.Receipt!;
        normal.Stage.Should().Be(BackgroundJobExecutionStage.Prepare);
        normal.NormalAttemptOrdinal.Should().Be(1);
        var prepared = await executor.ExecuteBusinessAsync(normal, normal.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare));
        prepared.CommitAcknowledged.Should().BeTrue();
        prepared.CleanupFailed.Should().BeFalse();
        (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, preparedId.Value)).Should().Be(1);
        (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, finalizedId.Value)).Should().Be(0);
        construction.Calls.Should().Be(0, "Prepare and admission construct no external adapter");
        var read = await executor.ReadExternalStateAsync(normal, CancellationToken.None);
        read.Handle.Should().NotBeNull();
        var snapshot = read.Handle!.Require(normal);
        var operation = snapshot.OperationId!.Value;
        (await provider.CountsAsync(operation)).Should().Be((0L, 0L, 0L));
        var arm = await executor.RecordExternalArmAsync(normal, read.Handle,
            normal.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm), CancellationToken.None);
        arm.CommitAcknowledged.Should().BeTrue();
        arm.CleanupFailed.Should().BeFalse();
        var executed = await executor.ExecuteExternalAsync(read.Handle, arm, CancellationToken.None);
        executed.Status.Should().Be(JobExternalExecutionStatus.Reported);
        executed.ProviderIssued.Should().BeTrue();
        executed.CleanupFailed.Should().BeFalse();
        executed.Observation.Should().NotBeNull();
        executed.Observation!.IsCompleted.Should().BeTrue();
        construction.Calls.Should().Be(1);
        construction.ExternalDenial.Should().BeTrue();
        var effectCount = terminalCode is null ? 1L : 0L;
        (await provider.CountsAsync(operation)).Should().Be((1L, 0L, effectCount));
        var outcome = await executor.RecordExternalOutcomeAsync(normal, read.Handle,
            normal.Run.OpenBoundary(BackgroundJobCheckpointKind.Outcome), executed.Observation, CancellationToken.None);
        outcome.CommitAcknowledged.Should().BeTrue();
        outcome.CleanupFailed.Should().BeFalse();

        var finalClaim = await host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>().TryClaimAsync(job,
            await GenerationAsync(database, job));
        finalClaim.Should().NotBeNull();
        finalClaim!.LedgerStage.Should().Be(BackgroundJobLedgerStage.Finalize);
        var finalStart = await executor.ExecuteAsync(finalClaim, BackgroundJobTestRun.Create());
        finalStart.Outcome.Should().Be(JobStartExecutionOutcome.Started);
        var final = finalStart.Receipt!;
        final.Stage.Should().Be(BackgroundJobExecutionStage.Finalize);
        final.SelectedNormalAttemptId.Should().Be(normal.AttemptId);
        final.AttemptId.Should().NotBe(normal.AttemptId);
        final.NormalAttemptOrdinal.Should().Be(1);
        final.FinalizeAttemptOrdinal.Should().Be(1);
        var finalized = await executor.ExecuteBusinessAsync(final, final.Run.OpenBoundary(BackgroundJobCheckpointKind.Finalize));
        finalized.CommitAcknowledged.Should().BeTrue();
        finalized.CleanupFailed.Should().BeFalse();
        metrics.Duration("prepare").Tags["outcome"].Should().Be("Acknowledged");
        metrics.Duration("finalize").Tags["outcome"].Should().Be("Acknowledged",
            "acknowledged failure finalization is a successful finalize stage");
        metrics.Duration("execute").Tags["outcome"].Should().Be(mode == "succeeded" ? "ProviderSucceeded"
            : mode == "cancelled_not_repeatable" ? "ProviderCancelledNotRepeatable" : "ProviderFailed");
        metrics.Points.Count(point => point.Name == "learnstack_job_failed_total").Should().Be(mode is "permanent" or "not_repeatable" ? 1 : 0);
        metrics.Points.Should().NotContain(point => point.Name == "learnstack_job_unknown_outcome_total");
        (await provider.CountsAsync(operation)).Should().Be((1L, 0L, effectCount), "Finalize never invokes Execute or Reconcile");
        construction.Calls.Should().Be(1);
        (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, finalizedId.Value)).Should().Be(1);

        await using var connection = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(connection, transaction);
        await EducationSchemaSeed.AnnounceAsync(connection, transaction, SchemaFixture.TenantA, null);
        await using var command = new NpgsqlCommand("""
            SELECT j.phase,j.normal_attempts,j.finalize_attempts,j.final_outcome_attempt_id,j.current_attempt_id,
                j.terminal_reason,j.business_finalization_incomplete,
                (SELECT count(*) FROM background_job_reservations r WHERE r.job_id=j.id),
                (SELECT bool_and(r.released_at IS NOT NULL) FROM background_job_reservations r WHERE r.job_id=j.id),
                (SELECT count(*) FROM audit_log a WHERE a.operation='tenancy.organization.create' AND a.entity_id=@prepared),
                (SELECT count(*) FROM audit_log a WHERE a.operation='tenancy.organization.create' AND a.entity_id=@finalized),
                (SELECT count(*) FROM audit_log a WHERE a.operation='platform.job.dead_letter' AND a.metadata->>'job_id'=@job_text)
            FROM background_jobs j WHERE j.id=@job
            """, (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        command.Parameters.AddWithValue("job", job.Value);
        command.Parameters.AddWithValue("job_text", job.Value.ToString("D"));
        command.Parameters.AddWithValue("prepared", preparedId.Value.ToString("D"));
        command.Parameters.AddWithValue("finalized", finalizedId.Value.ToString("D"));
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        row.GetString(0).Should().Be(terminalCode is null ? "completed" : "terminal_pending");
        row.GetInt32(1).Should().Be(1);
        row.GetInt32(2).Should().Be(1);
        row.GetGuid(3).Should().Be(normal.AttemptId.Value);
        row.GetGuid(4).Should().Be(final.AttemptId.Value);
        (row.IsDBNull(5) ? null : row.GetString(5)).Should().Be(terminalCode);
        row.GetBoolean(6).Should().BeFalse();
        row.GetInt64(7).Should().Be(1);
        row.GetBoolean(8).Should().BeTrue();
        row.GetInt64(9).Should().Be(1);
        row.GetInt64(10).Should().Be(1);
        row.GetInt64(11).Should().Be(terminalCode is null ? 0 : 1);
        await row.DisposeAsync();
        await AssertObservedTransactionsAsync(connection, transaction, terminalCode is not null);
    }

    private static async Task<long> GenerationAsync(DisposableSchemaDatabase database, BackgroundJobId job, Guid? tenant = null)
    {
        await using var connection = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(connection, transaction);
        await EducationSchemaSeed.AnnounceAsync(connection, transaction, tenant ?? SchemaFixture.TenantA, null);
        await using var command = new NpgsqlCommand("SELECT wake_generation FROM background_jobs WHERE id=@id",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        command.Parameters.AddWithValue("id", job.Value);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
