using System.Data.Common;
using FluentAssertions;
using LearnStack.Api.Jobs;
using LearnStack.Api.Messaging;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Contracts.IntegrationEvents;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRuntimeFacadeTests
{
    [Fact]
    public async Task Supported_pool_serves_native_business_during_combined_actual_messaging_and_expiration_ownership()
    {
        await using var database = await DatabaseAsync();
        var applicationName = "combined_capacity_" + Guid.NewGuid().ToString("N");
        var connectionString = new NpgsqlConnectionStringBuilder(database.OutboxConnectionString)
        { Pooling = true, MaxPoolSize = 52, ApplicationName = applicationName }.ConnectionString;
        var outboxHold = new CombinedTerminalHold();
        var deliveryHold = new CombinedTerminalHold();
        // Uncommitted unique-key contention identifies the exact stock expiration
        // connector through pg_blocking_pids; no provider SQL/hook is replaced.
        await using var blocker = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var blockedTransaction = await blocker.BeginTransactionAsync();
        await SchemaQueries.ExecuteAsync(blocker, blockedTransaction,
            "INSERT INTO public.lock(resource,acquired) VALUES ('locks:expirationmanager',clock_timestamp()+interval '1 hour')");
        var expirationPid = 0;
        var organization = Guid.CreateVersion7();
        PrivilegedDataSource? observedSource = null;
        HangfireSharedComponentWorker? expiration = null;
        var bodyObserved = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new BackgroundJobFixtureProbe
        {
            Body = async (request, services, token) =>
            {
                request.Should().Be("TenantRoot");
                AssertCombinedHolds(outboxHold, deliveryHold, expiration, observedSource);
                bodyObserved.TrySetResult(PoolStatistics(observedSource!).Busy);
                var result = await services.GetRequiredService<ISender>().Send(new CreateOrganizationCommand(
                    OrganizationId.From(organization), "combined-" + organization.ToString("N")[..12], "Combined capacity"), token);
                result.IsSuccess.Should().BeTrue();
                return Result.Ok(None.Value);
            },
        };
        await using var host = Fixture.Host(database, probe, configure: services =>
        {
            services.AddSingleton(new OutboxDispatchOptions { IdlePoll = TimeSpan.FromMilliseconds(20) });
            services.AddSingleton(new DeliveryDispatchOptions { IdlePoll = TimeSpan.FromMilliseconds(20) });
            services.AddScoped<PostgresAuditStore>();
            services.Replace(ServiceDescriptor.Scoped<IAuditStore>(provider =>
                new CombinedTerminalAuditStore(provider.GetRequiredService<PostgresAuditStore>(), outboxHold, deliveryHold)));
        }).WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:OutboxDispatcher", connectionString));
        using var globals = new GlobalFilterSnapshot();
        var services = host.Services;
        var source = services.GetRequiredKeyedService<PrivilegedDataSource>(OutboxDispatcherDataSource.Key);
        observedSource = source;
        var jobs = services.GetRequiredService<BackgroundJobHostedService>();
        var runtime = services.GetRequiredService<BackgroundJobRuntime>();
        var outbox = services.GetRequiredService<OutboxProcessor>();
        var delivery = services.GetRequiredService<DeliveryProcessor>();
        foreach (var owner in new object[] { runtime, outbox, delivery })
            owner.GetType().GetField("_source", PrivateInstance)!.GetValue(owner).Should().BeSameAs(source);
        typeof(BackgroundJobRuntime).GetField("_outboxCompletion", PrivateInstance)!.GetValue(runtime)
            .Should().BeSameAs(outbox.PrivilegedCompletion);
        typeof(BackgroundJobRuntime).GetField("_deliveryCompletion", PrivateInstance)!.GetValue(runtime)
            .Should().BeSameAs(delivery.PrivilegedCompletion);
        var subscription = services.GetRequiredService<OutboxEventRegistry>()
            .Resolve(typeof(TenantCreatedIntegrationEventV1).AssemblyQualifiedName!).Subscriptions[0].Id;
        try
        {
            // ConstructionOnlyApiFactory removes hosted aliases. Start their
            // genuine registered instances explicitly; this is not a proof of
            // GenericHost ordering, nor a direct facade with completed witnesses.
            await outbox.StartAsync(CancellationToken.None);
            await delivery.StartAsync(CancellationToken.None);
            await jobs.StartAsync(CancellationToken.None);
            await EventuallyAsync(() => Task.FromResult(runtime.Snapshot.IsHealthy));
            await AssertGuardedPoolAsync(source, new(true, 52));
            expiration = ActualExpiration(runtime);
            await EventuallyAsync(async () =>
            {
                expirationPid = await CombinedBlockedExpirationPidAsync(database, applicationName, blocker.ProcessID);
                return expirationPid > 0;
            });
            await EventuallyAsync(() => Task.FromResult(expiration.Snapshot is
            { Invocations: 1, OwnsResources: true, ActualJoined: false }));
            using (var callerWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
                await FluentActions.Awaiting(() => expiration.StopAsync(callerWait.Token))
                    .Should().ThrowAsync<OperationCanceledException>();
            await EventuallyAsync(() => Task.FromResult(expiration.Snapshot.State == HangfireSharedComponentState.Retained));
            var outboxEvent = await InsertComposedTerminalFixtureAsync(database, false, subscription);
            var deliveryEvent = await InsertComposedTerminalFixtureAsync(database, true, subscription);
            await Task.WhenAll(outboxHold.Signals.Entered.Task, deliveryHold.Signals.Entered.Task).WaitAsync(TestWait);
            await AssertComposedTerminalCommittedAsync(database, false, outboxEvent, subscription);
            await AssertComposedTerminalCommittedAsync(database, true, deliveryEvent, subscription);
            outbox.ExecuteTask!.IsCompleted.Should().BeFalse();
            delivery.ExecuteTask!.IsCompleted.Should().BeFalse();
            outbox.PrivilegedCompletion.IsCompleted.Should().BeFalse();
            delivery.PrivilegedCompletion.IsCompleted.Should().BeFalse();
            AssertCombinedHolds(outboxHold, deliveryHold, expiration, source);
            await AssertCombinedBackendsAsync(database, applicationName, outboxHold, deliveryHold, expirationPid);

            // The same predicate must reject a missing owner, rather than
            // accepting unrelated physical activity as the combined cohort.
            Action absentOwner = () => AssertCombinedHolds(new CombinedTerminalHold(), deliveryHold, expiration, source);
            absentOwner.Should().Throw<Xunit.Sdk.XunitException>();
            var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString, tenant: SchemaFixture.TenantB);
            (await bodyObserved.Task.WaitAsync(TestWait)).Should().BeInRange(3, 52);
            await EventuallyAsync(async () => (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString,
                job, tenant: SchemaFixture.TenantB))?.Phase == "completed");
            await AssertCombinedBusinessAsync(database, organization);
            await EventuallyAsync(() => CombinedNativeSucceededAsync(database, job));
            outbox.ExecuteTask!.IsCompleted.Should().BeFalse();
            delivery.ExecuteTask!.IsCompleted.Should().BeFalse();
            outbox.PrivilegedCompletion.IsCompleted.Should().BeFalse();
            delivery.PrivilegedCompletion.IsCompleted.Should().BeFalse();
            AssertCombinedHolds(outboxHold, deliveryHold, expiration, source);
            await AssertCombinedBackendsAsync(database, applicationName, outboxHold, deliveryHold, expirationPid);
            runtime.Snapshot.RunningPools.Should().Be(3);
            testOutput.WriteLine("Supported52 pool: real expiration + two distinct terminal-scope physical checkouts held; tenantB native business/MUST audit committed before release. Representative contention, not exact52 saturation.");
        }
        finally
        {
            outboxHold.Signals.Release.TrySetResult();
            deliveryHold.Signals.Release.TrySetResult();
            await blockedTransaction.RollbackAsync();
            using var cleanup = new CancellationTokenSource(TestWait);
            await Task.WhenAll(jobs.StopAsync(cleanup.Token), outbox.StopAsync(cleanup.Token), delivery.StopAsync(cleanup.Token));
            await Task.WhenAll(runtime.Completion, outbox.PrivilegedCompletion, delivery.PrivilegedCompletion).WaitAsync(cleanup.Token);
            PoolStatistics(source).Busy.Should().Be(0);
            await host.DisposeAsync();
            await SourceDisposalObserver(source).WaitAsync(cleanup.Token);
        }
        outboxHold.Signals.Joined.Task.IsCompletedSuccessfully.Should().BeTrue();
        deliveryHold.Signals.Joined.Task.IsCompletedSuccessfully.Should().BeTrue();
        expiration!.Snapshot.ActualJoined.Should().BeTrue();
        expiration.Snapshot.OwnsResources.Should().BeFalse();
        source.IsDisposalComplete.Should().BeTrue();
        source.HasUnconfirmedCleanup.Should().BeFalse();
        (await IndependentServerCountAsync(database)).Should().Be(0);
        await EventuallyAsync(async () => await PoolBackendCountAsync(database, applicationName) == 0);
    }

    private static void AssertCombinedHolds(CombinedTerminalHold outbox, CombinedTerminalHold delivery,
        HangfireSharedComponentWorker? expiration, PrivilegedDataSource? source)
    {
        outbox.Signals.Entered.Task.IsCompletedSuccessfully.Should().BeTrue();
        delivery.Signals.Entered.Task.IsCompletedSuccessfully.Should().BeTrue();
        outbox.Signals.Release.Task.IsCompleted.Should().BeFalse();
        delivery.Signals.Release.Task.IsCompleted.Should().BeFalse();
        outbox.Signals.Joined.Task.IsCompleted.Should().BeFalse();
        delivery.Signals.Joined.Task.IsCompleted.Should().BeFalse();
        outbox.ProcessId.Should().BeGreaterThan(0).And.NotBe(delivery.ProcessId);
        delivery.ProcessId.Should().BeGreaterThan(0);
        expiration.Should().NotBeNull();
        expiration!.Snapshot.Should().BeEquivalentTo(new { State = HangfireSharedComponentState.Retained, Invocations = 1L, OwnsResources = true, ActualJoined = false });
        source.Should().NotBeNull();
        PoolStatistics(source!).Busy.Should().BeInRange(3, 52);
    }

    private static async Task<int> CombinedBlockedExpirationPidAsync(DisposableSchemaDatabase database, string applicationName,
        int blockerPid)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var command = new NpgsqlCommand("""
            SELECT pid FROM pg_stat_activity WHERE datname=current_database()
                AND application_name=@application AND usename='learnstack_outbox_admin'
                AND @blocker=ANY(pg_blocking_pids(pid))
            """, connection);
        command.Parameters.AddWithValue("application", applicationName);
        command.Parameters.AddWithValue("blocker", blockerPid);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return 0;
        var pid = reader.GetInt32(0);
        (await reader.ReadAsync()).Should().BeFalse("only the exact expiration resource is blocked by this transaction");
        return pid;
    }

    private static async Task AssertCombinedBackendsAsync(DisposableSchemaDatabase database, string applicationName,
        CombinedTerminalHold outbox, CombinedTerminalHold delivery, int expirationPid)
    {
        expirationPid.Should().BeGreaterThan(0).And.NotBe(outbox.ProcessId).And.NotBe(delivery.ProcessId);
        // Observer/setup source is outside the bounded shared pool, so it cannot
        // manufacture its pressure. All three exact captured provider PIDs must still
        // be real connections of the confined role and this actual keyed source.
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM pg_stat_activity WHERE datname=current_database()
                AND application_name=@application AND usename='learnstack_outbox_admin' AND pid=ANY(@pids)
            """, connection);
        command.Parameters.AddWithValue("application", applicationName);
        command.Parameters.AddWithValue("pids", new[] { outbox.ProcessId, delivery.ProcessId, expirationPid });
        ((long)(await command.ExecuteScalarAsync())!).Should().Be(3);
    }

    private static async Task<bool> CombinedNativeSucceededAsync(DisposableSchemaDatabase database, BackgroundJobId job)
    {
        // Independent observer is outside the supported pool. The mutable hint
        // is cleared by business completion; inspect the actual stock row.
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM public.job WHERE arguments->>0=@job
                AND arguments->>1='1' AND statename='Succeeded')
            """, connection);
        command.Parameters.AddWithValue("job", System.Text.Json.JsonSerializer.Serialize(job.Value));
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task AssertCombinedBusinessAsync(DisposableSchemaDatabase database, Guid organization)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(connection, transaction);
        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantB);
        await using var command = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM organizations WHERE tenant_id=@tenant AND id=@organization)=1
                AND (SELECT count(*) FROM audit_log WHERE tenant_id=@tenant
                    AND operation='tenancy.organization.create' AND operation_class='Must'
                    AND entity_id=@organization::text)=1
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", SchemaFixture.TenantB);
        command.Parameters.AddWithValue("organization", organization);
        (await command.ExecuteScalarAsync()).Should().Be(true);
    }

    private sealed class CombinedTerminalHold
    {
        internal FacadeCleanupSignals Signals { get; } = new();
        internal int ProcessId;
    }

    private sealed class CombinedTerminalAuditStore(PostgresAuditStore inner, CombinedTerminalHold outbox,
        CombinedTerminalHold delivery) : IAuditStore, IAsyncDisposable
    {
        private CombinedTerminalHold? _hold;
        public Task WritePendingAsync(IUnitOfWork unit, CancellationToken token = default) => inner.WritePendingAsync(unit, token);
        public Task WriteStandaloneAsync(AuditEntryDraft entry, CancellationToken token = default) => inner.WriteStandaloneAsync(entry, token);
        public Task WriteBestEffortAsync(AuditEntryDraft entry, CancellationToken token = default) => inner.WriteBestEffortAsync(entry, token);
        public Task WritePlatformScopeAsync(AuditEntryDraft entry, DbConnection connection, DbTransaction transaction,
            CancellationToken token = default) => inner.WritePlatformScopeAsync(entry, connection, transaction, token);
        public Task WriteJobTerminalAsync(AuditEntryDraft entry, JobTerminalTransition transition, DbConnection connection,
            DbTransaction transaction, CancellationToken token = default) => inner.WriteJobTerminalAsync(entry, transition, connection, transaction, token);
        public async Task WriteEventTerminalAsync(AuditEntryDraft entry, EventTerminalTransition transition, DbConnection connection,
            DbTransaction transaction, CancellationToken token = default)
        {
            await inner.WriteEventTerminalAsync(entry, transition, connection, transaction, token);
            _hold = entry.Operation switch
            {
                "platform.outbox.dead_letter" => outbox,
                "platform.delivery.dead_letter" => delivery,
                _ => throw new InvalidOperationException("Unexpected terminal operation in the combined fixture."),
            };
            Interlocked.CompareExchange(ref _hold.ProcessId, ((NpgsqlConnection)connection).ProcessID, 0).Should().Be(0);
        }
        public async ValueTask DisposeAsync()
        {
            if (_hold is not { } hold) return;
            hold.Signals.Entered.TrySetResult();
            await hold.Signals.Release.Task.ConfigureAwait(false);
            hold.Signals.Joined.TrySetResult();
        }
    }
}
