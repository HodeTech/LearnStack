using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Application.Pipeline;

/// <summary>
/// MediatR pipeline behavior — step 6 of the canonical 8-step order
/// (ADR-0032 § Sub-decision 2). Opens a unit-of-work transaction; commits on
/// a success-<c>Result</c> and rolls back on a fail-<c>Result</c> or any
/// exception that bubbles through. Validation- and authorization-failed
/// requests short-circuit upstream and never open a transaction.
/// </summary>
/// <remarks>
/// Phase 02a Packet 3 ships the <strong>shell</strong>: there is no
/// per-module <c>DbContext</c> yet (those land starting in Packet 6 +
/// Phase 03). The shell just delegates to the inner pipeline so the
/// canonical eight-step order can be wired now; Packet 6 swaps the body for
/// the real <c>DbContext.Database.BeginTransactionAsync()</c> +
/// commit-on-success-Result / rollback-on-failure pattern without changing
/// registration order.
/// </remarks>
public sealed class TransactionBehavior<TRequest, TResponse>
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

        // TODO(2026-05-21, @platform): Phase 02a Packet 6 — open the UoW
        // transaction (per-module DbContext.Database.BeginTransactionAsync),
        // commit on success-Result, rollback on fail-Result, and rollback +
        // rethrow on exception (preserving the rethrow that AuditLogBehavior
        // owns one frame out).

        return next();
    }
}
