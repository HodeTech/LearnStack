namespace LearnStack.Infrastructure.Messaging;

/// <summary>Applies producer readiness before validating its stored envelope.</summary>
public sealed class OutboxEnvelopeReader(OutboxEventRegistry registry)
{
    public OutboxEnvelopeReadResult Read(OutboxDispatchClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var resolution = registry.Resolve(claim.Type);
        // Producer hold deliberately precedes payload validation. An already
        // accepted Active sibling uses the readiness-independent reader directly.
        if (resolution.Disposition == OutboxEventDisposition.Pending)
            return new(resolution.Disposition, resolution.EventTypeLabel, null, null);
        if (resolution.Disposition == OutboxEventDisposition.Unknown)
            return new(resolution.Disposition, "unknown", null, OutboxTerminalReason.UnknownType);
        if (resolution.Disposition == OutboxEventDisposition.Retired)
            return new(resolution.Disposition, "retired", null, OutboxTerminalReason.RetiredType);

        var envelope = claim.MetadataIsNull
            ? StoredEventEnvelopeReader.Read(new StoredEventEnvelope(
                claim.Id, claim.TenantId, claim.OrganizationId, claim.Type, claim.Topic,
                claim.PartitionKey, claim.OccurredAt, claim.CorrelationId, claim.CausationId,
                claim.ActorUserId, claim.Payload), resolution)
            : null;
        return envelope is null
            ? new(OutboxEventDisposition.Unknown, "invalid", null, OutboxTerminalReason.InvalidEnvelope)
            : new(resolution.Disposition, resolution.EventTypeLabel, envelope, null);
    }
}
