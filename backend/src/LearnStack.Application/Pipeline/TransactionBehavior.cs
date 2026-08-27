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
/// Phase 02a Packet 3 ships the <strong>shell</strong>: it delegates to the
/// inner pipeline so the canonical eight-step order can be wired now. Packet 6
/// replaces the body without changing registration order.
/// </remarks>
/// <remarks>
/// <para>
/// The replacement opens the transaction through
/// <c>IUnitOfWork.BeginTransactionAsync</c> and sets the tenant context through
/// <c>IUnitOfWork.SetTenantContextAsync</c> — <b>not</b> through a
/// <c>DbContext.Database.BeginTransactionAsync()</c>. Per
/// <see href="../../../../docs/decisions/0040-ambient-unit-of-work.md">ADR-0040</see>
/// the unit of work owns one connection per scope and every module
/// <c>DbContext</c> enlists on it; a behavior that reached for a context would
/// have to name a module, which is the thing this seam exists to avoid, and a
/// context on its own connection never sees the <c>SET LOCAL</c> and reads zero
/// rows.
/// </para>
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

        // TODO(2026-08-27, @platform): Phase 02a Packet 6 — replace this body per
        // ADR-0040: IUnitOfWork.BeginTransactionAsync, then SetTenantContextAsync
        // as the first statement inside it, then next(); CommitAsync on a
        // success-Result, RollbackAsync on a fail-Result, and rollback + rethrow
        // on exception (preserving the rethrow AuditLogBehavior owns one frame
        // out). A nested Begin joins the ambient transaction and does not commit.

        return next();
    }
}
