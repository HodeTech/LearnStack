using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using FluentAssertions;
using LearnStack.Api.Messaging;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Modules.Tenancy.Application.Contracts.IntegrationEvents;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>The registered production worker, real PostgreSQL roles and actual transport scopes.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class OutboxProcessorTests(SchemaFixture schema)
{
    private const string Traceparent = "00-11111111111111111111111111111111-2222222222222222-01";
    private const string SensitiveCanary = "private-outbox-canary-84e315";
    private static readonly DateTimeOffset Occurred = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan ProofDeadline = TimeSpan.FromSeconds(10);
    private static readonly OutboxDispatchOptions Bounds = new()
    {
        SqlTimeout = TimeSpan.FromSeconds(1),
        PublishTimeout = TimeSpan.FromMilliseconds(150),
        LeaseDuration = TimeSpan.FromSeconds(3),
        IdlePoll = TimeSpan.FromMilliseconds(20),
        ProgressTimeout = TimeSpan.FromSeconds(5),
        SnapshotInterval = TimeSpan.FromMilliseconds(20),
        ShutdownTimeout = TimeSpan.FromMilliseconds(200),
    };

    [Fact]
    public async Task The_actual_API_registers_exactly_one_worker_and_boots_its_guarded_source()
    {
        await using var test = await WorkerDatabase.CreateAsync(schema.Postgres);
        test.Start();

        test.Services.GetServices<IHostedService>().OfType<OutboxProcessor>()
            .Should().ContainSingle().Which.Should().BeSameAs(test.Worker);
        await UntilAsync(() => test.Observation.IsHealthy);
        var source = test.Services.GetRequiredKeyedService<Lazy<NpgsqlDataSource>>(OutboxDispatcherDataSource.Key);
        source.IsValueCreated.Should().BeTrue("startup must connect even when there are no rows");
        await using var connection = await source.Value.OpenConnectionAsync();
        await using var role = new NpgsqlCommand("SELECT session_user, current_user", connection);
        await using var reader = await role.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be(OutboxDispatcherDataSource.Role);
        reader.GetString(1).Should().Be(OutboxDispatcherDataSource.Role);
    }

    [Fact]
    public async Task Two_independent_actual_processors_cannot_publish_or_claim_one_live_lease_twice()
    {
        await using var test = await WorkerDatabase.CreateAsync(schema.Postgres);
        var liveBounds = Bounds with
        {
            PublishTimeout = TimeSpan.FromSeconds(10),
            LeaseDuration = TimeSpan.FromSeconds(30),
            ProgressTimeout = TimeSpan.FromSeconds(45),
        };
        var partition = Guid.CreateVersion7();
        var held = test.Callbacks.Hold(partition);
        test.Start(liveBounds);
        await using var contender = new WorkerApiFactory(test, test.OutboxConnectionString, liveBounds);
        OutboxProcessor? secondWorker = null;
        try
        {
            var secondServices = contender.Services;
            secondWorker = secondServices.GetServices<IHostedService>().OfType<OutboxProcessor>()
                .Should().ContainSingle().Which;
            secondWorker.Should().NotBeSameAs(test.Worker);
            var secondObservation = secondServices.GetRequiredService<OutboxObservation>();
            await UntilAsync(() => test.Observation.IsHealthy && secondObservation.IsHealthy);

            // Host construction and the first empty polls finish before the live
            // lease begins. The whole contention proof shares a shorter deadline.
            using var proof = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var id = await test.InsertAsync(partition: partition).WaitAsync(proof.Token);
            await held.Entered.Task.WaitAsync(proof.Token);
            var acquired = await test.ReadAsync(id).WaitAsync(proof.Token);
            acquired.Attempts.Should().Be(1);
            acquired.Token.Should().NotBeNullOrWhiteSpace();
            var firstPrevious = test.Observation.Snapshot.LastSnapshotTimestamp;
            var secondPrevious = secondObservation.Snapshot.LastSnapshotTimestamp;
            await UntilAsync(() => test.Observation.Snapshot.LastSnapshotTimestamp > firstPrevious
                || secondObservation.Snapshot.LastSnapshotTimestamp > secondPrevious, proof.Token);
            // Either warm processor may win; the other must continue real polls
            // while the winning processor is awaiting its callback.
            var observer = test.Observation.Snapshot.LastSnapshotTimestamp > firstPrevious
                ? test.Observation : secondObservation;
            for (var poll = 0; poll < 2; poll++)
            {
                var previous = observer.Snapshot.LastSnapshotTimestamp;
                await UntilAsync(() => observer.Snapshot.LastSnapshotTimestamp > previous, proof.Token);
            }

            (await test.LeaseIsLiveAsync(id).WaitAsync(proof.Token)).Should().BeTrue("the contention assertion must run before expiry or a timeout");
            (await test.ReadAsync(id).WaitAsync(proof.Token)).Should().Be(acquired);
            test.Callbacks.Constructions.Should().Be(1);
            observer.Snapshot.Queue.PendingCount.Should().Be(1);
            observer.Snapshot.Queue.OldestEligibleAgeSeconds.Should().Be(0);
            held.ReleaseCallback.TrySetResult();
            await UntilAsync(async () => (await test.ReadAsync(id)).ProcessedAt is not null, proof.Token);
            var completed = await test.ReadAsync(id).WaitAsync(proof.Token);
            completed.Attempts.Should().Be(1);
            completed.Token.Should().BeNull();
            test.Callbacks.Constructions.Should().Be(1);
        }
        finally
        {
            held.ReleaseCallback.TrySetResult();
            held.ReleaseCleanup.TrySetResult();
            await test.Worker.StopAsync(CancellationToken.None).WaitAsync(ProofDeadline);
            if (secondWorker is not null) await secondWorker.StopAsync(CancellationToken.None).WaitAsync(ProofDeadline);
            await test.Callbacks.AwaitCleanupAsync().WaitAsync(ProofDeadline);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("wrong-user")]
    [InlineData("unreachable")]
    public async Task Actual_host_startup_rejects_invalid_credentials_without_provider_or_secret_text(string invalid)
    {
        await using var test = await WorkerDatabase.CreateAsync(schema.Postgres);
        var credential = invalid switch
        {
            "missing" => string.Empty,
            "malformed" => "Password=" + SensitiveCanary + ";invalid-field=value",
            "wrong-user" => new NpgsqlConnectionStringBuilder(test.OutboxConnectionString)
            {
                Username = "learnstack_app",
                Password = SensitiveCanary,
            }.ConnectionString,
            "unreachable" => new NpgsqlConnectionStringBuilder(test.OutboxConnectionString)
            {
                Host = "127.0.0.1",
                Port = 1,
                Password = SensitiveCanary,
                Timeout = 1,
            }.ConnectionString,
            _ => throw new ArgumentOutOfRangeException(nameof(invalid)),
        };
        await using var factory = new WorkerApiFactory(test, credential);

        var failure = await Record.ExceptionAsync(() => Task.Run(() => _ = factory.Services).WaitAsync(ProofDeadline));

        failure.Should().NotBeNull();
        failure.Should().BeAssignableTo<InvalidOperationException>();
        failure!.ToString().Should().NotContain(SensitiveCanary).And.NotContain(credential == string.Empty ? "Password=" : credential);
        if (invalid == "missing")
            failure.Message.Should().Contain("ConnectionStrings:OutboxDispatcher")
                .And.Contain("Npgsql key/value").And.Contain("learnstack_outbox_admin");
        test.Logs.Text.Should().NotContain(SensitiveCanary);
    }

    [Fact]
    public async Task Real_provisioning_rows_remain_Pending_across_hosted_polls_without_construction_or_mutation()
    {
        await using var test = await WorkerDatabase.CreateAsync(schema.Postgres, seedTenant: false);
        test.Start();
        await using (var scope = test.Services.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ProvisionTenantCommand(
                TenantId.From(SchemaFixture.TenantA), "worker-probe", "Worker probe",
                OrganizationId.From(SchemaFixture.OrgA1), "hq", "HQ"));
            result.IsSuccess.Should().BeTrue();
        }
        var id = await test.SingleIdAsync();
        var before = await test.ReadAsync(id);
        for (var iteration = 0; iteration < 3; iteration++) await test.NextSnapshotAsync();

        (await test.ReadAsync(id)).Should().Be(before);
        before.Type.Should().Be(typeof(TenantCreatedIntegrationEventV1).AssemblyQualifiedName);
        before.Attempts.Should().Be(0);
        test.Callbacks.Constructions.Should().Be(0);
        test.Observation.Snapshot.Queue.PendingCount.Should().Be(1);
        test.Observation.Snapshot.Queue.DeferredCount.Should().Be(1);
        (await test.ReadTerminalAuditsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_poison_first_row_terminalizes_with_MUST_audit_and_the_following_real_handler_progresses()
    {
        await using var test = await WorkerDatabase.CreateAsync(schema.Postgres);
        using var traces = new DispatcherTraces();
        var poison = await test.InsertAsync(type: SensitiveCanary, payload: "{\"private\":\"" + SensitiveCanary + "\"}", correlation: SensitiveCanary);
        var following = await test.InsertAsync();
        test.Start();

        await UntilAsync(async () => (await test.ReadAsync(poison)).DeadLetteredAt is not null
            && (await test.ReadAsync(following)).ProcessedAt is not null);

        var row = await test.ReadAsync(poison);
        row.TerminalReason.Should().Be("unknown_type");
        row.Attempts.Should().Be(1);
        row.ProcessedAt.Should().BeNull();
        test.Callbacks.Constructions.Should().Be(1);
        var audit = (await test.ReadTerminalAuditsAsync()).Should().ContainSingle().Which;
        audit.OperationClass.Should().Be("Must");
        audit.OperationType.Should().Be("SecurityEvent");
        audit.Metadata.Should().Contain(poison.ToString("D")).And.NotContain(SensitiveCanary);
        audit.Correlation.Should().NotBe(SensitiveCanary);
        ActivityContext.TryParse(audit.Correlation, null, out _).Should().BeTrue();
        test.Logs.Text.Should().NotContain(SensitiveCanary);
        row.LastError.Should().Be("terminal:unknown_type:closed");
        traces.Records.Should().NotBeEmpty();
        string.Join("\n", traces.Records).Should().NotContain(SensitiveCanary);
        await test.NextSnapshotAsync();
        (await test.ReadTerminalAuditsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Ordinary_handler_failure_persists_a_closed_descriptor_and_allows_following_work()
    {
        await using var test = await WorkerDatabase.CreateAsync(schema.Postgres);
        using var traces = new DispatcherTraces();
        var partition = Guid.CreateVersion7();
        test.Callbacks.Fail(partition, new InvalidOperationException(SensitiveCanary));
        var failed = await test.InsertAsync(partition: partition);
        var following = await test.InsertAsync();
        test.Start();
        await UntilAsync(async () => (await test.ReadAsync(failed)).LastError == "publish:failed:provider");
        await test.PostponeAsync(failed);
        await UntilAsync(async () => (await test.ReadAsync(following)).ProcessedAt is not null);

        var row = await test.ReadAsync(failed);
        row.Attempts.Should().Be(1);
        row.Token.Should().BeNull();
        row.ProcessedAt.Should().BeNull();
        test.Callbacks.Constructions.Should().Be(2);
        test.Logs.Text.Should().NotContain(SensitiveCanary);
        traces.Records.Should().NotBeEmpty();
        string.Join("\n", traces.Records).Should().NotContain(SensitiveCanary);
    }

    [Fact]
    public async Task Explicit_intentional_absence_acknowledges_without_constructing_a_handler()
    {
        await using var test = await WorkerDatabase.CreateAsync(schema.Postgres);
        var fact = new UnsubscribedEventV1
        {
            EventId = Guid.CreateVersion7(),
            TenantId = SchemaFixture.TenantA,
            OccurredAt = Occurred,
            SubjectId = Guid.CreateVersion7(),
        };
        var id = await test.InsertAsync(fact: fact);
        test.Start();

        await UntilAsync(async () => (await test.ReadAsync(id)).ProcessedAt is not null);

        (await test.ReadAsync(id)).Attempts.Should().Be(1);
        test.Callbacks.Constructions.Should().Be(0);
        (await test.ReadTerminalAuditsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Timeout_persists_failure_and_holds_the_partition_until_actual_callback_scope_cleanup()
    {
        await using var test = await WorkerDatabase.CreateAsync(schema.Postgres);
        var partition = Guid.CreateVersion7();
        var held = test.Callbacks.Hold(partition, holdCleanup: true);
        var first = await test.InsertAsync(partition: partition);
        var sibling = await test.InsertAsync(partition: partition);
        var unrelated = await test.InsertAsync();
        test.Start();
        await held.Entered.Task.WaitAsync(ProofDeadline);
        await UntilAsync(async () => (await test.ReadAsync(first)).LastError == "publish:timeout:deadline");
        await test.PostponeAsync(first);
        await UntilAsync(async () => (await test.ReadAsync(unrelated)).ProcessedAt is not null);

        test.Observation.IsHealthy.Should().BeFalse();
        (await test.ReadAsync(sibling)).Attempts.Should().Be(0);
        held.Disposals.Should().Be(0);
        held.CancellationToken.IsCancellationRequested.Should().BeTrue();
        held.ReleaseCallback.TrySetResult();
        await held.CleanupEntered.Task.WaitAsync(ProofDeadline);
        await test.NextSnapshotAsync();
        (await test.ReadAsync(sibling)).Attempts.Should().Be(0, "partition ownership includes asynchronous scope cleanup");
        held.Disposals.Should().Be(0);
        _ = held.CancellationToken.WaitHandle;

        held.ReleaseCleanup.TrySetResult();
        await UntilAsync(async () => (await test.ReadAsync(sibling)).ProcessedAt is not null);
        await UntilAsync(() => test.Observation.Snapshot.OutstandingPublishes == 0);
        var failed = await test.ReadAsync(first);
        failed.ProcessedAt.Should().BeNull("late callback success cannot acknowledge a timed-out claim");
        failed.Attempts.Should().Be(1);
        failed.Token.Should().BeNull();
        held.Disposals.Should().Be(2, "the released partition subsequently constructs and disposes its sibling's handler");
    }

    [Fact]
    public async Task Four_retained_callbacks_stop_all_acquisition_and_releasing_one_restores_capacity()
    {
        await using var test = await WorkerDatabase.CreateAsync(schema.Postgres);
        var held = new List<CallbackBarrier>();
        var heldIds = new List<Guid>();
        for (var index = 0; index < 4; index++)
        {
            var partition = Guid.CreateVersion7();
            held.Add(test.Callbacks.Hold(partition));
            heldIds.Add(await test.InsertAsync(partition: partition));
        }
        var fifth = await test.InsertAsync();
        test.Start();
        await Task.WhenAll(held.Select(item => item.Entered.Task)).WaitAsync(ProofDeadline);
        await UntilAsync(() => test.Observation.Snapshot.OutstandingPublishes == 4);
        foreach (var id in heldIds) await test.PostponeAsync(id);
        await test.NextSnapshotAsync();

        (await test.ReadAsync(fifth)).Attempts.Should().Be(0, "the four retained callbacks exhaust the global allowance");
        test.Callbacks.Constructions.Should().Be(4);
        test.Observation.IsHealthy.Should().BeFalse();
        held[0].ReleaseCallback.TrySetResult();
        await UntilAsync(async () => (await test.ReadAsync(fifth)).ProcessedAt is not null);
        test.Observation.Snapshot.OutstandingPublishes.Should().Be(3);
        (await test.ReadAsync(heldIds[0])).ProcessedAt.Should().BeNull();
    }

    [Fact]
    public async Task Shutdown_uses_one_finite_wait_for_all_four_retained_callbacks()
    {
        await using var test = await WorkerDatabase.CreateAsync(schema.Postgres);
        var time = new ShutdownTimeProvider(Bounds.ShutdownTimeout);
        var held = new List<CallbackBarrier>();
        for (var index = 0; index < 4; index++)
        {
            var partition = Guid.CreateVersion7();
            held.Add(test.Callbacks.Hold(partition));
            await test.InsertAsync(partition: partition);
        }
        test.Start(time: time);
        await Task.WhenAll(held.Select(item => item.Entered.Task)).WaitAsync(ProofDeadline);
        await UntilAsync(() => test.Observation.Snapshot.OutstandingPublishes == 4);
        time.ControlShutdownTimers();
        var stop = test.Worker.StopAsync(CancellationToken.None);
        await time.BudgetRegistered.Task.WaitAsync(ProofDeadline);
        await test.Worker.ExecuteTask!.WaitAsync(ProofDeadline);

        // The worker has exited, but all callback scopes remain live. Expire
        // exactly the first deadline; a fresh drain budget must not get time.
        stop.IsCompleted.Should().BeFalse();
        time.Advance(Bounds.ShutdownTimeout);
        await stop.WaitAsync(ProofDeadline);

        test.Observation.Snapshot.OutstandingPublishes.Should().Be(4);
        foreach (var callback in held)
        {
            callback.Disposals.Should().Be(0);
            callback.CancellationToken.IsCancellationRequested.Should().BeTrue();
            _ = callback.CancellationToken.WaitHandle;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timeout_and_shutdown_bound_the_wait_and_keep_live_scope_and_CTS_owned_until_completion(bool synchronousBlock)
    {
        await using var test = await WorkerDatabase.CreateAsync(schema.Postgres);
        var partition = Guid.CreateVersion7();
        var held = test.Callbacks.Hold(partition, synchronousBlock: synchronousBlock);
        var id = await test.InsertAsync(partition: partition);
        test.Start();
        await held.Entered.Task.WaitAsync(ProofDeadline);
        await UntilAsync(async () => (await test.ReadAsync(id)).LastError == "publish:timeout:deadline");
        await UntilAsync(() => test.Observation.Snapshot.OutstandingPublishes == 1);
        var stop = Stopwatch.StartNew();

        await test.Worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        stop.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        held.Disposals.Should().Be(0);
        held.CancellationToken.IsCancellationRequested.Should().BeTrue();
        _ = held.CancellationToken.WaitHandle;
        held.ReleaseCallback.TrySetResult();
        await held.Completed.Task.WaitAsync(ProofDeadline);
        await UntilAsync(() => TokenSourceDisposed(held.CancellationToken));
        held.Disposals.Should().Be(1);
        (await test.ReadAsync(id)).ProcessedAt.Should().BeNull();
    }

    [Fact]
    public async Task A_failed_terminal_audit_reports_unhealthy_while_the_actual_healthz_remains_live()
    {
        await using var test = await WorkerDatabase.CreateAsync(schema.Postgres);
        test.Start();
        await UntilAsync(() => test.Observation.IsHealthy);
        await test.AuditInsertGrantAsync(grant: false);
        var id = await test.InsertAsync(type: SensitiveCanary, correlation: SensitiveCanary);
        await UntilAsync(() => test.Observation.Snapshot.TerminalAuditFailed);
        await UntilAsync(() => test.Logs.Text.Contains("Outbox dispatch iteration failed with outcome iteration_failed.",
            StringComparison.Ordinal));

        test.Observation.IsHealthy.Should().BeFalse();
        test.Logs.Text.Should().NotContain(SensitiveCanary);
        var row = await test.ReadAsync(id);
        row.TerminalReason.Should().Be("unknown_type");
        row.DeadLetteredAt.Should().BeNull();
        row.ProcessedAt.Should().BeNull();
        (await test.ReadTerminalAuditsAsync()).Should().BeEmpty();
        var health = await test.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(
            registration => registration.Name == OutboxHealthCheck.Name);
        health.Entries.Keys.Should().ContainSingle().Which.Should().Be(OutboxHealthCheck.Name);
        health.Status.Should().Be(HealthStatus.Unhealthy);
        var response = await test.Client.GetAsync("/healthz");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static bool TokenSourceDisposed(CancellationToken token)
    {
        try { _ = token.WaitHandle; return false; }
        catch (ObjectDisposedException) { return true; }
    }

    private static Task UntilAsync(Func<bool> condition, CancellationToken cancellationToken = default)
        => UntilAsync(() => Task.FromResult(condition()), cancellationToken);

    private static async Task UntilAsync(Func<Task<bool>> condition, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ProofDeadline);
        while (!await condition().WaitAsync(deadline.Token)) await Task.Delay(TimeSpan.FromMilliseconds(10), deadline.Token);
    }

    [IntegrationEventPartition(nameof(SubjectId))]
    public sealed record WorkerEventV1 : IntegrationEventBase
    {
        public required Guid SubjectId { get; init; }
        public override string Topic => "learnstack.test.worker";
        public override string PartitionKey => SubjectId.ToString("D");
    }

    [IntegrationEventPartition(nameof(SubjectId))]
    public sealed record UnsubscribedEventV1 : IntegrationEventBase
    {
        public required Guid SubjectId { get; init; }
        public override string Topic => "learnstack.test.unsubscribed";
        public override string PartitionKey => SubjectId.ToString("D");
    }

    /// <summary>One discovered, configurable real-bus handler; no production subscription is activated.</summary>
    public sealed class WorkerHandler(CallbackControls controls, ITenantContextAccessor accessor)
        : IIntegrationEventHandler<WorkerEventV1>, IAsyncDisposable
    {
        private readonly CallbackControls _controls = controls.RecordConstruction();
        private CallbackBarrier? _barrier;

        public Task HandleAsync(WorkerEventV1 integrationEvent, CancellationToken cancellationToken)
        {
            accessor.Current.Should().NotBeNull();
            accessor.Current!.TenantId.Value.Should().Be(integrationEvent.TenantId);
            accessor.Current.UserId.Should().Be(UserId.SystemActor);
            _barrier = _controls.Enter(integrationEvent.SubjectId, cancellationToken);
            if (_barrier.Failure is { } failure) throw failure;
            if (_barrier.SynchronousBlock) _barrier.ReleaseCallback.Task.GetAwaiter().GetResult();
            return _barrier.ReleaseCallback.Task;
        }

        public async ValueTask DisposeAsync()
        {
            if (_barrier is null) return;
            _barrier.CleanupEntered.TrySetResult();
            await _barrier.ReleaseCleanup.Task;
            _barrier.Disposed();
        }
    }

    public sealed class CallbackControls
    {
        private readonly ConcurrentDictionary<Guid, CallbackBarrier> _partitions = new();
        private int _constructions;
        public int Constructions => Volatile.Read(ref _constructions);

        public CallbackBarrier Hold(Guid partition, bool holdCleanup = false, bool synchronousBlock = false)
        {
            var barrier = new CallbackBarrier(holdCallback: true, holdCleanup, synchronousBlock);
            _partitions.TryAdd(partition, barrier).Should().BeTrue();
            return barrier;
        }

        public void Fail(Guid partition, Exception failure) => _partitions.TryAdd(partition,
            new CallbackBarrier(false, false, false) { Failure = failure }).Should().BeTrue();

        public CallbackControls RecordConstruction()
        {
            Interlocked.Increment(ref _constructions);
            return this;
        }

        public CallbackBarrier Enter(Guid partition, CancellationToken token)
        {
            var barrier = _partitions.GetOrAdd(partition, _ => new CallbackBarrier(false, false, false));
            barrier.CancellationToken = token;
            barrier.Entered.TrySetResult();
            return barrier;
        }

        public void ReleaseAll()
        {
            foreach (var barrier in _partitions.Values)
            {
                barrier.ReleaseCallback.TrySetResult();
                barrier.ReleaseCleanup.TrySetResult();
            }
        }

        public Task AwaitCleanupAsync() => Task.WhenAll(_partitions.Values
            .Where(barrier => barrier.Entered.Task.IsCompleted).Select(barrier => barrier.Completed.Task));
    }

    public sealed class CallbackBarrier
    {
        private int _disposals;
        public CallbackBarrier(bool holdCallback, bool holdCleanup, bool synchronousBlock)
        {
            SynchronousBlock = synchronousBlock;
            if (!holdCallback) ReleaseCallback.TrySetResult();
            if (!holdCleanup) ReleaseCleanup.TrySetResult();
        }

        public Exception? Failure { get; init; }
        public bool SynchronousBlock { get; }
        public int Disposals => Volatile.Read(ref _disposals);
        public CancellationToken CancellationToken { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCallback { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Disposed()
        {
            Interlocked.Increment(ref _disposals);
            Completed.TrySetResult();
        }
    }

    private sealed record Row(int Attempts, string? Token, DateTimeOffset? ProcessedAt,
        string? TerminalReason, DateTimeOffset? DeadLetteredAt, string? LastError,
        DateTimeOffset AvailableAfter, string Type, string Payload);
    private sealed record TerminalAudit(string OperationType, string OperationClass, string Correlation, string Metadata);

    /// <summary>Controls shutdown deadlines while all other host timers keep running normally.</summary>
    private sealed class ShutdownTimeProvider(TimeSpan shutdownTimeout) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ShutdownTimer> _timers = [];
        private bool _controlled;
        private long? _origin;
        private TimeSpan _elapsed;
        private TimeSpan _lastRegistration;
        public TaskCompletionSource BudgetRegistered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ControlShutdownTimers() { lock (_gate) _controlled = true; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                if (!_controlled || dueTime != shutdownTimeout)
                    return System.CreateTimer(callback, state, dueTime, period);
                period.Should().Be(Timeout.InfiniteTimeSpan);
                _origin ??= Stopwatch.GetTimestamp();
                var registration = _timers.Count == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_origin.Value);
                // Preserve the real ordering even below TimeSpan's resolution:
                // a later budget cannot expire at the first budget's deadline.
                if (_timers.Count > 0 && registration <= _lastRegistration)
                    registration = _lastRegistration + TimeSpan.FromTicks(1);
                _lastRegistration = registration;
                var timer = new ShutdownTimer(this, callback, state,
                    (_elapsed > registration ? _elapsed : registration) + dueTime);
                _timers.Add(timer);
                BudgetRegistered.TrySetResult();
                return timer;
            }
        }

        public void Advance(TimeSpan duration)
        {
            ShutdownTimer[] due;
            lock (_gate)
            {
                _elapsed += duration;
                due = _timers.Where(timer => timer.Due is { } at && at <= _elapsed).ToArray();
                foreach (var timer in due) timer.Due = null;
            }
            foreach (var timer in due) timer.Fire();
        }

        private sealed class ShutdownTimer(ShutdownTimeProvider owner, TimerCallback callback,
            object? state, TimeSpan due) : ITimer
        {
            private bool _disposed;
            public TimeSpan? Due { get; set; } = due;
            public void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    if (_disposed) return false;
                    period.Should().Be(Timeout.InfiniteTimeSpan);
                    Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._elapsed + dueTime;
                    return true;
                }
            }
            public void Dispose()
            {
                lock (owner._gate) { _disposed = true; Due = null; }
            }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class WorkerApiFactory(WorkerDatabase database, string? credential,
        OutboxDispatchOptions? bounds = null, TimeProvider? time = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("ConnectionStrings:Default", database.AppConnectionString);
            builder.UseSetting("ConnectionStrings:PlatformAdmin", database.PlatformConnectionString);
            builder.UseSetting("ConnectionStrings:OutboxDispatcher", credential);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<OutboxDispatchOptions>();
                services.AddSingleton(bounds ?? Bounds);
                if (time is not null)
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton(time);
                }
                services.RemoveAll<IntegrationEventHandlerRegistry>();
                var handlers = IntegrationEventHandlerRegistry.Discover(typeof(WorkerHandler).Assembly);
                handlers.All.Should().ContainSingle().Which.HandlerType.Should().Be<WorkerHandler>();
                services.AddSingleton(handlers);
                services.RemoveAll<OutboxEventRegistry>();
                services.AddSingleton(OutboxEventRegistry.Create([
                    OutboxEventRegistration.Required(typeof(WorkerEventV1),
                        [OutboxSubscriptionManifest.Active("test.worker.v1", typeof(WorkerHandler))]),
                    OutboxEventRegistration.IntentionallyUnsubscribed(typeof(UnsubscribedEventV1)),
                    OutboxEventRegistration.Required(typeof(TenantCreatedIntegrationEventV1), [
                        OutboxSubscriptionManifest.Pending("customization.initialize-content-type-default.v1"),
                        OutboxSubscriptionManifest.Pending("customization.initialize-taxonomy-default.v1"),
                    ]),
                ], [], handlers, [typeof(WorkerEventV1).Assembly, typeof(TenantCreatedIntegrationEventV1).Assembly]));
                services.AddSingleton(database.Callbacks);
                services.AddScoped<WorkerHandler>();
                services.AddSingleton<ILogger<OutboxProcessor>>(database.Logs);
            });
        }
    }

    private sealed class WorkerDatabase : IAsyncDisposable
    {
        private readonly DisposableSchemaDatabase _database;
        private readonly NpgsqlDataSource _appSource;
        private readonly NpgsqlDataSource _dispatchSource;
        private WorkerApiFactory? _factory;
        private HttpClient? _client;
        private int _insertSequence;
        private WorkerDatabase(DisposableSchemaDatabase database)
        {
            _database = database;
            _appSource = NpgsqlDataSource.Create(database.AppConnectionString);
            _dispatchSource = OutboxDispatcherDataSource.Build(database.OutboxConnectionString);
        }

        public string AppConnectionString => _database.AppConnectionString;
        public string PlatformConnectionString => _database.PlatformConnectionString;
        public string OutboxConnectionString => _database.OutboxConnectionString;
        public CallbackControls Callbacks { get; } = new();
        public RecordingLogger Logs { get; } = new();
        public IServiceProvider Services => _factory!.Services;
        public OutboxProcessor Worker => Services.GetServices<IHostedService>().OfType<OutboxProcessor>().Single();
        public OutboxObservation Observation => Services.GetRequiredService<OutboxObservation>();
        public HttpClient Client => _client!;

        public static async Task<WorkerDatabase> CreateAsync(PostgresFixture postgres, bool seedTenant = true)
        {
            var test = new WorkerDatabase(await DisposableSchemaDatabase.CreateAsync(postgres));
            try
            {
                if (seedTenant) await test.SeedTenantAsync();
                return test;
            }
            catch { await test.DisposeAsync(); throw; }
        }

        public void Start(OutboxDispatchOptions? bounds = null, TimeProvider? time = null)
        {
            _factory = new WorkerApiFactory(this, OutboxConnectionString, bounds, time);
            _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost") });
            Services.GetRequiredService<IEventBus>().Should().BeOfType<InProcessEventBus>();
        }

        public async Task NextSnapshotAsync()
        {
            var before = Observation.Snapshot.LastSnapshotTimestamp ?? long.MinValue;
            await UntilAsync(() => Observation.Snapshot.LastSnapshotTimestamp > before);
        }

        public async Task<Guid> InsertAsync(Guid? partition = null, string? type = null,
            string? payload = null, string? correlation = null, IntegrationEventBase? fact = null)
        {
            fact ??= new WorkerEventV1
            {
                EventId = Guid.CreateVersion7(),
                TenantId = SchemaFixture.TenantA,
                OccurredAt = Occurred,
                SubjectId = partition ?? Guid.CreateVersion7(),
            };
            await using var connection = await _appSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await SchemaQueries.AssertApplicationRoleAsync(connection);
            await SchemaQueries.SetTenantAsync(connection, transaction, fact.TenantId);
            await using var insert = new NpgsqlCommand("""
                INSERT INTO outbox_messages
                    (id, tenant_id, occurred_at, correlation_id, type, topic, partition_key, payload, available_after)
                VALUES (@id, @tenant, @occurred, @correlation, @type, @topic, @partition, @payload, @available)
                """, connection, transaction);
            insert.Parameters.AddWithValue("id", fact.EventId);
            insert.Parameters.AddWithValue("tenant", fact.TenantId);
            insert.Parameters.AddWithValue("occurred", fact.OccurredAt);
            insert.Parameters.AddWithValue("correlation", correlation ?? Traceparent);
            insert.Parameters.AddWithValue("type", type ?? fact.GetType().AssemblyQualifiedName!);
            insert.Parameters.AddWithValue("topic", fact.Topic);
            insert.Parameters.AddWithValue("partition", fact.PartitionKey);
            insert.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = payload ?? fact.ToPayloadJson() });
            insert.Parameters.AddWithValue("available", Occurred.AddMilliseconds(Interlocked.Increment(ref _insertSequence)));
            (await insert.ExecuteNonQueryAsync()).Should().Be(1);
            await transaction.CommitAsync();
            return fact.EventId;
        }

        public async Task<Row> ReadAsync(Guid id)
        {
            await using var connection = await _dispatchSource.OpenConnectionAsync();
            await using var read = new NpgsqlCommand("""
                SELECT attempts, locked_by, processed_at, terminal_reason, dead_lettered_at,
                    last_error, available_after, type, payload::text FROM outbox_messages WHERE id = @id
                """, connection);
            read.Parameters.AddWithValue("id", id);
            await using var reader = await read.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            return new Row(reader.GetInt32(0), Text(reader, 1), Timestamp(reader, 2), Text(reader, 3),
                Timestamp(reader, 4), Text(reader, 5), reader.GetFieldValue<DateTimeOffset>(6), reader.GetString(7), reader.GetString(8));
        }

        public async Task<Guid> SingleIdAsync()
        {
            await using var connection = await _dispatchSource.OpenConnectionAsync();
            await using var read = new NpgsqlCommand("SELECT id FROM outbox_messages", connection);
            await using var reader = await read.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            var id = reader.GetGuid(0);
            (await reader.ReadAsync()).Should().BeFalse();
            return id;
        }

        public async Task<bool> LeaseIsLiveAsync(Guid id)
        {
            await using var connection = await _dispatchSource.OpenConnectionAsync();
            await using var read = new NpgsqlCommand(
                "SELECT locked_until > clock_timestamp() FROM outbox_messages WHERE id = @id", connection);
            read.Parameters.AddWithValue("id", id);
            return await read.ExecuteScalarAsync() is true;
        }

        public async Task PostponeAsync(Guid id)
        {
            await using var connection = await _dispatchSource.OpenConnectionAsync();
            await using var update = new NpgsqlCommand(
                "UPDATE outbox_messages SET available_after = clock_timestamp() + interval '1 hour' WHERE id = @id", connection);
            update.Parameters.AddWithValue("id", id);
            (await update.ExecuteNonQueryAsync()).Should().Be(1);
        }

        public async Task AuditInsertGrantAsync(bool grant)
        {
            await using var connection = await PostgresFixture.OpenAsync(_database.MigrationConnectionString);
            await using var command = connection.CreateCommand();
            command.CommandText = grant ? "GRANT INSERT ON audit_log TO learnstack_outbox_admin"
                : "REVOKE INSERT ON audit_log FROM learnstack_outbox_admin";
            await command.ExecuteNonQueryAsync();
        }

        public async Task<List<TerminalAudit>> ReadTerminalAuditsAsync()
        {
            await using var connection = await _appSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await SchemaQueries.AssertApplicationRoleAsync(connection);
            await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
            await using var read = new NpgsqlCommand("""
                SELECT operation_type, operation_class, correlation_id, metadata::text
                FROM audit_log WHERE operation = 'platform.outbox.dead_letter'
                """, connection, transaction);
            var rows = new List<TerminalAudit>();
            await using var reader = await read.ExecuteReaderAsync();
            while (await reader.ReadAsync()) rows.Add(new TerminalAudit(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            return rows;
        }

        public async ValueTask DisposeAsync()
        {
            Callbacks.ReleaseAll();
            if (_factory is not null)
            {
                await Worker.StopAsync(CancellationToken.None).WaitAsync(ProofDeadline);
                await Callbacks.AwaitCleanupAsync().WaitAsync(ProofDeadline);
                _client?.Dispose();
                await _factory.DisposeAsync();
            }
            await _dispatchSource.DisposeAsync();
            await _appSource.DisposeAsync();
            await _database.DisposeAsync();
        }

        private async Task SeedTenantAsync()
        {
            await using var connection = await _appSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await SchemaQueries.AssertApplicationRoleAsync(connection);
            await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
            await SchemaQueries.ExecuteAsync(connection, transaction, """
                INSERT INTO tenants (id, slug, display_name, status, created_at, created_by, row_version)
                VALUES (@tenant, 'worker-probe', 'Worker probe', 'Trial', now(), @actor, 0);
                INSERT INTO organizations (id, tenant_id, slug, display_name, status, created_at, created_by, row_version)
                VALUES (@organization, @tenant, 'hq', 'HQ', 'Active', now(), @actor, 0);
                """, ("tenant", SchemaFixture.TenantA), ("organization", SchemaFixture.OrgA1), ("actor", UserId.SystemActor.Value));
            await transaction.CommitAsync();
        }

        private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        private static DateTimeOffset? Timestamp(NpgsqlDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
    }

    private sealed class DispatcherTraces : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly ConcurrentQueue<string> _records = new();
        public DispatcherTraces()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == OutboxObservation.InstrumentationName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => _records.Enqueue(string.Join("\n", new[]
                {
                    activity.DisplayName, activity.Id, activity.ParentId, activity.TraceStateString,
                    activity.StatusDescription,
                    string.Join(";", activity.TagObjects.Select(tag => tag.Key + "=" + tag.Value)),
                    string.Join(";", activity.Events.Select(@event => @event.Name + ":" +
                        string.Join(",", @event.Tags.Select(tag => tag.Key + "=" + tag.Value)))),
                })),
            };
            ActivitySource.AddActivityListener(_listener);
        }
        public IReadOnlyCollection<string> Records => _records.ToArray();
        public void Dispose() => _listener.Dispose();
    }

    private sealed class RecordingLogger : ILogger<OutboxProcessor>
    {
        private readonly ConcurrentQueue<string> _messages = new();
        public string Text => string.Join('\n', _messages);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => _messages.Enqueue(formatter(state, exception) + exception);
    }
}
