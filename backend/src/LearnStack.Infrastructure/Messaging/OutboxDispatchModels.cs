using LearnStack.SharedKernel.Messaging;

namespace LearnStack.Infrastructure.Messaging;

public enum OutboxClaimKind { Normal, Recovery, Terminal }
public enum OutboxTerminalReason { UnknownType, RetiredType, InvalidEnvelope, RetryExhausted }
public enum OutboxFailureReason { PublishFailed, PublishTimeout, Shutdown, UnknownOutcome }

/// <summary>A committed acquisition; its token is meaningful only until database-time expiry.</summary>
public sealed record OutboxDispatchClaim(
    Guid Id, Guid TenantId, Guid? OrganizationId, string Token, int Attempts,
    DateTimeOffset LockedUntil, OutboxClaimKind Kind, string Type, string Topic,
    string PartitionKey, DateTimeOffset OccurredAt, string CorrelationId,
    Guid? CausationId, Guid? ActorUserId, string Payload, bool MetadataIsNull);

/// <summary>One database observation; gauge callbacks consume only this immutable value.</summary>
public sealed record OutboxQueueSnapshot(long PendingCount, long DeferredCount, double OldestEligibleAgeSeconds);

public sealed record OutboxEnvelopeReadResult(
    OutboxEventDisposition Disposition, string EventTypeLabel,
    IntegrationEventEnvelope? Envelope, OutboxTerminalReason? TerminalReason);
