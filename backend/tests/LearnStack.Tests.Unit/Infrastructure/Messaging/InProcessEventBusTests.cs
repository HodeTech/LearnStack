using FluentAssertions;
using LearnStack.Infrastructure.Messaging;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Messaging;

/// <summary>Publication guards before the PostgreSQL acceptance boundary.</summary>
/// <remarks>Database acceptance and handler execution have separate proofs.</remarks>
public sealed class InProcessEventBusTests
{
    private static readonly Guid Tenant = Guid.Parse("018f4d40-0000-7000-8000-00000000000a");
    private static readonly Guid Subject = Guid.Parse("018f4d40-0000-7000-8000-00000000000b");
    private static readonly Guid Organization = Guid.Parse("018f4d40-0000-7000-8000-00000000000c");
    private const string Trace = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

    [Fact]
    public async Task Active_publication_restores_envelope_scope_before_acceptance_scope_creation()
    {
        var accessor = new FieldAccessor();
        var publisher = EventTenantContext.FromEnvelope(Envelope(New<AbsentEvent>()));
        accessor.Current = publisher;
        var scopes = new ScopeProbe(accessor);
        var partitions = new RecordingPartitions();
        var bus = Build(scopes, accessor, partitions);
        var actor = UserId.From(Guid.NewGuid());
        var envelope = new IntegrationEventEnvelope(New<ActiveEvent>(), Trace,
            OrganizationId: Organization, CausationId: Guid.NewGuid(), ActorUserId: actor);

        await ((Func<Task>)(() => bus.PublishAsync(envelope)))
            .Should().ThrowAsync<ScopeProbeException>();

        scopes.Calls.Should().Be(1);
        var restored = scopes.Context.Should().BeOfType<EventTenantContext>().Subject;
        restored.TenantId.Value.Should().Be(Tenant);
        restored.OrganizationId!.Value.Value.Should().Be(Organization);
        restored.CorrelationId.Should().Be(Trace);
        restored.UserId.Should().Be(UserId.SystemActor);
        restored.CausalActorUserId.Should().Be(actor);
        accessor.Current.Should().BeSameAs(publisher);
        partitions.Keys.Should().ContainSingle().Which.Should().Be(Subject.ToString("D"));
        DeclaredHandler.Constructions.Should().Be(0);
    }

    [Fact]
    public async Task Failed_acceptance_scope_creation_restores_a_null_previous_context()
    {
        var accessor = new FieldAccessor();
        var scopes = new ScopeProbe(accessor);
        var bus = Build(scopes, accessor);

        await ((Func<Task>)(() => bus.PublishAsync(Envelope(New<ActiveEvent>()))))
            .Should().ThrowAsync<ScopeProbeException>();

        scopes.Context.Should().BeOfType<EventTenantContext>();
        accessor.Current.Should().BeNull();
    }

    [Fact]
    public async Task Base_interface_publication_uses_runtime_contract_and_canonical_partition()
    {
        var accessor = new FieldAccessor();
        var scopes = new ScopeProbe(accessor);
        var partitions = new RecordingPartitions();
        var bus = Build(scopes, accessor, partitions);
        IIntegrationEvent fact = New<ActiveEvent>();

        await ((Func<Task>)(() => bus.PublishAsync(Envelope(fact))))
            .Should().ThrowAsync<ScopeProbeException>();

        scopes.Calls.Should().Be(1);
        partitions.Keys.Should().ContainSingle().Which.Should().Be(Subject.ToString("D"));
    }

    [Fact]
    public async Task Intentional_absence_validates_but_does_not_construct_an_acceptance_scope()
    {
        var accessor = new FieldAccessor();
        var publisher = EventTenantContext.FromEnvelope(Envelope(New<ActiveEvent>()));
        accessor.Current = publisher;
        var scopes = new ScopeProbe(accessor);
        var partitions = new RecordingPartitions();
        var bus = Build(scopes, accessor, partitions);

        await bus.PublishAsync(Envelope(New<AbsentEvent>()));

        scopes.Calls.Should().Be(0);
        partitions.Keys.Should().ContainSingle().Which.Should().Be(Subject.ToString("D"));
        accessor.Current.Should().BeSameAs(publisher);
        DeclaredHandler.Constructions.Should().Be(0);
    }

    [Fact]
    public async Task Pending_unknown_and_retired_contracts_refuse_before_any_scope()
    {
        var accessor = new FieldAccessor();
        var scopes = new ScopeProbe(accessor);
        var partitions = new RecordingPartitions();
        var bus = Build(scopes, accessor, partitions);

        foreach (var fact in new IntegrationEventBase[]
                 { New<PendingEvent>(), New<UnknownEvent>(), New<RetiredEvent>() })
        {
            await ((Func<Task>)(() => bus.PublishAsync(Envelope(fact))))
                .Should().ThrowAsync<InvalidOperationException>();
        }

        scopes.Calls.Should().Be(0);
        partitions.Keys.Should().BeEmpty();
        accessor.Current.Should().BeNull();
    }

