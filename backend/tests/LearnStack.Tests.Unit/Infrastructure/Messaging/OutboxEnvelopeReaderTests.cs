using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Infrastructure.Messaging;
using LearnStack.SharedKernel.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Messaging;

public sealed class OutboxEnvelopeReaderTests
{
    private const string Trace = "00-0123456789abcdef0123456789abcdef-0123456789abcdef-01";
    private const string Alias = "test.previous.fact";

    [Fact]
    public void Intentional_absence_and_alias_validate_the_full_envelope_before_acknowledgement()
    {
        var fact = Fact();
        var claim = Claim(fact) with { Type = Alias, OrganizationId = Guid.NewGuid() };
        var result = Reader().Read(claim);
        result.Disposition.Should().Be(OutboxEventDisposition.IntentionallyUnsubscribed);
        result.TerminalReason.Should().BeNull();
        result.Envelope!.Event.Should().BeEquivalentTo(fact);
        result.Envelope.OrganizationId.Should().Be(claim.OrganizationId);

        Reader().Read(claim with { CorrelationId = "not-a-trace" }).TerminalReason
            .Should().Be(OutboxTerminalReason.InvalidEnvelope);
    }

    [Fact]
    public void Pending_precedes_even_malformed_payload_and_envelope_validation()
    {
        var reader = Reader(pending: true);
        var result = reader.Read(Claim(Fact()) with { Payload = "{", CorrelationId = "invalid", Type = Alias });
        result.Disposition.Should().Be(OutboxEventDisposition.Pending);
        result.Envelope.Should().BeNull();
        result.TerminalReason.Should().BeNull();
    }

    [Fact]
    public void Unknown_and_explicitly_retired_identities_have_closed_distinct_reasons()
    {
        var claim = Claim(Fact()) with { Payload = "{" };
        Reader().Read(claim with { Type = "System.String, System.Private.CoreLib" }).TerminalReason
            .Should().Be(OutboxTerminalReason.UnknownType);
        Reader().Read(claim with { Type = "test.retired" }).TerminalReason
            .Should().Be(OutboxTerminalReason.RetiredType);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("tenant")]
    [InlineData("topic")]
    [InlineData("partition")]
    [InlineData("timestamp")]
    [InlineData("organization")]
    [InlineData("causation")]
    [InlineData("actor")]
    [InlineData("metadata")]
    [InlineData("payload")]
    public void Every_row_envelope_mismatch_refuses_without_parser_diagnostics(string mismatch)
    {
        var claim = Claim(Fact());
        claim = mismatch switch
        {
            "id" => claim with { Id = Guid.NewGuid() },
            "tenant" => claim with { TenantId = Guid.NewGuid() },
            "topic" => claim with { Topic = "learnstack.test.other" },
            "partition" => claim with { PartitionKey = Guid.NewGuid().ToString("D") },
            "timestamp" => claim with { OccurredAt = claim.OccurredAt.AddTicks(10) },
            "organization" => claim with { OrganizationId = Guid.Empty },
            "causation" => claim with { CausationId = Guid.Empty },
            "actor" => claim with { ActorUserId = Guid.Empty },
            "metadata" => claim with { MetadataIsNull = false },
            "payload" => claim with { Payload = "{\"private\":\"do-not-log\"" },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch)),
        };
        var result = Reader().Read(claim);
        result.TerminalReason.Should().Be(OutboxTerminalReason.InvalidEnvelope);
        result.EventTypeLabel.Should().Be("invalid");
        result.Envelope.Should().BeNull();
    }

    [Theory]
    [InlineData("Topic", "missing")]
    [InlineData("Topic", "changed")]
    [InlineData("Topic", "null")]
    [InlineData("Topic", "number")]
    [InlineData("Topic", "object")]
    [InlineData("PartitionKey", "missing")]
    [InlineData("PartitionKey", "changed")]
    [InlineData("PartitionKey", "null")]
    [InlineData("PartitionKey", "number")]
    [InlineData("PartitionKey", "object")]
    public void Stored_get_only_routing_fields_must_exist_and_match_the_row(string property, string mutation)
    {
        var claim = Claim(Fact());
        var root = JsonNode.Parse(claim.Payload)!.AsObject();
        if (mutation == "missing") root.Remove(property);
        else root[property] = mutation switch
        {
            "changed" => JsonValue.Create("private-invalid-routing"),
            "null" => null,
            "number" => JsonValue.Create(42),
            "object" => new JsonObject(),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

        var result = Reader().Read(claim with { Payload = root.ToJsonString() });

        result.TerminalReason.Should().Be(OutboxTerminalReason.InvalidEnvelope);
        result.EventTypeLabel.Should().Be("invalid");
        result.Envelope.Should().BeNull();
        Reader().Read(claim).Envelope.Should().NotBeNull("the unchanged control must still be admitted");
    }

    [Fact]
    public void PostgreSQL_microsecond_truncation_preserves_valid_occurrence_identity()
    {
        var fact = Fact() with
        {
            OccurredAt = DateTimeOffset.Parse("2026-10-12T12:00:00.1234567Z", System.Globalization.CultureInfo.InvariantCulture),
        };
        var claim = Claim(fact) with { OccurredAt = fact.OccurredAt.AddTicks(-7) };
        Reader().Read(claim).Envelope.Should().NotBeNull();
    }

    [Fact]
    public void Jsonb_whitespace_is_not_the_canonical_producer_byte_budget()
    {
        var claim = Claim(Fact());
        var root = JsonNode.Parse(claim.Payload)!;
        claim = claim with { Payload = root.ToJsonString(new() { WriteIndented = true }) + new string(' ', 262_144) };
        Reader().Read(claim).Envelope.Should().NotBeNull();
    }

    private static OutboxEnvelopeReader Reader(bool pending = false)
    {
        var registration = pending
            ? OutboxEventRegistration.Required(typeof(TestFact), [OutboxSubscriptionManifest.Pending("test.fact.v1")], Alias)
            : OutboxEventRegistration.IntentionallyUnsubscribed(typeof(TestFact), Alias);
        return new(OutboxEventRegistry.Create([registration], ["test.retired"],
            IntegrationEventHandlerRegistry.FromServiceDescriptors(new ServiceCollection()), [typeof(TestFact).Assembly]));
    }

    private static TestFact Fact() => new()
    {
        EventId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        OccurredAt = DateTimeOffset.UtcNow,
    };

    private static OutboxDispatchClaim Claim(TestFact fact) => new(fact.EventId, fact.TenantId, null,
        Guid.NewGuid().ToString("D"), 1, DateTimeOffset.UtcNow.AddSeconds(30), OutboxClaimKind.Normal,
        typeof(TestFact).AssemblyQualifiedName!, fact.Topic, fact.PartitionKey, fact.OccurredAt,
        Trace, null, null, fact.ToPayloadJson(), true);

    [IntegrationEventPartition(nameof(TenantId), tenantWide: true)]
    public sealed record TestFact : IntegrationEventBase
    {
        public override string Topic => "learnstack.test.fact";
        public override string PartitionKey => TenantId.ToString("D");
    }
}
