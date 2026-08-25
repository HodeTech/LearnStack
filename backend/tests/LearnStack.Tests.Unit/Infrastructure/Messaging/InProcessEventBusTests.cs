using System.Collections.Concurrent;
using FluentAssertions;
using LearnStack.Infrastructure.Messaging;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Messaging;

/// <summary>
/// The default <see cref="IEventBus"/>, per
/// <see href="../../../../../docs/decisions/0035-demand-gated-infrastructure.md">ADR-0035</see>.
/// </summary>
/// <remarks>
/// Every case here asserts one of the four obligations ADR-0035 makes a condition
/// of the gating — same handler contract, same deduplication seam, same
/// tenant-context restoration, same per-partition ordering. A transport that
/// dropped any of them would be a development path where the production
/// behaviour is never exercised, which is the opposite of what a default
/// implementation is for.
/// </remarks>
public sealed class InProcessEventBusTests
{
    private static readonly Guid Tenant = Guid.Parse("018f4d40-0000-7000-8000-00000000000a");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_Handler_Receives_The_Event()
    {
        var recorder = new Recorder();
        var (bus, _) = Build(recorder, services =>
            services.AddScoped<IIntegrationEventHandler<Thing>, ThingHandler>());

        await bus.PublishAsync(NewThing("a"), "p1");

        recorder.Handled.Should().ContainSingle().Which.Should().Be("a");
    }

    [Fact]
    public async Task An_Internal_Handler_Is_Invoked_Too()
    {
        // The reason handlers are called through the interface's MethodInfo
        // rather than by `dynamic`: the dynamic binder honours accessibility, so
        // an internal handler — the normal shape for a module's own consumer —
        // would fail to bind at runtime, and the failure would be a
        // RuntimeBinderException out of the transport rather than anything a
        // consumer could diagnose.
        var recorder = new Recorder();
        var (bus, _) = Build(recorder, services =>
            services.AddScoped<IIntegrationEventHandler<Thing>, InternalThingHandler>());

        await bus.PublishAsync(NewThing("a"), "p1");

        recorder.Handled.Should().ContainSingle().Which.Should().Be("internal:a");
    }

    [Fact]
    public async Task Publishing_Through_The_Base_Interface_Still_Reaches_The_Handler()
    {
        // The reason PublishAsync is not generic. The outbox processor
        // deserialises to object and publishes through the base interface, so a
        // generic parameter would bind to IIntegrationEvent — and resolving
        // IIntegrationEventHandler<IIntegrationEvent> finds nothing, because no
        // concrete consumer implements it. The publish would reach zero handlers
        // and report success, which is the worst shape a bug can take.
        var recorder = new Recorder();
        var (bus, _) = Build(recorder, services =>
            services.AddScoped<IIntegrationEventHandler<Thing>, ThingHandler>());

        IIntegrationEvent asBase = NewThing("a");
        await bus.PublishAsync(asBase, "p1");

        recorder.Handled.Should().ContainSingle();
    }

    [Fact]
    public async Task Every_Handler_For_The_Event_Runs()
    {
        var recorder = new Recorder();
        var (bus, _) = Build(recorder, services =>
        {
            services.AddScoped<IIntegrationEventHandler<Thing>, ThingHandler>();
            services.AddScoped<IIntegrationEventHandler<Thing>, SecondThingHandler>();
        });

        await bus.PublishAsync(NewThing("a"), "p1");

        recorder.Handled.Should().BeEquivalentTo(["a", "second:a"]);
    }

    [Fact]
    public async Task An_Event_With_No_Handler_Is_Not_An_Error()
    {
        var (bus, _) = Build(new Recorder(), _ => { });

        var act = () => bus.PublishAsync(NewThing("a"), "p1");

        await act.Should().NotThrowAsync();
    }

    // ---- obligation: tenant context is restored, and put back ----------------

    [Fact]
    public async Task The_Handler_Runs_Under_The_Events_Tenant()
    {
        // A consumer runs outside the request that produced the fact, so there is
        // no ambient context to inherit. Without this the handler executes with
        // no tenant and every query filter and RLS policy is evaluated against
        // nothing.
        var recorder = new Recorder();
        var (bus, _) = Build(recorder, services =>
            services.AddScoped<IIntegrationEventHandler<Thing>, TenantReadingHandler>());

        await bus.PublishAsync(NewThing("a"), "p1");

        recorder.Tenants.Should().ContainSingle().Which.Should().Be(Tenant);
    }

