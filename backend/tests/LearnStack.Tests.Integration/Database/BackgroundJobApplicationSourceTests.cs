using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Reflection;
using FluentAssertions;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Caching;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Actual application-role connections constrain the closed main/auxiliary ownership inventory.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class BackgroundJobApplicationSourceTests(SchemaFixture schema)
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);
    private static readonly BackgroundJobFixtureContext Context = new();

    [Fact]
    public async Task Actual_main_and_audit_helpers_share_two_slots_and_clean_SQL_failure_permits_the_next_intent()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var source = ApplicationDataSource.Build(Pooled(database.AppConnectionString, 2));
        await using var metrics = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var owner = JobApplicationSourceOwner.Create();
        using var binding = owner.Enter();
        var unit = new NpgsqlUnitOfWork(source, NullLogger<NpgsqlUnitOfWork>.Instance);
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(Context);
        await SchemaQueries.AssertApplicationRoleAsync(unit.Connection);
        var second = new NpgsqlUnitOfWork(source, NullLogger<NpgsqlUnitOfWork>.Instance);
        try
        {
            var extra = () => second.Connection;
            extra.Should().Throw<InvalidOperationException>();
            var classifier = Classifier(source, metrics.GetRequiredService<IMeterFactory>());
            (await classifier.ClassifyAsync(Context.TenantId, Entry())).Should().Be(AuditClassification.Should);
            owner.RequireOwnedMainUnit(unit);
            var store = new PostgresAuditStore(new AuditStateCapture(), new Lazy<NpgsqlDataSource>(() => source),
                NullLogger<PostgresAuditStore>.Instance, metrics.GetRequiredService<IMeterFactory>(), new AuditHealth(),
                new AuditCatalog([]), new StaticTenantContextAccessor(Context));
            var invalid = Draft() with { Metadata = "not-json" };
            var failed = () => store.WriteStandaloneAsync(invalid);
            await failed.Should().ThrowAsync<AuditWriteFailedException>();
            owner.RequireOwnedMainUnit(unit);
            var valid = Draft();
            await store.WriteStandaloneAsync(valid);
            await using var read = new NpgsqlCommand("SELECT count(*) FROM audit_log WHERE id=@id AND tenant_id=@tenant",
                (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!);
            read.Parameters.AddWithValue("id", valid.Id.Value);
            read.Parameters.AddWithValue("tenant", Context.TenantId.Value);
            ((long)(await read.ExecuteScalarAsync())!).Should().Be(1);
            await frame.CompleteAsync();
            await frame.DisposeAsync();
            owner.CleanupConfirmed.Should().BeFalse("the physical main connection still belongs to its unit");
        }
        finally
        {
            await second.DisposeAsync();
            await unit.DisposeAsync();
        }
        owner.CleanupConfirmed.Should().BeTrue();
        owner.RequireFreshScopeReady();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cache_waiter_timeout_retains_actual_connection_cleanup_and_dirty_cleanup_seals_replacement(bool dirty)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var blocker = new NpgsqlConnection(database.MigrationConnectionString);
        await blocker.OpenAsync();
        await using var locked = await blocker.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("LOCK TABLE audit_config IN ACCESS EXCLUSIVE MODE", blocker, locked))
            await command.ExecuteNonQueryAsync();
        await using var source = ApplicationDataSource.Build(Pooled(database.AppConnectionString, 2));
        await using var metrics = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var owner = JobApplicationSourceOwner.Create();
        using var binding = owner.Enter();
        var classifier = Classifier(source, metrics.GetRequiredService<IMeterFactory>(), TimeSpan.FromSeconds(1));
        var classification = classifier.ClassifyAsync(Context.TenantId, Entry());
        await WaitUntilAsync(() => AuxiliaryConnection(owner)?.State == ConnectionState.Open);
        var resources = Auxiliary(owner)!;
        var borrowed = AuxiliaryConnection(owner)!;
        var actual = (Task)typeof(JobApplicationAuxiliaryResources).GetField("_actualTask", PrivateInstance)!.GetValue(resources)!;
        var entered = Signal();
        var release = Signal();
        var closes = 0;
        StateChangeEventHandler cleanup = (_, change) =>
        {
            if (change.CurrentState != ConnectionState.Closed) return;
            Interlocked.Increment(ref closes);
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            if (dirty) throw new InvalidOperationException("deliberate actual connection cleanup failure");
        };
        borrowed.StateChange += cleanup;
        try
        {
            (await classification.WaitAsync(Budget)).Should().Be(AuditClassification.Should);
            await entered.Task.WaitAsync(Budget);
            actual.IsCompleted.Should().BeFalse("the real factory still owns its connection disposal after the cache waiter timed out");
            owner.CleanupConfirmed.Should().BeFalse();
            var activation = () => owner.RequireFreshScopeReady();
            activation.Should().Throw<InvalidOperationException>();
            var replacement = () => owner.BeginStandaloneAudit();
            replacement.Should().Throw<InvalidOperationException>();
            Auxiliary(owner).Should().BeSameAs(resources);
        }
        finally
        {
            release.TrySetResult();
            await locked.RollbackAsync();
            await actual.WaitAsync(Budget).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
            actual.IsCompleted.Should().BeTrue("a canceled caller wait is not an actual cleanup join");
            borrowed.StateChange -= cleanup;
        }
        closes.Should().Be(1);
        owner.CleanupConfirmed.Should().Be(!dirty);
        if (dirty)
        {
            Auxiliary(owner).Should().BeSameAs(resources);
            var replacement = () => owner.BeginStandaloneAudit();
            replacement.Should().Throw<InvalidOperationException>();
        }
        else owner.RequireFreshScopeReady();
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 2)]
    public async Task Actual_job_inventory_fills_the_supported_pool_and_only_explicit_headroom_admits_unrelated_connections(
        bool maximum, int headroom)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var topology = maximum ? MaximumTopology() : BackgroundJobInfrastructureRegistry.CreateDefault();
        var capacity = BackgroundJobApplicationCapacity.Calculate(topology);
        capacity.RequiredConnections.Should().Be(maximum ? 128 : 32);
        var invocationPools = topology.Pools.Values
            .Where(pool => pool.Kind is BackgroundJobWorkerPoolKind.Transactional or BackgroundJobWorkerPoolKind.External)
            .SelectMany(pool => Enumerable.Repeat(pool.Kind, BackgroundJobRuntimeOptions.RetainedHardBound(pool.Workers)))
            .ToArray();
        invocationPools.Count(kind => kind == BackgroundJobWorkerPoolKind.Transactional).Should().Be(maximum ? 54 : 5);
        invocationPools.Count(kind => kind == BackgroundJobWorkerPoolKind.External).Should().Be(maximum ? 5 : 6);
        invocationPools.Should().HaveCount(capacity.InvocationOwners);
        await using var source = ApplicationDataSource.Build(Pooled(database.AppConnectionString, capacity.RequiredConnections + headroom));
        var release = Signal();
        var allBorrowed = Signal();
        // One process-wide terminal owner has the same concrete main/auxiliary
        // inventory but is independent of the generation invocation pools.
        var terminalOwner = JobApplicationSourceOwner.Create();
        // The four recurrence recipients have independent concrete app owners;
        // none increases the stock native generation's invocation allowance.
        capacity.RecipientOwners.Should().Be(4);
        var recipients = Enumerable.Range(0, capacity.RecipientOwners).Select(_ => JobApplicationSourceOwner.Create()).ToArray();
        var applicationOwners = invocationPools.Select(_ => JobApplicationSourceOwner.Create())
            .Append(terminalOwner).Concat(recipients).ToArray();
        var remaining = applicationOwners.Length;
        var owners = new ConcurrentBag<JobApplicationSourceOwner>();
        var backends = new ConcurrentBag<int>();
        var workers = applicationOwners.Select(owner => Task.Run(async () =>
        {
            owners.Add(owner);
            using var binding = owner.Enter();
            var unit = new NpgsqlUnitOfWork(source, NullLogger<NpgsqlUnitOfWork>.Instance);
            try
            {
                await using var frame = await unit.BeginTransactionAsync();
                await SchemaQueries.AssertApplicationRoleAsync(unit.Connection);
                backends.Add(((NpgsqlConnection)unit.Connection).ProcessID);
                var resources = owner.BeginStandaloneAudit();
                var actual = HoldAuxiliaryAsync(source, resources, release.Task, backends);
                resources.HoldActualTask(actual);
                await WaitUntilAsync(() => AuxiliaryConnection(owner)?.State == ConnectionState.Open);
                if (Interlocked.Decrement(ref remaining) == 0) allBorrowed.TrySetResult();
                await actual;
                await frame.CompleteAsync();
            }
            finally { await unit.DisposeAsync(); }
            owner.CloseIssuance();
            owner.CleanupConfirmed.Should().BeTrue();
        })).ToArray();
        var unrelated = new List<NpgsqlConnection>();
        try
        {
            await allBorrowed.Task.WaitAsync(Budget);
            await WaitUntilAsync(() => backends.Count == capacity.RequiredConnections);
            // These are two explicitly budgeted application-role probes. They do
            // not model or cap all HTTP/delivery arrivals in the process.
            for (var index = 0; index < headroom; index++)
            {
                var connection = await source.OpenConnectionAsync();
                unrelated.Add(connection);
                await SchemaQueries.AssertApplicationRoleAsync(connection);
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            Func<Task> overflow = async () =>
            {
                await using var connection = await source.OpenConnectionAsync(deadline.Token);
            };
            await overflow.Should().ThrowAsync<OperationCanceledException>();
            backends.Distinct().Should().HaveCount(capacity.RequiredConnections);
        }
        finally
        {
            release.TrySetResult();
            foreach (var connection in unrelated) await connection.DisposeAsync();
            await Task.WhenAll(workers).WaitAsync(Budget);
        }
        owners.Should().HaveCount(capacity.InvocationOwners + 1 + capacity.RecipientOwners);
        owners.Should().Contain(terminalOwner);
        owners.Should().Contain(recipients);
        owners.Should().OnlyContain(owner => owner.CleanupConfirmed);
        await using var afterRelease = await source.OpenConnectionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(afterRelease);
    }

    [Fact]
    public async Task A_disposal_request_retains_the_actual_source_until_both_job_and_messaging_work_clean_up()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var lifetime = new ApplicationDataSourceLifetime(Pooled(database.AppConnectionString, 4));
        var start = Signal();
        var jobRelease = Signal();
        var messagingRelease = Signal();
        var jobBorrowed = Signal();
        var messagingBorrowed = Signal();
        var jobs = OwnedJobAsync();
        var messaging = OwnedMessagingAsync();
        var actualCompletion = Task.WhenAll(jobs, messaging);
        typeof(ApplicationDataSourceLifetime).GetMethod("RetainBackgroundJobsUntil", PrivateInstance)!
            .Invoke(lifetime, [actualCompletion]);
        start.SetResult();
        try
        {
            await Task.WhenAll(jobBorrowed.Task, messagingBorrowed.Task).WaitAsync(Budget);
            await lifetime.DisposeAsync();
            jobRelease.SetResult();
            await jobs.WaitAsync(Budget);
            actualCompletion.IsCompleted.Should().BeFalse();
            await using (var completionSql = await lifetime.Source.Value.OpenConnectionAsync())
                await SchemaQueries.AssertApplicationRoleAsync(completionSql);
            messagingRelease.SetResult();
            await actualCompletion.WaitAsync(Budget);
            var observer = (Task)typeof(ApplicationDataSourceLifetime).GetField("_retentionObserver", PrivateInstance)!.GetValue(lifetime)!;
            await observer.WaitAsync(Budget);
            Func<Task> acquireAfterActualDisposal = async () =>
            {
                await using var connection = await lifetime.Source.Value.OpenConnectionAsync();
            };
            await acquireAfterActualDisposal.Should().ThrowAsync<ObjectDisposedException>();
        }
        finally
        {
            jobRelease.TrySetResult();
            messagingRelease.TrySetResult();
            await actualCompletion.WaitAsync(Budget);
        }

        async Task OwnedJobAsync()
        {
            await start.Task;
            var owner = JobApplicationSourceOwner.Create();
            using var binding = owner.Enter();
            var unit = new NpgsqlUnitOfWork(lifetime.Source.Value, NullLogger<NpgsqlUnitOfWork>.Instance);
            try
            {
                await using var frame = await unit.BeginTransactionAsync();
                await SchemaQueries.AssertApplicationRoleAsync(unit.Connection);
                var resources = owner.BeginStandaloneAudit();
                var auxiliary = HoldAuxiliaryAsync(lifetime.Source.Value, resources, jobRelease.Task, new ConcurrentBag<int>());
                resources.HoldActualTask(auxiliary);
                await WaitUntilAsync(() => AuxiliaryConnection(owner)?.State == ConnectionState.Open);
                jobBorrowed.TrySetResult();
                await auxiliary;
                await frame.CompleteAsync();
            }
            finally { await unit.DisposeAsync(); }
            owner.CloseIssuance();
            owner.CleanupConfirmed.Should().BeTrue();
        }

        async Task OwnedMessagingAsync()
        {
            await start.Task;
            await using var connection = await lifetime.Source.Value.OpenConnectionAsync();
            await SchemaQueries.AssertApplicationRoleAsync(connection);
            messagingBorrowed.TrySetResult();
            await messagingRelease.Task;
        }
    }

    private static async Task HoldAuxiliaryAsync(NpgsqlDataSource source, JobApplicationAuxiliaryResources resources,
        Task release, ConcurrentBag<int> backends)
    {
        try
        {
            var connection = source.CreateConnection();
            resources.HoldConnection(connection);
            await connection.OpenAsync();
            var transaction = await connection.BeginTransactionAsync();
            resources.HoldTransaction(transaction);
            var command = new NpgsqlCommand("SELECT current_user, rolbypassrls OR rolsuper FROM pg_roles WHERE rolname=current_user",
                connection, transaction);
            resources.HoldCommand(command);
            var reader = await command.ExecuteReaderAsync();
            resources.HoldReader(reader);
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetString(0).Should().Be("learnstack_app");
            reader.GetBoolean(1).Should().BeFalse();
            backends.Add(connection.ProcessID);
            await resources.DisposeReaderAsync();
            await release;
            await transaction.CommitAsync();
        }
        finally { await resources.CleanupAsync(); }
    }

    private static AuditConfigService Classifier(NpgsqlDataSource source, IMeterFactory metrics, TimeSpan? factoryTimeout = null) =>
        new(new InMemoryCacheService(new SystemClock(), metrics, factoryTimeout), new Lazy<NpgsqlDataSource>(() => source),
            NullLogger<AuditConfigService>.Instance, new StaticTenantContextAccessor(Context));

    private static AuditCatalogEntry Entry() => new("tenancy", "tenancy.application-source.proof", OperationType.Create,
        OperationClass.Should, typeof(object));

    private static AuditEntryDraft Draft() => new()
    {
        Id = AuditEntryId.From(Guid.CreateVersion7()),
        TenantId = Context.TenantId,
        OrganizationId = null,
        ActorUserId = UserId.SystemActor,
        ActorEmail = null,
        ModuleName = "tenancy",
        Operation = "tenancy.application-source.proof",
        OperationType = OperationType.Create,
        OperationClass = OperationClass.Must,
        EntityType = null,
        EntityId = null,
        Outcome = AuditOutcome.Success,
        ErrorKey = null,
        Reason = null,
        BeforeState = null,
        AfterState = null,
        Changes = null,
        CorrelationId = null,
        IpAddress = null,
        UserAgent = null,
        Timestamp = DateTimeOffset.UtcNow,
        Metadata = null,
    };

    private static string Pooled(string connectionString, int maximum) => new NpgsqlConnectionStringBuilder(connectionString)
    { Pooling = true, MaxPoolSize = maximum, Timeout = 5, CommandTimeout = 5 }.ConnectionString;

    private static JobApplicationAuxiliaryResources? Auxiliary(JobApplicationSourceOwner owner) =>
        (JobApplicationAuxiliaryResources?)typeof(JobApplicationSourceOwner).GetField("_auxiliary", PrivateInstance)!.GetValue(owner);

    private static NpgsqlConnection? AuxiliaryConnection(JobApplicationSourceOwner owner) => Auxiliary(owner) is { } auxiliary
        ? (NpgsqlConnection?)typeof(JobApplicationAuxiliaryResources).GetField("_connection", PrivateInstance)!.GetValue(auxiliary) : null;

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTimeOffset.UtcNow + Budget;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("The actual application acquisition did not reach the required boundary.");
            await Task.Delay(10);
        }
    }

    private static BackgroundJobInfrastructureRegistry MaximumTopology()
    {
        var workers = new[] { 16, 10, 1, 1, 1, 1 };
        var pools = workers.Select((count, index) => new BackgroundJobWorkerPoolRegistration(
            "transactional_" + index.ToString(CultureInfo.InvariantCulture), BackgroundJobWorkerPoolKind.Transactional,
            count, ["transactional_" + index.ToString(CultureInfo.InvariantCulture)])).Concat(
            [new("external", BackgroundJobWorkerPoolKind.External, 1, ["external"]),
                new("control", BackgroundJobWorkerPoolKind.Control, 1, ["control"])]);
        return BackgroundJobInfrastructureRegistry.Create(pools, [], [], []);
    }
}
