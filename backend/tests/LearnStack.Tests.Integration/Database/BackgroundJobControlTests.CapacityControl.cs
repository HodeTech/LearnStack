using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobControlTests
{
    [Fact]
    public async Task Another_instance_parks_expired_grouped_work_without_cleanup_and_keeps_its_occupancy_counted()
    {
        await using var database = await DatabaseAsync();
        var definitions = Fixture.CapacityRegistry();
        var probe = new BackgroundJobFixtureProbe();
        await using var host = Fixture.Host(database, probe, configure: services =>
        {
            services.RemoveAll<BackgroundJobDefinitionRegistry>();
            services.AddSingleton(definitions);
        });
        await using var source = Source(database.OutboxConnectionString);
        var store = Control(source, definitions);
        var routing = host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>();
        var executor = host.Services.GetRequiredService<JobExecutor>();
        var first = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.CapacityKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup);
        var claim = (await routing.TryClaimAsync(first, 1))!;
        var admitted = await executor.ExecuteAsync(claim);
        admitted.Outcome.Should().Be(JobStartExecutionOutcome.Started);
        try
        {
            await DispatchAsync(database,
                "UPDATE background_jobs SET ownership_until=clock_timestamp()-interval '1 microsecond' WHERE id=@id", ("id", first.Value));
            var recovered = await store.RunRecoveryQuantumAsync(BackgroundJobControlLane.Recovery, []);
            recovered.ThrowIfFailed();
            recovered.Value.Should().Contain(change => change.JobId == first.Value && change.Phase == "reconciling");
            var parked = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, first))!;
            parked.Phase.Should().Be("reconciling");
            parked.NormalAttempts.Should().Be(1);
            parked.CurrentAttempt.Should().Be(admitted.Receipt!.AttemptId.Value);
            parked.OwnershipToken.Should().BeNull();
            await DispatchAsync(database,
                "UPDATE background_jobs SET probe_after=clock_timestamp()-interval '1 microsecond' WHERE id=@id", ("id", first.Value));
            var probed = await store.RunRecoveryQuantumAsync(BackgroundJobControlLane.CapacityProbe, []);
            probed.ThrowIfFailed();
            probed.Value.Should().Contain(change => change.JobId == first.Value && change.Phase == "reconciling");
            var second = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
                BackgroundJobRuntimeFixture.CapacityKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup);
            var secondClaim = (await routing.TryClaimAsync(second, 1))!;
            var refused = await executor.ExecuteAsync(secondClaim);
            refused.Outcome.Should().Be(JobStartExecutionOutcome.CapacityDeferred);
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, second)).Should().Be(0);
            probe.Constructions.Should().BeEmpty("neither recovery nor capacity refusal constructs business work");
            await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
            await using var transaction = await app.BeginTransactionAsync();
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            (await SchemaQueries.CountAsync(app,
                "SELECT count(*) FROM background_job_reservations WHERE released_at IS NULL", transaction)).Should().Be(1L);
            (await SchemaQueries.CountAsync(app,
                "SELECT count(*) FROM background_job_attempts WHERE transactional_cleanup_at IS NOT NULL OR no_start_at IS NOT NULL", transaction))
                .Should().Be(0L, "a different instance cannot manufacture the origin's positive cleanup witness");
        }
        finally { admitted.SourceOwner.CloseIssuance(); }
    }
}
