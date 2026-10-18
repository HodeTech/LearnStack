using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class HangfireGenerationTests
{
    [Fact]
    public async Task Metadata_observation_distinguishes_unobserved_from_confirmed_zero_and_tracks_actual_heartbeat_success()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var host = Host(database);
        await using var source = Source(database);
        using var globals = new GlobalFilterSnapshot();
        var topology = host.Services.GetRequiredService<BackgroundJobDefinitionRegistry>().Infrastructure;
        var gate = new BackgroundJobRuntimeGate(topology);
        var provider = HangfireStorageBootstrap.Create(source, topology, new HangfireBoundedLogProvider(), gate);
        var inventory = new HangfireComponentInventory(provider);
        var clock = new ObservationClock();
        var generation = Generation(host.Services, provider, inventory, HangfireRuntimeFilters.Create(),
            topology.Pools["transactional"], gate, clock);
        generation.Snapshot.MetadataHeartbeatTimestamp.Should().BeNull();
        using var callback = new PoolHangfireStorage(provider, generation, inventory.Selected).GetConnection();
        Task? heartbeat = null;
        generation.Start();
        try
        {
            (await generation.Ready.WaitAsync(TestWait)).Should().BeTrue();
            generation.Snapshot.MetadataHeartbeatTimestamp.Should().Be(0L,
                "the actual successful announcement confirms zero rather than treating it as missing");
            var server = await ServerIdAsync(source, "transactional");
            clock.Advance(TimeSpan.FromSeconds(2));
            // This exercises the real closed callback and stock metadata SQL,
            // not Core's separate thirty-second scheduling cadence.
            heartbeat = Task.Run(() => callback.Heartbeat(server));
            await heartbeat.WaitAsync(TestWait);
            generation.Snapshot.MetadataHeartbeatTimestamp.Should().Be(TimeSpan.FromSeconds(2).Ticks);
            generation.Snapshot.HeartbeatFault.Should().BeFalse();
        }
        finally
        {
            using var deadline = new CancellationTokenSource(TestWait);
            // Issue stop before observing either task. A faulted heartbeat
            // cannot skip the generation's independent positive-cleanup join.
            var stop = generation.StopAsync(deadline.Token);
            await Task.WhenAll((heartbeat ?? Task.CompletedTask).WaitAsync(deadline.Token), stop);
        }
        generation.Snapshot.CleanupComplete.Should().BeTrue();
    }

    private sealed class ObservationClock : TimeProvider
    {
        private long _now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _now);
        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref _now, elapsed.Ticks);
    }
}
