using System.Diagnostics;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Messaging;

/// <summary>
/// One integration event plus its validated dispatch metadata. Topic and
/// partition key are forwarded from the event rather than supplied again.
/// </summary>
/// <remarks>
/// <para>
/// The canonical <c>outbox_messages</c> row
/// (<see href="../../../../docs/standards/05-database.md">Standards 05</see>)
/// requires <c>topic</c> and <c>correlation_id</c> as <c>NOT NULL</c> and carries
/// <c>organization_id</c>, <c>causation_id</c> and <c>actor_user_id</c>. Topic is
/// declared by the event type. Correlation, organization, causation and causal
/// actor describe the <i>delivery</i>, not the fact, so they live here. Without
/// somewhere to put that metadata, a dispatcher had no way to hand it to a
/// consumer — correlation was read from the publisher's ambient context, which
/// is <c>null</c> in the background service the outbox processor is, so the trace
/// chain broke at exactly the boundary
/// <see href="../../../../docs/standards/10-observability.md">Standards 10</see>
/// requires it to cross.
/// </para>
/// <para>
/// One type rather than more parameters, and now rather than later: ADR-0038
/// says it in this repository's own words — adding a required
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
/// The human who caused the fact, retained as causal audit metadata. A consumer
/// writing state always attributes the asynchronous work to
/// <see cref="UserId.SystemActor"/> rather than impersonating this user.
/// </param>
public sealed record IntegrationEventEnvelope
{
    public IntegrationEventEnvelope(
        IIntegrationEvent Event,
        string CorrelationId,
        Guid? OrganizationId = null,
        Guid? CausationId = null,
        UserId? ActorUserId = null)
    {
        ArgumentNullException.ThrowIfNull(Event);
        ArgumentException.ThrowIfNullOrWhiteSpace(CorrelationId);

        if (!ActivityContext.TryParse(CorrelationId, traceState: null, out _))
        {
            throw new ArgumentException(
                "CorrelationId must be a W3C traceparent value.",
                nameof(CorrelationId));
        }

        if (Event.EventId == Guid.Empty)
        {
            throw new ArgumentException("An integration event requires an event id.", nameof(Event));
        }

        if (Event.TenantId == Guid.Empty)
        {
            throw new ArgumentException("An integration event requires a tenant id.", nameof(Event));
        }

        if (Event.OccurredAt == default)
        {
            throw new ArgumentException(
                "An integration event requires an occurrence timestamp.", nameof(Event));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(Event.Topic);
        ArgumentException.ThrowIfNullOrWhiteSpace(Event.PartitionKey);

        if (OrganizationId == Guid.Empty)
        {
            throw new ArgumentException(
                "OrganizationId cannot be an empty identifier.", nameof(OrganizationId));
        }

        if (Event is IOrganizationScopedIntegrationEvent && OrganizationId is null)
        {
            throw new ArgumentException(
                "An organization-scoped integration event requires OrganizationId.",
                nameof(OrganizationId));
        }

        if (CausationId == Guid.Empty)
        {
            throw new ArgumentException(
                "CausationId cannot be an empty identifier.", nameof(CausationId));
        }

        if (ActorUserId is { } actor
            && (!actor.IsInitialized() || actor.Value == Guid.Empty))
        {
            throw new ArgumentException(
                "ActorUserId cannot be an uninitialized identifier.", nameof(ActorUserId));
        }

        this.Event = Event;
        this.CorrelationId = CorrelationId;
        this.OrganizationId = OrganizationId;
        this.CausationId = CausationId;
        this.ActorUserId = ActorUserId;
    }

    /// <summary>The fact being published.</summary>
    public IIntegrationEvent Event { get; }

    /// <summary>The producer's W3C traceparent.</summary>
    public string CorrelationId { get; }

    /// <summary>The organization scope, when the event belongs to one.</summary>
    public Guid? OrganizationId { get; }

    /// <summary>The event or command that caused this delivery.</summary>
    public Guid? CausationId { get; }

    /// <summary>The causal human actor, distinct from the consumer's effective actor.</summary>
    public UserId? ActorUserId { get; }

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
