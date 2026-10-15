using System.Text.Json;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Messaging;

namespace LearnStack.Infrastructure.Messaging;

/// <summary>Immutable persisted fact and causal envelope, read under tenant authority.</summary>
internal sealed record StoredEventEnvelope(
    Guid EventId, Guid TenantId, Guid? OrganizationId, string Type, string Topic,
    string PartitionKey, DateTimeOffset OccurredAt, string CorrelationId,
    Guid? CausationId, Guid? ActorUserId, string Payload);

/// <summary>Shared envelope validation, independent of producer subscription readiness.</summary>
internal static class StoredEventEnvelopeReader
{
    internal static IntegrationEventEnvelope? Read(StoredEventEnvelope stored, OutboxEventResolution resolution)
    {
        if (resolution.EventType is null) return null;
        try
        {
            if (stored.TenantId == Guid.Empty
                || stored.TenantId == TenantId.PlatformSentinel.Value || stored.OrganizationId == Guid.Empty)
                return null;

            using var document = JsonDocument.Parse(stored.Payload);
            var root = document.RootElement;
            // Read the stored routing fields before deserialization: get-only
            // properties would otherwise silently regenerate missing or altered values.
            if (root.ValueKind != JsonValueKind.Object
                || root.GetProperty(nameof(IIntegrationEvent.Topic)).GetString() != stored.Topic
                || root.GetProperty(nameof(IIntegrationEvent.PartitionKey)).GetString() != stored.PartitionKey)
                return null;

            var fact = root.Deserialize(resolution.EventType,
                IntegrationEventBase.PayloadJsonOptions) as IntegrationEventBase;
            if (fact is null) return null;
            var serialized = OutboxEventContract.Serialize(fact);
            // Npgsql writes timestamps at microsecond precision by truncating
            // sub-microsecond ticks; compare that stored precision, not a tolerance.
            if (serialized.EventId != stored.EventId || serialized.TenantId != stored.TenantId
                || serialized.Topic != stored.Topic || serialized.PartitionKey != stored.PartitionKey
                || serialized.OccurredAt.UtcTicks / 10 != stored.OccurredAt.UtcTicks / 10)
                return null;

            var envelope = new IntegrationEventEnvelope(fact, stored.CorrelationId,
                stored.OrganizationId, stored.CausationId,
                stored.ActorUserId is { } actor ? UserId.From(actor) : null);
            return envelope;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException
            or InvalidOperationException or NotSupportedException or KeyNotFoundException)
        {
            // Stored data is untrusted; neither its text nor parser diagnostics leave this boundary.
            return null;
        }
    }
}
