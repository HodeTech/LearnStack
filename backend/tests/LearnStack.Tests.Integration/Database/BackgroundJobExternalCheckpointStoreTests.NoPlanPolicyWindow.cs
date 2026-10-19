using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobExternalCheckpointStoreTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(null, true)]
    public async Task Actual_clean_permanent_Prepare_survives_expiry_but_cannot_clear_a_replaced_owner(
        bool? committed, bool replacedOwner)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var host = NoPlanRefusalHost(database, admissionWindow: TimeSpan.FromSeconds(2));
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var normal = await NoPlanOrdinalAsync(database, host.Services, 1, store, runtime);
        var failed = await host.Services.GetRequiredService<JobExecutor>().ExecuteBusinessAsync(normal,
            normal.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare));
        failed.ExecutionIssued.Should().BeTrue();
        failed.KnownRollback.Should().BeTrue();
        failed.CleanupFailed.Should().BeFalse();
        failed.SourceOwner.CleanupConfirmed.Should().BeTrue();
        failed.Decision.TerminalReason.Should().Be(BackgroundJobTerminalReason.PermanentFailure);
        failed.Decision.ErrorCode.Should().Be("permanent_failure");
        normal.Run.Close();
        normal.SourceOwner.CloseIssuance();
        using (var expiry = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while (await RetryDatabaseClockAsync(database) < normal.Deadline) await Task.Delay(10, expiry.Token);
        var identity = BackgroundJobExecutionIdentity.FromReceipt(normal);
        var original = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, normal.Envelope.JobId))!;
        original.OwnershipUntil.Should().BeOnOrBefore(await RetryDatabaseClockAsync(database));
        var immutable = await RetainedSelectionFactsAsync(database, normal);
        var unproven = await store.RequestNoPlanExternalTerminalAsync(identity,
            BackgroundJobTerminalReason.PermanentFailure, CancellationToken.None);
        unproven.ThrowIfFailed();
        unproven.CleanupConfirmed.Should().BeTrue();
        unproven.CommitAcknowledged.Should().BeFalse();
        unproven.Value.Should().BeNull("expired authority alone cannot preserve a business classifier");
        if (replacedOwner)
        {
            // This lease already expired naturally above; never move its
            // timestamp forward. Park only the expired original authority.
            await using (var sql = await PostgresFixture.OpenAsync(database.OutboxConnectionString))
                await SchemaQueries.ExecuteAsync(sql, null, """
                    UPDATE background_jobs SET phase='reconciling',ownership_token=NULL,ownership_kind=NULL,
                        ownership_until=NULL,probe_after=clock_timestamp()
                    WHERE id=@id AND ownership_token=@token AND ownership_until<=clock_timestamp()
                    """, ("id", normal.Envelope.JobId.Value), ("token", identity.Token));
            var acquired = await store.AcquireExternalRecoveryAsync(normal.Envelope.JobId,
                await RecoveryGenerationAsync(database, normal.Envelope.JobId), normal.Envelope.Queue, CancellationToken.None);
            acquired.ThrowIfFailed();
            acquired.CommitAcknowledged.Should().BeTrue();
            acquired.CleanupConfirmed.Should().BeTrue();
            acquired.Value!.RecoveryToken.Should().NotBe(identity.Token);
            original = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, normal.Envelope.JobId))!;
        }
        await using var proxy = new CommitResponseDroppingProxy(database.OutboxConnectionString,
            dropCommitRequest: committed == false);
        await using var broken = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(proxy.ConnectionString));
        var actual = committed is null ? store
            : new PostgresBackgroundJobControlStore(broken, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var request = await actual.RequestNoPlanExternalTerminalAsync(identity,
            BackgroundJobTerminalReason.PermanentFailure, CancellationToken.None, failed);
        request.CleanupConfirmed.Should().BeTrue();
        if (replacedOwner)
        {
            request.ThrowIfFailed();
            request.CommitAcknowledged.Should().BeFalse();
            request.Value.Should().BeNull();
            request.Probes.Should().BeEmpty();
            (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, normal.Envelope.JobId)).Should().Be(original);
            return;
        }
        if (committed is null)
        {
            request.ThrowIfFailed();
            request.CommitAcknowledged.Should().BeTrue();
        }
        else
        {
            request.CommitUncertain.Should().BeTrue();
            request.CommitAcknowledged.Should().BeFalse();
            proxy.DroppedCommitRequests.Should().Be(committed.Value ? 0 : 1);
            proxy.DroppedCommitResponses.Should().Be(committed.Value ? 1 : 0);
        }
        var observed = await store.ProbeCommitAsync(request.Probes.Should().ContainSingle().Subject);
        observed.ThrowIfFailed();
        observed.CleanupConfirmed.Should().BeTrue();
        observed.Value!.Disposition.Should().Be(committed == false
            ? BackgroundJobProbeDisposition.RolledBack : BackgroundJobProbeDisposition.ControlCommitted);
        if (committed == false)
        {
            (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, normal.Envelope.JobId)).Should().Be(original);
            var retry = await store.RequestNoPlanExternalTerminalAsync(identity,
                BackgroundJobTerminalReason.PermanentFailure, CancellationToken.None, failed);
            retry.ThrowIfFailed();
            retry.CommitAcknowledged.Should().BeTrue();
            retry.CleanupConfirmed.Should().BeTrue();
        }
        failed.Decision.ErrorCode.Should().Be("permanent_failure");
        normal.Run.IsClosed.Should().BeTrue();
        (await RetainedSelectionFactsAsync(database, normal)).Should().Be(immutable);
        await AssertNoPlanRequestAsync(database, normal, "permanent_failure");
    }
}
