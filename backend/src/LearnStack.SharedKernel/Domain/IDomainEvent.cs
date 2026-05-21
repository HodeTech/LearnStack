using MediatR;

namespace LearnStack.SharedKernel.Domain;

/// <summary>
/// In-process domain event raised by an aggregate method. Dispatched
/// in-process by MediatR — the cross-module integration-event path
/// (outbox + Dapr pub/sub) is a different mechanism per
/// <see href="../../../../docs/decisions/0010-cross-module-communication.md">ADR-0010</see>.
/// </summary>
public interface IDomainEvent : INotification
{
    /// <summary>
    /// Unique event identifier. UUIDv7 so insertion-order matches occurrence
    /// order when an event is persisted for replay or debugging.
    /// </summary>
    Guid EventId { get; }

    /// <summary>
    /// UTC instant the event was raised.
    /// </summary>
    DateTimeOffset OccurredAt { get; }
}
