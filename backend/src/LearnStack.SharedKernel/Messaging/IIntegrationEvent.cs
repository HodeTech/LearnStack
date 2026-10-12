namespace LearnStack.SharedKernel.Messaging;

/// <summary>
/// A fact one module publishes for others to consume, per
/// <see href="../../../../docs/decisions/0006-events-and-outbox.md">ADR-0006</see>.
/// Crosses a module boundary through the outbox; never a method call.
/// </summary>
/// <remarks>
/// Implemented by inheriting <see cref="IntegrationEventBase"/> rather than by
/// implementing this interface directly, so that every event carries the same
/// identity, tenancy and ordering fields and a consumer can rely on them without
/// knowing the type. The architecture test
/// <c>Integration_Events_Inherit_From_IntegrationEventBase</c> is implemented by
/// P02b-2 against the declared concrete contract registry.
/// </remarks>
public interface IIntegrationEvent
{
    /// <summary>Identity for consumer-side deduplication.</summary>
    /// <remarks>
    /// Delivery is at-least-once; P02b-3's transport-owned shared inbox uses this
    /// value together with tenant and stable subscription identity.
    /// It is assigned once by the producer and never re-derived — a redelivery
    /// carries the same value, which is the entire point.
    /// </remarks>
    Guid EventId { get; }

    /// <summary>The tenant the fact belongs to.</summary>
    /// <remarks>
    /// Carried on the event because a consumer runs outside the request that
    /// produced it: there is no ambient context to inherit, so the transport
    /// restores it from here before a handler runs. Without it a consumer would
    /// execute with no tenant, and every query filter and RLS policy would be
    /// evaluated against nothing.
    /// </remarks>
    Guid TenantId { get; }

    /// <summary>When the fact happened, from <c>IClock</c> — never <c>DateTime.UtcNow</c>.</summary>
    DateTimeOffset OccurredAt { get; }

    /// <summary>
    /// The channel this event is published on, <c>learnstack.{module}.{aggregate}</c>.
    /// </summary>
    /// <remarks>
    /// A property of the event <i>type</i>, not of one delivery: two events of
    /// the same type always go to the same topic, and the name is derivable from
    /// the type. Declaring it here rather than passing it alongside is the same
    /// rule as <see cref="PartitionKey"/>, for the same reason — a value with
    /// two sources is a value that can disagree with itself, and the transport
    /// would read one of them.
    /// </remarks>
    string Topic { get; }

    /// <summary>
    /// The partition identity this event belongs to.
    /// </summary>
    /// <remarks>
    /// Normally the id of the aggregate the event is about; the tenant id for
    /// events about the tenant as a whole. Resolved once by the producer, never
    /// re-derived downstream. ADR-0046 bounds eligible selection within one
    /// processor; this key alone promises no enqueue, commit, retry or
    /// cross-process publication order.
    /// </remarks>
    string PartitionKey { get; }
}
