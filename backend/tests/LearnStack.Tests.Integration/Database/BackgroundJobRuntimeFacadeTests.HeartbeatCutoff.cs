using System.Globalization;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.PostgreSql;
using Hangfire.States;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRuntimeFacadeTests
{
    private static readonly TimeSpan HeartbeatQualificationWait = TimeSpan.FromSeconds(90);

    [Fact]
    public async Task Actual_stored_metadata_heartbeat_resets_the_owner_cutoff_and_the_exact_boundary_issues_no_SQL()
    {
        await using var database = await DatabaseAsync();
        await using var host = Fixture.Host(database, new BackgroundJobFixtureProbe());
        await using var source = Source(database);
        using var globals = new GlobalFilterSnapshot();
        using var deadline = new CancellationTokenSource(HeartbeatQualificationWait);
        var time = new HangfirePoolOwnerTests.ManualTimeProvider();
        var runtime = Runtime(host.Services, source, timeProvider: time);
        await using var blocker = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString, deadline.Token);
        NpgsqlTransaction? held = null;
        Task? callback = null;
        try
        {
            await runtime.StartAsync(deadline.Token);
            await EventuallyQualificationAsync(() => Task.FromResult(runtime.Snapshot.IsHealthy), deadline.Token);
            var generation = ActualGeneration(runtime, "transactional");
            HangfireGeneration.HeartbeatCutoff.Should().Be(TimeSpan.FromMinutes(4));
            HangfireGeneration.HeartbeatPacing.Should().Be(TimeSpan.FromSeconds(1));
            var provider = ActualProvider(runtime);
            var storage = QualificationStorage(generation);
            var server = await QualificationServerAsync(source, "transactional", deadline.Token);
            held = await blocker.BeginTransactionAsync(deadline.Token);
            await LockQualificationServerAsync(blocker, held, server, deadline.Token);
            var prior = await StoredHeartbeatAsync(blocker, held, server);
            var operation = MetadataHeartbeatOperation(provider, storage, server);
            // This invokes the exact production owner callback and real stock
            // SQL. Core's separate thirty-second native cadence is unchanged.
            callback = Task.Run(() => generation.Heartbeat(operation), deadline.Token);
            await EventuallyQualificationAsync(() => Task.FromResult(generation.Snapshot.HeartbeatFault
                && time.HasDelay(HangfireGeneration.HeartbeatPacing)), deadline.Token);
            LifecycleAttempted(operation).Should().BeTrue();
            generation.Snapshot.Draining.Should().BeFalse();
            (await StoredHeartbeatAsync(blocker, held, server)).Should().Be(prior);

            // Establish the actual transient SQL premise independently while the
            // owned callback waits on its existing one-second logical pacing.
            var stockTimeout = () =>
            {
                using var connection = provider.GetConnection();
                connection.Heartbeat(server);
            };
            stockTimeout.Should().Throw<PostgresException>().Which.SqlState.Should()
                .BeOneOf(PostgresErrorCodes.QueryCanceled, PostgresErrorCodes.LockNotAvailable);

            time.Advance(TimeSpan.FromMinutes(3));
            await held.RollbackAsync(deadline.Token);
            await held.DisposeAsync();
            held = null;
            await callback.WaitAsync(deadline.Token);
            generation.Snapshot.HeartbeatFault.Should().BeFalse();
            generation.Snapshot.Draining.Should().BeFalse();
            var recovered = await StoredHeartbeatAsync(blocker, null, server);
            recovered.Should().BeAfter(prior, "only actual stored success resets the owner deadline");

            // Cross the original announcement cutoff, but remain inside the
            // window established by the stored success at logical minute three.
            time.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromTicks(1));
            var afterOriginalCutoff = MetadataHeartbeatOperation(provider, storage, server);
            callback = Task.Run(() => generation.Heartbeat(afterOriginalCutoff), deadline.Token);
            await callback.WaitAsync(deadline.Token);
            LifecycleAttempted(afterOriginalCutoff).Should().BeTrue();
            generation.Snapshot.Draining.Should().BeFalse();
            generation.Snapshot.HeartbeatFault.Should().BeFalse();
            (await StoredHeartbeatAsync(blocker, null, server)).Should().BeAfter(recovered);

            held = await blocker.BeginTransactionAsync(deadline.Token);
            await LockQualificationServerAsync(blocker, held, server, deadline.Token);
            var atBoundary = await StoredHeartbeatAsync(blocker, held, server);
            time.Advance(TimeSpan.FromMinutes(4));
            var expired = MetadataHeartbeatOperation(provider, storage, server);
            callback = Task.Run(() => generation.Heartbeat(expired), deadline.Token);
            await EventuallyQualificationAsync(() => Task.FromResult(generation.Snapshot.Draining || LifecycleAttempted(expired)), deadline.Token);
            LifecycleAttempted(expired).Should().BeFalse("at/past cutoff the owner must refuse before any provider acquisition or SQL");
            generation.Snapshot.Reason.Should().Be(HangfireGenerationReason.HeartbeatTransient);
            (await StoredHeartbeatAsync(blocker, held, server)).Should().Be(atBoundary);
            await FluentActions.Awaiting(() => callback.WaitAsync(deadline.Token))
                .Should().ThrowAsync<InvalidOperationException>();
            generation.Snapshot.NormalChildrenJoined.Should().BeTrue();
            generation.Completion.IsCompleted.Should().BeFalse("the actual removal still waits for this row lock");
        }
        finally
        {
            runtime.RequestStop();
            using var cleanup = new CancellationTokenSource(HeartbeatQualificationWait);
            if (held is not null)
            {
                await held.RollbackAsync(cleanup.Token);
                await held.DisposeAsync();
            }
            if (callback is not null)
            {
                try { await callback.WaitAsync(cleanup.Token); }
                catch (InvalidOperationException) { } // The closed stop/cutoff refusal was asserted above.
            }
            await runtime.StopAsync(cleanup.Token);
            await runtime.Completion.WaitAsync(cleanup.Token);
            await DisposePinnedSourceAsync(source);
        }
    }

    [Fact]
    public async Task Actual_common_database_outage_drains_all_pools_and_recovery_keeps_the_old_held_pool_closed_despite_its_spare_slot()
    {
        // This local fixture owns its container. The collection's shared schema
        // container is never paused or otherwise used by the outage control.
        var postgres = new PostgresFixture();
        try
        {
            await postgres.InitializeAsync();
            await using var database = await DisposableSchemaDatabase.CreateAsync(postgres);
            await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
            var cleanupSignals = new FacadeCleanupSignals();
            var reads = 0;
            var observation = new BackgroundJobAdmissionObservation
            {
                OnRead = (_query, services) =>
                {
                    if (Interlocked.Increment(ref reads) == 1)
                        _ = services.GetRequiredService<FacadeHeldReadCleanup>();
                    return Task.CompletedTask;
                },
            };
            var probe = new BackgroundJobFixtureProbe();
            await using var host = Fixture.Host(database, probe, configure: services =>
            {
                observation.Configure(services);
                services.AddScoped(_ => new FacadeHeldReadCleanup(cleanupSignals));
            });
            var physical = new BackgroundJobPhysicalPoolSettings(true, 45);
            await using var source = Source(database, physical);
            using var globals = new GlobalFilterSnapshot();
            using var deadline = new CancellationTokenSource(HeartbeatQualificationWait);
            var time = new HangfirePoolOwnerTests.ManualTimeProvider();
            var bounds = new BackgroundJobRuntimeOptions { TransactionTimeout = TimeSpan.FromSeconds(2) };
            var runtime = Runtime(host.Services, source, bounds, physical, time);
            var original = new Dictionary<string, HangfireGeneration>(StringComparer.Ordinal);
            var paused = false;
            try
            {
                await runtime.StartAsync(deadline.Token);
                var owners = QualificationPoolOwners(runtime);
                owners.Keys.Should().BeEquivalentTo(Fixture.Registry.Infrastructure.Pools.Keys);
                await EventuallyQualificationAsync(() => Task.FromResult(owners.Values.All(owner => owner.Snapshot.Generation is not null)), deadline.Token);
                foreach (var name in owners.Keys) original.Add(name, ActualGeneration(runtime, name));
                await EventuallyQualificationAsync(() => Task.FromResult(runtime.Snapshot.IsHealthy), deadline.Token);
                var provider = ActualProvider(runtime);
                var transactionalPool = Fixture.Registry.Infrastructure.Pools["transactional"];
                var affected = original["transactional"];
                HangfireGeneration.HeartbeatCutoff.Should().Be(TimeSpan.FromMinutes(4));
                HangfireGeneration.HeartbeatPacing.Should().Be(TimeSpan.FromSeconds(1));
                var heldJob = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
                var heldVendor = CreateQualificationWakeup(provider, transactionalPool, heldJob);
                await cleanupSignals.Entered.Task.WaitAsync(deadline.Token);
                await EventuallyQualificationAsync(() => Task.FromResult(time.HasDelay(bounds.TransactionTimeout)), deadline.Token);
                // Only the owner observation clock advances. The held disposable
                // is a synthetic scope-cleanup barrier around actual native work.
                time.Advance(bounds.TransactionTimeout);
                await EventuallyQualificationAsync(async () => affected.WorkSnapshot.Retained == 1
                    && await StateAsync(source, heldVendor) == "Succeeded", deadline.Token);
                affected.WorkSnapshot.Should().Be(new HangfireGenerationWorkSnapshot(0, 1, false));
                affected.TryAdmitInvocation().Should().BeTrue();
                cleanupSignals.Joined.Task.IsCompleted.Should().BeFalse();
                var beforeOutage = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
                var beforeVendor = CreateQualificationWakeup(provider, transactionalPool, beforeOutage);
                await EventuallyQualificationAsync(async () => await StateAsync(source, beforeVendor) == "Succeeded"
                    && await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, beforeOutage) == 1, deadline.Token);

                paused = true;
                await postgres.PauseOwnedServerAsync(deadline.Token);
                // Core still uses its real thirty-second heartbeat cadence and
                // the actual guarded provider's finite native SQL/socket bounds.
                await EventuallyQualificationAsync(() => Task.FromResult(original.Values.All(generation => generation.Snapshot.HeartbeatFault)), deadline.Token);
                original.Values.Should().OnlyContain(generation => !generation.Snapshot.Draining);
                time.Advance(TimeSpan.FromMinutes(4));
                // Shared failure backoff also uses one second, so a global timer
                // count cannot witness every heartbeat's pacing registration.
                // Bounded owner-clock pulses cover a late registration without
                // extending into the shared five-minute invocation window. This
                // case proves past-cutoff drain; the separate case pins equality.
                for (var pulse = 0; pulse < 16 && original.Values.Any(generation => !generation.Snapshot.Draining); pulse++)
                {
                    time.Advance(HangfireGeneration.HeartbeatPacing);
                    await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token);
                }
                original.Values.Should().OnlyContain(generation => generation.Snapshot.Draining);
                original.Values.Should().OnlyContain(generation => generation.Snapshot.Reason == HangfireGenerationReason.HeartbeatTransient);
                await postgres.UnpauseOwnedServerAsync(deadline.Token);
                paused = false;

                var cleanOwners = owners.Where(pair => pair.Key != "transactional").Select(pair => pair.Value).ToArray();
                await Task.WhenAll(original.Where(pair => pair.Key != "transactional").Select(pair => pair.Value.Completion))
                    .WaitAsync(deadline.Token);
                await EventuallyQualificationAsync(() => Task.FromResult(cleanOwners.All(owner => owner.Snapshot.State == HangfirePoolOwnerState.Backoff)
                    && time.DelayCount(HangfireGeneration.ReplacementBackoff) >= cleanOwners.Length), deadline.Token);
                time.Advance(HangfireGeneration.ReplacementBackoff);
                await EventuallyQualificationAsync(() => Task.FromResult(cleanOwners.All(owner => owner.Snapshot is
                { State: HangfirePoolOwnerState.Running, Generations: 2 })), deadline.Token);
                var externalPool = Fixture.Registry.Infrastructure.Pools["external"];
                var healthyVendor = HangfireGenerationTests.CreateOrphanWakeup(provider, HangfireRuntimeFilters.Create(), externalPool);
                await EventuallyQualificationAsync(async () => await StateAsync(source, healthyVendor) == "Succeeded", deadline.Token);

                var pendingJob = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
                var pendingVendor = CreateQualificationWakeup(provider, transactionalPool, pendingJob);
                await EventuallyQualificationAsync(() => Task.FromResult(affected.Snapshot.OuterJoined), deadline.Token);
                affected.Snapshot.NormalChildrenJoined.Should().BeTrue();
                affected.Completion.IsCompleted.Should().BeFalse();
                affected.TryAdmitInvocation().Should().BeFalse();
                affected.WorkSnapshot.Should().Be(new HangfireGenerationWorkSnapshot(0, 1, true));
                owners["transactional"].Snapshot.Generations.Should().Be(1);
                ActualGeneration(runtime, "transactional").Should().BeSameAs(affected);
                // Cross another existing replacement backoff with healthy real
                // storage. Neither recovery nor a free native slot supplies join.
                time.Advance(HangfireGeneration.ReplacementBackoff);
                (await StateAsync(source, pendingVendor)).Should().Be("Enqueued");
                (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, pendingJob)).Should().Be(0);
                (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, heldJob)).Should().Be(0);
                owners["transactional"].Snapshot.Generations.Should().Be(1);
                ActualGeneration(runtime, "transactional").Should().BeSameAs(affected);
                cleanupSignals.Joined.Task.IsCompleted.Should().BeFalse();
                await AssertGuardedPoolAsync(source, physical);
                probe.Constructions.Should().BeEmpty("Step 4 qualifies actual framework transport without business activation");

                cleanupSignals.Release.TrySetResult();
                await affected.Completion.WaitAsync(deadline.Token);
                cleanupSignals.Joined.Task.IsCompletedSuccessfully.Should().BeTrue();
                await EventuallyQualificationAsync(() => Task.FromResult(owners["transactional"].Snapshot.State == HangfirePoolOwnerState.Backoff
                    && time.HasDelay(HangfireGeneration.ReplacementBackoff)), deadline.Token);
                time.Advance(HangfireGeneration.ReplacementBackoff);
                await EventuallyQualificationAsync(() => Task.FromResult(owners["transactional"].Snapshot is
                { State: HangfirePoolOwnerState.Running, Generations: 2 }), deadline.Token);
                ActualGeneration(runtime, "transactional").Should().NotBeSameAs(affected);
                await EventuallyQualificationAsync(async () => await StateAsync(source, pendingVendor) == "Succeeded"
                    && await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, pendingJob) == 1, deadline.Token);
                (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, heldJob)).Should().Be(0,
                    "the old expired invocation's late cleanup cannot reopen its admission");
                owners["transactional"].Snapshot.Generations.Should().Be(2);
            }
            finally
            {
                using var cleanup = new CancellationTokenSource(HeartbeatQualificationWait);
                // Always restore the real database before resource/row cleanup,
                // including failed assertions and intentionally broken mutants.
                if (paused) await postgres.UnpauseOwnedServerAsync(cleanup.Token);
                cleanupSignals.Release.TrySetResult();
                runtime.RequestStop();
                foreach (var generation in original.Values) generation.RequestHostStop();
                await Task.WhenAll(original.Values.Select(generation => generation.Completion)).WaitAsync(cleanup.Token);
                await runtime.StopAsync(cleanup.Token);
                await runtime.Completion.WaitAsync(cleanup.Token);
                await DisposePinnedSourceAsync(source);
            }
        }
        finally { await postgres.DisposeAsync(); }
    }

    private static PoolHangfireStorage QualificationStorage(HangfireGeneration generation) =>
        typeof(HangfireGeneration).GetField("_storage", PrivateInstance)?.GetValue(generation) as PoolHangfireStorage
        ?? throw new InvalidOperationException("The actual generation storage is missing.");

    private static PoolHangfireLifecycleOperation MetadataHeartbeatOperation(PostgreSqlStorage provider,
        PoolHangfireStorage storage, string server) => new(provider, false, PoolHangfireLifecycleMethod.Heartbeat, server, null, storage);

    private static bool LifecycleAttempted(PoolHangfireLifecycleOperation operation) =>
        (bool)(typeof(PoolHangfireLifecycleOperation).GetField("_attempted", PrivateInstance)?.GetValue(operation)
            ?? throw new InvalidOperationException("The actual lifecycle issuance observation is missing."));

    private static Dictionary<string, HangfirePoolOwner> QualificationPoolOwners(BackgroundJobRuntime runtime) =>
        (typeof(BackgroundJobRuntime).GetField("_pools", PrivateInstance)?.GetValue(runtime) as IEnumerable<HangfirePoolOwner>
            ?? throw new InvalidOperationException("The actual pool owner inventory is missing."))
        .ToDictionary(owner => owner.Snapshot.Pool, StringComparer.Ordinal);

    private static async Task<string> QualificationServerAsync(LearnStack.Infrastructure.Persistence.PrivilegedDataSource source,
        string pool, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT id FROM public.server WHERE id LIKE @prefix", connection);
        command.Parameters.AddWithValue("prefix", "learnstack-" + pool + ":%");
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task LockQualificationServerAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string server, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT id FROM public.server WHERE id=@id FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("id", server);
        (await command.ExecuteScalarAsync(cancellationToken)).Should().Be(server);
    }

    private static string CreateQualificationWakeup(PostgreSqlStorage provider, BackgroundJobWorkerPoolRegistration pool, BackgroundJobId id)
    {
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            var job = new Job(typeof(HangfireJobWakeup), HangfireInvocationContract.JobMethod, [id, 1L], pool.Queues[0]);
            return new BackgroundJobClient(provider, HangfireRuntimeFilters.Create()) { RetryAttempts = 0 }
                .Create(job, new EnqueuedState(pool.Queues[0]))
                ?? throw new InvalidOperationException("The real qualification wakeup was not created.");
        }
        finally { CultureInfo.CurrentCulture = culture; CultureInfo.CurrentUICulture = uiCulture; }
    }

    private static async Task EventuallyQualificationAsync(Func<Task<bool>> predicate, CancellationToken cancellationToken)
    {
        while (!await predicate()) await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
    }
}
