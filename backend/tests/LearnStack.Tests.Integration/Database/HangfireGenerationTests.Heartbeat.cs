using FluentAssertions;
using System.Reflection;
using Hangfire.PostgreSql;
using Hangfire.Storage;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class HangfireGenerationTests
{
    [Fact]
    public async Task Real_swallowed_fetched_renewal_failure_stays_unhealthy_until_actual_storage_recovery()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var host = Host(database);
        await using var source = Source(database);
        using var globals = new GlobalFilterSnapshot();
        using var deadline = new CancellationTokenSource(TestWait);
        var topology = host.Services.GetRequiredService<BackgroundJobDefinitionRegistry>().Infrastructure;
        var gate = new BackgroundJobRuntimeGate(topology);
        var diagnostics = new HangfireBoundedLogProvider();
        var provider = HangfireStorageBootstrap.Create(source, topology, diagnostics, gate);
        var pool = topology.Pools["transactional"];
        var vendor = CreateOrphanWakeup(provider, HangfireRuntimeFilters.Create(), pool);
        using var connection = provider.GetConnection();
        using var fetched = FetchWithAcceleratedRenewal(provider, connection, pool.Queues.ToArray(), deadline.Token);
        fetched.JobId.Should().Be(vendor);
        var initialFetchedAt = await FetchedAtAsync(source, vendor, deadline.Token);
        var shared = new HangfireSharedComponents(new(provider), gate, TimeProvider.System, diagnostics);
        try
        {
            await SetFetchedRenewalPrivilegeAsync(database, grant: false, deadline.Token);
            await using (var sql = await source.OpenConnectionAsync(deadline.Token))
            await using (var privilege = new NpgsqlCommand(
                "SELECT has_column_privilege(current_user,'public.jobqueue','fetchedat','UPDATE')", sql))
                (await privilege.ExecuteScalarAsync(deadline.Token)).Should().Be(false);

            shared.Start();
            HangfireSharedComponentSnapshot? refused = null;
            await EventuallyAsync(() =>
            {
                var snapshot = shared.FetchedJobHeartbeat;
                // Waiting with actual join proves the stock pass returned and
                // cleanup finished; the provider swallows the renewal exception.
                if (snapshot.State == HangfireSharedComponentState.Waiting && snapshot.ActualJoined
                    && snapshot.Outcome == HangfireSharedComponentOutcome.Failed && snapshot.StorageFault)
                    refused = snapshot;
                return Task.FromResult(refused is not null);
            });

            var failedPass = refused ?? throw new InvalidOperationException("The failed renewal pass was not observed.");
            failedPass.IsHealthy.Should().BeFalse();
            failedPass.OwnsResources.Should().BeFalse();
            diagnostics.FetchedHeartbeat.Faults.Should().BeGreaterThan(0);
            diagnostics.FetchedHeartbeat.Renewals.Should().Be(0);
            (await FetchedAtAsync(source, vendor, deadline.Token)).Should().Be(initialFetchedAt);
            await AssertNoBusinessAdmissionAsync(source);

            await SetFetchedRenewalPrivilegeAsync(database, grant: true, deadline.Token);
            await EventuallyAsync(async () =>
                await FetchedAtAsync(source, vendor, deadline.Token) > initialFetchedAt
                && diagnostics.FetchedHeartbeat.Renewals > 0
                && shared.FetchedJobHeartbeat is { StorageFault: false, IsHealthy: true });

            // Old fault evidence remains recorded. Recovery comes from actual
            // renewed fetched-at storage, not erasing a counter or an empty pass.
            diagnostics.FetchedHeartbeat.Faults.Should().BeGreaterThan(0);
            shared.FetchedJobHeartbeat.StorageFault.Should().BeFalse();
            shared.FetchedJobHeartbeat.IsHealthy.Should().BeTrue();
            await AssertNoBusinessAdmissionAsync(source);
        }
        finally
        {
            using var cleanupBudget = new CancellationTokenSource(TestWait);
            try { await SetFetchedRenewalPrivilegeAsync(database, grant: true, cleanupBudget.Token); }
            finally
            {
                gate.StopStarts();
                shared.RequestStop();
                await shared.StopAsync(cleanupBudget.Token);
            }
        }

        shared.Completion.IsCompletedSuccessfully.Should().BeTrue();
        shared.FetchedJobHeartbeat.ActualJoined.Should().BeTrue();
        shared.FetchedJobHeartbeat.OwnsResources.Should().BeFalse();
        fetched.RemoveFromQueue();
        await AssertNoBusinessAdmissionAsync(source);
    }

    private static IFetchedJob FetchWithAcceleratedRenewal(PostgreSqlStorage provider,
        IStorageConnection connection, string[] queues, CancellationToken cancellationToken)
    {
        var options = typeof(PostgreSqlStorage).GetProperty("Options", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(provider) as PostgreSqlStorageOptions
            ?? throw new InvalidOperationException("The pinned provider options are missing.");
        var original = options.InvisibilityTimeout;
        try
        {
            // Test-only cadence acceleration: the real fetched object snapshots
            // one fifth of this value as its interval. Reflection only obtains
            // test cadence configuration; stock code/SQL and the
            // shared component's one-second pacing remain unchanged.
            options.InvisibilityTimeout = TimeSpan.FromSeconds(1);
            return connection.FetchNextJob(queues, cancellationToken);
        }
        finally { options.InvisibilityTimeout = original; }
    }

    private static async Task<DateTime> FetchedAtAsync(PrivilegedDataSource source, string vendor,
        CancellationToken cancellationToken)
    {
        await using var sql = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT fetchedat FROM public.jobqueue WHERE jobid=@job::bigint", sql);
        command.Parameters.AddWithValue("job", vendor);
        return (DateTime)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task SetFetchedRenewalPrivilegeAsync(DisposableSchemaDatabase database,
        bool grant, CancellationToken cancellationToken)
    {
        await using var sql = (NpgsqlConnection)await PostgresFixture.OpenAsync(
            database.MigrationConnectionString, cancellationToken);
        await using var command = new NpgsqlCommand(grant
            ? "GRANT UPDATE (fetchedat) ON public.jobqueue TO learnstack_outbox_admin"
            : "REVOKE UPDATE (fetchedat) ON public.jobqueue FROM learnstack_outbox_admin", sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
