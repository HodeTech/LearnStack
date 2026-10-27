using FluentAssertions;
using LearnStack.Api.Composition;
using LearnStack.Api.Jobs;
using LearnStack.Api.Messaging;
using LearnStack.Api.Tenancy;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Contracts.IntegrationEvents;
using LearnStack.Modules.Identity.Infrastructure.Authorization;
using LearnStack.SharedKernel.Audit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRuntimeFacadeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_API_composition_keeps_both_sources_after_host_disposal_until_the_last_messaging_SQL_owner_joins(bool holdDelivery)
    {
        await using var database = await DatabaseAsync();
        var applicationName = "composed_lifetime_" + Guid.NewGuid().ToString("N");
        var connectionString = new NpgsqlConnectionStringBuilder(database.OutboxConnectionString)
        {
            Pooling = true,
            MaxPoolSize = 52,
            ApplicationName = applicationName,
        }.ConnectionString;
        var cleanup = new FacadeCleanupSignals();
        await using var application = ComposedApplication(database, connectionString, cleanup);
        using var globals = new GlobalFilterSnapshot();
        var services = application.Services;
        // Resolve the actual aliases before startup, exactly as GenericHost does.
        // BackgroundJobHostedService binds both real processor facts here;
        // this proof supplies no Task.CompletedTask replacement witnesses.
        var hosted = services.GetServices<IHostedService>().ToArray();
        var jobs = services.GetRequiredService<BackgroundJobHostedService>();
        var runtime = services.GetRequiredService<BackgroundJobRuntime>();
        var outbox = services.GetRequiredService<OutboxProcessor>();
        var delivery = services.GetRequiredService<DeliveryProcessor>();
        var source = services.GetRequiredKeyedService<PrivilegedDataSource>(OutboxDispatcherDataSource.Key);
        var disposal = SourceDisposalObserver(source);
        var applicationSource = services.GetRequiredService<ApplicationDataSourceLifetime>();
        var applicationDisposal = (Task)typeof(ApplicationDataSourceLifetime).GetField("_retentionObserver", PrivateInstance)!.GetValue(applicationSource)!;
        hosted.OfType<BackgroundJobHostedService>().Should().ContainSingle().Which.Should().BeSameAs(jobs);
        hosted.OfType<OutboxProcessor>().Should().ContainSingle().Which.Should().BeSameAs(outbox);
        hosted.OfType<DeliveryProcessor>().Should().ContainSingle().Which.Should().BeSameAs(delivery);
        typeof(BackgroundJobRuntime).GetField("_source", PrivateInstance)!.GetValue(runtime).Should().BeSameAs(source);
        typeof(OutboxProcessor).GetField("_source", PrivateInstance)!.GetValue(outbox).Should().BeSameAs(source);
        typeof(DeliveryProcessor).GetField("_source", PrivateInstance)!.GetValue(delivery).Should().BeSameAs(source);
        typeof(BackgroundJobRuntime).GetField("_outboxCompletion", PrivateInstance)!.GetValue(runtime)
            .Should().BeSameAs(outbox.PrivilegedCompletion);
        typeof(BackgroundJobRuntime).GetField("_deliveryCompletion", PrivateInstance)!.GetValue(runtime)
            .Should().BeSameAs(delivery.PrivilegedCompletion);
        BackgroundService heldWorker = holdDelivery ? delivery : outbox;
        BackgroundService otherWorker = holdDelivery ? outbox : delivery;
        var heldCompletion = holdDelivery ? delivery.PrivilegedCompletion : outbox.PrivilegedCompletion;
        var otherCompletion = holdDelivery ? outbox.PrivilegedCompletion : delivery.PrivilegedCompletion;
        try
        {
            // A real application-role checkout warms the exact registered source;
            // this is not a synthetic replacement or an HTTP throughput proof.
            await using (var warm = await applicationSource.Source.Value.OpenConnectionAsync())
                await SchemaQueries.AssertApplicationRoleAsync(warm);
            await application.StartAsync();
            await EventuallyAsync(() => Task.FromResult(runtime.Snapshot.IsHealthy
                && services.GetRequiredService<OutboxObservation>().IsHealthy
                && services.GetRequiredService<DeliveryObservation>().IsHealthy));
            await AssertGuardedPoolAsync(source, new(true, 52));
            (await ServerCountAsync(source)).Should().Be(3);
            outbox.ExecuteTask.Should().NotBeNull();
            delivery.ExecuteTask.Should().NotBeNull();
            outbox.PrivilegedCompletion.IsCompleted.Should().BeFalse();
            delivery.PrivilegedCompletion.IsCompleted.Should().BeFalse();

            // Join the other two real owners first to isolate the remaining
            // messaging witness. This is controlled shutdown ordering, not an
            // assertion about GenericHost's ordering or no-issued work.
            using var joinBudget = new CancellationTokenSource(TestWait);
            await jobs.StopAsync(joinBudget.Token);
            await runtime.Completion.WaitAsync(joinBudget.Token);
            await otherWorker.StopAsync(joinBudget.Token);
            await otherCompletion.WaitAsync(joinBudget.Token);
            runtime.Snapshot.CleanupConfirmed.Should().BeTrue();

            var subscription = services.GetRequiredService<OutboxEventRegistry>()
                .Resolve(typeof(TenantCreatedIntegrationEventV1).AssemblyQualifiedName!)
                .Subscriptions[0].Id;
            var eventId = await InsertComposedTerminalFixtureAsync(database, holdDelivery, subscription);
            await cleanup.Entered.Task.WaitAsync(TestWait);
            // The synthetic disposable holds the real terminal-audit scope only.
            // Both stores dispose that scope after COMMIT and before their owned
            // connection. Independent visibility proves the actual SQL and MUST
            // audit committed; this does not simulate a provider cleanup failure.
            await AssertComposedTerminalCommittedAsync(database, holdDelivery, eventId, subscription);
            PoolStatistics(source).Busy.Should().Be(1);
            var heldExecution = heldWorker.ExecuteTask
                ?? throw new InvalidOperationException("The actual messaging execution task is missing.");
            heldExecution.IsCompleted.Should().BeFalse();
            heldCompletion.IsCompleted.Should().BeFalse();
            using var callerWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await heldWorker.StopAsync(callerWait.Token).WaitAsync(TestWait);
            heldCompletion.IsCompleted.Should().BeFalse("the finite StopAsync wait cannot join the held real audit scope");
            heldExecution.IsCompleted.Should().BeFalse();
            cleanup.Joined.Task.IsCompleted.Should().BeFalse();

            // Actual container disposal requests the keyed source's disposal.
            // All workers retain their real completion tasks after DI releases
            // ownership. Only the test-owned scope disposable ignores cancellation;
            // real command, transaction and connection cleanup remain unchanged.
            await application.DisposeAsync();
            ((bool)typeof(PrivilegedDataSource).GetField("_disposeRequested", PrivateInstance)!.GetValue(source)!)
                .Should().BeTrue();
            heldCompletion.IsCompleted.Should().BeFalse();
            heldExecution.IsCompleted.Should().BeFalse();
            cleanup.Joined.Task.IsCompleted.Should().BeFalse();
            runtime.Completion.IsCompletedSuccessfully.Should().BeTrue();
            otherCompletion.IsCompletedSuccessfully.Should().BeTrue();
            source.IsDisposalComplete.Should().BeFalse();
            disposal.IsCompleted.Should().BeFalse();
            applicationDisposal.IsCompleted.Should().BeFalse();
            applicationSource.IsDisposalComplete.Should().BeFalse();
            applicationSource.Source.IsValueCreated.Should().BeTrue();
            await using (var remaining = await applicationSource.Source.Value.OpenConnectionAsync())
                await SchemaQueries.AssertApplicationRoleAsync(remaining);
            source.HasUnconfirmedCleanup.Should().BeFalse("pending scope ownership is not a disposal fault");
            PoolStatistics(source).Busy.Should().Be(1);
            await AssertGuardedPoolAsync(source, new(true, 52));
            PoolStatistics(source).Busy.Should().Be(1);

            cleanup.Release.TrySetResult();
            await Task.WhenAll(runtime.Completion, outbox.PrivilegedCompletion, delivery.PrivilegedCompletion).WaitAsync(TestWait);
            await Task.WhenAll(disposal, applicationDisposal).WaitAsync(TestWait);
            heldExecution.IsCompleted.Should().BeTrue();
            cleanup.Joined.Task.IsCompletedSuccessfully.Should().BeTrue();
            source.IsDisposalComplete.Should().BeTrue();
            applicationSource.IsDisposalComplete.Should().BeTrue();
            applicationSource.HasUnconfirmedCleanup.Should().BeFalse();
            source.HasUnconfirmedCleanup.Should().BeFalse();
            SourceDisposalObserver(source).Should().BeSameAs(disposal);
            await AssertComposedTerminalCommittedAsync(database, holdDelivery, eventId, subscription);
            (await IndependentServerCountAsync(database)).Should().Be(0);
            await EventuallyAsync(async () => await PoolBackendCountAsync(database, applicationName) == 0);
        }
        finally
        {
            cleanup.Release.TrySetResult();
            await application.DisposeAsync();
            await Task.WhenAll(runtime.Completion, outbox.PrivilegedCompletion, delivery.PrivilegedCompletion).WaitAsync(TestWait);
            await Task.WhenAll(disposal, applicationDisposal).WaitAsync(TestWait);
        }
    }

    private static WebApplication ComposedApplication(DisposableSchemaDatabase database, string dispatcherConnectionString,
        FacadeCleanupSignals cleanup)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Deployment:Mode"] = "Development",
            ["Tenancy:PlatformHosts:0"] = "localhost",
            ["ConnectionStrings:Default"] = database.AppConnectionString,
            ["ConnectionStrings:PlatformAdmin"] = database.PlatformConnectionString,
            ["ConnectionStrings:OutboxDispatcher"] = dispatcherConnectionString,
        });
        builder.AddLearnStackCrossCuttingFoundation(builder.Configuration.RequireDeploymentMode(),
            typeof(LearnStack.Modules.Tenancy.Application.AssemblyMarker).Assembly,
            typeof(LearnStack.Modules.Customization.Application.AssemblyMarker).Assembly,
            typeof(LearnStack.Modules.Education.Application.AssemblyMarker).Assembly,
            typeof(LearnStack.Modules.Identity.Application.AssemblyMarker).Assembly);
        builder.Services.AddLearnStackTenancyEdge(builder.Configuration);
        // These supported test bounds alter caller waiting/poll cadence only;
        // SqlTimeout and the actual guarded source's cancellation bounds remain.
        builder.Services.AddSingleton(new OutboxDispatchOptions
        {
            ShutdownTimeout = TimeSpan.FromMilliseconds(50),
            IdlePoll = TimeSpan.FromMilliseconds(20),
            SnapshotInterval = TimeSpan.FromMilliseconds(50),
        });
        builder.Services.AddSingleton(new DeliveryDispatchOptions
        {
            ShutdownTimeout = TimeSpan.FromMilliseconds(50),
            IdlePoll = TimeSpan.FromMilliseconds(20),
            SnapshotInterval = TimeSpan.FromMilliseconds(50),
        });
        // The actual persistence composition calls AddLearnStackOutboxDispatcher
        // once, preserving its three exact hosted aliases and source capture.
        builder.Services.AddLearnStackPersistence(builder.Configuration);
        // Complete the actual authorizer graph without granting a human candidate
        // or qualifying an unused read lane in this worker-lifetime component proof.
        builder.Services.AddSingleton(_ => new IdentityAuthorizationReadSource(database.AppConnectionString));
        builder.Services.AddIdentityAuthorizationReader();
        builder.Services.AddIdentityOperationPolicies();
        // Keep the genuine audit writer and all its production dependencies.
        // Only its real terminal scope gains a synthetic async cleanup hold.
        builder.Services.AddScoped<PostgresAuditStore>();
        builder.Services.AddScoped(_ => new MessagingHeldAuditScopeCleanup(cleanup));
        builder.Services.Replace(ServiceDescriptor.Scoped<IAuditStore>(provider =>
        {
            var audit = provider.GetRequiredService<PostgresAuditStore>();
            _ = provider.GetRequiredService<MessagingHeldAuditScopeCleanup>();
            return audit;
        }));
        return builder.Build();
    }

    private static async Task<Guid> InsertComposedTerminalFixtureAsync(DisposableSchemaDatabase database,
        bool delivery, string subscription)
    {
        var fact = new TenantCreatedIntegrationEventV1
        {
            EventId = Guid.CreateVersion7(),
            TenantId = SchemaFixture.TenantA,
            OccurredAt = DateTimeOffset.UtcNow,
        };
        // Fixture setup publishes one complete terminal request atomically so
        // the running worker cannot race through a normal claim. This use of the
        // schema owner is setup, not a tenant-isolation or producer-write proof.
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, fact.TenantId);
        await using var command = new NpgsqlCommand(delivery ? """
            INSERT INTO event_deliveries
                (tenant_id, subscription_id, event_id, occurred_at, correlation_id,
                 type, topic, partition_key, payload, attempts, terminal_reason)
            VALUES (@tenant, @subscription, @event, @occurred, @trace,
                    @type, @topic, @partition, @payload, 6, 'retry_exhausted')
            """ : """
            INSERT INTO outbox_messages
                (tenant_id, id, occurred_at, correlation_id, type, topic,
                 partition_key, payload, attempts, terminal_reason)
            VALUES (@tenant, @event, @occurred, @trace, @type, @topic,
                    @partition, @payload, 6, 'retry_exhausted')
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", fact.TenantId);
        if (delivery) command.Parameters.AddWithValue("subscription", subscription);
        command.Parameters.AddWithValue("event", fact.EventId);
        command.Parameters.AddWithValue("occurred", fact.OccurredAt);
        command.Parameters.AddWithValue("trace", "00-11111111111111111111111111111111-2222222222222222-01");
        command.Parameters.AddWithValue("type", typeof(TenantCreatedIntegrationEventV1).AssemblyQualifiedName!);
        command.Parameters.AddWithValue("topic", fact.Topic);
        command.Parameters.AddWithValue("partition", fact.PartitionKey);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, fact.ToPayloadJson());
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
        await transaction.CommitAsync();
        return fact.EventId;
    }

    private static async Task AssertComposedTerminalCommittedAsync(DisposableSchemaDatabase database,
        bool delivery, Guid eventId, string subscription)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(connection, transaction);
        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
        var terminal = delivery
            ? "SELECT 1 FROM event_deliveries WHERE tenant_id=@tenant AND event_id=@event AND subscription_id=@subscription"
            : "SELECT 1 FROM outbox_messages WHERE tenant_id=@tenant AND id=@event";
        await using var command = new NpgsqlCommand($"""
            SELECT EXISTS ({terminal} AND dead_lettered_at IS NOT NULL
                AND processed_at IS NULL AND locked_by IS NULL
                AND terminal_reason='retry_exhausted' AND attempts=6)
                AND (SELECT count(*) FROM audit_log WHERE tenant_id=@tenant
                    AND operation=@operation AND operation_class='Must'
                    AND operation_type='SecurityEvent' AND metadata->>'event_id'=@event_text) = 1
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);
        command.Parameters.AddWithValue("event", eventId);
        command.Parameters.AddWithValue("event_text", eventId.ToString("D"));
        if (delivery) command.Parameters.AddWithValue("subscription", subscription);
        command.Parameters.AddWithValue("operation", delivery ? "platform.delivery.dead_letter" : "platform.outbox.dead_letter");
        (await command.ExecuteScalarAsync()).Should().Be(true,
            "the real terminal transition and exactly one MUST audit must be durable before scope cleanup joins");
    }

    private sealed class MessagingHeldAuditScopeCleanup(FacadeCleanupSignals state) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            state.Entered.TrySetResult();
            await state.Release.Task.ConfigureAwait(false);
            state.Joined.TrySetResult();
        }
    }
}
