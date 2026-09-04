using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Tenancy;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel.Messaging;

/// <summary>
/// The shape of the integration-event contract, which the doc comments argue
/// for at length and nothing was checking.
/// </summary>
/// <remarks>
/// The comments defer to catalogued architecture tests
/// (<c>Integration_Events_Inherit_From_IntegrationEventBase</c>,
/// <c>Integration_Event_Declares_PartitionKey</c>) that are booked for Phase 02b
/// and do not exist yet — so they read as enforced today and are not. These are
/// the parts that can be asserted from the kernel alone.
/// </remarks>
public sealed class IntegrationEventContractTests
{
    private static readonly Guid Tenant = Guid.Parse("018f4d40-0000-7000-8000-00000000000a");
    private const string Trace = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

    [Theory]
    [InlineData(nameof(IIntegrationEvent.Topic))]
    [InlineData(nameof(IIntegrationEvent.PartitionKey))]
    public void The_Event_Declares_Its_Own_Channel_And_Ordering_Domain(string member)
    {
        // Both are properties of the event TYPE, not of one delivery, and both
        // were briefly carried alongside the event instead — where a value with
        // two sources can disagree with itself and the transport reads one of
        // them. Abstract means the compiler asks every event for its own.
        typeof(IntegrationEventBase).GetProperty(member)!
            .GetGetMethod()!.IsAbstract.Should().BeTrue();

        typeof(IntegrationEventEnvelope).GetProperty(member)!
            .CanWrite.Should().BeFalse($"the envelope reads {member} off the event");
    }

    [Fact]
    public void The_Envelope_Carries_The_Events_Own_Channel_And_Ordering_Domain()
    {
        // By VALUE, not merely structurally. Asserting only that the getters are
        // read-only left the forwarding unchecked: returning `Event.Topic + "-x"`
        // — or reading a stale captured field instead of the event — passed
        // every test in the suite. That is the exact bug class these properties
        // exist to prevent, where the transport reads one source and the event
        // declares another.
        var @event = NewSample();
        var envelope = new IntegrationEventEnvelope(@event, Trace);

        envelope.Topic.Should().Be(@event.Topic);
        envelope.PartitionKey.Should().Be(@event.PartitionKey);
        envelope.Event.Should().BeSameAs(@event);
    }

    [Fact]
    public void PartitionKey_Is_Abstract_So_No_Event_Can_Inherit_A_Default()
    {
        // A default would have to be the tenant id, which silently serialises a
        // tenant's whole stream onto one partition — a real throughput cost
        // taken by accident. Making it virtual with that default survived every
        // other test.
        typeof(IntegrationEventBase)
            .GetProperty(nameof(IIntegrationEvent.PartitionKey))!
            .GetGetMethod()!.IsAbstract.Should().BeTrue();
    }

    [Theory]
    [InlineData(nameof(IIntegrationEvent.EventId))]
    [InlineData(nameof(IIntegrationEvent.TenantId))]
    [InlineData(nameof(IIntegrationEvent.OccurredAt))]
    public void IntegrationEventBase_Identity_Fields_Are_Required_Members(string member)
    {
        // `required` is what makes a half-populated event a compile error at the
        // producer and a loud JsonException at the reader, rather than an event
        // carrying Guid.Empty into a consumer.
        typeof(IntegrationEventBase).GetProperty(member)!
            .GetCustomAttribute<RequiredMemberAttribute>()
            .Should().NotBeNull($"{member} must be required");
    }

    [Fact]
    public void A_Payload_Written_Through_The_Base_Keeps_Its_Own_Fields()
    {
        // The trap the non-generic port creates. Serializing with
        // IIntegrationEvent as the declared type — which it is at every dispatch
        // boundary — emits the five interface members and silently drops
        // everything the concrete event added: valid JSON, no exception, and the
        // loss commits inside the transaction that reported success.
        var @event = NewSample();
        IIntegrationEvent asBase = @event;

        var naive = JsonSerializer.Serialize(asBase);
        naive.Should().NotContain(nameof(Sample.LearnerName),
            "the declared type is the interface, so only its five members survive");

        var written = @event.ToPayloadJson();

        written.Should().Contain(nameof(Sample.LearnerName));
        JsonSerializer.Deserialize<Sample>(written, IntegrationEventBase.PayloadJsonOptions)
            .Should().Be(@event);
    }

