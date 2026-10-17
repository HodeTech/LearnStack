using System.Diagnostics;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobPositiveSdkTests
{
    private static async Task<(JobExecutor Executor, ConfirmedStartReceipt Receipt, JobExternalReadHandle Read,
        JobExternalCheckpointResult Arm, Guid Operation, DateTimeOffset Lease)> AdmitPrepareAndArmAsync(
        DisposableSchemaDatabase database, WebApplicationFactory<Program> host, Guid resource,
        OrganizationId prepared, OrganizationId finalized, TimeSpan? observedLease = null)
    {
        var submitted = await Fixture.SubmitAsync(host.Services, Fixture.Parameters(resource, prepared, finalized));
        submitted.IsSuccess.Should().BeTrue();
        var job = submitted.Value;
        var claim = await host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>()
            .TryClaimAsync(job, await GenerationAsync(database, job));
        claim.Should().NotBeNull();
        var executor = host.Services.GetRequiredService<JobExecutor>();
        var admitted = await executor.ExecuteAsync(claim!, BackgroundJobTestRun.Create());
        admitted.Outcome.Should().Be(JobStartExecutionOutcome.Started);
        admitted.CleanupFailed.Should().BeFalse();
        var receipt = admitted.Receipt!;
        receipt.Stage.Should().Be(BackgroundJobExecutionStage.Prepare);
        var result = await executor.ExecuteBusinessAsync(receipt,
            receipt.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare));
        result.CommitAcknowledged.Should().BeTrue();
        result.CleanupFailed.Should().BeFalse();
        var read = await executor.ReadExternalStateAsync(receipt, CancellationToken.None);
        read.Handle.Should().NotBeNull();
        DateTimeOffset lease = receipt.OwnershipUntil;
        if (observedLease is { } duration)
        {
            // Lower-layer timing control: actual role-fenced lease, no native timer.
            await using var connection = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
            await using var command = new NpgsqlCommand("""
                UPDATE background_jobs SET ownership_until=clock_timestamp()+@duration
                WHERE id=@id AND current_attempt_id=@attempt AND ownership_token=@token
                RETURNING ownership_until
                """, (NpgsqlConnection)connection) { CommandTimeout = 5 };
            command.Parameters.AddWithValue("duration", duration);
            command.Parameters.AddWithValue("id", job.Value);
            command.Parameters.AddWithValue("attempt", receipt.AttemptId.Value);
            command.Parameters.AddWithValue("token", receipt.Claim.Token);
            await using var row = await command.ExecuteReaderAsync();
            (await row.ReadAsync()).Should().BeTrue();
            lease = row.GetFieldValue<DateTimeOffset>(0);
        }
        var arm = await executor.RecordExternalArmAsync(receipt, read.Handle!,
            receipt.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm), CancellationToken.None);
        arm.CommitAcknowledged.Should().BeTrue();
        arm.CleanupFailed.Should().BeFalse();
        return (executor, receipt, read.Handle!, arm, read.Handle!.Require(receipt).OperationId!.Value, lease);
    }

    private static async Task WaitForDatabaseLeaseAsync(DisposableSchemaDatabase database, DateTimeOffset until)
    {
        var waiting = Stopwatch.StartNew();
        await using var connection = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        while (true)
        {
            await using var command = new NpgsqlCommand("SELECT clock_timestamp()>=@until", (NpgsqlConnection)connection)
            { CommandTimeout = 5 };
            command.Parameters.AddWithValue("until", until);
            if (await command.ExecuteScalarAsync() is true) return;
            if (waiting.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Observed SDK lease did not expire.");
            await Task.Delay(10);
        }
    }
}
