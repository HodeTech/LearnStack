using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using LearnStack.Application.Messaging;
using LearnStack.Infrastructure.Messaging;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Persistence;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel.Messaging;

public sealed class OutboxContractTests
{
    private static readonly Guid FactId = Guid.Parse("01988331-0000-7000-8000-000000000001");
    private static readonly Guid Tenant = Guid.Parse("01988331-0000-7000-8000-000000000002");
    private static readonly Guid Aggregate = Guid.Parse("01988331-aaaa-7000-8000-000000000003");

    [Fact]
    public void The_port_has_no_metadata_or_routing_override_arguments()
    {
        var enqueue = typeof(IOutbox).GetMethod(nameof(IOutbox.EnqueueAsync))!;

        enqueue.ReturnType.Should().Be<Task>();
        enqueue.GetParameters().Select(parameter => parameter.ParameterType).Should()
            .Equal(typeof(IIntegrationEvent), typeof(CancellationToken));
        enqueue.GetParameters()[1].HasDefaultValue.Should().BeTrue();
        OutboxPayloadLimits.MaxBytes.Should().Be(262_144);
    }

    [Fact]
    public void Guid_and_approved_nullable_identifiers_keep_the_canonical_runtime_payload()
    {
        var fact = New<IdentifierEvent>();

        var serialized = OutboxEventContract.Serialize(fact);

        serialized.Payload.Should().Be(fact.ToPayloadJson());
        serialized.TypeName.Should().Be(typeof(IdentifierEvent).AssemblyQualifiedName);
        serialized.EventId.Should().Be(FactId);
        serialized.TenantId.Should().Be(Tenant);
        serialized.PartitionKey.Should().Be(Aggregate.ToString("D"));
        using var payload = JsonDocument.Parse(serialized.Payload);
        payload.RootElement.GetProperty(nameof(fact.RelatedTenant)).GetGuid().Should().Be(Tenant);
        payload.RootElement.GetProperty(nameof(fact.RelatedOrganization)).GetGuid().Should().Be(Aggregate);
        payload.RootElement.GetProperty(nameof(fact.RelatedUser)).GetGuid().Should().Be(UserId.SystemActor.Value);
        payload.RootElement.GetProperty(nameof(fact.OptionalId)).ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void An_approved_strongly_typed_partition_source_is_canonicalized_as_a_guid()
    {
        var serialized = OutboxEventContract.Serialize(New<TypedPartitionEvent>());

        serialized.PartitionKey.Should().Be(Aggregate.ToString("D"));
    }

    [Fact]
    public void Tenant_partitioning_is_accepted_only_when_deliberately_declared()
    {
        OutboxEventContract.Serialize(New<TenantPartitionEvent>()).PartitionKey.Should().Be(Tenant.ToString("D"));
    }

    [Theory]
    [InlineData(typeof(MissingPartitionEvent))]
    [InlineData(typeof(ImplicitTenantPartitionEvent))]
    [InlineData(typeof(MislabelledTenantPartitionEvent))]
    [InlineData(typeof(UnknownSourceEvent))]
    [InlineData(typeof(EventIdentityPartitionEvent))]
    [InlineData(typeof(NullablePartitionEvent))]
    [InlineData(typeof(EmptyPartitionEvent))]
    [InlineData(typeof(WrongPartitionEvent))]
    [InlineData(typeof(UppercasePartitionEvent))]
    public void Missing_or_inconsistent_partition_contracts_are_refused(Type type)
    {
        var serialize = () => OutboxEventContract.Serialize(New(type));

        serialize.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(typeof(TextPayloadEvent))]
    [InlineData(typeof(DtoPayloadEvent))]
    [InlineData(typeof(CollectionPayloadEvent))]
    [InlineData(typeof(BinaryPayloadEvent))]
    [InlineData(typeof(RawJsonPayloadEvent))]
    [InlineData(typeof(UnapprovedIdEvent))]
    [InlineData(typeof(ExtraFieldEvent))]
    [InlineData(typeof(HiddenRoutingEvent))]
    [InlineData(typeof(RemappedInterfaceEvent))]
    [InlineData(typeof(IgnoredTextEvent))]
    [InlineData(typeof(RenamedIdentifierEvent))]
    [InlineData(typeof(ConverterPayloadEvent))]
    [InlineData(typeof(SerializationCallbackEvent))]
    public void Non_identifier_payloads_and_serializer_escape_hatches_are_refused(Type type)
    {
        EscapeConverter.Writes = 0;
        SerializationCallbackEvent.Calls = 0;
        var serialize = () => OutboxEventContract.Serialize(New(type));

        serialize.Should().Throw<ArgumentException>();
        EscapeConverter.Writes.Should().Be(0, "the contract is refused before a custom converter runs");
        SerializationCallbackEvent.Calls.Should().Be(0, "callbacks cannot mutate the canonical payload");
    }

    [Fact]
    public void A_computed_identifier_cannot_serialize_a_different_partition_source()
    {
        UnstableIdentifierEvent.Reads = 0;
        var serialize = () => OutboxEventContract.Serialize(New<UnstableIdentifierEvent>());

        serialize.Should().Throw<ArgumentException>().WithMessage("*same identity and routing*");
    }

    [Theory]
    [InlineData("learnstack.education.course", true)]
    [InlineData("learnstack.hub.custom-domain.activated", true)]
    [InlineData("learnstack.a.b", true)]
    [InlineData("learnstack.education.course.activated", false)]
    [InlineData("Learnstack.education.course", false)]
    [InlineData("learnstack.-education.course", false)]
    [InlineData("learnstack.education-.course", false)]
    [InlineData("learnstack.1education.course", false)]
    [InlineData("learnstack.education.course\n", false)]
    public void The_runtime_topic_guard_accepts_only_the_existing_topic_convention(string topic, bool accepted)
    {
        TopicEvent.DeclaredTopic = topic;
        var serialize = () => OutboxEventContract.Serialize(New<TopicEvent>());

        if (accepted) serialize.Should().NotThrow();
        else serialize.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("a", 1)]
    [InlineData("é", 2)]
    [InlineData("😀", 4)]
    public void The_inclusive_payload_budget_counts_utf8_bytes(string text, int bytes)
    {
        var boundary = string.Concat(Enumerable.Repeat(text, OutboxPayloadLimits.MaxBytes / bytes));
        Encoding.UTF8.GetByteCount(boundary).Should().Be(OutboxPayloadLimits.MaxBytes);
        var accepted = () => OutboxEventContract.EnsurePayloadFits(boundary);
        var refused = () => OutboxEventContract.EnsurePayloadFits(boundary + "a");

        accepted.Should().NotThrow();
        refused.Should().Throw<ArgumentException>().WithMessage("*262144 bytes*");
    }

    [Fact]
    public async Task An_unenrolled_outbox_refuses_before_reading_connection_or_event_getters()
    {
        var unit = Substitute.For<IUnitOfWork>();
        var outbox = new PostgresOutbox(unit, new ProducerExecution(unit));
        UnstableIdentifierEvent.Reads = 0;
        var enqueue = () => outbox.EnqueueAsync(New<UnstableIdentifierEvent>());

        await enqueue.Should().ThrowAsync<InvalidOperationException>();
        UnstableIdentifierEvent.Reads.Should().Be(0);
        _ = unit.DidNotReceive().Connection;
    }

    [Fact]
    public async Task Cancellation_refuses_before_acquiring_connection()
    {
        var unit = Substitute.For<IUnitOfWork>();
        var outbox = new PostgresOutbox(unit, new ProducerExecution(unit));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var enqueue = () => outbox.EnqueueAsync(New<IdentifierEvent>(), cancellation.Token);

        await enqueue.Should().ThrowAsync<OperationCanceledException>();
        _ = unit.DidNotReceive().Connection;
    }

    private static T New<T>() where T : IntegrationEventBase => (T)New(typeof(T));

    private static IntegrationEventBase New(Type type)
    {
        // The same initialization for every planted shape, including types that hide
        // members. Set the real base properties so a refusal cannot pass on empty IDs.
        var fact = (IntegrationEventBase)Activator.CreateInstance(type, nonPublic: true)!;
        typeof(IntegrationEventBase).GetProperty(nameof(fact.EventId))!.SetValue(fact, FactId);
        typeof(IntegrationEventBase).GetProperty(nameof(fact.TenantId))!.SetValue(fact, Tenant);
        typeof(IntegrationEventBase).GetProperty(nameof(fact.OccurredAt))!.SetValue(fact, DateTimeOffset.UnixEpoch);
        return fact;
    }

    private abstract record AggregateEvent : IntegrationEventBase
    {
        public Guid AggregateId { get; init; } = Aggregate;
        public override string Topic => "learnstack.education.course";
        public override string PartitionKey => AggregateId.ToString("D");
    }

    [IntegrationEventPartition(nameof(AggregateId))]
    private sealed record IdentifierEvent : AggregateEvent
    {
        public Guid? OptionalId { get; init; }
        public TenantId RelatedTenant { get; init; } = LearnStack.SharedKernel.Identifiers.TenantId.From(Tenant);
        public OrganizationId? RelatedOrganization { get; init; } = OrganizationId.From(Aggregate);
        public UserId RelatedUser { get; init; } = UserId.SystemActor;
    }

    [IntegrationEventPartition(nameof(SubjectId))]
    private sealed record TypedPartitionEvent : AggregateEvent
    {
        public OrganizationId SubjectId { get; init; } = OrganizationId.From(Aggregate);
        public override string PartitionKey => SubjectId.Value.ToString("D");
    }

    [IntegrationEventPartition(nameof(TenantId), tenantWide: true)]
    private sealed record TenantPartitionEvent : AggregateEvent
    {
        public override string PartitionKey => TenantId.ToString("D");
    }

    private sealed record MissingPartitionEvent : AggregateEvent;

    [IntegrationEventPartition(nameof(TenantId))]
    private sealed record ImplicitTenantPartitionEvent : AggregateEvent;

    [IntegrationEventPartition(nameof(AggregateId), tenantWide: true)]
    private sealed record MislabelledTenantPartitionEvent : AggregateEvent;

    [IntegrationEventPartition("MissingId")]
    private sealed record UnknownSourceEvent : AggregateEvent;

    [IntegrationEventPartition(nameof(EventId))]
    private sealed record EventIdentityPartitionEvent : AggregateEvent;

    [IntegrationEventPartition(nameof(SubjectId))]
    private sealed record NullablePartitionEvent : AggregateEvent
    {
        public Guid? SubjectId { get; init; } = Aggregate;
    }

    [IntegrationEventPartition(nameof(EmptyId))]
    private sealed record EmptyPartitionEvent : AggregateEvent
    {
        public Guid EmptyId { get; init; }
        public override string PartitionKey => EmptyId.ToString("D");
    }

    [IntegrationEventPartition(nameof(AggregateId))]
    private sealed record WrongPartitionEvent : AggregateEvent
    {
        public override string PartitionKey => TenantId.ToString("D");
    }

    [IntegrationEventPartition(nameof(AggregateId))]
    private sealed record UppercasePartitionEvent : AggregateEvent
    {
        public override string PartitionKey => AggregateId.ToString("D").ToUpperInvariant();
    }

    [IntegrationEventPartition(nameof(AggregateId))]
    private sealed record TextPayloadEvent : AggregateEvent
    {
        public string Name { get; init; } = "Private name";
    }

    [IntegrationEventPartition(nameof(AggregateId))]
    private sealed record DtoPayloadEvent : AggregateEvent
    {
        public object Details { get; init; } = new { Name = "Private name" };
    }

    [IntegrationEventPartition(nameof(AggregateId))]
    private sealed record CollectionPayloadEvent : AggregateEvent
    {
        public Guid[] Ids { get; init; } = [Aggregate];
    }

    [IntegrationEventPartition(nameof(AggregateId))]
    private sealed record BinaryPayloadEvent : AggregateEvent
    {
        public byte[] Bytes { get; init; } = [1];
    }

    [IntegrationEventPartition(nameof(AggregateId))]
    private sealed record RawJsonPayloadEvent : AggregateEvent
    {
        public JsonElement Data { get; init; }
    }

    private readonly record struct UnapprovedId(Guid Value) : IStronglyTypedId<Guid>
    {
        public bool IsInitialized() => true;
    }

    [IntegrationEventPartition(nameof(AggregateId))]
    private sealed record UnapprovedIdEvent : AggregateEvent
    {
        public UnapprovedId SubjectId { get; init; } = new(Aggregate);
    }

    [IntegrationEventPartition(nameof(AggregateId))]
    private sealed record ExtraFieldEvent : AggregateEvent
    {
        public Guid ExtraId = Aggregate;
    }

    [IntegrationEventPartition(nameof(AggregateId))]
    private sealed record HiddenRoutingEvent : AggregateEvent
    {
        public new Guid Topic { get; init; } = Aggregate;
    }

    [IntegrationEventPartition(nameof(AggregateId))]
    private sealed record RemappedInterfaceEvent : AggregateEvent, IIntegrationEvent
    {
        Guid IIntegrationEvent.TenantId => Aggregate;
    }

    [IntegrationEventPartition(nameof(AggregateId))]
    private sealed record IgnoredTextEvent : AggregateEvent
    {
        [JsonIgnore] public string Name { get; init; } = "Private name";
    }

    [IntegrationEventPartition(nameof(AggregateId))]
    private sealed record RenamedIdentifierEvent : AggregateEvent
    {
        [JsonPropertyName("OtherId")] public Guid SubjectId { get; init; } = Aggregate;
    }

    [IntegrationEventPartition(nameof(AggregateId))]
    [JsonConverter(typeof(EscapeConverter))]
    private sealed record ConverterPayloadEvent : AggregateEvent;

    private sealed class EscapeConverter : JsonConverter<ConverterPayloadEvent>
    {
        public static int Writes { get; set; }
        public override ConverterPayloadEvent? Read(ref Utf8JsonReader reader, Type typeToConvert,
            JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, ConverterPayloadEvent value, JsonSerializerOptions options)
        {
            Writes++;
            writer.WriteStringValue("Private name");
        }
    }

    [IntegrationEventPartition(nameof(AggregateId))]
    private sealed record SerializationCallbackEvent : AggregateEvent, IJsonOnSerializing
    {
        public static int Calls { get; set; }
        public void OnSerializing() => Calls++;
    }

    [IntegrationEventPartition(nameof(SubjectId))]
    private sealed record UnstableIdentifierEvent : AggregateEvent
    {
        public static int Reads { get; set; }
        public Guid SubjectId => ++Reads == 1 ? AggregateId : TenantId;
    }

    [IntegrationEventPartition(nameof(AggregateId))]
    private sealed record TopicEvent : AggregateEvent
    {
        public static string DeclaredTopic { get; set; } = "learnstack.education.course";
        public override string Topic => DeclaredTopic;
    }
}
