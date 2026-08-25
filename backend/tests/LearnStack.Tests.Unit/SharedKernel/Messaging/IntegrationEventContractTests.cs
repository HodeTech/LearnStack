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
        var envelope = new IntegrationEventEnvelope(@event, "trace-1");

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
    public void The_Envelope_Fields_Are_Required(string member)
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
        // boundary — emits the four interface members and silently drops
        // everything the concrete event added: valid JSON, no exception, and the
        // loss commits inside the transaction that reported success.
        var @event = NewSample();
        IIntegrationEvent asBase = @event;

        var naive = JsonSerializer.Serialize(asBase);
        naive.Should().NotContain(nameof(Sample.LearnerName),
            "the declared type is the interface, so only its four members survive");

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

    // ---- the consumer's context ---------------------------------------------

    [Fact]
    public void The_Consumer_Context_Has_The_Shape_A_Handler_Needs()
    {
        var actor = UserId.From(Guid.Parse("018f4d40-0000-7000-8000-0000000000aa"));
        var organization = Guid.Parse("018f4d40-0000-7000-8000-0000000000c1");

        var context = EventTenantContext.FromEnvelope(new IntegrationEventEnvelope(
            NewSample(), "trace-1", OrganizationId: organization, ActorUserId: actor));

        // IsResolved false would make TenantContextBehavior short-circuit every
        // consumer that sends a MediatR command — silently, before its business
        // logic ran.
        context.IsResolved.Should().BeTrue();
        context.TenantId.Should().Be(Tenant);
        context.OrganizationId.Should().Be(organization);
        context.UserId.Should().Be(actor);
        context.CorrelationId.Should().Be("trace-1");
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

    public sealed record Sample : IntegrationEventBase
    {
        public required string LearnerName { get; init; }

        public override string Topic => "learnstack.test.sample";

        // Independent of the payload, so the truncation is demonstrated on a
        // member the interface does not carry rather than on a value that
        // happens to appear through PartitionKey.
        public override string PartitionKey => "ordering-domain";
    }
}
