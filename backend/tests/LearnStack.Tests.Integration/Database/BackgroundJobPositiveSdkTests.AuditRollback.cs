using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobPositiveSdkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Either_real_MUST_audit_failure_rolls_back_domain_and_terminal_state_but_preserves_prior_provider_facts(bool terminalAudit)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        var resource = Guid.CreateVersion7();
        var prepared = OrganizationId.From(Guid.CreateVersion7());
        var finalized = OrganizationId.From(Guid.CreateVersion7());
        await InstallTransactionObserverAsync(database, prepared, finalized);
        await provider.SetModeAsync(resource, "permanent");
        await using var host = Fixture.Host(database, provider.Settings);
        var flow = await AdmitPrepareAndArmAsync(database, host, resource, prepared, finalized);
        var report = await flow.Executor.ExecuteExternalAsync(flow.Read, flow.Arm, CancellationToken.None);
        report.Status.Should().Be(JobExternalExecutionStatus.Reported);
        var outcome = await flow.Executor.RecordExternalOutcomeAsync(flow.Receipt, flow.Read,
            flow.Receipt.Run.OpenBoundary(BackgroundJobCheckpointKind.Outcome), report.Observation!, CancellationToken.None);
        outcome.CommitAcknowledged.Should().BeTrue();
        outcome.CleanupFailed.Should().BeFalse();
        var job = flow.Receipt.Envelope.JobId;
        await using (var setup = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
        await using (var reject = new NpgsqlCommand($"""
            CREATE FUNCTION sdk_reject_exact_audit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF {(terminalAudit
                    ? $"NEW.operation='platform.job.dead_letter' AND NEW.metadata->>'job_id'='{job.Value:D}'"
                    : $"NEW.operation='tenancy.organization.create' AND NEW.entity_id='{finalized.Value:D}'")} THEN
                    RAISE EXCEPTION 'fixture rejects the exact MUST audit' USING ERRCODE='23514';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER sdk_reject_audit BEFORE INSERT ON audit_log FOR EACH ROW
                EXECUTE FUNCTION sdk_reject_exact_audit();
            """, (NpgsqlConnection)setup)
        { CommandTimeout = 5 })
            await reject.ExecuteNonQueryAsync();
        var claim = await host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>()
            .TryClaimAsync(job, await GenerationAsync(database, job));
        claim.Should().NotBeNull();
        var admitted = await flow.Executor.ExecuteAsync(claim!, BackgroundJobTestRun.Create());
        admitted.Outcome.Should().Be(JobStartExecutionOutcome.Started);
        var receipt = admitted.Receipt!;
        var result = await flow.Executor.ExecuteBusinessAsync(receipt,
            receipt.Run.OpenBoundary(BackgroundJobCheckpointKind.Finalize));
        result.CommitAcknowledged.Should().BeFalse();
        result.KnownRollback.Should().BeTrue();
        result.CleanupFailed.Should().BeFalse();
        (await provider.CountsAsync(flow.Operation)).Should().Be((1L, 0L, 0L));
        (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, prepared.Value)).Should().Be(1);
        (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, finalized.Value)).Should().Be(0);
        await using var connection = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(connection, transaction);
        await EducationSchemaSeed.AnnounceAsync(connection, transaction, SchemaFixture.TenantA, null);
        await using var command = new NpgsqlCommand("""
            SELECT j.phase,j.terminal_reason,j.dead_lettered_at,j.final_outcome_attempt_id,
                (SELECT count(*) FROM audit_log WHERE operation='tenancy.organization.create' AND entity_id=@prepared),
                (SELECT count(*) FROM audit_log WHERE operation='tenancy.organization.create' AND entity_id=@finalized AND outcome='success'),
                (SELECT count(*) FROM audit_log WHERE operation='platform.job.dead_letter' AND metadata->>'job_id'=@job_text),
                (SELECT count(*) FROM sdk_transaction_observations WHERE label LIKE 'finalize-%'),
                (SELECT count(*) FROM sdk_transaction_observations WHERE label LIKE 'prepare-%'),
                (SELECT count(*) FROM audit_log WHERE operation='tenancy.organization.create' AND entity_id=@finalized AND outcome='failed')
            FROM background_jobs j WHERE id=@job
            """, (NpgsqlConnection)connection, (NpgsqlTransaction)transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("prepared", prepared.Value.ToString("D"));
        command.Parameters.AddWithValue("finalized", finalized.Value.ToString("D"));
        command.Parameters.AddWithValue("job", job.Value);
        command.Parameters.AddWithValue("job_text", job.Value.ToString("D"));
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        row.GetString(0).Should().Be("finalizing");
        row.IsDBNull(1).Should().BeTrue();
        row.IsDBNull(2).Should().BeTrue();
        row.GetGuid(3).Should().Be(flow.Receipt.AttemptId.Value);
        row.GetInt64(4).Should().Be(1);
        row.GetInt64(5).Should().Be(0);
        row.GetInt64(6).Should().Be(0);
        row.GetInt64(7).Should().Be(0, "the final owner transaction and its test observations rolled back together");
        row.GetInt64(8).Should().Be(2, "the earlier acknowledged Prepare transaction remains committed");
        row.GetInt64(9).Should().Be(terminalAudit ? 1 : 0,
            "ADR-0033 reconciles a failed attempt separately when its audit INSERT remains available; it never records success");
    }
}
