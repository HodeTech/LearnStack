using System.Collections.Concurrent;
using FluentAssertions;
using LearnStack.Infrastructure.Messaging;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
    private const string Trace = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

    [Fact]
    public async Task A_Handler_Receives_The_Event()
    {
        var recorder = new Recorder();
        var (bus, _) = Build(recorder, services =>
            services.AddScoped<IIntegrationEventHandler<Thing>, ThingHandler>());

        await bus.PublishAsync(Envelope(NewThing("a")));

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

        await bus.PublishAsync(Envelope(NewThing("a")));

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
        await bus.PublishAsync(new IntegrationEventEnvelope(asBase, Trace));

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

        await bus.PublishAsync(Envelope(NewThing("a")));

        recorder.Handled.Should().BeEquivalentTo(["a", "second:a"]);
    }

    [Fact]
    public async Task An_Event_With_No_Handler_Is_Not_An_Error()
    {
        var (bus, _) = Build(new Recorder(), _ => { });

        var act = () => bus.PublishAsync(Envelope(NewThing("a")));

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

        await bus.PublishAsync(Envelope(NewThing("a")));

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

        var publisher = EventTenantContext.FromEnvelope(
            Envelope(NewThing("x") with { TenantId = Guid.NewGuid() }));
        accessor.Current = publisher;

        await bus.PublishAsync(Envelope(NewThing("a")));

        accessor.Current.Should().BeSameAs(publisher);
    }

    [Fact]
    public async Task A_Handler_That_Throws_Leaves_No_Context_Behind()
    {
        var recorder = new Recorder();
        var (bus, accessor) = Build(recorder, services =>
            services.AddScoped<IIntegrationEventHandler<Thing>, ThrowingHandler>());
        accessor.Current = null;

        var act = () => bus.PublishAsync(Envelope(NewThing("a")));

        await act.Should().ThrowAsync<InvalidOperationException>();
        accessor.Current.Should().BeNull();
    }

    [Fact]
    public async Task A_Handler_Reads_The_Scoped_Tenant_Context_Too()
    {
        // Setting only the ambient accessor left the SCOPED ITenantContext — the
        // one ITenantContext's own doc says is handed to MediatR handlers, EF
        // interceptors and the audit pipeline — unresolved inside the dispatch
        // scope. A handler injecting it threw, and one sending a MediatR command
        // was short-circuited by TenantContextBehavior before its business logic
        // ran. The obligation the transport advertises was half-delivered.
        var recorder = new Recorder();
        var (bus, _) = Build(recorder, services =>
            services.AddScoped<IIntegrationEventHandler<Thing>, ScopedContextReadingHandler>());

        await bus.PublishAsync(Envelope(NewThing("a")));

        recorder.Tenants.Should().ContainSingle().Which.Should().Be(Tenant);
    }

    [Fact]
    public async Task The_Consumer_Acts_As_The_System_When_The_Envelope_Names_No_Actor()
    {
        // AuditableEntity.MarkCreated refuses default(UserId) and Guid.Empty, so
        // a null actor left every state-writing consumer with no value it could
        // legally pass — it could not create an aggregate at all.
        var recorder = new Recorder();
        var (bus, _) = Build(recorder, services =>
            services.AddScoped<IIntegrationEventHandler<Thing>, ActorReadingHandler>());

        await bus.PublishAsync(Envelope(NewThing("a")));

        recorder.Actors.Should().ContainSingle().Which.Should().Be(UserId.SystemActor);
    }

    [Fact]
    public async Task The_Envelopes_Actor_And_Organization_Reach_The_Handler()
    {
        var recorder = new Recorder();
        var (bus, _) = Build(recorder, services =>
            services.AddScoped<IIntegrationEventHandler<Thing>, ActorReadingHandler>());

        var actor = UserId.From(Guid.Parse("018f4d40-0000-7000-8000-0000000000aa"));
        var organization = Guid.Parse("018f4d40-0000-7000-8000-0000000000c1");

        await bus.PublishAsync(new IntegrationEventEnvelope(
            NewThing("a"), Trace, OrganizationId: organization, ActorUserId: actor));

        recorder.Actors.Should().ContainSingle().Which.Should().Be(actor);
        recorder.Organizations.Should().ContainSingle().Which.Should().Be(organization);
    }

    [Fact]
    public async Task An_Event_With_No_Tenant_Is_Refused_Rather_Than_Dispatched()
    {
        // A confidently-resolved context for a tenant that does not exist is
        // worse than an unresolved one: once SET LOCAL app.tenant_id runs, every
        // query silently returns nothing instead of failing.
        var recorder = new Recorder();
        var (bus, _) = Build(recorder, services =>
            services.AddScoped<IIntegrationEventHandler<Thing>, ThingHandler>());

        var act = () => bus.PublishAsync(
            Envelope(NewThing("a") with { TenantId = Guid.Empty }));

        await act.Should().ThrowAsync<ArgumentException>();
        recorder.Handled.Should().BeEmpty();
    }

    // ---- failure isolation ---------------------------------------------------

    [Fact]
    public async Task One_Failing_Handler_Does_Not_Deny_The_Others_The_Event()
    {
        // Poison-message containment is per subscription: one module's broken
        // handler must not stop another module consuming the same event, and
        // in-process there is no retry and no dead-letter to make up for it.
        var recorder = new Recorder();
        var (bus, _) = Build(recorder, services =>
        {
            services.AddScoped<IIntegrationEventHandler<Thing>, ThrowingHandler>();
            services.AddScoped<IIntegrationEventHandler<Thing>, SecondThingHandler>();
        });

        var act = () => bus.PublishAsync(Envelope(NewThing("a")));

        await act.Should().ThrowAsync<InvalidOperationException>();
        recorder.Handled.Should().ContainSingle().Which.Should().Be("second:a",
            "the surviving subscription still got its delivery");
    }

    [Fact]
    public async Task Several_Failures_Are_Reported_Together()
    {
        var recorder = new Recorder();
        var (bus, _) = Build(recorder, services =>
        {
            services.AddScoped<IIntegrationEventHandler<Thing>, ThrowingHandler>();
            services.AddScoped<IIntegrationEventHandler<Thing>, ThrowingHandler>();
        });

        var act = () => bus.PublishAsync(Envelope(NewThing("a")));

        (await act.Should().ThrowAsync<AggregateException>())
            .Which.InnerExceptions.Should().HaveCount(2);
    }

    [Fact]
    public async Task Each_Handler_Gets_Its_Own_Scope()
    {
        // Under a broker each subscription gets its own. Sharing one here would
        // hand two modules' consumers the same DbContext and the same unit of
        // work, so a failed handler's dirty state would cross a module boundary
        // the architecture otherwise enforces hard.
        var recorder = new Recorder();
        var (bus, _) = Build(recorder, services =>
        {
            services.AddScoped<ScopeMarker>();
            services.AddScoped<IIntegrationEventHandler<Thing>, ScopeReadingHandler>();
            services.AddScoped<IIntegrationEventHandler<Thing>, SecondScopeReadingHandler>();
        });

        await bus.PublishAsync(Envelope(NewThing("a")));

        recorder.Scopes.Should().HaveCount(2);
        recorder.Scopes.Distinct().Should().HaveCount(2, "two scopes, two markers");
    }

    // ---- cancellation --------------------------------------------------------

    [Fact]
    public async Task A_Publish_On_An_Already_Cancelled_Token_Does_Not_Dispatch()
    {
        var recorder = new Recorder();
        var (bus, _) = Build(recorder, services =>
            services.AddScoped<IIntegrationEventHandler<Thing>, ThingHandler>());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = () => bus.PublishAsync(Envelope(NewThing("a")), cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        recorder.Handled.Should().BeEmpty("a broker-backed transport would fail before any I/O");
    }

    [Fact]
    public async Task A_Handler_Cancelled_By_A_Foreign_Token_Fails_Rather_Than_Cancels()
    {
        // An outbox processor cannot distinguish "we are shutting down, retry
        // later" from "the handler ran and gave up" if both arrive as a cancelled
        // task — and its shutdown path swallows the former.
        var recorder = new Recorder();
        var (bus, _) = Build(recorder, services =>
            services.AddScoped<IIntegrationEventHandler<Thing>>(_ => new ForeignCancelHandler()));

        var publish = bus.PublishAsync(Envelope(NewThing("a")));

        var act = () => publish;
        await act.Should().ThrowAsync<InvalidOperationException>();
        publish.IsCanceled.Should().BeFalse();
    }

    [Fact]
    public async Task A_Handler_That_Faults_After_An_Await_Still_Faults_The_Publish()
    {
        // Every throwing handler in this suite threw SYNCHRONOUSLY, which comes
        // out of MethodInfo.Invoke and is rethrown before `await delivery` is
        // ever reached. So the path every real async handler takes — the one
        // that hits a database — had no coverage for faults at all: wrapping the
        // await in a swallowing catch survived the whole suite.
        var recorder = new Recorder();
        var (bus, _) = Build(recorder, services =>
            services.AddScoped<IIntegrationEventHandler<Thing>, AsyncThrowingHandler>());

        var act = () => bus.PublishAsync(Envelope(NewThing("a")));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Be("async handler failed");
    }

    [Fact]
    public async Task The_Tenant_Is_Still_Set_After_A_Handler_Awaits()
    {
        // Every tenant-reading handler read it synchronously, so the suite
        // proved the context was set when a handler STARTED — not that it was
        // still set when its continuation resumed, which is when the query RLS
        // evaluates actually runs. Restoring the publisher's context just before
        // the await survived every test.
        var recorder = new Recorder();
        var (bus, _) = Build(recorder, services =>
            services.AddScoped<IIntegrationEventHandler<Thing>, LateTenantReadingHandler>());

        await bus.PublishAsync(Envelope(NewThing("a")));

        recorder.Tenants.Should().ContainSingle().Which.Should().Be(Tenant);
    }

    [Fact]
    public async Task The_Publish_Token_Reaches_The_Handler()
    {
        // The token was threaded through but never asserted to arrive: passing
        // CancellationToken.None instead survived, so a shutdown or a timeout
        // would never reach a consumer.
        var recorder = new Recorder();
        var (bus, _) = Build(recorder, services =>
            services.AddScoped<IIntegrationEventHandler<Thing>, TokenReadingHandler>());
        using var cancelled = new CancellationTokenSource();

        var publish = bus.PublishAsync(Envelope(NewThing("a")), cancelled.Token);
        await cancelled.CancelAsync();
        await publish;

        // The token ITSELF, not merely "a cancellable token" — asserting
        // CanBeCanceled was true of any token at all, so threading a freshly
        // minted CancellationTokenSource through instead would have passed while
        // a shutdown never reached a consumer, which is the failure this test
        // names.
        recorder.HandlerToken.Should().Be(cancelled.Token);
    }

    [Fact]
    public async Task A_Handler_That_Cannot_Be_Built_Is_Reported_As_Such()
    {
        // Handler construction happens before the per-handler loop that provides
        // isolation — the container materialises the whole array before
        // returning any element — so a constructor that throws takes every
        // sibling with it and nothing here can contain it. Without the explicit
        // report the exception was swallowed, the count came back zero, and a
        // broken registration looked exactly like "nobody subscribed".
        var recorder = new Recorder();
        var (bus, _) = Build(recorder, services =>
        {
            services.AddScoped<IIntegrationEventHandler<Thing>, ThingHandler>();
            services.AddScoped<IIntegrationEventHandler<Thing>, UnconstructableHandler>();
        });

        var act = () => bus.PublishAsync(Envelope(NewThing("a")));

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Contain("failed to construct");
        thrown.Which.InnerException!.Message.Should().Be("this handler cannot be built");
        recorder.Handled.Should().BeEmpty("no handler for that event could run");
    }

    [Fact]
    public async Task A_Handler_Returning_No_Task_Is_Named()
    {
        // Otherwise the caller gets a bare NullReferenceException from inside a
        // transport it did not know it was in.
        var recorder = new Recorder();
        var (bus, _) = Build(recorder, services =>
            services.AddScoped<IIntegrationEventHandler<Thing>, NullTaskHandler>());

        var act = () => bus.PublishAsync(Envelope(NewThing("a")));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain(nameof(NullTaskHandler));
    }

    [Fact]
    public async Task The_Dispatch_Scope_Is_Disposed()
    {
        // An undisposed scope leaks a DbContext per publish, and nothing noticed.
        var recorder = new Recorder();
        var (bus, _) = Build(recorder, services =>
        {
            services.AddScoped<DisposalProbe>();
            services.AddScoped<IIntegrationEventHandler<Thing>, DisposalProbingHandler>();
        });

        await bus.PublishAsync(Envelope(NewThing("a")));

        recorder.Probes.Should().ContainSingle().Which.Disposed.Should().BeTrue();
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

        var first = bus.PublishAsync(Envelope(NewThing("a", "same-key")));
        var second = bus.PublishAsync(Envelope(NewThing("b", "same-key")));
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
        // TWO gates, each side waiting on the OTHER's. Sharing one semaphore
        // let each side consume its own release, so it never waited for
        // anything and passed with both keys on a single chain.
        var recorder = new Recorder();
        using var firstArrived = new SemaphoreSlim(0);
        using var secondArrived = new SemaphoreSlim(0);
        var (bus, _) = Build(recorder, services =>
            services.AddScoped<IIntegrationEventHandler<Thing>>(_ =>
                new RendezvousHandler(recorder, firstArrived, secondArrived)));

        var first = bus.PublishAsync(Envelope(NewThing("a", "key-1")));
        var second = bus.PublishAsync(Envelope(NewThing("b", "key-2")));

        await Task.WhenAll(first, second).WaitAsync(Timeout);
        recorder.Rendezvoused.Should().Be(2, "neither key waited for the other");
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

        var failing = bus.PublishAsync(Envelope(NewThing("boom", "same-key")));
        await ((Func<Task>)(() => failing)).Should().ThrowAsync<InvalidOperationException>();

        await bus.PublishAsync(Envelope(NewThing("after", "same-key"))).WaitAsync(Timeout);

        recorder.Handled.Should().Contain("after");
    }

    // ---- helpers -------------------------------------------------------------

    /// <summary>
    /// Wraps an event for dispatch.
    /// </summary>
    /// <remarks>
    /// The partition key is the event's own — the envelope reads it and cannot
    /// disagree with it. An earlier two-parameter shape could: every test here
    /// published an event declaring one key with a different one passed
    /// alongside, the transport used the parameter and never the event, and
    /// nothing noticed. Ordering is guaranteed per partition key, so a key that
    /// can differ from itself is a guarantee that cannot be stated.
    /// </remarks>
    private static IntegrationEventEnvelope Envelope(Thing @event) =>
        new(@event, Trace);

    private static Thing NewThing(string payload, string? partitionKey = null) => new()
    {
        EventId = Guid.NewGuid(),
        TenantId = Tenant,
        OccurredAt = DateTimeOffset.UnixEpoch,
        Payload = payload,
        Key = partitionKey,
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

        // The production binding, verbatim (CrossCuttingFoundationExtensions):
        // the scoped context resolves FROM the accessor. Registering anything
        // else here would test a container this application never builds.
        services.AddScoped<ITenantContext>(sp =>
            sp.GetRequiredService<ITenantContextAccessor>().Current
            ?? UnresolvedTenantContext.Instance);

        register(services);

        var provider = services.BuildServiceProvider();

        return (
            new InProcessEventBus(
                provider.GetRequiredService<IServiceScopeFactory>(),
                accessor,
                new PartitionSerializer(),
                NullLogger<InProcessEventBus>.Instance),
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

        public ConcurrentQueue<UserId> Actors { get; } = new();

        public ConcurrentQueue<Guid> Organizations { get; } = new();

        public ConcurrentQueue<Guid> Scopes { get; } = new();

        public ConcurrentQueue<DisposalProbe> Probes { get; } = new();

        public CancellationToken HandlerToken { get; set; }

        public int Rendezvoused => _rendezvoused;

        private int _rendezvoused;

        public void Rendezvous() => Interlocked.Increment(ref _rendezvoused);

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

        /// <summary>An ordering domain independent of the payload, for the ordering cases.</summary>
        public string? Key { get; init; }

        public override string Topic => "learnstack.test.thing";

        public override string PartitionKey => Key ?? Payload;
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

    public sealed class ScopedContextReadingHandler(Recorder recorder, ITenantContext context)
        : IIntegrationEventHandler<Thing>
    {
        public Task HandleAsync(Thing @event, CancellationToken cancellationToken = default)
        {
            recorder.Tenants.Enqueue(context.TenantId);
            return Task.CompletedTask;
        }
    }

    public sealed class ActorReadingHandler(Recorder recorder, ITenantContext context)
        : IIntegrationEventHandler<Thing>
    {
        public Task HandleAsync(Thing @event, CancellationToken cancellationToken = default)
        {
            recorder.Actors.Enqueue(context.UserId!.Value);

            if (context.OrganizationId is { } organization)
            {
                recorder.Organizations.Enqueue(organization);
            }

            return Task.CompletedTask;
        }
    }

    public sealed class ScopeMarker
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    public sealed class ScopeReadingHandler(Recorder recorder, ScopeMarker marker)
        : IIntegrationEventHandler<Thing>
    {
        public Task HandleAsync(Thing @event, CancellationToken cancellationToken = default)
        {
            recorder.Scopes.Enqueue(marker.Id);
            return Task.CompletedTask;
        }
    }

    public sealed class SecondScopeReadingHandler(Recorder recorder, ScopeMarker marker)
        : IIntegrationEventHandler<Thing>
    {
        public Task HandleAsync(Thing @event, CancellationToken cancellationToken = default)
        {
            recorder.Scopes.Enqueue(marker.Id);
            return Task.CompletedTask;
        }
    }

    public sealed class ForeignCancelHandler : IIntegrationEventHandler<Thing>
    {
        public Task HandleAsync(Thing @event, CancellationToken cancellationToken = default)
        {
            using var unrelated = new CancellationTokenSource();
            unrelated.Cancel();
            unrelated.Token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    public sealed class AsyncThrowingHandler : IIntegrationEventHandler<Thing>
    {
        public async Task HandleAsync(Thing @event, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new InvalidOperationException("async handler failed");
        }
    }

    public sealed class LateTenantReadingHandler(Recorder recorder, ITenantContextAccessor accessor)
        : IIntegrationEventHandler<Thing>
    {
        public async Task HandleAsync(Thing @event, CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), CancellationToken.None);
            recorder.Tenants.Enqueue(accessor.Current!.TenantId);
        }
    }

    public sealed class TokenReadingHandler(Recorder recorder) : IIntegrationEventHandler<Thing>
    {
        public Task HandleAsync(Thing @event, CancellationToken cancellationToken = default)
        {
            recorder.HandlerToken = cancellationToken;
            return Task.CompletedTask;
        }
    }

    public sealed class NullTaskHandler : IIntegrationEventHandler<Thing>
    {
        public Task HandleAsync(Thing @event, CancellationToken cancellationToken = default) =>
            null!;
    }

    public sealed class UnconstructableHandler : IIntegrationEventHandler<Thing>
    {
        public UnconstructableHandler() =>
            throw new InvalidOperationException("this handler cannot be built");

        public Task HandleAsync(Thing @event, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class DisposalProbe : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    public sealed class DisposalProbingHandler(Recorder recorder, DisposalProbe probe)
        : IIntegrationEventHandler<Thing>
    {
        public Task HandleAsync(Thing @event, CancellationToken cancellationToken = default)
        {
            recorder.Probes.Enqueue(probe);
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

    public sealed class RendezvousHandler(
        Recorder recorder, SemaphoreSlim first, SemaphoreSlim second)
        : IIntegrationEventHandler<Thing>
    {
        public async Task HandleAsync(Thing @event, CancellationToken cancellationToken = default)
        {
            // The key decides which gate is this side's, so the two invocations
            // never pick the same one — a shared counter would be shared across
            // tests running in parallel.
            var mine = @event.PartitionKey == "key-1" ? first : second;
            var theirs = ReferenceEquals(mine, first) ? second : first;

            mine.Release();
            (await theirs.WaitAsync(Timeout, CancellationToken.None)).Should().BeTrue(
                "the other key's handler must be running at the same time");

            recorder.Rendezvous();
        }
    }
}
