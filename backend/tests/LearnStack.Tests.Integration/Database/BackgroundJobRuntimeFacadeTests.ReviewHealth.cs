using System.Reflection;
using FluentAssertions;
using Hangfire.PostgreSql;
using Hangfire.Storage;
using LearnStack.Api.Jobs;
using LearnStack.Infrastructure.Jobs;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRuntimeFacadeTests
{
    [Fact]
    public async Task Actual_failed_fetched_renewal_and_stored_recovery_preserve_their_separate_health_outcomes()
    {
        await using var database = await DatabaseAsync();
        await using var host = Fixture.Host(database, new BackgroundJobFixtureProbe());
        await using var source = Source(database);
        using var globals = new GlobalFilterSnapshot();
        using var deadline = new CancellationTokenSource(TestWait);
        var runtime = Runtime(host.Services, source);
        var check = new BackgroundJobHealthCheck(runtime);
        IStorageConnection? connection = null;
        IFetchedJob? fetched = null;
        try
        {
            await runtime.StartAsync();
            await EventuallyAsync(() => Task.FromResult(runtime.Snapshot.IsHealthy));
            var initial = runtime.Snapshot;
            initial.Pools.Should().HaveCount(3).And.OnlyContain(pool => pool.Generations == 1
                && pool.RunningInvocations == 0 && pool.RetainedInvocations == 0
                && pool.GenerationCaptured && pool.MetadataHeartbeatTimestamp.HasValue
                && !pool.NormalChildrenJoined && !pool.OuterJoined && !pool.GenerationCleanupComplete);
            var pools = initial.Pools.Should().BeAssignableTo<IList<BackgroundJobPoolHealth>>().Which;
            Action replacePool = () => pools[0] = pools[0];
            replacePool.Should().Throw<NotSupportedException>();
            var maintenance = initial.Maintenance.Should().BeAssignableTo<IList<BackgroundJobMaintenanceHealth>>().Which;
            Action replaceMaintenance = () => maintenance[0] = maintenance[0];
            replaceMaintenance.Should().Throw<NotSupportedException>();
            var provider = ActualProvider(runtime);
            // This fixed fixture-only orphan queue has no native consumer. Fetch
            // through the facade's actual provider: renewal tracking belongs to
            // that exact storage instance, not a second bootstrap's inventory.
            var pool = new BackgroundJobWorkerPoolRegistration("heartbeat_probe", BackgroundJobWorkerPoolKind.Transactional,
                1, ["heartbeat_probe"]);
            var vendor = HangfireGenerationTests.CreateOrphanWakeup(provider, HangfireRuntimeFilters.Create(), pool);
            connection = provider.GetConnection();
            fetched = HangfireGenerationTests.FetchWithAcceleratedRenewal(provider, connection, pool.Queues.ToArray(), deadline.Token);
            fetched.JobId.Should().Be(vendor);
            // Only the test-owned fetched object's cadence is accelerated;
            // production native options are restored by the existing helper.
            await HangfireGenerationTests.SetFetchedRenewalPrivilegeAsync(database, grant: false, deadline.Token);
            BackgroundJobMaintenanceHealth? firstFailure = null;
            await EventuallyAsync(async () =>
            {
                var health = await check.CheckHealthAsync(new HealthCheckContext());
                var renewal = (BackgroundJobMaintenanceHealth)health.Data["maintenance:fetched_job_heartbeat"];
                if (renewal is { State: "Waiting", Outcome: "Failed", ActualJoined: true, StorageFault: true })
                    firstFailure = renewal;
                return firstFailure is not null;
            });
            var prior = await HangfireGenerationTests.FetchedAtAsync(source, vendor, deadline.Token);
            HealthCheckResult? failedPass = null;
            await EventuallyAsync(async () =>
            {
                var health = await check.CheckHealthAsync(new HealthCheckContext());
                var renewal = (BackgroundJobMaintenanceHealth)health.Data["maintenance:fetched_job_heartbeat"];
                if (renewal is { State: "Waiting", Outcome: "Failed", ActualJoined: true, StorageFault: true }
                    && renewal.Invocations > firstFailure!.Invocations) failedPass = health;
                return failedPass is not null;
            });
            var failed = failedPass!.Value;
            failed.Status.Should().Be(HealthStatus.Unhealthy);
            failed.Data["maintenance:expiration"].Should().BeOfType<BackgroundJobMaintenanceHealth>()
                .Which.IsHealthy.Should().BeTrue();
            failed.Data["maintenance:fetched_job_heartbeat"].Should().BeOfType<BackgroundJobMaintenanceHealth>()
                .Which.Should().BeEquivalentTo(new
                {
                    Component = "fetched_job_heartbeat",
                    Outcome = "Failed",
                    ActualJoined = true,
                    OwnsResources = false,
                    StorageFault = true,
                    IsHealthy = false,
                });
            runtime.Snapshot.Pools.Should().OnlyContain(item => !item.MetadataHeartbeatFault);
            (await HangfireGenerationTests.FetchedAtAsync(source, vendor, deadline.Token)).Should().Be(prior);

            await HangfireGenerationTests.SetFetchedRenewalPrivilegeAsync(database, grant: true, deadline.Token);
            await EventuallyAsync(async () =>
                await HangfireGenerationTests.FetchedAtAsync(source, vendor, deadline.Token) > prior
                && runtime.Snapshot.IsHealthy);
            var recovered = await check.CheckHealthAsync(new HealthCheckContext());
            recovered.Status.Should().Be(HealthStatus.Healthy);
            recovered.Data["maintenance:fetched_job_heartbeat"].Should().BeOfType<BackgroundJobMaintenanceHealth>()
                .Which.Should().BeEquivalentTo(new { StorageFault = false, IsHealthy = true });
            (await HangfireGenerationTests.FetchedAtAsync(source, vendor, deadline.Token)).Should().BeAfter(prior);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TestWait);
            try { await HangfireGenerationTests.SetFetchedRenewalPrivilegeAsync(database, grant: true, cleanup.Token); }
            finally
            {
                await runtime.StopAsync(cleanup.Token);
                await runtime.Completion.WaitAsync(TestWait);
                fetched?.RemoveFromQueue();
                fetched?.Dispose();
                connection?.Dispose();
                await DisposePinnedSourceAsync(source);
            }
        }
    }

    private static PostgreSqlStorage ActualProvider(BackgroundJobRuntime runtime)
    {
        var bootstrap = typeof(BackgroundJobRuntime).GetField("_bootstrap", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(runtime) ?? throw new InvalidOperationException("The actual bootstrap owner is missing.");
        return bootstrap.GetType().GetField("_provider", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(bootstrap) as PostgreSqlStorage
            ?? throw new InvalidOperationException("The actual runtime provider is missing.");
    }

    [Fact]
    public async Task Actual_metadata_heartbeat_fault_is_unhealthy_before_drain_and_only_stored_success_restores_health()
    {
        await using var database = await DatabaseAsync();
        await using var host = Fixture.Host(database, new BackgroundJobFixtureProbe());
        await using var source = Source(database);
        using var globals = new GlobalFilterSnapshot();
        var runtime = Runtime(host.Services, source);
        await using var blocker = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var transaction = await blocker.BeginTransactionAsync();
        IStorageConnection? callbackConnection = null;
        Task? heartbeat = null;
        var lockReleased = false;
        try
        {
            await runtime.StartAsync();
            await EventuallyAsync(() => Task.FromResult(runtime.Snapshot.IsHealthy));
            var generation = ActualGeneration(runtime, "transactional");
            await using var row = new NpgsqlCommand("""
                SELECT id FROM public.server WHERE id LIKE 'learnstack-transactional:%' FOR UPDATE
                """, blocker, transaction);
            var server = (string)(await row.ExecuteScalarAsync())!;
            var prior = await StoredHeartbeatAsync(blocker, transaction, server);
            var storage = typeof(HangfireGeneration).GetField("_storage", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(generation) as PoolHangfireStorage
                ?? throw new InvalidOperationException("The actual generation storage is missing.");
            callbackConnection = storage.GetConnection();
            // Invoke the exact closed metadata-heartbeat callback with real stock
            // SQL. Only its row is locked; Core's thirty-second cadence is not
            // accelerated or asserted by this control.
            heartbeat = Task.Run(() => callbackConnection.Heartbeat(server));
            await EventuallyAsync(() => Task.FromResult(generation.Snapshot.HeartbeatFault));
            generation.Snapshot.Draining.Should().BeFalse("a transient SQL timeout stays within the retry window");
            var failed = runtime.Snapshot;
            failed.RunningPools.Should().Be(3);
            failed.RetainedPools.Should().Be(0);
            failed.ExpirationHealthy.Should().BeTrue();
            failed.FetchedJobHeartbeatHealthy.Should().BeTrue();
            failed.Pools.Should().ContainSingle(pool => pool.Pool == "transactional" && pool.MetadataHeartbeatFault);
            failed.IsHealthy.Should().BeFalse("running metadata-heartbeat retries must be visible before drain");
            (await new BackgroundJobHealthCheck(runtime).CheckHealthAsync(new HealthCheckContext()))
                .Status.Should().Be(HealthStatus.Unhealthy);
            (await StoredHeartbeatAsync(blocker, transaction, server)).Should().Be(prior);

            await transaction.RollbackAsync();
            lockReleased = true;
            await heartbeat.WaitAsync(TestWait);
            await EventuallyAsync(() => Task.FromResult(runtime.Snapshot.IsHealthy));
            generation.Snapshot.HeartbeatFault.Should().BeFalse();
            generation.Snapshot.Draining.Should().BeFalse();
            runtime.Snapshot.Pools.Should().OnlyContain(pool => !pool.MetadataHeartbeatFault);
            await using var independent = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.OutboxConnectionString);
            (await StoredHeartbeatAsync(independent, null, server)).Should().BeAfter(prior);
            (await new BackgroundJobHealthCheck(runtime).CheckHealthAsync(new HealthCheckContext()))
                .Status.Should().Be(HealthStatus.Healthy);
        }
        finally
        {
            runtime.RequestStop();
            try
            {
                if (!lockReleased) await transaction.RollbackAsync();
                if (heartbeat is not null)
                {
                    try { await heartbeat.WaitAsync(TestWait); }
                    catch (InvalidOperationException)
                    {
                        // Failed assertions still reach positive owner cleanup;
                        // a stop-time callback refusal is not a new test result.
                    }
                }
            }
            finally
            {
                callbackConnection?.Dispose();
                using var deadline = new CancellationTokenSource(TestWait);
                await runtime.StopAsync(deadline.Token);
                await runtime.Completion.WaitAsync(TestWait);
                await DisposePinnedSourceAsync(source);
            }
        }
    }

    private static async Task<DateTime> StoredHeartbeatAsync(NpgsqlConnection connection,
        NpgsqlTransaction? transaction, string server)
    {
        await using var command = new NpgsqlCommand("SELECT lastheartbeat FROM public.server WHERE id=@id", connection, transaction);
        command.Parameters.AddWithValue("id", server);
        return (DateTime)(await command.ExecuteScalarAsync())!;
    }
}
