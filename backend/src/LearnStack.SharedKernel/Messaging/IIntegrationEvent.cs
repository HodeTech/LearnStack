namespace LearnStack.SharedKernel.Messaging;

/// <summary>
/// A fact one module publishes for others to consume, per
/// <see href="../../../../docs/decisions/0006-events-and-outbox.md">ADR-0006</see>.
/// Crosses a module boundary through the outbox; never a method call.
/// </summary>
/// <remarks>
/// Implemented by inheriting <see cref="IntegrationEventBase"/> rather than by
/// implementing this interface directly — the architecture test
/// <c>Integration_Events_Inherit_From_IntegrationEventBase</c> enforces it, so
/// that every event carries the same identity, tenancy and ordering fields and a
/// consumer can rely on them without knowing the type.
/// </remarks>
public interface IIntegrationEvent
{
    /// <summary>Identity for consumer-side deduplication.</summary>
    /// <remarks>
    /// Delivery is at-least-once, so this is what <c>IInboxGuard</c> keys on.
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
    /// The ordering domain this event belongs to.
    /// </summary>
    /// <remarks>
    /// Ordering is guaranteed per partition key and nowhere else, so a partition
    /// key nobody sets is a guarantee nobody has. Normally the id of the
    /// aggregate the event is about; the tenant id for events about the tenant as
    /// a whole. Resolved once, by the producer that knows the domain, and never
    /// re-derived downstream.
    /// </remarks>
    string PartitionKey { get; }
}