    [Fact]
    public async Task Invalid_tenant_payload_or_partition_refuses_even_intentional_absence()
    {
        var accessor = new FieldAccessor();
        var scopes = new ScopeProbe(accessor);
        var partitions = new RecordingPartitions();
        var bus = Build(scopes, accessor, partitions);
        var sentinel = New<AbsentEvent>() with { TenantId = TenantId.PlatformSentinel.Value };

        foreach (var fact in new IntegrationEventBase[]
                 { sentinel, New<InvalidShapeEvent>(), New<InvalidPartitionEvent>() })
        {
            await ((Func<Task>)(() => bus.PublishAsync(Envelope(fact))))
                .Should().ThrowAsync<ArgumentException>();
        }

        scopes.Calls.Should().Be(0);
        partitions.Keys.Should().BeEmpty();
        accessor.Current.Should().BeNull();
    }

    [Fact]
    public async Task Null_envelope_and_pre_canceled_token_never_construct_a_scope()
    {
        var accessor = new FieldAccessor();
        var scopes = new ScopeProbe(accessor);
        var bus = Build(scopes, accessor);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await ((Func<Task>)(() => bus.PublishAsync(null!)))
            .Should().ThrowAsync<ArgumentNullException>();
        await ((Func<Task>)(() => bus.PublishAsync(Envelope(New<ActiveEvent>()), cancellation.Token)))
            .Should().ThrowAsync<OperationCanceledException>();

        scopes.Calls.Should().Be(0);
        accessor.Current.Should().BeNull();
    }

