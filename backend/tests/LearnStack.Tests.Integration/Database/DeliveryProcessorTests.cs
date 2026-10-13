using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using LearnStack.Api.Messaging;
using LearnStack.Application.Messaging;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Modules.Customization.Domain;
using LearnStack.Modules.Customization.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Time;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class DeliveryProcessorTests(SchemaFixture schema)
{
    private const string First = "proof.first.v1";
    private const string Second = "proof.second.v1";
    private const string Pending = "proof.pending.v1";
    private const string Privileged = "proof.privileged.v1";
    private const string Canary = "private_delivery_worker_canary_84e315";
    private const string Trace = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private static readonly DeliveryDispatchOptions Bounds = new()
    {
        SqlTimeout = TimeSpan.FromSeconds(1),
        ExecutionTimeout = TimeSpan.FromSeconds(2),
        LeaseDuration = TimeSpan.FromSeconds(8),
        ProgressTimeout = TimeSpan.FromSeconds(12),
        IdlePoll = TimeSpan.FromMilliseconds(20),
        SnapshotInterval = TimeSpan.FromMilliseconds(40),
        ShutdownTimeout = TimeSpan.FromMilliseconds(200),
    };

    [Fact]
    public async Task Actual_API_registers_one_delivery_worker_with_shared_guarded_source_and_private_health()
    {
        await using var fixture = await Fixture.CreateAsync(schema);
        fixture.Start();
        fixture.Services.GetServices<IHostedService>().OfType<DeliveryProcessor>().Should().ContainSingle();
        await UntilAsync(() => fixture.Observation.IsHealthy);
        var source = fixture.Services.GetRequiredKeyedService<Lazy<NpgsqlDataSource>>(OutboxDispatcherDataSource.Key);
        source.IsValueCreated.Should().BeTrue();
        await using var connection = await source.Value.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT current_user", connection);
        (await command.ExecuteScalarAsync()).Should().Be(OutboxDispatcherDataSource.Role);
        using var client = fixture.Host.CreateClient();
        (await client.GetAsync("/healthz")).IsSuccessStatusCode.Should().BeTrue();
        (await client.GetAsync("/health/delivery")).StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Siblings_have_independent_retries_and_Active_delivery_runs_beside_exact_Pending_and_privileged_holds()
    {
        await using var fixture = await Fixture.CreateAsync(schema);
        var fact = Fact();
        fixture.Controls.For(fact, First).Fault = "handler";
        await fixture.InsertAsync(fact, First);
        await fixture.InsertAsync(fact, Second);
        await fixture.InsertAsync(fact, Pending);
        await fixture.InsertAsync(fact, Privileged);
        fixture.Start();
        await UntilAsync(async () => (await fixture.RowAsync(fact, First)).Error is not null
            && (await fixture.RowAsync(fact, Second)).Processed);
        await fixture.PostponeAsync(fact, First);
        var failed = await fixture.RowAsync(fact, First);
        var completed = await fixture.RowAsync(fact, Second);
        failed.Attempts.Should().Be(1);
        completed.Attempts.Should().Be(1);
        (await fixture.RowAsync(fact, Pending)).Attempts.Should().Be(0);
        (await fixture.RowAsync(fact, Privileged)).Attempts.Should().Be(0);
        (await fixture.CountAsync("inbox_messages", fact.EventId)).Should().Be(1);
        fixture.Controls.For(fact, First).Fault = null;
        await fixture.MakeDueAsync(fact, First);
        await UntilAsync(async () => (await fixture.RowAsync(fact, First)).Processed);
        (await fixture.RowAsync(fact, First)).Attempts.Should().Be(2);
        (await fixture.RowAsync(fact, Second)).Should().Be(completed);
        fixture.Controls.For(fact, Second).Executions.Should().Be(1);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task Expired_claim_recovery_spends_no_execution_and_six_has_no_seventh_invocation(int attempts)
    {
        await using var fixture = await Fixture.CreateAsync(schema);
        var fact = Fact();
        await fixture.InsertAsync(fact);
        await fixture.ScheduleAsync(fact, First, attempts, expired: true);
        fixture.Start();
        await UntilAsync(async () => (await fixture.RowAsync(fact)).Error is not null);
        if (attempts < 6) await fixture.PostponeAsync(fact, First);
        var row = await fixture.RowAsync(fact);
        row.Attempts.Should().Be(attempts);
        fixture.Controls.For(fact).Executions.Should().Be(0);
        if (attempts == 6)
        {
            await UntilAsync(async () => (await fixture.RowAsync(fact)).Deadlettered);
            (await fixture.RowAsync(fact)).Terminal.Should().Be("retry_exhausted");
        }
        else row.Terminal.Should().BeNull();
    }

    [Theory]
    [InlineData("handler")]
    [InlineData("foreign-cancel")]
    [InlineData("owned-cancel")]
    [InlineData("null-task")]
    [InlineData("constructor")]
    public async Task Worker_bounds_handler_faults_and_a_different_partition_keeps_progressing(string fault)
    {
        await using var fixture = await Fixture.CreateAsync(schema);
        var bad = Fact();
        var good = Fact();
        fixture.Controls.For(bad).Fault = fault;
        if (fault == "constructor") fixture.Controls.ConstructorFaults = 1;
        await fixture.InsertAsync(bad);
        await fixture.InsertAsync(good, Second);
        fixture.Start();
        await UntilAsync(async () => (await fixture.RowAsync(bad)).Error is not null
            && (await fixture.RowAsync(good, Second)).Processed);
        await fixture.PostponeAsync(bad, First);
        (await fixture.CountAsync("inbox_messages", bad.EventId)).Should().Be(0);
        (await fixture.BusinessAsync(fixture.Controls.For(bad).EffectId)).Should().Be(0);
        fixture.Logs.Text.Should().NotContain(Canary).And.NotContain(bad.EventId.ToString());
        (await fixture.RowAsync(bad)).Error.Should().NotContain(Canary);
    }

    [Fact]
    public async Task Failed_timeout_transition_still_closes_gate_and_ignored_token_cannot_commit_late()
    {
        await using var fixture = await Fixture.CreateAsync(schema);
        var fact = Fact();
        var plan = fixture.Controls.For(fact);
        plan.HoldHandler = true;
        await fixture.InsertAsync(fact);
        fixture.Start();
        await plan.Entered.Task.WaitAsync(Deadline);
        await fixture.SchemaAsync("REVOKE UPDATE (attempts, available_after, locked_by, locked_until, terminal_reason, dead_lettered_at, last_error) ON event_deliveries FROM learnstack_outbox_admin");
        await UntilAsync(() => fixture.Observation.Snapshot.LoopFailed);
        fixture.Observation.Snapshot.OutstandingExecutions.Should().Be(1);
        plan.ReleaseHandler.TrySetResult();
        await plan.Disposed.Task.WaitAsync(Deadline);
        (await fixture.BusinessAsync(plan.EffectId)).Should().Be(0);
        (await fixture.CountAsync("inbox_messages", fact.EventId)).Should().Be(0);
        (await fixture.RowAsync(fact)).Processed.Should().BeFalse();
    }

    [Fact]
    public async Task Synchronous_handler_construction_cannot_monopolize_worker_wait_or_other_partitions()
    {
        await using var fixture = await Fixture.CreateAsync(schema);
        fixture.Controls.HoldConstructor = true;
        var held = Fact();
        var following = Fact();
        await fixture.InsertAsync(held);
        await fixture.InsertAsync(following, Second);
        fixture.Start();
        await fixture.Controls.ConstructorEntered.Task.WaitAsync(Deadline);
        await UntilAsync(async () => (await fixture.RowAsync(following, Second)).Processed);
        fixture.Observation.Snapshot.OutstandingExecutions.Should().Be(1);
        (await fixture.RowAsync(held)).Processed.Should().BeFalse();
        fixture.Controls.ReleaseConstructor.TrySetResult();
        await UntilAsync(() => fixture.Observation.Snapshot.OutstandingExecutions == 0);
        (await fixture.BusinessAsync(fixture.Controls.For(held).EffectId)).Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Acknowledged_commit_survives_hung_or_faulted_scope_cleanup_without_retry(bool fault)
    {
        await using var fixture = await Fixture.CreateAsync(schema);
        var fact = Fact();
        var plan = fixture.Controls.For(fact);
        plan.HoldCleanup = !fault;
        plan.FaultCleanup = fault;
        await fixture.InsertAsync(fact);
        fixture.Start();
        await plan.CleanupEntered.Task.WaitAsync(Deadline);
        await UntilAsync(async () => (await fixture.RowAsync(fact)).Processed);
        if (!fault)
        {
            await UntilAsync(() => fixture.Observation.Snapshot.OutstandingExecutions == 1);
            fixture.Observation.IsHealthy.Should().BeFalse();
            plan.Disposed.Task.IsCompleted.Should().BeFalse();
        }
        var completed = await fixture.RowAsync(fact);
        completed.Error.Should().BeNull();
        completed.Attempts.Should().Be(1);
        plan.ReleaseCleanup.TrySetResult();
        await plan.Disposed.Task.WaitAsync(Deadline);
        await UntilAsync(() => fixture.Observation.IsHealthy);
        (await fixture.RowAsync(fact)).Should().Be(completed);
        (await fixture.BusinessAsync(plan.EffectId)).Should().Be(1);
        plan.Executions.Should().Be(1);
    }

    [Fact]
    public async Task Four_retained_scopes_saturate_capacity_and_release_unblocks_only_available_partitions()
    {
        await using var fixture = await Fixture.CreateAsync(schema);
        var held = Enumerable.Range(0, 4).Select(_ => Fact()).ToArray();
        foreach (var fact in held)
        {
            fixture.Controls.For(fact).HoldHandler = true;
            await fixture.InsertAsync(fact);
        }
        var samePartition = Fact() with { SubjectId = held[0].SubjectId };
        var otherPartition = Fact();
        await fixture.InsertAsync(samePartition);
        await fixture.InsertAsync(otherPartition);
        fixture.Start();
        await UntilAsync(() => fixture.Observation.Snapshot.OutstandingExecutions == 4);
        fixture.Observation.IsHealthy.Should().BeFalse();
        (await fixture.RowAsync(otherPartition)).Attempts.Should().Be(0);
        fixture.Controls.For(held[1]).ReleaseHandler.TrySetResult();
        await UntilAsync(async () => (await fixture.RowAsync(otherPartition)).Processed);
        (await fixture.RowAsync(samePartition)).Attempts.Should().Be(0);
        fixture.Controls.For(held[0]).ReleaseHandler.TrySetResult();
        await UntilAsync(async () => (await fixture.RowAsync(samePartition)).Processed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Blocked_cancellation_callback_retains_actual_scope_and_shutdown_observes_one_shared_budget(bool hostCanceled)
    {
        await using var fixture = await Fixture.CreateAsync(schema);
        var fact = Fact();
        var plan = fixture.Controls.For(fact);
        plan.HoldCancellation = true;
        plan.Fault = "owned-cancel";
        await fixture.InsertAsync(fact);
        var time = new ShutdownTimeProvider(Bounds.ShutdownTimeout);
        fixture.Start(time: time);
        await plan.CancellationEntered.Task.WaitAsync(Deadline);
        plan.CleanupEntered.Task.IsCompleted.Should().BeFalse("the actual application scope must outlive callbacks");
        using var shutdown = new CancellationTokenSource();
        if (hostCanceled) shutdown.Cancel();
        time.ControlShutdownTimers();
        var stopping = fixture.Worker.StopAsync(shutdown.Token);
        await time.BudgetRegistered.Task.WaitAsync(Deadline);
        time.Advance(Bounds.ShutdownTimeout);
        await stopping.WaitAsync(Deadline);
        time.RegisteredCount.Should().Be(1, "base service and callback drain share one total deadline");
        plan.Disposed.Task.IsCompleted.Should().BeFalse();
        fixture.Observation.Snapshot.OutstandingExecutions.Should().Be(1);
        plan.ReleaseCancellation.TrySetResult();
        await plan.Disposed.Task.WaitAsync(Deadline);
        plan.CallbackObservedLiveScope.Should().BeTrue();
        (await fixture.BusinessAsync(plan.EffectId)).Should().Be(0);
    }

    [Fact]
    public async Task Faulting_cancellation_callback_is_observed_and_actual_scope_still_disposes()
    {
        await using var fixture = await Fixture.CreateAsync(schema);
        var fact = Fact();
        var plan = fixture.Controls.For(fact);
        plan.FaultCancellation = true;
        plan.Fault = "owned-cancel";
        await fixture.InsertAsync(fact);
        fixture.Start();
        await plan.Disposed.Task.WaitAsync(Deadline);
        await UntilAsync(async () => (await fixture.RowAsync(fact)).Error is not null);
        await fixture.PostponeAsync(fact, First);
        (await fixture.BusinessAsync(plan.EffectId)).Should().Be(0);
        fixture.Logs.Text.Should().NotContain(Canary);
    }

    [Fact]
    public async Task Controlled_executor_reuse_refuses_without_disposing_the_original_live_control()
    {
        await using var fixture = await Fixture.CreateAsync(schema);
        var fact = Fact();
        var plan = fixture.Controls.For(fact);
        plan.HoldHandler = true;
        await fixture.InsertAsync(fact);
        fixture.Start(removeWorker: true);
        var claim = await fixture.Services.GetRequiredService<DeliveryDispatchStore>().TryClaimAsync([], CancellationToken.None);
        var executor = fixture.Services.GetRequiredService<DeliveryExecutor>();
        var control = new DeliveryExecutionControl();
        var running = Task.Run(() => executor.ExecuteControlledAsync(claim!, control));
        await plan.Entered.Task.WaitAsync(Deadline);
        await FluentActions.Awaiting(() => executor.ExecuteControlledAsync(claim!, control))
            .Should().ThrowAsync<InvalidOperationException>();
        running.IsCompleted.Should().BeFalse();
        control.RequestCancellation();
        control.Gate.IsClosed.Should().BeTrue();
        plan.ReleaseHandler.TrySetResult();
        var result = await running.WaitAsync(Deadline);
        result.CommitAcknowledged.Should().BeFalse();
        (await fixture.BusinessAsync(plan.EffectId)).Should().Be(0);
    }

    [Fact]
    public async Task Lost_sixth_COMMIT_acknowledgement_is_reconciled_from_durable_completion_without_seventh_execution()
    {
        await using var fixture = await Fixture.CreateAsync(schema);
        var fact = Fact();
        await fixture.InsertAsync(fact);
        await fixture.PrimeLastAttemptAsync(fact);
        await using var proxy = new CommitResponseDroppingProxy(fixture.AppConnectionString);
        fixture.Start(appConnection: proxy.ConnectionString);
        await UntilAsync(async () => (await fixture.RowAsync(fact)).Processed);
        await UntilAsync(() => fixture.Observation.IsHealthy);
        proxy.DroppedCommitResponses.Should().Be(1);
        var completed = await fixture.RowAsync(fact);
        completed.Attempts.Should().Be(6);
        completed.Terminal.Should().BeNull();
        (await fixture.CountAsync("inbox_messages", fact.EventId)).Should().Be(1);
        (await fixture.BusinessAsync(fixture.Controls.For(fact).EffectId)).Should().Be(1);
        fixture.Controls.For(fact).Executions.Should().Be(1);
        await fixture.ScheduleAsync(fact, First, 6, expired: true);
        var before = fixture.Observation.Snapshot.LastSnapshotTimestamp;
        await UntilAsync(() => fixture.Observation.Snapshot.LastSnapshotTimestamp > before);
        (await fixture.RowAsync(fact)).Should().Be(completed);
        fixture.Controls.For(fact).Executions.Should().Be(1);
    }

    [Fact]
    public async Task Successful_executions_do_not_force_scans_and_refresh_failure_keeps_the_previous_snapshot()
    {
        await using var fixture = await Fixture.CreateAsync(schema);
        var time = new SnapshotTimeProvider();
        fixture.Start(time: time, bounds: Bounds with { SnapshotInterval = TimeSpan.FromSeconds(5) });
        await UntilAsync(() => fixture.Observation.IsHealthy);
        var initial = fixture.Observation.Snapshot;
        var fact = Fact();
        await fixture.InsertAsync(fact);
        await UntilAsync(async () => (await fixture.RowAsync(fact)).Processed);
        fixture.Observation.Snapshot.LastSnapshotTimestamp.Should().Be(initial.LastSnapshotTimestamp);
        time.Advance(TimeSpan.FromSeconds(4));
        var second = Fact();
        await fixture.InsertAsync(second);
        await UntilAsync(async () => (await fixture.RowAsync(second)).Processed);
        fixture.Observation.Snapshot.LastSnapshotTimestamp.Should().Be(initial.LastSnapshotTimestamp);
        time.Advance(TimeSpan.FromSeconds(1));
        await UntilAsync(() => fixture.Observation.Snapshot.LastSnapshotTimestamp > initial.LastSnapshotTimestamp);
        var good = fixture.Observation.Snapshot;
        foreach (var held in Enumerable.Range(0, 4).Select(_ => Fact()))
        {
            fixture.Controls.For(held).HoldHandler = true;
            await fixture.InsertAsync(held);
        }
        await UntilAsync(() => fixture.Observation.Snapshot.OutstandingExecutions == 4);
        await fixture.SchemaAsync("REVOKE SELECT (occurred_at) ON event_deliveries FROM learnstack_outbox_admin");
        time.Advance(TimeSpan.FromSeconds(5));
        await UntilAsync(() => fixture.Observation.Snapshot.RefreshFailed);
        fixture.Observation.Snapshot.Queue.Should().BeSameAs(good.Queue);
        fixture.Observation.Snapshot.LastSnapshotTimestamp.Should().Be(good.LastSnapshotTimestamp);
        fixture.Observation.IsHealthy.Should().BeFalse();
    }

    private sealed class SnapshotTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _timestamp, duration.Ticks);
    }

    private sealed class ShutdownTimeProvider(TimeSpan shutdownTimeout) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ShutdownTimer> _timers = [];
        private bool _controlled;
        private long? _origin;
        private TimeSpan _elapsed;
        private TimeSpan _lastRegistration;
        public TaskCompletionSource BudgetRegistered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RegisteredCount { get { lock (_gate) return _timers.Count; } }

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

    private static async Task UntilAsync(Func<bool> condition) =>
        await UntilAsync(() => Task.FromResult(condition()));
    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        using var deadline = new CancellationTokenSource(Deadline);
        while (!await condition()) await Task.Delay(10, deadline.Token);
    }

    private static ProofEvent Fact() => new()
    {
        EventId = Guid.CreateVersion7(),
        TenantId = SchemaFixture.TenantA,
        OccurredAt = new DateTimeOffset(2026, 10, 13, 9, 0, 0, TimeSpan.Zero),
        SubjectId = Guid.CreateVersion7(),
    };
    [IntegrationEventPartition(nameof(SubjectId))]
    public sealed record ProofEvent : IntegrationEventBase
    {
        public required Guid SubjectId { get; init; }
        public override string Topic => "learnstack.proof.worker";
        public override string PartitionKey => SubjectId.ToString("D");
    }
    public sealed class Controls
    {
        private readonly ConcurrentDictionary<(Guid, string), Plan> _plans = new();
        internal int ConstructorFaults;
        public bool HoldConstructor { get; set; }
        public TaskCompletionSource ConstructorEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseConstructor { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Plan For(ProofEvent fact, string subscription = First) =>
            _plans.GetOrAdd((fact.EventId, subscription), _ => new Plan());
        public void ReleaseAll()
        {
            ReleaseConstructor.TrySetResult();
            foreach (var plan in _plans.Values)
            {
                plan.ReleaseHandler.TrySetResult();
                plan.ReleaseCancellation.TrySetResult();
                plan.ReleaseCleanup.TrySetResult();
            }
        }
        public Task DrainAsync() => Task.WhenAll(_plans.Values.Where(plan => plan.Entered.Task.IsCompleted)
            .Select(plan => plan.Disposed.Task));
    }
    public sealed class Plan
    {
        public Guid EffectId { get; } = Guid.CreateVersion7();
        public string? Fault { get; set; }
        public bool HoldHandler { get; set; }
        public bool HoldCleanup { get; set; }
        public bool FaultCleanup { get; set; }
        public bool HoldCancellation { get; set; }
        public bool FaultCancellation { get; set; }
        public bool CallbackObservedLiveScope { get; set; }
        internal int Executions;
        public TaskCompletionSource Entered { get; } = Signal();
        public TaskCompletionSource ReleaseHandler { get; } = Signal();
        public TaskCompletionSource CleanupEntered { get; } = Signal();
        public TaskCompletionSource ReleaseCleanup { get; } = Signal();
        public TaskCompletionSource CancellationEntered { get; } = Signal();
        public TaskCompletionSource ReleaseCancellation { get; } = Signal();
        public TaskCompletionSource Disposed { get; } = Signal();
        private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public sealed class FirstHandler(IServiceProvider services, Controls controls)
        : Handler(services, controls, First), IIntegrationEventHandler<ProofEvent>;
    public sealed class SecondHandler(IServiceProvider services, Controls controls)
        : Handler(services, controls, Second), IIntegrationEventHandler<ProofEvent>;
    public sealed class PrivilegedHandler(IServiceProvider services, Controls controls)
        : Handler(services, controls, Privileged), IIntegrationEventHandler<ProofEvent>;
    public abstract class Handler
    {
        private readonly IServiceProvider _services;
        private readonly Controls _controls;
        private readonly string _subscription;
        protected Handler(IServiceProvider services, Controls controls, string subscription)
        {
            _services = services;
            _controls = controls;
            _subscription = subscription;
            if (subscription == First && controls.HoldConstructor)
            {
                controls.ConstructorEntered.TrySetResult();
                controls.ReleaseConstructor.Task.GetAwaiter().GetResult();
            }
            if (subscription == First && Interlocked.CompareExchange(ref controls.ConstructorFaults, 0, 1) == 1)
                throw new InvalidOperationException(Canary);
        }
        public Task HandleAsync(ProofEvent fact, CancellationToken cancellationToken = default)
        {
            var plan = _controls.For(fact, _subscription);
            Interlocked.Increment(ref plan.Executions);
            var cleanup = _services.GetRequiredService<Cleanup>();
            cleanup.Plan = plan;
            return plan.Fault == "null-task" ? null! : WorkAsync(fact, plan, cleanup, cancellationToken);
        }
        private async Task WorkAsync(ProofEvent fact, Plan plan, Cleanup cleanup, CancellationToken cancellationToken)
        {
            await Task.Yield();
            var unit = _services.GetRequiredService<IUnitOfWork>();
            await SchemaQueries.AssertApplicationRoleAsync(unit.Connection);
            var db = _services.GetRequiredService<CustomizationDbContext>();
            db.Add(TenantContentType.Create(TenantContentTypeId.From(plan.EffectId), TenantId.From(fact.TenantId),
                $"worker-{plan.EffectId:N}"[..24], 1, LocalizedText.From(("en", "Worker proof")),
                """{"type":"object"}""", "default-card", _services.GetRequiredService<IClock>(), UserId.SystemActor));
            await db.SaveChangesAsync(cancellationToken);
            if (plan.HoldCancellation || plan.FaultCancellation)
                cleanup.Registration = cancellationToken.Register(() =>
                {
                    plan.CancellationEntered.TrySetResult();
                    if (plan.HoldCancellation) plan.ReleaseCancellation.Task.GetAwaiter().GetResult();
                    plan.CallbackObservedLiveScope = !plan.Disposed.Task.IsCompleted && !plan.CleanupEntered.Task.IsCompleted;
                    if (plan.FaultCancellation) throw new InvalidOperationException(Canary);
                });
            plan.Entered.TrySetResult();
            if (plan.HoldHandler) await plan.ReleaseHandler.Task;
            if (plan.Fault == "owned-cancel") await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (plan.Fault == "handler") throw new InvalidOperationException(Canary);
            if (plan.Fault == "foreign-cancel") throw new OperationCanceledException(Canary, new CancellationToken(true));
        }
    }
    public sealed class Cleanup : IAsyncDisposable
    {
        public Plan? Plan { get; set; }
        public CancellationTokenRegistration Registration { get; set; }
        public async ValueTask DisposeAsync()
        {
            if (Plan is not { } plan) return;
            plan.CleanupEntered.TrySetResult();
            if (plan.HoldCleanup) await plan.ReleaseCleanup.Task;
            Registration.Dispose();
            plan.Disposed.TrySetResult();
            if (plan.FaultCleanup) throw new InvalidOperationException(Canary);
        }
    }
    private sealed class Catalog : IAuditCatalogSource
    {
        public string ModuleName => "customization";
        public void Describe(IAuditCatalogBuilder builder)
        {
            builder.MustAudit<FirstHandler>("customization.content_type.worker_first", OperationType.Create, typeof(TenantContentType));
            builder.MustAudit<SecondHandler>("customization.content_type.worker_second", OperationType.Create, typeof(TenantContentType));
            builder.MustAudit<PrivilegedHandler>("customization.content_type.worker_privileged", OperationType.Create, typeof(TenantContentType));
        }
    }
    private sealed record Row(int Attempts, bool Processed, string? Error, string? Terminal, bool Deadlettered);
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly DisposableSchemaDatabase _database;
        private WorkerFactory? _host;
        private Fixture(DisposableSchemaDatabase database) => _database = database;
        public string AppConnectionString => _database.AppConnectionString;
        public Controls Controls { get; } = new();
        public Logs Logs { get; } = new();
        public WorkerFactory Host => _host!;
        public IServiceProvider Services => Host.Services;
        public DeliveryObservation Observation => Services.GetRequiredService<DeliveryObservation>();
        public DeliveryProcessor Worker => Services.GetServices<IHostedService>().OfType<DeliveryProcessor>().Single();
        public static async Task<Fixture> CreateAsync(SchemaFixture schema)
        {
            var fixture = new Fixture(await DisposableSchemaDatabase.CreateAsync(schema.Postgres));
            await using var connection = await fixture.AppAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
            await SchemaQueries.ExecuteAsync(connection, transaction, """
                INSERT INTO tenants (id, slug, display_name, status, created_at, created_by, row_version)
                VALUES (@tenant, 'delivery-worker', 'Delivery worker', 'Trial', now(), @actor, 0)
                """, ("tenant", SchemaFixture.TenantA), ("actor", UserId.SystemActor.Value));
            await transaction.CommitAsync();
            return fixture;
        }
        public void Start(bool removeWorker = false, TimeProvider? time = null, DeliveryDispatchOptions? bounds = null, string? appConnection = null)
        {
            _host = new WorkerFactory(this, removeWorker, time, bounds, appConnection);
            _ = Services;
        }
        public async Task InsertAsync(ProofEvent fact, string subscription = First)
        {
            await using var connection = await AppAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await SchemaQueries.SetTenantAsync(connection, transaction, fact.TenantId);
            await using var command = new NpgsqlCommand("""
                INSERT INTO event_deliveries
                    (tenant_id, subscription_id, event_id, occurred_at, correlation_id, type, topic, partition_key, payload)
                VALUES (@tenant, @subscription, @event, @occurred, @trace, @type, @topic, @partition, @payload)
                """, connection, transaction);
            command.Parameters.AddWithValue("tenant", fact.TenantId);
            command.Parameters.AddWithValue("subscription", subscription);
            command.Parameters.AddWithValue("event", fact.EventId);
            command.Parameters.AddWithValue("occurred", fact.OccurredAt);
            command.Parameters.AddWithValue("trace", Trace);
            command.Parameters.AddWithValue("type", typeof(ProofEvent).AssemblyQualifiedName!);
            command.Parameters.AddWithValue("topic", fact.Topic);
            command.Parameters.AddWithValue("partition", fact.PartitionKey);
            command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, fact.ToPayloadJson());
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
        public async Task<Row> RowAsync(ProofEvent fact, string subscription = First)
        {
            await using var connection = await DispatchAsync();
            await using var command = new NpgsqlCommand("""
                SELECT attempts, processed_at IS NOT NULL, last_error, terminal_reason, dead_lettered_at IS NOT NULL
                FROM event_deliveries WHERE event_id = @event AND subscription_id = @subscription
                """, connection);
            command.Parameters.AddWithValue("event", fact.EventId);
            command.Parameters.AddWithValue("subscription", subscription);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            return new(reader.GetInt32(0), reader.GetBoolean(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetBoolean(4));
        }
        public Task PrimeLastAttemptAsync(ProofEvent fact) => UpdateAsync(fact, First,
            "UPDATE event_deliveries SET attempts = 5 WHERE event_id = @event AND subscription_id = @subscription");
        public Task MakeDueAsync(ProofEvent fact, string subscription) => UpdateAsync(fact, subscription,
            "UPDATE event_deliveries SET available_after = clock_timestamp() WHERE event_id = @event AND subscription_id = @subscription");
        public Task PostponeAsync(ProofEvent fact, string subscription) => UpdateAsync(fact, subscription,
            "UPDATE event_deliveries SET available_after = clock_timestamp() + interval '1 hour' WHERE event_id = @event AND subscription_id = @subscription");
        public Task ScheduleAsync(ProofEvent fact, string subscription, int attempts, bool expired) => UpdateAsync(fact, subscription,
            $"UPDATE event_deliveries SET attempts = {attempts}, locked_by = '{Guid.CreateVersion7():D}', locked_until = clock_timestamp() {(expired ? "-" : "+")} interval '1 minute' WHERE event_id = @event AND subscription_id = @subscription");
        private async Task UpdateAsync(ProofEvent fact, string subscription, string sql)
        {
            await using var connection = await DispatchAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("event", fact.EventId);
            command.Parameters.AddWithValue("subscription", subscription);
            await command.ExecuteNonQueryAsync();
        }
        public async Task<long> CountAsync(string table, Guid eventId) => await ScalarAsync(
            $"SELECT count(*) FROM {table} WHERE event_id = @id", eventId);
        public Task<long> BusinessAsync(Guid id) => ScalarAsync("SELECT count(*) FROM tenant_content_types WHERE id = @id", id);
        private async Task<long> ScalarAsync(string sql, Guid id)
        {
            await using var connection = await AppAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("id", id);
            return (long)(await command.ExecuteScalarAsync())!;
        }
        public async Task SchemaAsync(string sql)
        {
            await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.MigrationConnectionString);
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
        private async Task<NpgsqlConnection> AppAsync()
        {
            var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);
            await SchemaQueries.AssertApplicationRoleAsync(connection);
            return connection;
        }
        private async Task<NpgsqlConnection> DispatchAsync() =>
            (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.OutboxConnectionString);
        public async ValueTask DisposeAsync()
        {
            Controls.ReleaseAll();
            if (_host is not null)
            {
                var worker = Services.GetServices<IHostedService>().OfType<DeliveryProcessor>().SingleOrDefault();
                if (worker is not null) await worker.StopAsync(CancellationToken.None).WaitAsync(Deadline);
                await Controls.DrainAsync().WaitAsync(Deadline);
                await _host.DisposeAsync();
            }
            await _database.DisposeAsync();
        }
        public sealed class WorkerFactory(Fixture fixture, bool removeWorker, TimeProvider? time,
            DeliveryDispatchOptions? bounds, string? appConnection) : WebApplicationFactory<Program>
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseEnvironment(Environments.Development);
                builder.UseSetting("ConnectionStrings:Default", appConnection ?? fixture._database.AppConnectionString);
                builder.UseSetting("ConnectionStrings:PlatformAdmin", fixture._database.PlatformConnectionString);
                builder.UseSetting("ConnectionStrings:OutboxDispatcher", fixture._database.OutboxConnectionString);
                builder.ConfigureTestServices(services =>
                {
                    foreach (var descriptor in services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                                 && (descriptor.ImplementationType == typeof(OutboxProcessor)
                                     || removeWorker && descriptor.ImplementationType == typeof(DeliveryProcessor))).ToArray())
                        services.Remove(descriptor);
                    services.RemoveAll<DeliveryDispatchOptions>();
                    services.AddSingleton(bounds ?? Bounds);
                    if (time is not null) { services.RemoveAll<TimeProvider>(); services.AddSingleton(time); }
                    var handlers = IntegrationEventHandlerRegistry.FromServiceDescriptors([
                        ServiceDescriptor.Scoped<IIntegrationEventHandler<ProofEvent>, FirstHandler>(),
                        ServiceDescriptor.Scoped<IIntegrationEventHandler<ProofEvent>, SecondHandler>(),
                        ServiceDescriptor.Scoped<IIntegrationEventHandler<ProofEvent>, PrivilegedHandler>(),
                    ]);
                    services.RemoveAll<IntegrationEventHandlerRegistry>();
                    services.AddSingleton(handlers);
                    services.RemoveAll<OutboxEventRegistry>();
                    services.AddSingleton(OutboxEventRegistry.Create([
                        OutboxEventRegistration.Required(typeof(ProofEvent), [
                            OutboxSubscriptionManifest.Active(First, typeof(FirstHandler)),
                            OutboxSubscriptionManifest.Active(Second, typeof(SecondHandler)),
                            OutboxSubscriptionManifest.Pending(Pending),
                            OutboxSubscriptionManifest.Active(Privileged, typeof(PrivilegedHandler), OutboxSubscriptionExecutionScope.PrivilegedPlatform),
                        ]),
                    ], [], handlers, [typeof(ProofEvent).Assembly]));
                    services.AddSingleton(fixture.Controls);
                    services.AddScoped<FirstHandler>();
                    services.AddScoped<SecondHandler>();
                    services.AddScoped<PrivilegedHandler>();
                    services.AddScoped<Cleanup>();
                    services.AddSingleton<IAuditCatalogSource, Catalog>();
                    services.AddSingleton<ILogger<DeliveryProcessor>>(fixture.Logs);
                });
            }
        }
    }
    public sealed class Logs : ILogger<DeliveryProcessor>
    {
        private readonly ConcurrentQueue<string> _messages = new();
        public string Text => string.Join('\n', _messages);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => _messages.Enqueue(formatter(state, exception) + exception);
    }
}
