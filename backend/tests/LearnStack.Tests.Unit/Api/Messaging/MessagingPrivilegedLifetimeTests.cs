using System.Reflection;
using FluentAssertions;
using LearnStack.Api.Messaging;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Messaging;

/// <summary>Synthetic lifecycle controls; these do not qualify real PostgreSQL cleanup.</summary>
public sealed class MessagingPrivilegedLifetimeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Stop_or_dispose_before_start_seals_actual_processor_without_constructing_source(
        bool delivery, bool dispose)
    {
        var constructions = 0;
        using var fixture = new ProcessorFixture(() =>
        {
            Interlocked.Increment(ref constructions);
            throw new InvalidOperationException("Unexpected synthetic source acquisition.");
        });
        var processor = fixture.Processor(delivery);
        var completion = fixture.Completion(delivery);
        completion.IsCompleted.Should().BeFalse();

        if (dispose) processor.Dispose();
        else await processor.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        completion.IsCompletedSuccessfully.Should().BeTrue();
        fixture.Completion(delivery).Should().BeSameAs(completion);
        completion.AsyncState.Should().BeOfType<MessagingPrivilegedLifetime>();
        await FluentActions.Awaiting(() => processor.StartAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
        processor.ExecuteTask.Should().BeNull();
        constructions.Should().Be(0);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Actual_processor_retains_held_startup_after_stop_or_dispose_returns(
        bool delivery, bool dispose)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var fixture = new ProcessorFixture(() =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The synthetic startup factory was not released.");
            throw new InvalidOperationException("Synthetic source construction refusal.");
        });
        var processor = fixture.Processor(delivery);
        var completion = fixture.Completion(delivery);
        var start = Task.Run(() => processor.StartAsync(CancellationToken.None));
        Exception? startupError;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (dispose) processor.Dispose();
            else await processor.StopAsync(new CancellationToken(canceled: true))
                .WaitAsync(TimeSpan.FromSeconds(5));

            start.IsCompleted.Should().BeFalse();
            completion.IsCompleted.Should().BeFalse("startup still owns source acquisition after caller waiting ends");
            await FluentActions.Awaiting(() => processor.StartAsync(CancellationToken.None))
                .Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            release.Set();
            startupError = await Record.ExceptionAsync(() => start.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        startupError.Should().BeOfType<InvalidOperationException>();
        startupError!.InnerException.Should().BeNull();
        await completion.WaitAsync(TimeSpan.FromSeconds(5));
        processor.ExecuteTask.Should().BeNull("the failed admitted startup never issued the loop");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Synthetic_reserved_but_unpublished_execution_never_becomes_a_no_issued_proof(bool delivery)
    {
        using var fixture = new ProcessorFixture();
        var lifetime = fixture.Lifetime(delivery);
        lifetime.Completion.AsyncState.Should().BeSameAs(lifetime);
        lifetime.BeginStart();
        lifetime.TryReserveExecution().Should().BeTrue();
        lifetime.SealStopping();
        lifetime.FinishStart();

        lifetime.Completion.IsCompleted.Should().BeFalse("a failed task publication is an ambiguous issuance");
        lifetime.TryReserveExecution().Should().BeFalse();
        FluentActions.Invoking(lifetime.BeginStart).Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Synthetic_actual_background_task_outlives_caller_expiry_and_completes_only_after_return(bool delivery)
    {
        using var fixture = new ProcessorFixture();
        var lifetime = fixture.Lifetime(delivery);
        using var service = new HeldBackgroundService();
        lifetime.BeginStart();
        lifetime.TryReserveExecution().Should().BeTrue();
        await service.StartAsync(CancellationToken.None);
        lifetime.ObserveExecution(service.ExecuteTask!);
        lifetime.FinishStart();
        try
        {
            await service.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            lifetime.SealStopping();
            await service.StopAsync(new CancellationToken(canceled: true)).WaitAsync(TimeSpan.FromSeconds(5));

            service.ExecuteTask!.IsCompleted.Should().BeFalse();
            lifetime.Completion.IsCompleted.Should().BeFalse("base StopAsync returned only because its caller expired");
        }
        finally
        {
            service.Release.TrySetResult();
            await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await lifetime.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Synthetic_unconfirmed_store_cleanup_stays_owned_after_completed_execution(bool delivery)
    {
        using var fixture = new ProcessorFixture();
        var lifetime = fixture.Lifetime(delivery);
        lifetime.BeginStart();
        lifetime.TryReserveExecution().Should().BeTrue();
        // Plant only the store's memory-only monotonic outcome. Real connection
        // disposal qualification belongs to the PostgreSQL integration controls.
        var store = delivery ? (object)fixture.DeliveryStore : fixture.OutboxStore;
        store.GetType().GetField("_cleanupUnconfirmed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(store, true);
        lifetime.ObserveExecution(Task.CompletedTask);
        lifetime.FinishStart();
        lifetime.SealStopping();

        lifetime.Completion.IsCompleted.Should().BeFalse();
        lifetime.Completion.AsyncState.Should().BeSameAs(lifetime,
            "the retained positive task must root the concrete store even after its observer returns");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Synthetic_completed_execution_cannot_outrun_startup_settlement(bool delivery)
    {
        using var fixture = new ProcessorFixture();
        var lifetime = fixture.Lifetime(delivery);
        lifetime.BeginStart();
        lifetime.TryReserveExecution().Should().BeTrue();
        lifetime.ObserveExecution(Task.CompletedTask);
        lifetime.SealStopping();
        lifetime.Completion.IsCompleted.Should().BeFalse();

        var connection = new NpgsqlConnection();
        await lifetime.DisposeStartupConnectionAsync(connection);
        lifetime.Completion.IsCompleted.Should().BeFalse();
        lifetime.FinishStart();
        lifetime.Completion.IsCompletedSuccessfully.Should().BeTrue();
        FluentActions.Invoking(() => connection.ConnectionString = "Host=unused")
            .Should().Throw<ObjectDisposedException>();
    }

    private sealed class HeldBackgroundService : BackgroundService
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Entered.TrySetResult();
            // A synthetic uncancellable cleanup body proves actual task return.
            await Release.Task.ConfigureAwait(false);
        }
    }

    private sealed record LifetimeProofEvent : IntegrationEventBase
    {
        public override string Topic => "learnstack.test.lifetime";
        public override string PartitionKey => TenantId.ToString("D");
    }

    private sealed class ProcessorFixture : IDisposable
    {
        private readonly ServiceProvider _provider = new ServiceCollection().BuildServiceProvider();
        private readonly PrivilegedDataSource _source;
        private readonly OutboxObservation _outboxObservation;
        private readonly DeliveryObservation _deliveryObservation;
        private readonly OutboxProcessor _outbox;
        private readonly DeliveryProcessor _delivery;

        internal OutboxDispatchStore OutboxStore { get; }
        internal DeliveryDispatchStore DeliveryStore { get; }

        internal ProcessorFixture(Func<NpgsqlDataSource>? sourceFactory = null)
        {
            _source = new PrivilegedDataSource(sourceFactory
                ?? (() => throw new InvalidOperationException("Unexpected synthetic source construction.")));
            var scopes = _provider.GetRequiredService<IServiceScopeFactory>();
            var handlers = IntegrationEventHandlerRegistry.FromServiceDescriptors(new ServiceCollection());
            var registry = OutboxEventRegistry.Create(
                [OutboxEventRegistration.IntentionallyUnsubscribed(typeof(LifetimeProofEvent))], [],
                handlers, [typeof(LifetimeProofEvent).Assembly]);
            var outboxOptions = new OutboxDispatchOptions();
            var deliveryOptions = new DeliveryDispatchOptions();
            var ids = new SystemGuidFactory();
            var clock = new SystemClock();
            OutboxStore = new(_source, registry, outboxOptions, ids, clock, scopes);
            DeliveryStore = new(_source, registry, deliveryOptions, ids, clock, scopes);
            _outboxObservation = new(TimeProvider.System, outboxOptions, registry);
            _deliveryObservation = new(TimeProvider.System, deliveryOptions, registry);
            _outbox = new(_source, OutboxStore, new OutboxEnvelopeReader(registry), registry,
                outboxOptions, TimeProvider.System, scopes, _outboxObservation, NullLogger<OutboxProcessor>.Instance);
            var executor = new DeliveryExecutor(scopes, new StaticTenantContextAccessor(null),
                new PartitionSerializer(), registry, handlers);
            _delivery = new(_source, DeliveryStore, executor, registry, deliveryOptions,
                TimeProvider.System, _deliveryObservation, NullLogger<DeliveryProcessor>.Instance);
        }

        internal BackgroundService Processor(bool delivery) => delivery ? _delivery : _outbox;
        internal Task Completion(bool delivery) => delivery ? _delivery.PrivilegedCompletion : _outbox.PrivilegedCompletion;
        internal MessagingPrivilegedLifetime Lifetime(bool delivery) =>
            delivery ? new(DeliveryStore) : new(OutboxStore);

        public void Dispose()
        {
            _outbox.Dispose();
            _delivery.Dispose();
            _outboxObservation.Dispose();
            _deliveryObservation.Dispose();
            _source.Dispose();
            _provider.Dispose();
        }
    }
}
