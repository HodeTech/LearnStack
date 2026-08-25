using System.Diagnostics.CodeAnalysis;

namespace LearnStack.SharedKernel.Messaging;

/// <summary>
/// Publishes an integration event to whichever transport is registered, per
/// <see href="../../../../docs/decisions/0014-adopt-dapr.md">ADR-0014</see> and
/// its Amendment 2. Modules never inject a broker client.
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
/// The partition key is a parameter rather than being read off the event because
/// the producer resolves it once, at enqueue time, and writes it to the outbox
/// row; the processor passes back what it stored. Nothing downstream re-derives
/// it, so the ordering domain cannot drift between enqueue and publish.
/// </para>
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1716:Identifiers should not match keywords",
    Justification = "LearnStack is C#-only per ADR-0032, and architecture/15 "
        + "publishes this signature with the parameter spelled @event; renaming "
        + "would put the corpus and the code out of step for a cross-language "
        + "concern that does not exist.")]
public interface IEventBus
{
    /// <summary>Publishes one event, ordered against others sharing its partition key.</summary>
    Task PublishAsync(
        IIntegrationEvent @event,
        string partitionKey,
        CancellationToken cancellationToken = default);
}