    [Fact]
    public void A_Payload_Round_Trips_Through_Its_Runtime_Type()
    {
        // What the outbox processor does: it knows the type from the row's
        // `type` column and deserializes to it.
        var @event = NewSample();
        var json = @event.ToPayloadJson();

        // The type comes from a variable, exactly as the processor takes it from
        // the row's `type` column — it does not know it statically, which is the
        // whole reason the payload has to be written by runtime type.
        var storedType = @event.GetType();
        var revived = (Sample)JsonSerializer.Deserialize(
            json, storedType, IntegrationEventBase.PayloadJsonOptions)!;

        revived.Should().Be(@event);
        revived.PartitionKey.Should().Be(@event.PartitionKey);
    }

    [Fact]
    public void The_Payload_Options_Do_Not_Rename_Members()
    {
        // The options are part of the wire contract, not a formatting
        // preference: a writer using web defaults and a reader using these would
        // disagree on every member and dead-letter everything.
        IntegrationEventBase.PayloadJsonOptions.PropertyNamingPolicy.Should().BeNull();
    }

    [Fact]
    public void The_Payload_Options_Are_Frozen_Before_Their_First_Use()
    {
        IntegrationEventBase.PayloadJsonOptions.IsReadOnly.Should().BeTrue();

        var act = () => IntegrationEventBase.PayloadJsonOptions.WriteIndented = true;

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void The_Envelope_Rejects_A_Null_Event_And_A_Blank_Correlation()
    {
        var nullEvent = () => new IntegrationEventEnvelope(null!, Trace);
        var nullCorrelation = () => new IntegrationEventEnvelope(NewSample(), null!);
        var blankCorrelation = () => new IntegrationEventEnvelope(NewSample(), " ");

        nullEvent.Should().Throw<ArgumentNullException>();
        nullCorrelation.Should().Throw<ArgumentNullException>();
        blankCorrelation.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_Envelope_Rejects_Malformed_Event_Identity_And_Trace_Metadata()
    {
        var invalid = new Action[]
        {
            () => _ = new IntegrationEventEnvelope(
                NewSample() with { EventId = Guid.Empty }, Trace),
            () => _ = new IntegrationEventEnvelope(
                NewSample() with { TenantId = Guid.Empty }, Trace),
            () => _ = new IntegrationEventEnvelope(
                NewSample() with { OccurredAt = default }, Trace),
            () => _ = new IntegrationEventEnvelope(NewSample(), "not-a-traceparent"),
            () => _ = new IntegrationEventEnvelope(
                NewSample(), Trace, OrganizationId: Guid.Empty),
            () => _ = new IntegrationEventEnvelope(
                NewSample(), Trace, CausationId: Guid.Empty),
            () => _ = new IntegrationEventEnvelope(
                NewInvalidMetadataSample(topic: "", partitionKey: "valid"), Trace),
            () => _ = new IntegrationEventEnvelope(
                NewInvalidMetadataSample(topic: "learnstack.test.invalid", partitionKey: ""), Trace),
        };

        invalid.Should().AllSatisfy(action => action.Should().Throw<ArgumentException>());
    }

    [Fact]
    public void An_Organization_Scoped_Event_Requires_A_Non_Empty_Organization()
    {
        var missing = () => new IntegrationEventEnvelope(NewOrganizationSample(), Trace);
        var valid = () => new IntegrationEventEnvelope(
            NewOrganizationSample(),
            Trace,
            OrganizationId: Guid.Parse("018f4d40-0000-7000-8000-0000000000c1"));

        missing.Should().Throw<ArgumentException>();
        valid.Should().NotThrow();
    }

    [Fact]
    public void The_Envelope_Rejects_An_Uninitialized_Causal_Actor()
    {
        var fixture = new UnassignedActorFixture();

        var act = () => new IntegrationEventEnvelope(
            NewSample(), Trace, ActorUserId: fixture.ActorUserId);

        act.Should().Throw<ArgumentException>();
    }

    // ---- the consumer's context ---------------------------------------------

    [Fact]
    public void The_Consumer_Context_Has_The_Shape_A_Handler_Needs()
    {
        var actor = UserId.From(Guid.Parse("018f4d40-0000-7000-8000-0000000000aa"));
        var organization = Guid.Parse("018f4d40-0000-7000-8000-0000000000c1");

        var context = EventTenantContext.FromEnvelope(new IntegrationEventEnvelope(
            NewSample(), Trace, OrganizationId: organization, ActorUserId: actor));

        // IsResolved false would make TenantContextBehavior short-circuit every
        // consumer that sends a MediatR command — silently, before its business
        // logic ran.
        context.IsResolved.Should().BeTrue();

        // Matrix row 17. The envelope carried the tenant, so there is no host and no
        // token to reconcile and no matrix to apply — which is also the proof that
        // TenantContextFactory is the only producer of the TenantContext TYPE rather
        // than the only producer of a resolved ITenantContext.
        context.Origin.Should().Be(TenantContextOrigin.Ambient);
        context.TenantId.Should().Be(TenantId.From(Tenant));
        context.OrganizationId.Should().Be(
            organization is { } org ? OrganizationId.From(org) : null);
        context.UserId.Should().Be(UserId.SystemActor);
        context.CausalActorUserId.Should().Be(actor);
        context.CorrelationId.Should().Be(Trace);
        context.ModuleName.Should().BeNull();
    }

    [Fact]
    public void A_Null_Envelope_Is_Refused()
    {
        var act = () => EventTenantContext.FromEnvelope(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    private static Sample NewSample() => new()
    {
        EventId = Guid.Parse("018f4d40-0000-7000-8000-0000000000e1"),
        TenantId = Tenant,
        OccurredAt = DateTimeOffset.UnixEpoch,
        LearnerName = "Ada",
    };

    private static InvalidMetadataSample NewInvalidMetadataSample(
        string topic,
        string partitionKey) => new()
        {
            EventId = Guid.Parse("018f4d40-0000-7000-8000-0000000000e2"),
            TenantId = Tenant,
            OccurredAt = DateTimeOffset.UnixEpoch,
            DeclaredTopic = topic,
            DeclaredPartitionKey = partitionKey,
        };

    private static OrganizationSample NewOrganizationSample() => new()
    {
        EventId = Guid.Parse("018f4d40-0000-7000-8000-0000000000e3"),
        TenantId = Tenant,
        OccurredAt = DateTimeOffset.UnixEpoch,
    };

    public sealed record Sample : IntegrationEventBase
    {
        public required string LearnerName { get; init; }

        public override string Topic => "learnstack.test.sample";

        // Independent of the payload, so the truncation is demonstrated on a
        // member the interface does not carry rather than on a value that
        // happens to appear through PartitionKey.
        public override string PartitionKey => "ordering-domain";
    }

    private sealed record InvalidMetadataSample : IntegrationEventBase
    {
        public required string DeclaredTopic { get; init; }
        public required string DeclaredPartitionKey { get; init; }
        public override string Topic => DeclaredTopic;
        public override string PartitionKey => DeclaredPartitionKey;
    }

    private sealed record OrganizationSample
        : IntegrationEventBase, IOrganizationScopedIntegrationEvent
    {
        public override string Topic => "learnstack.test.organization-sample";
        public override string PartitionKey => "organization";
    }

    private sealed record UnassignedActorFixture
    {
        public UserId ActorUserId { get; init; }
    }
}
