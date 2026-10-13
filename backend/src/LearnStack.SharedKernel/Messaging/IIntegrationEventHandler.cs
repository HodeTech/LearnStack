using System.Diagnostics.CodeAnalysis;

namespace LearnStack.SharedKernel.Messaging;

/// <summary>
/// Consumes one integration-event type. The <b>only</b> consumer-side contract:
/// never a bare <c>MediatR.INotificationHandler&lt;T&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// One interface, because two would mean two implementations per consumer — and
/// the one exercised in development would not be the one that runs in
/// production. Both transports resolve this same contract.
/// </para>
/// <para>
/// Delivery is at-least-once. Under
/// <see href="../../../../docs/decisions/0046-durable-event-delivery-and-recovery.md">ADR-0046</see>,
/// the transport claims the shared <c>IInboxGuard</c> before constructing the
/// handler; handlers do not call it. Business state, inbox completion and fenced
/// delivery completion share one transaction. Durable acceptance, the shared
/// <c>inbox_messages</c> table and the nonempty production proof for
/// <c>Integration_Event_Handlers_Use_InboxGuard</c> belong to P02b-3.
/// Publication now durably accepts delivery identities without constructing handlers;
/// the business execution boundary is delivered separately in P02b-3.
/// </para>
/// </remarks>
/// <remarks>
/// <para>
/// <b>Invariant on purpose.</b> Declaring <c>in TEvent</c> would promise a
/// variance the container does not honour: measured, a handler registered for a
/// base event type compiles, registers, and is never invoked, because
/// <c>GetServices</c> matches the closed generic exactly — and "no handler"
/// was not an error in the immediate-broadcast baseline, so publication could
/// report success having reached nobody. The durable registry now refuses
/// undeclared contracts and requires exact Active handler declarations.
/// A promise the runtime cannot keep is worse than no promise.
/// </para>
/// </remarks>
/// <typeparam name="TEvent">The concrete event type this handler consumes.</typeparam>
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "The name is fixed by the corpus, not chosen here: ADR-0035, "
        + "Standards 20, architecture/15 and the architecture test "
        + "Integration_Event_Handlers_Use_InboxGuard all name this contract. The "
        + "suffix warns against confusion with a CLR event handler; nothing in "
        + "LearnStack uses CLR events, and renaming would mean a cross-corpus "
        + "decision record for a spelling.")]
[SuppressMessage(
    "Naming",
    "CA1716:Identifiers should not match keywords",
    Justification = "LearnStack is C#-only per ADR-0032, and every published "
        + "consumer sketch in architecture/15 spells the parameter @event; a "
        + "different name here would put the corpus and the code out of step for "
        + "a cross-language concern that does not exist.")]
public interface IIntegrationEventHandler<TEvent>
    where TEvent : IIntegrationEvent
{
    /// <summary>Handles one delivery. May be called more than once per event.</summary>
    Task HandleAsync(TEvent @event, CancellationToken cancellationToken = default);
}