    [Fact]
    public async Task The_Publishers_Own_Context_Is_Put_Back()
    {
        // Dispatch is synchronous here, so a transport that set the context and
        // walked away would leak the event's tenant into the caller that
        // published it — and that caller goes on to run its own queries.
        var recorder = new Recorder();
        var (bus, accessor) = Build(recorder, services =>
            services.AddScoped<IIntegrationEventHandler<Thing>, TenantReadingHandler>());

        var publisher = EventTenantContext.FromEvent(NewThing("x") with { TenantId = Guid.NewGuid() });
        accessor.Current = publisher;

        await bus.PublishAsync(NewThing("a"), "p1");

        accessor.Current.Should().BeSameAs(publisher);
    }

    [Fact]
    public async Task A_Handler_That_Throws_Leaves_No_Context_Behind()
    {
        var recorder = new Recorder();
        var (bus, accessor) = Build(recorder, services =>
            services.AddScoped<IIntegrationEventHandler<Thing>, ThrowingHandler>());
        accessor.Current = null;

        var act = () => bus.PublishAsync(NewThing("a"), "p1");

        await act.Should().ThrowAsync<InvalidOperationException>();
        accessor.Current.Should().BeNull();
    }

    // ---- obligation: ordering per partition key ------------------------------

    [Fact]
    public async Task Two_Events_On_One_Partition_Key_Do_Not_Overlap()
    {
        // Ordering is guaranteed per partition key and nowhere else. An
        // in-process transport that dispatched concurrently would let an
        // ordering assumption pass every test and fail in production.
        var recorder = new Recorder();
        var (bus, _) = Build(recorder, services =>
            services.AddScoped<IIntegrationEventHandler<Thing>, OverlapDetectingHandler>());

        var first = bus.PublishAsync(NewThing("a"), "same-key");
        var second = bus.PublishAsync(NewThing("b"), "same-key");
        await Task.WhenAll(first, second).WaitAsync(Timeout);

        recorder.Overlapped.Should().BeFalse("dispatch is sequential within one key");
        recorder.Handled.Should().BeEquivalentTo(["a", "b"], o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task Different_Partition_Keys_Run_Concurrently()
    {
        // The other half of the guarantee: serialising everything would be
        // correct and useless, so the test that proves ordering must be paired
        // with one that proves it is not global.
        var recorder = new Recorder();
        using var bothArrived = new SemaphoreSlim(0);
        var (bus, _) = Build(recorder, services =>
            services.AddScoped<IIntegrationEventHandler<Thing>>(
                _ => new RendezvousHandler(bothArrived)));

        var first = bus.PublishAsync(NewThing("a"), "key-1");
        var second = bus.PublishAsync(NewThing("b"), "key-2");

        // Each handler releases once and waits for the other. If the two keys
        // were serialised, the first would wait forever and this would time out.
        await Task.WhenAll(first, second).WaitAsync(Timeout);
    }

    [Fact]
    public async Task A_Failed_Delivery_Does_Not_Block_The_Rest_Of_Its_Partition()
    {
        // A handler that throws must not stop every later event for that
        // aggregate. The failure belongs to the one publish that caused it.
        var recorder = new Recorder();
        var (bus, _) = Build(recorder, services =>
        {
            services.AddScoped<IIntegrationEventHandler<Thing>, ThrowOnFirstHandler>();
        });

        var failing = bus.PublishAsync(NewThing("boom"), "same-key");
        await ((Func<Task>)(() => failing)).Should().ThrowAsync<InvalidOperationException>();

        await bus.PublishAsync(NewThing("after"), "same-key").WaitAsync(Timeout);

        recorder.Handled.Should().Contain("after");
    }

    // ---- helpers -------------------------------------------------------------

    private static Thing NewThing(string payload) => new()
    {
        EventId = Guid.NewGuid(),
        TenantId = Tenant,
        OccurredAt = DateTimeOffset.UnixEpoch,
        Payload = payload,
    };

    private static (IEventBus Bus, ITenantContextAccessor Accessor) Build(
        Recorder recorder, Action<IServiceCollection> register)
    {
        var accessor = new TestTenantContextAccessor();

        var services = new ServiceCollection();
        services.AddSingleton(recorder);

        // The SAME accessor the bus writes through, so a handler resolved from
        // the dispatch scope reads what the transport restored rather than a
        // second, empty one.
        services.AddSingleton<ITenantContextAccessor>(accessor);
        register(services);

        var provider = services.BuildServiceProvider();

        return (
            new InProcessEventBus(
                provider.GetRequiredService<IServiceScopeFactory>(),
                accessor,
                new PartitionSerializer()),
            accessor);
    }

    /// <summary>
    /// A plain field-backed accessor, deliberately NOT <see cref="AsyncLocal{T}"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="ITenantContextAccessor"/> promises nothing about flow
    /// isolation, and the production implementation being AsyncLocal-backed is a
    /// detail of another assembly. With an AsyncLocal accessor a leaked context
    /// is invisible here — dispatch runs in its own flow, so the write never
    /// reaches the publisher and the restore looks unnecessary even when it is
    /// removed. This accessor makes the guarantee observable, which is the only
    /// way the test constrains the transport rather than the accessor.
    /// </remarks>
    private sealed class TestTenantContextAccessor : ITenantContextAccessor
    {
        public ITenantContext? Current { get; set; }
    }

    public sealed class Recorder
    {
        private int _inFlight;

        public ConcurrentQueue<string> Handled { get; } = new();

        public ConcurrentQueue<Guid> Tenants { get; } = new();

        /// <summary>Whether two handlers were ever inside the dispatch at once.</summary>
        public bool Overlapped { get; private set; }

        public void Enter()
        {
            if (Interlocked.Increment(ref _inFlight) > 1)
            {
                Overlapped = true;
            }
        }

        public void Exit() => Interlocked.Decrement(ref _inFlight);
    }

    public sealed record Thing : IntegrationEventBase
    {
        public required string Payload { get; init; }

        public override string PartitionKey => Payload;
    }

    public sealed class ThingHandler(Recorder recorder) : IIntegrationEventHandler<Thing>
    {
        public Task HandleAsync(Thing @event, CancellationToken cancellationToken = default)
        {
            recorder.Handled.Enqueue(@event.Payload);
            return Task.CompletedTask;
        }
    }

    public sealed class SecondThingHandler(Recorder recorder) : IIntegrationEventHandler<Thing>
    {
        public Task HandleAsync(Thing @event, CancellationToken cancellationToken = default)
        {
            recorder.Handled.Enqueue($"second:{@event.Payload}");
            return Task.CompletedTask;
        }
    }

    internal sealed class InternalThingHandler(Recorder recorder) : IIntegrationEventHandler<Thing>
    {
        public Task HandleAsync(Thing @event, CancellationToken cancellationToken = default)
        {
            recorder.Handled.Enqueue($"internal:{@event.Payload}");
            return Task.CompletedTask;
        }
    }

    public sealed class TenantReadingHandler(Recorder recorder, ITenantContextAccessor accessor)
        : IIntegrationEventHandler<Thing>
    {
        public Task HandleAsync(Thing @event, CancellationToken cancellationToken = default)
        {
            recorder.Tenants.Enqueue(accessor.Current!.TenantId);
            return Task.CompletedTask;
        }
    }

    public sealed class ThrowingHandler : IIntegrationEventHandler<Thing>
    {
        public Task HandleAsync(Thing @event, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("handler failed");
    }

    public sealed class ThrowOnFirstHandler(Recorder recorder) : IIntegrationEventHandler<Thing>
    {
        public Task HandleAsync(Thing @event, CancellationToken cancellationToken = default)
        {
            if (@event.Payload == "boom")
            {
                throw new InvalidOperationException("handler failed");
            }

            recorder.Handled.Enqueue(@event.Payload);
            return Task.CompletedTask;
        }
    }

    public sealed class OverlapDetectingHandler(Recorder recorder) : IIntegrationEventHandler<Thing>
    {
        public async Task HandleAsync(Thing @event, CancellationToken cancellationToken = default)
        {
            recorder.Enter();
            await Task.Delay(TimeSpan.FromMilliseconds(30), CancellationToken.None);
            recorder.Handled.Enqueue(@event.Payload);
            recorder.Exit();
        }
    }

    public sealed class RendezvousHandler(SemaphoreSlim gate) : IIntegrationEventHandler<Thing>
    {
        public async Task HandleAsync(Thing @event, CancellationToken cancellationToken = default)
        {
            gate.Release();
            await gate.WaitAsync(Timeout, CancellationToken.None);
        }
    }
}
