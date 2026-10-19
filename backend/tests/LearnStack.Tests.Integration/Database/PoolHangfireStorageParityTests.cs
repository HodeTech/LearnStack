using System.Reflection;
using System.Runtime.ExceptionServices;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.PostgreSql;
using Hangfire.Server;
using Hangfire.States;
using Hangfire.Storage;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Jobs;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class PoolHangfireStorageParityTests(SchemaFixture schema)
{
    private static readonly string[] Queues = ["transactional"];
    private const string ServerId = "learnstack-parity:1:11111111-1111-4111-8111-111111111111";

    [Fact]
    public async Task Real_storage_keeps_features_extended_casts_monitoring_and_frozen_component_instances()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = Source(database);
        var diagnostics = new HangfireBoundedLogProvider();
        var provider = HangfireStorageBootstrap.Create(source, BackgroundJobInfrastructureRegistry.CreateDefault(), diagnostics);
        var inventory = new HangfireComponentInventory(provider);
#pragma warning disable CS0618 // Prove the pinned public component seam with the actual selected objects.
        var pool = new PoolHangfireStorage(provider, new ExecutingLifecycle(), inventory.Selected);
        pool.GetComponents().Should().Equal(inventory.Expiration, inventory.FetchedJobHeartbeat);
        pool.GetComponents().First().Should().BeSameAs(inventory.Expiration);
#pragma warning restore CS0618
        pool.JobExpirationTimeout.Should().Be(provider.JobExpirationTimeout);
        pool.LinearizableReads.Should().Be(provider.LinearizableReads);
        pool.ToString().Should().Be(provider.ToString());
        pool.GetStateHandlers().Should().Equal(provider.GetStateHandlers());
        pool.GetServerRequiredProcesses().Should().Equal(provider.GetServerRequiredProcesses());
        pool.GetStorageWideProcesses().Should().Equal(provider.GetStorageWideProcesses());
        pool.GetMonitoringApi().GetType().Should().Be(provider.GetMonitoringApi().GetType());
        pool.GetMonitoringApi().GetStatistics().Should().BeEquivalentTo(provider.GetMonitoringApi().GetStatistics());
        pool.WriteOptionsToLog(diagnostics.GetLogger("Hangfire.PostgreSql.PostgreSqlStorage"));
        foreach (var feature in Features()) pool.HasFeature(feature).Should().Be(provider.HasFeature(feature), feature);
        pool.HasFeature(JobStorageFeatures.Connection.BatchedGetFirstByLowest).Should().BeTrue();
        pool.HasFeature("fixture.unknown").Should().BeFalse();
        using var regular = pool.GetConnection();
        using var readOnly = pool.GetReadOnlyConnection();
        regular.Should().BeAssignableTo<JobStorageConnection>();
        readOnly.Should().BeAssignableTo<JobStorageConnection>();
        ((JobStorageConnection)readOnly).GetCounter("stats:succeeded").Should().Be(0);
        await using var sql = await source.OpenConnectionAsync();
        using var role = new NpgsqlCommand("SELECT current_user", sql);
        (await role.ExecuteScalarAsync()).Should().Be("learnstack_outbox_admin");
    }

    [Fact]
    public async Task Real_extended_reads_writes_and_unsupported_features_match_the_guarded_provider()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await BackgroundJobSchemaSeed.SeedRecipientsAsync(database.OutboxConnectionString);
        await using var source = Source(database);
        var provider = HangfireStorageBootstrap.Create(source, BackgroundJobInfrastructureRegistry.CreateDefault(), new HangfireBoundedLogProvider());
        var pool = Pool(provider, new ExecutingLifecycle());
        using var direct = (JobStorageConnection)provider.GetConnection();
        using var wrapped = (JobStorageConnection)pool.GetConnection();
        var now = DateTime.UtcNow;
        var first = wrapped.CreateExpiredJob(TransportJob(), new Dictionary<string, string> { ["RetryCount"] = "0", ["Time"] = "1" }, now, TimeSpan.FromDays(1));
        var second = direct.CreateExpiredJob(TransportJob(), new Dictionary<string, string>(), now, TimeSpan.FromDays(1));
        wrapped.SetJobParameter(first, "Time", "2");
        wrapped.GetJobParameter(first, "Time").Should().Be(direct.GetJobParameter(first, "Time")).And.Be("2");
        wrapped.GetJobData(first).Should().BeEquivalentTo(direct.GetJobData(first));
        wrapped.GetJobData(first).EnsureLoaded();

        using (var transaction = wrapped.CreateWriteTransaction())
        {
            using var originalTransaction = direct.CreateWriteTransaction();
            transaction.GetType().Should().Be(originalTransaction.GetType());
            transaction.AddToSet("schedule", first, 1);
            transaction.AddToSet("schedule", second, 2);
            transaction.InsertToList("succeeded", first);
            transaction.InsertToList("succeeded", second);
            transaction.IncrementCounter("stats:succeeded");
            transaction.SetJobState(first, new EnqueuedState("transactional"));
            transaction.Commit();
        }
        var fields = new Dictionary<string, string> { ["Queue"] = "transactional", ["TimeZoneId"] = "UTC", ["Cron"] = "* * * * *" };
        wrapped.SetRangeInHash("recurring-job:fixture.tenants", fields);
        wrapped.GetStateData(first).Should().BeEquivalentTo(direct.GetStateData(first));
        wrapped.GetAllItemsFromSet("schedule").Should().BeEquivalentTo(direct.GetAllItemsFromSet("schedule"));
        wrapped.GetFirstByLowestScoreFromSet("schedule", 0, 3).Should().Be(direct.GetFirstByLowestScoreFromSet("schedule", 0, 3)).And.Be(first);
        wrapped.GetFirstByLowestScoreFromSet("schedule", 0, 3, 2).Should().Equal(direct.GetFirstByLowestScoreFromSet("schedule", 0, 3, 2)).And.Equal(first, second);
        wrapped.GetSetCount("schedule").Should().Be(direct.GetSetCount("schedule")).And.Be(2);
        wrapped.GetRangeFromSet("schedule", 0, 1).Should().Equal(direct.GetRangeFromSet("schedule", 0, 1));
        wrapped.GetSetTtl("schedule").Should().Be(direct.GetSetTtl("schedule"));
        wrapped.GetListCount("succeeded").Should().Be(direct.GetListCount("succeeded")).And.Be(2);
        wrapped.GetAllItemsFromList("succeeded").Should().Equal(direct.GetAllItemsFromList("succeeded"));
        wrapped.GetRangeFromList("succeeded", 0, 1).Should().Equal(direct.GetRangeFromList("succeeded", 0, 1));
        wrapped.GetListTtl("succeeded").Should().Be(direct.GetListTtl("succeeded"));
        wrapped.GetAllEntriesFromHash("recurring-job:fixture.tenants").Should().BeEquivalentTo(direct.GetAllEntriesFromHash("recurring-job:fixture.tenants"));
        wrapped.GetValueFromHash("recurring-job:fixture.tenants", "Queue").Should().Be(direct.GetValueFromHash("recurring-job:fixture.tenants", "Queue"));
        wrapped.GetHashCount("recurring-job:fixture.tenants").Should().Be(direct.GetHashCount("recurring-job:fixture.tenants")).And.Be(3);
        wrapped.GetHashTtl("recurring-job:fixture.tenants").Should().Be(direct.GetHashTtl("recurring-job:fixture.tenants"));
        wrapped.GetCounter("stats:succeeded").Should().Be(direct.GetCounter("stats:succeeded")).And.Be(1);
        SameUnsupported(() => direct.GetUtcDateTime(), () => wrapped.GetUtcDateTime());
        SameUnsupported(() => direct.GetSetContains("schedule", first), () => wrapped.GetSetContains("schedule", first));
        SameUnsupported(() => direct.GetSetCount(["schedule"], 2), () => wrapped.GetSetCount(["schedule"], 2));
        var directInvalid = () => direct.SetJobParameter(first, "TenantId", "synthetic-secret-canary");
        var wrappedInvalid = () => wrapped.SetJobParameter(first, "TenantId", "synthetic-secret-canary");
        directInvalid.Should().Throw<PostgresException>().Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        wrappedInvalid.Should().Throw<PostgresException>().Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Real_lifecycle_lock_and_fetched_job_keep_stock_semantics_through_actual_disposal()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = Source(database);
        var provider = HangfireStorageBootstrap.Create(source, BackgroundJobInfrastructureRegistry.CreateDefault(), new HangfireBoundedLogProvider());
        var lifecycle = new ExecutingLifecycle();
        var pool = Pool(provider, lifecycle);
        using var direct = (JobStorageConnection)provider.GetConnection();
        using var wrapped = (JobStorageConnection)pool.GetConnection();
        var context = new ServerContext { WorkerCount = 1, Queues = Queues };
        wrapped.AnnounceServer(ServerId, context);
        direct.Heartbeat(ServerId);
        wrapped.Heartbeat(ServerId);
        wrapped.RemoveServer(ServerId);
        lifecycle.Completed.Should().Be(3);
        var missingDirect = () => direct.Heartbeat(ServerId);
        var missingWrapped = () => wrapped.Heartbeat(ServerId);
        missingDirect.Should().Throw<BackgroundServerGoneException>();
        missingWrapped.Should().Throw<BackgroundServerGoneException>();
        direct.AnnounceServer(ServerId, context);
        wrapped.Heartbeat(ServerId);
        direct.RemoveServer(ServerId);
        wrapped.RemoveTimedOutServers(TimeSpan.FromMinutes(5)).Should().Be(direct.RemoveTimedOutServers(TimeSpan.FromMinutes(5))).And.Be(0);
        using (var held = wrapped.AcquireDistributedLock("locks:schedulepoller", TimeSpan.FromSeconds(5)))
            held.GetType().FullName.Should().StartWith("Hangfire.PostgreSql.");
        using (var held = direct.AcquireDistributedLock("locks:schedulepoller", TimeSpan.FromSeconds(5)))
            held.GetType().FullName.Should().StartWith("Hangfire.PostgreSql.");

        var id = wrapped.CreateExpiredJob(TransportJob(), new Dictionary<string, string>(), DateTime.UtcNow, TimeSpan.FromDays(1));
        using (var transaction = wrapped.CreateWriteTransaction())
        {
            transaction.AddToQueue("transactional", id);
            transaction.Commit();
        }
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Type originalType;
        using (var original = direct.FetchNextJob(Queues, stop.Token))
        {
            originalType = original.GetType();
            original.JobId.Should().Be(id);
            original.Requeue();
        }
        using (var fetched = wrapped.FetchNextJob(Queues, stop.Token))
        {
            fetched.GetType().Should().Be(originalType);
            fetched.JobId.Should().Be(id);
            var acknowledge = JobStorageFeatures.Transaction.RemoveFromQueue(fetched.GetType());
            pool.HasFeature(acknowledge).Should().Be(provider.HasFeature(acknowledge));
            fetched.RemoveFromQueue();
        }
        wrapped.Dispose();
        wrapped.Dispose();
        lifecycle.Completed.Should().Be(4, "only successful lifecycle attempts count, and outer disposal never repeats them");
        await using var sql = await source.OpenConnectionAsync();
        (await SchemaQueries.CountAsync(sql, "SELECT count(*) FROM public.jobqueue")).Should().Be(0);
    }

    private static PrivilegedDataSource Source(DisposableSchemaDatabase database) =>
        new(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));

    private static PoolHangfireStorage Pool(PostgreSqlStorage provider, ExecutingLifecycle lifecycle) =>
        new(provider, lifecycle, new HangfireComponentInventory(provider).Selected);

    private static Job TransportJob() => new(typeof(HangfireJobWakeup), HangfireInvocationContract.JobMethod,
        [BackgroundJobId.From(Guid.Parse("11111111-1111-7111-8111-111111111111")), 1L], "transactional");

    private static IEnumerable<string> Features() =>
        new[] { typeof(JobStorageFeatures) }.Concat(typeof(JobStorageFeatures).GetNestedTypes(BindingFlags.Public))
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(field => field.FieldType == typeof(string)).Select(field => (string)field.GetValue(null)!);

    private static void SameUnsupported<T>(Func<T> direct, Func<T> wrapped)
    {
        var expected = direct.Should().Throw<NotSupportedException>().Which;
        wrapped.Should().Throw<NotSupportedException>().Which.Message.Should().Be(expected.Message);
    }

    // Executes only the closed stock operation. Production retry, stop, and retention
    // behavior belongs to the generation owner and is deliberately not simulated here.
    private sealed class ExecutingLifecycle : PoolHangfireLifecycle
    {
        internal int Completed { get; private set; }
        internal override void AnnounceServer(PoolHangfireLifecycleOperation operation) => Execute(operation);
        internal override void Heartbeat(PoolHangfireLifecycleOperation operation) => Execute(operation);
        internal override void RemoveServer(PoolHangfireLifecycleOperation operation) => Execute(operation);

        private void Execute(PoolHangfireLifecycleOperation operation)
        {
            var result = operation.ExecuteAttempt();
            if (result.DisposalFailure is not null) ExceptionDispatchInfo.Capture(result.DisposalFailure).Throw();
            if (result.Failure is not null) ExceptionDispatchInfo.Capture(result.Failure).Throw();
            operation.RetainsConnection.Should().BeFalse();
            Completed++;
        }
    }
}
