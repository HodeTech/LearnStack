using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Application.Pipeline;

/// <summary>
/// MediatR pipeline behavior — step 7 of the canonical 8-step order
/// (ADR-0032 § Sub-decision 2). Enrols <c>IOutbox</c> messages in the
/// current unit-of-work transaction; the outbox processor publishes them
/// via <c>IEventBus</c> on commit (see
/// <see href="../../../../docs/architecture/15-event-and-outbox.md">15-event-and-outbox.md</see>).
/// </summary>
/// <remarks>
/// Phase 02a Packet 3 ships the <strong>shell</strong>: there is no
/// <c>IOutbox</c> contract yet (it lands in Phase 02b). The shell delegates
/// to the inner pipeline so the order is correct now; Phase 02b lights up
/// the enrolment without changing the eight-step registration.
/// </remarks>
public sealed class OutboxFlushBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : IResultBase
{
    public Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        // TODO(2026-05-21, @platform): Phase 02b — on a success-Result, flush
        // IOutbox messages collected during the handler into outbox_messages
        // via the unit-of-work seam so Dapr pub/sub dispatches them after
        // commit. Per ADR-0006 + ADR-0038 + ADR-0032 § Sub-decision 12.

        return next();
    }
}
