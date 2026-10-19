using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobControlTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task Locked_admission_history_distinguishes_absence_from_original_or_peer_occupancy(
        bool originalCommitted, bool peerAdmits, bool originalReleased)
    {
        await using var database = await DatabaseAsync();
        var definitions = Fixture.CapacityRegistry();
        var constructions = new BackgroundJobFixtureProbe();
        await using var host = Fixture.Host(database, constructions, configure: services =>
        {
            services.RemoveAll<BackgroundJobDefinitionRegistry>();
            services.AddSingleton(definitions);
        });
        await using var source = Source(database.OutboxConnectionString);
        var store = Control(source, definitions);
        var routing = host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>();
        var executor = host.Services.GetRequiredService<JobExecutor>();
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.CapacityKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup);
        var original = (await routing.TryClaimAsync(job, 1))!;
        JobStartExecutionResult? first = null, peer = null;
        try
        {
            if (originalCommitted)
            {
                first = await executor.ExecuteAsync(original, BackgroundJobTestRun.Create());
                first.Outcome.Should().Be(JobStartExecutionOutcome.Started);
            }
            await DispatchAsync(database,
                "UPDATE background_jobs SET ownership_until=clock_timestamp()-interval '1 microsecond' WHERE id=@id", ("id", job.Value));
            var recovered = await store.RunRecoveryQuantumAsync(BackgroundJobControlLane.Recovery, []);
            recovered.ThrowIfFailed();
            recovered.CommitAcknowledged.Should().BeTrue();
            recovered.Value.Should().ContainSingle(change => change.JobId == job.Value
                && change.Phase == (originalCommitted ? "reconciling" : "pending"));
            if (originalReleased)
            {
                // Guarded app-role history fixture, not a minted runtime cleanup
                // witness: establish immutable positive no-start/release facts.
                await DispatchAsync(database, """
                    UPDATE background_jobs SET ownership_token=@token, ownership_kind='recovery',
                        ownership_until=clock_timestamp()+interval '1 minute' WHERE id=@id AND ownership_token IS NULL
                    """, ("id", job.Value), ("token", Guid.NewGuid()));
                await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
                await using (var transaction = await app.BeginTransactionAsync())
                {
                    await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
                    await SchemaQueries.ExecuteAsync(app, transaction, """
                        UPDATE background_job_attempts SET no_start_at=clock_timestamp() WHERE id=@attempt;
                        UPDATE background_job_reservations SET released_at=clock_timestamp(), release_reason='never_started'
                            WHERE attempt_id=@attempt
                        """, ("attempt", first!.Receipt!.AttemptId.Value));
                    await transaction.CommitAsync();
                }
                await DispatchAsync(database,
                    "UPDATE background_jobs SET ownership_until=clock_timestamp()-interval '1 microsecond' WHERE id=@id", ("id", job.Value));
                var routed = await store.RunRecoveryQuantumAsync(BackgroundJobControlLane.Recovery, []);
                routed.ThrowIfFailed();
                routed.Value.Should().ContainSingle(change => change.JobId == job.Value && change.Phase == "pending" && change.WakeGeneration == 2);
                await DispatchAsync(database, "UPDATE background_jobs SET available_after=clock_timestamp() WHERE id=@id", ("id", job.Value));
            }
            if (peerAdmits)
            {
                var next = (await routing.TryClaimAsync(job, originalReleased ? 2 : 1))!;
                next.Should().NotBeNull();
                next.Token.Should().NotBe(original.Token);
                peer = await executor.ExecuteAsync(next, BackgroundJobTestRun.Create());
                peer.Outcome.Should().Be(JobStartExecutionOutcome.Started);
            }
            var before = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
            before.OwnershipToken.Should().NotBe(original.Token);
            var probed = await store.ProbeCommitAsync(BackgroundJobCommitProbe.ForAdmission(original)
                with
            { CapacityGroup = BackgroundJobRuntimeFixture.CapacityGroup });
            probed.ThrowIfFailed();
            probed.CleanupConfirmed.Should().BeTrue();
            probed.Value!.Disposition.Should().Be(originalReleased ? BackgroundJobProbeDisposition.AdmissionReleased
                : originalCommitted ? BackgroundJobProbeDisposition.Admitted : BackgroundJobProbeDisposition.RolledBack);
            probed.CommitAcknowledged.Should().BeFalse();
            probed.Value.RecoveryReceipt.Should().BeNull("historical read evidence never reconstructs executable or recovery authority");
            var after = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
            after.Should().Be(before, "the historical probe does not change the peer's current header");
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job))
                .Should().Be((originalCommitted ? 1 : 0) + (peerAdmits ? 1 : 0));
            constructions.Constructions.Should().BeEmpty();
            await using var observed = await PostgresFixture.OpenAsync(database.AppConnectionString);
            await using var read = await observed.BeginTransactionAsync();
            await EducationSchemaSeed.AnnounceAsync(observed, read, SchemaFixture.TenantA, null);
            (await SchemaQueries.CountAsync(observed,
                "SELECT count(*) FROM background_job_reservations WHERE released_at IS NULL", read))
                .Should().Be(originalCommitted || peerAdmits ? 1L : 0L);
            (await SchemaQueries.CountAsync(observed,
                "SELECT count(*) FROM background_job_reservations WHERE release_reason='never_started'", read))
                .Should().Be(originalReleased ? 1L : 0L);
        }
        finally
        {
            first?.SourceOwner.CloseIssuance();
            peer?.SourceOwner.CloseIssuance();
        }
    }
}
