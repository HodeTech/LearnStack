using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobExternalCheckpointStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task History_never_publishes_before_the_actual_probe_rollback_ack_and_a_new_probe_cannot_rewrite_the_old_failure(bool loseResponse)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external");
        await using var host = RecoveryHost(database, preparing: true);
        var original = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        var executor = host.Services.GetRequiredService<JobExecutor>();
        (await executor.ExecuteBusinessAsync(original, original.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare)))
            .CommitAcknowledged.Should().BeTrue();
        var read = await executor.ReadExternalStateAsync(original, CancellationToken.None);
        read.Handle.Should().NotBeNull();
        await using var commitProxy = new CommitResponseDroppingProxy(database.AppConnectionString);
        await using var brokenHost = RecoveryHost(database, preparing: true, appConnectionString: commitProxy.ConnectionString);
        var gate = original.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm);
        var arm = await brokenHost.Services.GetRequiredService<JobExecutor>()
            .RecordExternalArmAsync(original, read.Handle!, gate, CancellationToken.None);
        commitProxy.DroppedCommitResponses.Should().Be(1);
        arm.CommitUncertain.Should().BeTrue();
        var origin = BackgroundJobCheckpointHistoryOrigin.ForArm(arm);
        await using var probeProxy = new CommitResponseDroppingProxy(database.OutboxConnectionString);
        probeProxy.HoldNextCheckpointHistoryRollbackResponse(loseResponse);
        await using var proxySource = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(probeProxy.ConnectionString));
        var store = new PostgresBackgroundJobControlStore(proxySource, Fixture.Registry, new(), new SystemGuidFactory());
        var probing = store.ProbeExternalCheckpointAsync(origin, CancellationToken.None);
        Exception? premiseFailure = null;
        try
        {
            await probeProxy.CheckpointHistoryRollbackHeld.WaitAsync(TimeSpan.FromSeconds(15));
            probing.IsCompleted.Should().BeFalse();
            gate.CommitConfirmed.Should().BeFalse();
            gate.CommitAcknowledged.Should().BeFalse();
            await using var peer = await PostgresFixture.OpenAsync(database.AppConnectionString);
            await using var transaction = await peer.BeginTransactionAsync();
            await SchemaQueries.AssertApplicationRoleAsync(peer, transaction);
            await EducationSchemaSeed.AnnounceAsync(peer, transaction, SchemaFixture.TenantA, null);
            await using var command = peer.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT id FROM background_jobs WHERE id=@id FOR UPDATE SKIP LOCKED";
            var id = command.CreateParameter();
            id.ParameterName = "id";
            id.Value = job.Value;
            command.Parameters.Add(id);
            (await command.ExecuteScalarAsync()).Should().Be(job.Value,
                "the actual server ROLLBACK released the row lock before the withheld client acknowledgement");
            gate.CommitConfirmed.Should().BeFalse("server lock release is still not the probe client's rollback ACK");
            await transaction.RollbackAsync();
        }
        catch (Exception failure) { premiseFailure = failure; }
        finally { probeProxy.ReleaseCheckpointHistoryRollbackResponse(); }
        var result = await probing.WaitAsync(TimeSpan.FromSeconds(15));
        premiseFailure.Should().BeNull();
        result.CommitAcknowledged.Should().BeFalse();
        result.KnownRollback.Should().BeFalse();
        if (loseResponse)
        {
            result.Failed.Should().BeTrue();
            result.Value.Should().BeNull();
            gate.CommitConfirmed.Should().BeFalse();
            await using var healthySource = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
            var healthy = new PostgresBackgroundJobControlStore(healthySource, Fixture.Registry, new(), new SystemGuidFactory());
            var later = await healthy.ProbeExternalCheckpointAsync(origin, CancellationToken.None);
            later.ThrowIfFailed();
            later.Value!.Disposition.Should().Be(BackgroundJobCheckpointHistoryDisposition.Committed);
            result.Failed.Should().BeTrue("a fresh clean probe cannot change the old operation's resource/failure facts");
            result.Value.Should().BeNull();
        }
        else
        {
            result.ThrowIfFailed();
            result.CleanupConfirmed.Should().BeTrue();
            result.Value!.Disposition.Should().Be(BackgroundJobCheckpointHistoryDisposition.Committed);
        }
        gate.CommitConfirmed.Should().BeTrue();
        gate.CommitAcknowledged.Should().BeFalse();
        arm.CommitAcknowledged.Should().BeFalse();
        arm.KnownRollback.Should().BeFalse();
    }
}
