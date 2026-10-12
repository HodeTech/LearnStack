using System.Text.Json;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Messaging;

namespace LearnStack.Infrastructure.Messaging;

/// <summary>Validates a stored fact using only the registry's declared CLR contract.</summary>
public sealed class OutboxEnvelopeReader(OutboxEventRegistry registry)
{
    public OutboxEnvelopeReadResult Read(OutboxDispatchClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var resolution = registry.Resolve(claim.Type);
        // A Pending hold precedes validation, including deliberately malformed payloads.
        if (resolution.Disposition == OutboxEventDisposition.Pending)
            return new(resolution.Disposition, resolution.EventTypeLabel, null, null);
        if (resolution.Disposition == OutboxEventDisposition.Unknown)
            return new(resolution.Disposition, "unknown", null, OutboxTerminalReason.UnknownType);
        if (resolution.Disposition == OutboxEventDisposition.Retired)
            return new(resolution.Disposition, "retired", null, OutboxTerminalReason.RetiredType);

        try
        {
            if (!claim.MetadataIsNull || claim.TenantId == Guid.Empty
                || claim.TenantId == TenantId.PlatformSentinel.Value || claim.OrganizationId == Guid.Empty)
                return Invalid();

            var fact = JsonSerializer.Deserialize(claim.Payload, resolution.EventType!,
                IntegrationEventBase.PayloadJsonOptions) as IntegrationEventBase;
            if (fact is null) return Invalid();
            var serialized = OutboxEventContract.Serialize(fact);
            if (serialized.EventId != claim.Id || serialized.TenantId != claim.TenantId
                || serialized.Topic != claim.Topic || serialized.PartitionKey != claim.PartitionKey
                || serialized.OccurredAt.UtcTicks / 10 != claim.OccurredAt.UtcTicks / 10)
                return Invalid();

            var envelope = new IntegrationEventEnvelope(fact, claim.CorrelationId,
                claim.OrganizationId, claim.CausationId,
                claim.ActorUserId is { } actor ? UserId.From(actor) : null);
            return new(resolution.Disposition, resolution.EventTypeLabel, envelope, null);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException
            or InvalidOperationException or NotSupportedException or KeyNotFoundException)
        {
            // Stored data is untrusted; neither its text nor parser diagnostics leave this boundary.
            return Invalid();
        }

        static OutboxEnvelopeReadResult Invalid() =>
            new(OutboxEventDisposition.Unknown, "invalid", null, OutboxTerminalReason.InvalidEnvelope);
    }
}