    [Fact]
    public async Task Cancellation_while_queued_prevents_acceptance_scope_creation()
    {
        var accessor = new FieldAccessor();
        var scopes = new ScopeProbe(accessor);
        var partitions = new PartitionSerializer();
        var bus = Build(scopes, accessor, partitions);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = partitions.RunSequentiallyFor(Subject.ToString("D"), async () =>
        {
            entered.SetResult();
            await release.Task;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();

        try
        {
            var queued = bus.PublishAsync(Envelope(New<ActiveEvent>()), cancellation.Token);
            await cancellation.CancelAsync();
            release.SetResult();
            await holding.WaitAsync(TimeSpan.FromSeconds(10));

            await ((Func<Task>)(() => queued)).Should().ThrowAsync<OperationCanceledException>();
            scopes.Calls.Should().Be(0);
            accessor.Current.Should().BeNull();
        }
        finally
        {
            release.TrySetResult();
            await holding.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static InProcessEventBus Build(
        IServiceScopeFactory scopes, ITenantContextAccessor accessor,
        IPartitionSerializer? partitions = null)
    {
        DeclaredHandler.Constructions = 0;
        var descriptors = new ServiceCollection();
        descriptors.AddScoped<IIntegrationEventHandler<ActiveEvent>, DeclaredHandler>();
        var handlers = IntegrationEventHandlerRegistry.FromServiceDescriptors(descriptors);
        var registry = OutboxEventRegistry.Create(
        [
            OutboxEventRegistration.Required(typeof(ActiveEvent),
                [OutboxSubscriptionManifest.Active("test.active.v1", typeof(DeclaredHandler))]),
            OutboxEventRegistration.IntentionallyUnsubscribed(typeof(AbsentEvent)),
            OutboxEventRegistration.Required(typeof(PendingEvent),
                [OutboxSubscriptionManifest.Pending("test.pending.v1")]),
            OutboxEventRegistration.IntentionallyUnsubscribed(typeof(InvalidShapeEvent)),
            OutboxEventRegistration.IntentionallyUnsubscribed(typeof(InvalidPartitionEvent)),
        ],
        [typeof(RetiredEvent).AssemblyQualifiedName!],
        handlers,
        [typeof(ActiveEvent).Assembly]);
        return new InProcessEventBus(scopes, accessor, partitions ?? new PartitionSerializer(), registry);
    }

    private static IntegrationEventEnvelope Envelope(IIntegrationEvent fact) => new(fact, Trace);

    private static T New<T>() where T : IntegrationEventBase => typeof(T) switch
    {
        var type when type == typeof(ActiveEvent) => (T)(IntegrationEventBase)new ActiveEvent
        { EventId = Guid.NewGuid(), TenantId = Tenant, OccurredAt = DateTimeOffset.UnixEpoch, SubjectId = Subject },
        var type when type == typeof(AbsentEvent) => (T)(IntegrationEventBase)new AbsentEvent
        { EventId = Guid.NewGuid(), TenantId = Tenant, OccurredAt = DateTimeOffset.UnixEpoch, SubjectId = Subject },
        var type when type == typeof(PendingEvent) => (T)(IntegrationEventBase)new PendingEvent
        { EventId = Guid.NewGuid(), TenantId = Tenant, OccurredAt = DateTimeOffset.UnixEpoch, SubjectId = Subject },
        var type when type == typeof(UnknownEvent) => (T)(IntegrationEventBase)new UnknownEvent
        { EventId = Guid.NewGuid(), TenantId = Tenant, OccurredAt = DateTimeOffset.UnixEpoch, SubjectId = Subject },
        var type when type == typeof(RetiredEvent) => (T)(IntegrationEventBase)new RetiredEvent
        { EventId = Guid.NewGuid(), TenantId = Tenant, OccurredAt = DateTimeOffset.UnixEpoch, SubjectId = Subject },
        var type when type == typeof(InvalidShapeEvent) => (T)(IntegrationEventBase)new InvalidShapeEvent
        { EventId = Guid.NewGuid(), TenantId = Tenant, OccurredAt = DateTimeOffset.UnixEpoch, SubjectId = Subject, Label = "invalid" },
        var type when type == typeof(InvalidPartitionEvent) => (T)(IntegrationEventBase)new InvalidPartitionEvent
        { EventId = Guid.NewGuid(), TenantId = Tenant, OccurredAt = DateTimeOffset.UnixEpoch, SubjectId = Subject },
        _ => throw new ArgumentOutOfRangeException(nameof(T)),
    };

    private sealed class FieldAccessor : ITenantContextAccessor
    {
        public ITenantContext? Current { get; set; }
    }

    private sealed class ScopeProbe(ITenantContextAccessor accessor) : IServiceScopeFactory
    {
        public int Calls { get; private set; }
        public ITenantContext? Context { get; private set; }

        public IServiceScope CreateScope()
        {
            Calls++;
            Context = accessor.Current;
            throw new ScopeProbeException();
        }
    }

    private sealed class ScopeProbeException : Exception { }

    private sealed class RecordingPartitions : IPartitionSerializer
    {
        public List<string> Keys { get; } = [];
        public Task RunSequentiallyFor(string partitionKey, Func<Task> work)
        {
            Keys.Add(partitionKey);
            return work();
        }
    }

    [IntegrationEventPartition(nameof(SubjectId))]
    private sealed record ActiveEvent : IntegrationEventBase
    {
        public required Guid SubjectId { get; init; }
        public override string Topic => "learnstack.test.active";
        public override string PartitionKey => SubjectId.ToString("D");
    }

    [IntegrationEventPartition(nameof(SubjectId))]
    private sealed record AbsentEvent : IntegrationEventBase
    {
        public required Guid SubjectId { get; init; }
        public override string Topic => "learnstack.test.absent";
        public override string PartitionKey => SubjectId.ToString("D");
    }

    [IntegrationEventPartition(nameof(SubjectId))]
    private sealed record PendingEvent : IntegrationEventBase
    {
        public required Guid SubjectId { get; init; }
        public override string Topic => "learnstack.test.pending";
        public override string PartitionKey => SubjectId.ToString("D");
    }

    [IntegrationEventPartition(nameof(SubjectId))]
    private sealed record UnknownEvent : IntegrationEventBase
    {
        public required Guid SubjectId { get; init; }
        public override string Topic => "learnstack.test.unknown";
        public override string PartitionKey => SubjectId.ToString("D");
    }

    [IntegrationEventPartition(nameof(SubjectId))]
    private sealed record RetiredEvent : IntegrationEventBase
    {
        public required Guid SubjectId { get; init; }
        public override string Topic => "learnstack.test.retired";
        public override string PartitionKey => SubjectId.ToString("D");
    }

    [IntegrationEventPartition(nameof(SubjectId))]
    private sealed record InvalidShapeEvent : IntegrationEventBase
    {
        public required Guid SubjectId { get; init; }
        public required string Label { get; init; }
        public override string Topic => "learnstack.test.invalidshape";
        public override string PartitionKey => SubjectId.ToString("D");
    }

    [IntegrationEventPartition(nameof(SubjectId))]
    private sealed record InvalidPartitionEvent : IntegrationEventBase
    {
        public required Guid SubjectId { get; init; }
        public override string Topic => "learnstack.test.invalidpartition";
        public override string PartitionKey => TenantId.ToString("D");
    }

    private sealed class DeclaredHandler : IIntegrationEventHandler<ActiveEvent>
    {
        public static int Constructions;
        public DeclaredHandler()
        {
            Constructions++;
            throw new InvalidOperationException("Publication constructed a business handler.");
        }

        public Task HandleAsync(ActiveEvent integrationEvent,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
