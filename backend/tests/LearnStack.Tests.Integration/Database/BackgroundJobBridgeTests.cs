using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Real confined bridge SQL and stock client. These tests do not activate the pending server-generation owner.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed partial class BackgroundJobBridgeTests(SchemaFixture schema)
{
    private static BackgroundJobRuntimeFixture Fixture => BackgroundJobRuntimeFixture.Shared;

    [Fact]
    public async Task Future_intent_is_handed_to_the_real_stock_client_once_then_checked_after_due_plus_grace()
    {
        await using var database = await DatabaseAsync();
        var due = DateTimeOffset.UtcNow.AddHours(1);
        var id = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString, due: due);
        await using var source = Source(database);
        var options = new BackgroundJobRuntimeOptions();
        var store = Store(source, options);
        var storage = HangfireStorageBootstrap.Create(source, Fixture.Registry.Infrastructure, new HangfireBoundedLogProvider());
        var bridge = new BackgroundJobBridge(store, new HangfireWakeupClient(storage, Filters()), options, TimeProvider.System);
        await bridge.RunQuantumAsync();
        await EventuallyAsync(() => bridge.Snapshot.Outstanding == 0);
        bridge.Snapshot.Acknowledged.Should().Be(1, "the real stock future schedule must commit and acknowledge its opaque handoff");
        bridge.Snapshot.PriorHintRepairsAcknowledged.Should().Be(0);
        bridge.Snapshot.ExpiredTokenRepairsAcknowledged.Should().Be(0);
        (await bridge.RunQuantumAsync()).Should().BeFalse();
        bridge.Snapshot.Acknowledged.Should().Be(1);
        await bridge.StopAsync();

        var row = await ReadAsync(database, id);
        row.Generation.Should().Be(1);
        row.Attempts.Should().Be(0);
        row.Vendor.Should().NotBeNull();
        row.Check.Should().BeOnOrAfter(due.Add(options.HandoffRepairGrace).AddMilliseconds(-1));
        row.BridgeToken.Should().BeNull();
        await using var sql = await source.OpenConnectionAsync();
        var state = await ScalarAsync(sql, "SELECT statename FROM public.job WHERE id=@vendor::bigint", ("vendor", row.Vendor!)); // ACK proves numeric id.
        state.Should().Be("Scheduled");
        var parameters = (string)(await ScalarAsync(sql,
            "SELECT coalesce(string_agg(value,''),'') FROM public.jobparameter WHERE jobid=@vendor::bigint", ("vendor", row.Vendor!)))!; // Aggregate always returns text.
        parameters.Should().NotContain("tr-TR").And.NotContain("en-US");
    }

    [Fact]
    public async Task Callback_admission_can_commit_before_its_matching_bridge_acknowledgement()
    {
        await using var database = await DatabaseAsync();
        var id = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        await using var source = Source(database);
        var store = Store(source);
        var claim = await store.TryClaimAsync([]);
        claim.Should().NotBeNull();
        await using var host = Fixture.Host(database, new BackgroundJobFixtureProbe());
        var routing = host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>();
        var route = await routing.TryClaimAsync(id, 1);
        route.Should().NotBeNull();
        var admitted = await host.Services.GetRequiredService<JobExecutor>().ExecuteAsync(route!, BackgroundJobTestRun.Create()); // Actual route was asserted.
        admitted.Outcome.Should().Be(JobStartExecutionOutcome.Started);
        (await store.AcknowledgeAsync(claim!, "123")).Should().BeTrue(); // Actual bridge claim was asserted.
        var row = await ReadAsync(database, id);
        row.Phase.Should().Be("executing");
        row.Attempts.Should().Be(1);
        row.Vendor.Should().Be("123");
        (await routing.TryClaimAsync(id, 1)).Should().BeNull();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("Failed")]
    [InlineData("Succeeded")]
    public async Task Vendor_hint_state_never_prevents_same_generation_repair(string hintState)
    {
        await using var database = await DatabaseAsync();
        var id = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        await using var source = Source(database);
        var store = Store(source);
        var storage = HangfireStorageBootstrap.Create(source, Fixture.Registry.Infrastructure, new HangfireBoundedLogProvider());
        var client = new HangfireWakeupClient(storage, Filters());
        var first = (await store.TryClaimAsync([]))!; // Seeded eligible row is the sole candidate.
        first.Kind.Should().Be(BackgroundJobBridgeClaimKind.Unmarked);
        var vendor = CreateInvariant(client, first)!; // Stock client creation is not canceled by these filters.
        (await store.AcknowledgeAsync(first, vendor)).Should().BeTrue();
        await using (var sql = await source.OpenConnectionAsync())
        {
            if (hintState == "missing") await ExecuteAsync(sql, "DELETE FROM public.job WHERE id=@vendor::bigint", ("vendor", vendor));
            else await ExecuteAsync(sql, "UPDATE public.job SET statename=@state WHERE id=@vendor::bigint", ("state", hintState), ("vendor", vendor));
            await ExecuteAsync(sql, "UPDATE background_jobs SET handoff_check_at=clock_timestamp()-interval '1 second' WHERE id=@id", ("id", id.Value));
        }
        var bridge = new BackgroundJobBridge(store, client, new(), TimeProvider.System);
        try
        {
            (await bridge.RunQuantumAsync()).Should().BeTrue();
            await EventuallyAsync(() => bridge.Snapshot.Outstanding == 0);
            bridge.Snapshot.Acknowledged.Should().Be(1);
            bridge.Snapshot.PriorHintRepairsAcknowledged.Should().Be(1);
            bridge.Snapshot.ExpiredTokenRepairsAcknowledged.Should().Be(0);
            var row = await ReadAsync(database, id);
            row.Generation.Should().Be(first.WakeGeneration);
            row.Vendor.Should().NotBe(vendor);
            row.Attempts.Should().Be(0);
        }
        finally { await bridge.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task Expired_and_replaced_bridge_tokens_cannot_acknowledge_current_work()
    {
        await using var database = await DatabaseAsync();
        var id = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        await using var source = Source(database);
        var store = Store(source);
        var first = (await store.TryClaimAsync([]))!; // The seeded row is the sole candidate.
        await using (var sql = await source.OpenConnectionAsync())
            await ExecuteAsync(sql, "UPDATE background_jobs SET bridge_until=clock_timestamp()-interval '1 second', handoff_check_at=clock_timestamp()-interval '1 second' WHERE id=@id", ("id", id.Value));
        (await store.AcknowledgeAsync(first, "1")).Should().BeFalse();
        var second = (await store.TryClaimAsync([]))!; // Its expired bridge lease is now reclaimable.
        (await store.AcknowledgeAsync(first, "2")).Should().BeFalse();
        (await store.AcknowledgeAsync(second, "3")).Should().BeTrue();
        (await ReadAsync(database, id)).Vendor.Should().Be("3");
    }

    [Fact]
    public async Task Acknowledgement_rechecks_database_expiry_after_an_actual_row_lock_wait()
    {
        await using var database = await DatabaseAsync();
        var id = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        await using var source = Source(database);
        var store = Store(source);
        var claim = (await store.TryClaimAsync([]))!; // The seeded row is the sole candidate.
        await using var observer = await source.OpenConnectionAsync();
        await ExecuteAsync(observer,
            "UPDATE background_jobs SET bridge_until=clock_timestamp()+interval '1 second' WHERE id=@id", ("id", id.Value));
        await using var locker = await source.OpenConnectionAsync();
        await using var transaction = await locker.BeginTransactionAsync();
        await using (var locked = new NpgsqlCommand("SELECT id FROM background_jobs WHERE id=@id FOR UPDATE", locker, transaction))
        {
            locked.Parameters.AddWithValue("id", id.Value);
            (await locked.ExecuteScalarAsync()).Should().Be(id.Value);
        }
        var acknowledge = store.AcknowledgeAsync(claim, "123");
        var lockReleased = false;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (await ScalarAsync(observer, """
                SELECT EXISTS (SELECT FROM pg_stat_activity WHERE datname=current_database() AND usename=current_user
                    AND pid<>pg_backend_pid() AND state='active' AND wait_event_type='Lock'
                    AND position('background_jobs' in query)>0)
                """) is not true)
                await Task.Delay(10, deadline.Token);
            while (await ScalarAsync(observer,
                       "SELECT clock_timestamp()>bridge_until FROM background_jobs WHERE id=@id", ("id", id.Value)) is not true)
                await Task.Delay(10, deadline.Token);
            await transaction.CommitAsync();
            lockReleased = true;
            (await acknowledge).Should().BeFalse("the bridge lease expired while the actual lock was held");
        }
        finally
        {
            if (!lockReleased) await transaction.RollbackAsync();
            await acknowledge;
        }
        var row = await ReadAsync(database, id);
        row.Vendor.Should().BeNull();
        row.BridgeToken.Should().Be(claim.Token);
    }

    [Theory]
    [InlineData(BackgroundJobRuntimeFixture.PendingKey)]
    [InlineData("jobssubmissionfixture.unknown.v1")]
    public async Task Unready_definitions_defer_durably_without_vendor_calls_or_attempts(string key)
    {
        await using var database = await DatabaseAsync();
        var id = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString, key);
        await using var source = Source(database);
        var never = new NeverClient();
        var bridge = new BackgroundJobBridge(Store(source), new HangfireWakeupClient(never), new(), TimeProvider.System);
        (await bridge.RunQuantumAsync()).Should().BeTrue();
        bridge.Snapshot.Deferred.Should().Be(1);
        never.Calls.Should().Be(0);
        var row = await ReadAsync(database, id);
        row.Error.Should().Be("deployment_mismatch");
        row.Attempts.Should().Be(0);
        row.Vendor.Should().BeNull();
        row.Check.Should().BeAfter(DateTimeOffset.UtcNow);
        (await bridge.RunQuantumAsync()).Should().BeFalse();
        await bridge.StopAsync();
    }

    [Fact]
    public async Task Four_actual_synchronous_calls_are_retained_across_finite_shutdown_wait_without_a_fifth_start()
    {
        await using var database = await DatabaseAsync();
        for (var index = 0; index < 5; index++) await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        await using var source = Source(database);
        var options = new BackgroundJobRuntimeOptions { BridgeWaitTimeout = TimeSpan.FromMilliseconds(1) };
        var ambient = new AsyncLocal<string?> { Value = "synthetic-secret-canary" };
        using var held = new HeldClient(ambient);
        var bridge = new BackgroundJobBridge(Store(source, options), new HangfireWakeupClient(held), options, TimeProvider.System);
        try
        {
            for (var index = 0; index < 4; index++) (await bridge.RunQuantumAsync()).Should().BeTrue();
            await EventuallyAsync(() => held.Calls == 4 && bridge.Snapshot.Retained == 4);
            (await bridge.RunQuantumAsync()).Should().BeFalse();
            held.Calls.Should().Be(4);
            held.Cultures.Should().OnlyContain(culture => culture == "|");
            held.AmbientValues.Should().OnlyContain(value => value == "absent");
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            var stop = () => bridge.StopAsync(deadline.Token);
            await stop.Should().ThrowAsync<OperationCanceledException>();
            bridge.Snapshot.Outstanding.Should().Be(4);
            (await bridge.RunQuantumAsync()).Should().BeFalse();
        }
        finally
        {
            held.Release();
            await bridge.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        bridge.Snapshot.Outstanding.Should().Be(0);
        bridge.Snapshot.Acknowledged.Should().Be(0, "late completion after host stop opens no fresh ACK acquisition");
        held.Calls.Should().Be(4);
    }

    [Fact]
    public async Task Catchable_thread_start_failure_preserves_the_already_live_operation_and_its_observer()
    {
        await using var database = await DatabaseAsync();
        await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        await using var source = Source(database);
        using var held = new HeldClient(new AsyncLocal<string?>());
        var bridge = new BackgroundJobBridge(Store(source), new HangfireWakeupClient(held), new(), TimeProvider.System);
        try
        {
            await bridge.RunQuantumAsync();
            await EventuallyAsync(() => held.Calls == 1);
            // Exercise Thread.Start's real ThreadStateException without adding a
            // production fault delegate or replacing its actual background thread.
            var field = typeof(BackgroundJobBridge).GetField("_operations", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The owned operation inventory is missing.");
            var operations = (System.Collections.IEnumerable)(field.GetValue(bridge)
                ?? throw new InvalidOperationException("The owned operation inventory is null."));
            var operation = operations.Cast<object>().Single();
            var start = operation.GetType().GetMethod("Start", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The owned thread start is missing.");
            var repeat = () => start.Invoke(operation, null);
            repeat.Should().NotThrow();
            bridge.Snapshot.Outstanding.Should().Be(1);
            held.Calls.Should().Be(1);
        }
        finally
        {
            held.Release();
            await bridge.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        bridge.Snapshot.Outstanding.Should().Be(0);
    }

    [Fact]
    public async Task Separate_bridge_instances_claim_distinct_obligations_without_waiting_on_each_other()
    {
        await using var database = await DatabaseAsync();
        await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        await using var source = Source(database);
        var first = Store(source);
        var second = Store(source);
        var claims = await Task.WhenAll(first.TryClaimAsync([]), second.TryClaimAsync([]));
        claims.Should().OnlyContain(claim => claim != null);
        claims.Select(claim => claim!.JobId).Should().OnlyHaveUniqueItems(); // Both real claims were asserted.
        (await first.TryClaimAsync([])).Should().BeNull();
    }

    private async Task<DisposableSchemaDatabase> DatabaseAsync()
    {
        var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        return database;
    }

    private static PrivilegedDataSource Source(DisposableSchemaDatabase database) =>
        new(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
    private static PostgresBackgroundJobBridgeStore Store(PrivilegedDataSource source, BackgroundJobRuntimeOptions? options = null) =>
        new(source, Fixture.Registry, options ?? new(), new SystemGuidFactory());

    private static JobFilterCollection Filters()
    {
        var filters = new JobFilterCollection();
        filters.Add(new CaptureCultureAttribute());
        filters.Add(new AutomaticRetryAttribute { Attempts = 0 });
        return filters;
    }

    private static string? CreateInvariant(HangfireWakeupClient client, BackgroundJobBridgeClaim claim)
    {
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            return client.Create(claim);
        }
        finally { CultureInfo.CurrentCulture = culture; CultureInfo.CurrentUICulture = uiCulture; }
    }

    private static async Task<BridgeRow> ReadAsync(DisposableSchemaDatabase database, BackgroundJobId id)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        await using var command = new NpgsqlCommand("SELECT wake_generation, normal_attempts, vendor_job_id, bridge_token, handoff_check_at, last_error, phase FROM background_jobs WHERE id=@id", app, transaction);
        command.Parameters.AddWithValue("id", id.Value);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        return new(row.GetInt64(0), row.GetInt32(1), row.IsDBNull(2) ? null : row.GetString(2),
            row.IsDBNull(3) ? null : row.GetGuid(3), row.GetFieldValue<DateTimeOffset>(4),
            row.IsDBNull(5) ? null : row.GetString(5), row.GetString(6));
    }

    private static async Task EventuallyAsync(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate()) await Task.Delay(10, deadline.Token);
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value);
        return await command.ExecuteScalarAsync();
    }
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] values) =>
        _ = await ScalarAsync(connection, sql, values);

    private sealed record BridgeRow(long Generation, int Attempts, string? Vendor, Guid? BridgeToken,
        DateTimeOffset Check, string? Error, string Phase);

    private sealed class NeverClient : IBackgroundJobClient
    {
        internal int Calls { get; private set; }
        public string Create(Job job, IState state) { Calls++; throw new InvalidOperationException("Unexpected creation."); }
        public bool ChangeState(string jobId, IState state, string expectedState) => throw new NotSupportedException();
    }

    private sealed class HeldClient(AsyncLocal<string?> ambient) : IBackgroundJobClient, IDisposable
    {
        private readonly ManualResetEventSlim _released = new();
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        internal ConcurrentQueue<string> Cultures { get; } = new();
        internal ConcurrentQueue<string> AmbientValues { get; } = new();
        public string Create(Job job, IState state)
        {
            Cultures.Enqueue(CultureInfo.CurrentCulture.Name + "|" + CultureInfo.CurrentUICulture.Name);
            AmbientValues.Enqueue(ambient.Value ?? "absent");
            var call = Interlocked.Increment(ref _calls);
            _released.Wait();
            return call.ToString(CultureInfo.InvariantCulture);
        }
        public bool ChangeState(string jobId, IState state, string expectedState) => throw new NotSupportedException();
        internal void Release() => _released.Set();
        public void Dispose() => _released.Dispose();
    }
}
