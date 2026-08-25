using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Messaging;

/// <summary>
/// One integration event plus the dispatch metadata the outbox row carries and
/// the event itself does not.
/// </summary>
/// <remarks>
/// <para>
/// The canonical <c>outbox_messages</c> row
/// (<see href="../../../../docs/standards/05-database.md">Standards 05</see>)
/// requires <c>topic</c> and <c>correlation_id</c> as <c>NOT NULL</c> and carries
/// <c>organization_id</c>, <c>causation_id</c> and <c>actor_user_id</c>. None of
/// them belong on the event: they describe the <i>delivery</i>, not the fact.
/// Without somewhere to put them, a dispatcher had no way to hand them to a
/// consumer at all — correlation was read from the publisher's ambient context,
/// which is <c>null</c> in the background service the outbox processor is, so the
/// trace chain broke at exactly the boundary
/// <see href="../../../../docs/standards/10-observability.md">Standards 10</see>
/// requires it to cross.
/// </para>
/// <para>
/// One type rather than more parameters, and now rather than later: ADR-0014
/// Amendment 2 says it in this repository's own words — adding a required
/// parameter after the first consumer exists breaks every call site. There is
/// not one yet.
/// </para>
/// </remarks>
/// <param name="Event">The fact being published.</param>
/// <param name="CorrelationId">
/// The originating request's W3C traceparent, taken from the outbox row rather
/// than from whatever context happens to be ambient at dispatch.
/// </param>
/// <param name="OrganizationId">
/// The organization the fact belongs to, when it belongs to one. A consumer is
/// restored into this scope, so a tenant-wide event and an organization-scoped
/// one are no longer indistinguishable to it.
/// </param>
/// <param name="CausationId">The event or command that caused this one, if any.</param>
/// <param name="ActorUserId">
/// Who caused the fact. A consumer writing state attributes to
/// <see cref="UserId.SystemActor"/> when this is absent.
/// </param>
public sealed record IntegrationEventEnvelope(
    IIntegrationEvent Event,
    string CorrelationId,
    Guid? OrganizationId = null,
    Guid? CausationId = null,
    UserId? ActorUserId = null)
{
    /// <summary>
    /// The ordering domain, read from the event and from nowhere else.
    /// </summary>
    /// <remarks>
    /// It was briefly a separate parameter alongside the event's own
    /// <see cref="IIntegrationEvent.PartitionKey"/>, which meant two sources for
    /// one value with nothing reconciling them — measured, the transport read
    /// the parameter and never the event, and the tests published events whose
    /// declared key disagreed with the one passed, green. Ordering is guaranteed
    /// per partition key, so a key that can differ from itself is a guarantee
    /// that cannot be stated.
    /// </remarks>
    public string PartitionKey => Event.PartitionKey;

    /// <summary>
    /// The channel, read from the event and from nowhere else.
    /// </summary>
    /// <remarks>
    /// It was briefly a producer-supplied string on this record. The topic is a
    /// property of the event <i>type</i> — two events of one type always go to
    /// the same channel — so a per-delivery parameter invited exactly the drift
    /// <see cref="PartitionKey"/> already had, where the transport read one
    /// source and the event declared another. It also made
    /// <c>Integration_Event_TopicNames_FollowConvention</c> unimplementable:
    /// the rule asserts over declared event types, and nothing declared one.
    /// </remarks>
    public string Topic => Event.Topic;
}
