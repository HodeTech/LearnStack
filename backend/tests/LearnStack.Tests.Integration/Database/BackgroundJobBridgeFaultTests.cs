using FluentAssertions;
using Hangfire;
using Hangfire.Client;
using Hangfire.Common;
using Hangfire.States;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Actual stock provider creation boundaries; no claim of qualified Core generation startup.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class BackgroundJobBridgeFaultTests(SchemaFixture schema)
{
    private static BackgroundJobRuntimeFixture Fixture => BackgroundJobRuntimeFixture.Shared;

    [Fact]
    public async Task Created_but_uninitialized_stock_rows_and_queued_but_unacknowledged_rows_remain_repairable()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var id = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var store = new PostgresBackgroundJobBridgeStore(source, Fixture.Registry, new(), new SystemGuidFactory());
        var storage = HangfireStorageBootstrap.Create(source, Fixture.Registry.Infrastructure, new HangfireBoundedLogProvider());
        var filters = new JobFilterCollection();
        filters.Add(new AutomaticRetryAttribute { Attempts = 0 });
        var fault = new CreateWithoutInitialization();
        var dirtyClient = new HangfireWakeupClient(new BackgroundJobClient(storage, fault, new BackgroundJobStateChanger(filters)));
        var first = (await store.TryClaimAsync([]))!; // Only one durable obligation exists.
        var create = () => dirtyClient.Create(first);
        create.Should().Throw<BackgroundJobClientException>();
        await using var sql = await source.OpenConnectionAsync();
        (await ScalarAsync(sql, "SELECT count(*) FROM public.job WHERE statename IS NULL")).Should().Be(1L);
        (await ScalarAsync(sql, "SELECT count(*) FROM public.jobqueue")).Should().Be(0L);
        await ExpireAsync(sql, id.Value);
        var second = (await store.TryClaimAsync([]))!; // Expired bridge, unchanged generation.
        second.WakeGeneration.Should().Be(1);
        var realClient = new HangfireWakeupClient(storage, filters);
        var vendor = realClient.Create(second);
        vendor.Should().NotBeNull();
        (await ScalarAsync(sql, "SELECT count(*) FROM public.jobqueue")).Should().Be(1L);
        // Lose this successful vendor create's ACK too. Repair must not interpret
        // either uninitialized or enqueued vendor storage as business delivery.
        await ExpireAsync(sql, id.Value);
        var third = (await store.TryClaimAsync([]))!; // Queued-before-ACK remains eligible after grace/lease expiry.
        third.WakeGeneration.Should().Be(1);
        var duplicate = realClient.Create(third);
        duplicate.Should().NotBe(vendor);
        (await store.AcknowledgeAsync(third, duplicate!)).Should().BeTrue(); // Stock client returned a positive id.
        (await ScalarAsync(sql, "SELECT count(*) FROM public.jobqueue")).Should().Be(2L);
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, id)).Should().Be(0);
    }

    [Fact]
    public async Task Rolled_back_authoritative_intent_never_becomes_a_bridge_obligation()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using (var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            await SchemaQueries.ExecuteAsync(app, transaction, """
                INSERT INTO background_jobs (id,tenant_id,definition_key,profile,queue,parameters,original_due_at,traceparent)
                VALUES (uuidv7(),@tenant,@definition,'transactional','transactional',@parameters,clock_timestamp(),@trace)
                """, ("tenant", SchemaFixture.TenantA), ("definition", BackgroundJobRuntimeFixture.TenantKey),
                ("parameters", "{\"ResourceId\":\"12345678-1234-7123-8123-123456789abc\"}"),
                ("trace", BackgroundJobRuntimeFixture.Traceparent));
        }
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var store = new PostgresBackgroundJobBridgeStore(source, Fixture.Registry, new(), new SystemGuidFactory());
        (await store.TryClaimAsync([])).Should().BeNull();
    }

    private static Task ExpireAsync(NpgsqlConnection connection, Guid id) => ExecuteAsync(connection,
        "UPDATE background_jobs SET bridge_until=clock_timestamp()-interval '1 second', handoff_check_at=clock_timestamp()-interval '1 second' WHERE id=@id", ("id", id));

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value);
        return await command.ExecuteScalarAsync();
    }
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] values) =>
        _ = await ScalarAsync(connection, sql, values);

    private sealed class CreateWithoutInitialization : IBackgroundJobFactory
    {
        public IStateMachine StateMachine { get; } = new StateMachine(new JobFilterCollection());

        public BackgroundJob Create(CreateContext context)
        {
            // The real provider's public API commits its stock job creation, then
            // the test aborts before any initialization/state/queue operation.
            _ = context.Connection.CreateExpiredJob(context.Job, new Dictionary<string, string>(),
                DateTime.UtcNow, TimeSpan.FromDays(1));
            throw new InvalidOperationException("synthetic-secret-canary");
        }
    }
}
