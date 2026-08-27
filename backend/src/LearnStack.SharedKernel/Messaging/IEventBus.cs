namespace LearnStack.SharedKernel.Messaging;

/// <summary>
/// Publishes an integration event to whichever transport is registered, per
/// <see href="../../../../docs/decisions/0038-cross-cutting-port-and-event-contracts.md">ADR-0038</see>.
/// Modules never inject a broker client.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not generic</b>, and that was a correction rather than a preference. The
/// outbox processor deserialises a stored payload to <c>object</c> and publishes
/// through this interface, so a generic parameter would bind to
/// <see cref="IIntegrationEvent"/> at the only call site that matters — and a
/// transport resolving <c>IIntegrationEventHandler&lt;TEvent&gt;</c> would then
/// look for a handler of <c>IIntegrationEvent</c>, which no concrete consumer
/// implements. The publish would reach zero handlers and report success. Both
/// transports resolve by the event's <b>runtime</b> type instead.
/// </para>
/// <para>
/// The envelope carries the dispatch metadata the outbox row holds and the event
/// does not — topic, correlation, organization, causation, actor — and reads the
/// partition key off the event, so the ordering domain has exactly one source.
/// </para>
/// </remarks>
public interface IEventBus
{
    /// <summary>Publishes one envelope, ordered against others sharing its partition key.</summary>
    Task PublishAsync(
        IntegrationEventEnvelope envelope,
        CancellationToken cancellationToken = default);
}
