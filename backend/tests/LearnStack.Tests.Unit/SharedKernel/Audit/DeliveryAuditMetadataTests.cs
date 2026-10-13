using System.Text.Json;
using FluentAssertions;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Tenancy;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel.Audit;

public sealed class DeliveryAuditMetadataTests
{
    private const string Traceparent = "00-11111111111111111111111111111111-2222222222222222-01";
    private static readonly DateTimeOffset Declared = DateTimeOffset.UnixEpoch.AddMinutes(1);
    private static readonly Guid IncomingEventId = Guid.Parse("bbbbbbbb-0000-7000-8000-000000000001");
    private static readonly UserId CausalActor = UserId.From(Guid.Parse("cccccccc-0000-7000-8000-000000000001"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Both_writers_preserve_delivery_identifiers_without_impersonating_the_causal_actor(bool hasActor)
    {
        var intent = Intent() with
        {
            CausationId = IncomingEventId,
            CausalActorUserId = hasActor ? CausalActor : null,
        };

        foreach (var row in BothPaths(intent))
        {
            row.ActorUserId.Should().Be(UserId.SystemActor);
            row.CorrelationId.Should().Be(Traceparent);
            using var metadata = JsonDocument.Parse(row.Metadata!);
            var properties = metadata.RootElement.EnumerateObject().ToArray();
            string[] expectedNames = hasActor
                ? ["causation_id", "causal_actor_user_id"]
                : ["causation_id"];
            properties.Select(property => property.Name).Should().Equal(expectedNames);
            properties.Should().OnlyContain(property => property.Value.ValueKind == JsonValueKind.String);
            metadata.RootElement.GetProperty("causation_id").GetGuid().Should().Be(IncomingEventId);
            if (hasActor)
            {
                metadata.RootElement.GetProperty("causal_actor_user_id").GetGuid().Should().Be(CausalActor.Value);
            }
        }
    }

    [Fact]
    public void Ordinary_request_intents_keep_null_metadata_and_their_effective_actor()
    {
        var intent = Intent() with { ActorUserId = CausalActor };
        foreach (var row in BothPaths(intent))
        {
            row.Metadata.Should().BeNull();
            row.ActorUserId.Should().Be(CausalActor);
        }
    }

    [Fact]
    public void A_causal_actor_alone_is_still_identifier_only_metadata()
    {
        foreach (var row in BothPaths(Intent() with { CausalActorUserId = CausalActor }))
        {
            using var metadata = JsonDocument.Parse(row.Metadata!);
            metadata.RootElement.EnumerateObject().Should().ContainSingle()
                .Which.Name.Should().Be("causal_actor_user_id");
            metadata.RootElement.GetProperty("causal_actor_user_id").GetGuid().Should().Be(CausalActor.Value);
        }
    }

    [Theory]
    [InlineData("empty_event")]
    [InlineData("empty_actor")]
    [InlineData("uninitialized_actor")]
    public void Both_writers_refuse_invalid_causal_identifiers(string invalid)
    {
        var intent = invalid switch
        {
            "empty_event" => Intent() with { CausationId = Guid.Empty },
            "empty_actor" => Intent() with { CausalActorUserId = UserId.From(Guid.Empty) },
            _ => Intent() with { CausalActorUserId = (new UserId[1])[0] },
        };

        FluentActions.Invoking(() => AuditDraftComposer.InTransaction(intent, []))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => AuditDraftComposer.Reconciled(
                intent, [], AuditIntentState.Indeterminate, Declared.AddSeconds(1)))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Event_context_carries_the_incoming_identity_separately_from_its_ancestors_causation()
    {
        var incoming = new ProbeEvent
        {
            EventId = IncomingEventId,
            TenantId = Guid.CreateVersion7(),
            OccurredAt = Declared,
        };
        var ancestor = Guid.CreateVersion7();
        var context = EventTenantContext.FromEnvelope(new IntegrationEventEnvelope(
            incoming, Traceparent, CausationId: ancestor, ActorUserId: CausalActor));

        context.EventId.Should().Be(IncomingEventId).And.NotBe(ancestor);
        context.CausalActorUserId.Should().Be(CausalActor);
        context.UserId.Should().Be(UserId.SystemActor);
    }

    private static IEnumerable<AuditEntryDraft> BothPaths(AuditIntent intent)
    {
        yield return AuditDraftComposer.InTransaction(intent, []);
        foreach (var state in new[] { AuditIntentState.RolledBack, AuditIntentState.Indeterminate, AuditIntentState.Committed })
        {
            yield return AuditDraftComposer.Reconciled(intent, [], state, Declared.AddSeconds(1));
        }
    }

    private static AuditIntent Intent() => new(
        AuditEntryId.From(Guid.CreateVersion7()),
        TenantId.From(Guid.CreateVersion7()),
        OrganizationId: null,
        ActorUserId: UserId.SystemActor,
        CorrelationId: Traceparent,
        ModuleName: "customization",
        Operation: "customization.content_type.initialize",
        OperationType: OperationType.Create,
        OperationClass: OperationClass.Must,
        EntityType: null,
        DeclaredAt: Declared);

    private sealed record ProbeEvent : IntegrationEventBase
    {
        public override string Topic => "learnstack.customization.probe.v1";
        public override string PartitionKey => TenantId.ToString("D");
    }
}
