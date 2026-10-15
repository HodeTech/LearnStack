namespace LearnStack.Infrastructure.Messaging;

public enum DeliveryClaimKind { Normal, Recovery, Terminal }

/// <summary>A scheduler claim carries routing and lease metadata, never the causal envelope or payload.</summary>
public sealed record DeliveryClaim(
    Guid TenantId, string SubscriptionId, Guid EventId, Guid? OrganizationId,
    string Token, int Attempts, DateTimeOffset LockedUntil, DeliveryClaimKind Kind,
    string Type, string Topic, string PartitionKey, DateTimeOffset OccurredAt);

public enum DeliveryExecutionOutcome
{
    Completed, Duplicate, LostClaim, InvalidEnvelope, InvalidSubscription,
    Refused, Failed, Canceled, Indeterminate,
}

/// <summary>Bounded execution evidence. Acknowledged commit remains definitive despite cleanup failure.</summary>
public sealed record DeliveryExecutionResult(
    DeliveryExecutionOutcome Outcome, bool CommitAcknowledged, bool CleanupFailed);

public enum DeliveryFailureReason { ExecutionFailed, ExecutionTimeout, UnknownOutcome, ExecutionRefused }

public enum DeliveryTerminalReason { UnknownType, RetiredType, InvalidEnvelope, InvalidSubscription, RetryExhausted }

public sealed record DeliveryQueueSnapshot(long PendingCount, long DeferredCount, double OldestEligibleAgeSeconds);
