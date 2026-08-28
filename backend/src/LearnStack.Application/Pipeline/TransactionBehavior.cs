using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;

namespace LearnStack.Application.Pipeline;

/// <summary>
/// MediatR pipeline behavior — step 6 of the canonical 8-step order
/// (ADR-0032 § Sub-decision 2). Opens the ambient transaction, issues the Row
/// Level Security session variables as its first statement, then commits on a
/// success-<c>Result</c> and rolls back on a fail-<c>Result</c> or any exception
/// that bubbles through.
/// </summary>
/// <remarks>
/// <para>
/// <b>Through <c>IUnitOfWork</c>, never a <c>DbContext</c>.</b> Per
/// <see href="../../../../docs/decisions/0040-ambient-unit-of-work.md">ADR-0040</see>
/// the unit of work owns one connection per scope and every module
/// <c>DbContext</c> enlists on it. A behavior that reached for a context would
/// have to name a module — the thing this seam exists to avoid — and a context
/// on its own connection never sees the <c>SET LOCAL</c> and reads zero rows.
/// <c>TransactionBehavior_Does_Not_Reference_A_Module_Assembly</c> is the guard.
/// </para>
/// <para>
/// <b>No gate.</b> Everything reaching step 6 needs a transaction, because the
/// requests that must not open one have already short-circuited: a validation
/// failure at step 1, an unresolved tenant at step 4, an authorization denial at
/// step 5. A <c>RequiresTransaction(request)</c> predicate would be a fourth
/// exemption defined nowhere.
/// </para>
/// <para>
/// <b>The commit is outside the catch, deliberately.</b> The filter
/// <c>when (!committing)</c> is what stops the cleanup path from running after a
/// faulted <c>COMMIT</c>. Two things go wrong without it, both measured: the
/// rollback's own complaint replaces the database's exception, so a constraint
/// violation at commit time reaches the client as a bookkeeping error with no
/// inner exception; and because the replacement is not an
/// <c>OperationCanceledException</c>, a client that disconnects mid-commit is
/// audited as a failure, captured by <c>IErrorTrackingProvider</c> and answered
/// <c>500</c> instead of <c>499</c> — three ADR-0032 behaviours inverted at once.
/// A faulted commit also leaves the outcome genuinely unknown, which is
/// <see href="../../../../docs/decisions/0033-audit-durability-model.md">ADR-0033</see>'s
/// <c>Indeterminate</c>, not a failure to roll back.
/// </para>
/// <para>
/// <b>The exception path marks the unit; the fail-<c>Result</c> path does not.</b>
/// ADR-0040 § Nesting: an inner <c>Result.Fail</c> that an outer handler
/// deliberately absorbs is not a failure of the unit — the outer handler took
/// responsibility, and its own work still commits. An exception is, so it calls
/// <c>MarkRollbackOnly</c> before failing its frame.
/// </para>
/// <para>
/// <b>What is not here yet.</b> The MUST-class audit write —
/// <c>IAuditStore.WritePendingAsync(unitOfWork, ct)</c> immediately before
/// <c>COMMIT</c>, per ADR-0033 — belongs on the marked line and lands with
/// <c>IAuditStore</c> in
/// <see href="../../../../docs/roadmap/phase-02a-kernel-tenancy.md">Packet 9</see>,
/// together with the <c>IAuditStateCapture</c> transitions that make the commit
/// the only place durability is claimed. The commit boundary is here now so that
/// the write has somewhere to go.
/// </para>
/// </remarks>
public sealed class TransactionBehavior<TRequest, TResponse>(
    IUnitOfWork unitOfWork,
    ITenantContext tenantContext)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : IResultBase
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        // Through the handle, not the frame-blind CommitAsync: the handle knows
        // its own depth, so a nested frame nobody resolved is an exception here
        // rather than a commit that quietly resolves the wrong frame, writes
        // nothing, and returns success.
        await using var scope = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var committing = false;

        try
        {
            // First statement inside the transaction, per ADR-0003 Amendment 3, and
            // inside the try so a failure to issue it fails the frame rather than
            // leaving it open for the scope to clean up later. Until Packet 7's
            // TenantResolverMiddleware populates ITenantContext this writes the
            // empty string, and that is correct: the policies read
            // NULLIF(current_setting(...), '')::uuid, so an unresolved context is a
            // NULL predicate and every tenant-owned table returns zero rows.
            await unitOfWork.SetTenantContextAsync(tenantContext, cancellationToken);

            var response = await next();

            if (response.IsFailure)
            {
                // A business-rule failure is not an exception (ADR-0032
                // § Sub-decision 4), and it still must not commit: the handler
                // may have written before deciding it could not finish. It does
                // not mark the unit — see the class remarks.
                await scope.FailAsync(CancellationToken.None);
                return response;
            }

            // TODO(2026-08-28, @platform, phase-02a-packet-9): the MUST-class
            // audit write goes here, immediately before the commit —
            // await auditStore.WritePendingAsync(unitOfWork, cancellationToken);
            // It throws on failure, which reaches the catch below and rolls the
            // business write back, which is what ADR-0033 means by fail-closed.
            committing = true;
            await scope.CompleteAsync(cancellationToken);
            return response;
        }
        catch when (!committing)
        {
            // An exception marks the unit, so an outer frame that absorbs it
            // cannot commit a partial one (ADR-0040 § Nesting).
            unitOfWork.MarkRollbackOnly();

            // CancellationToken.None: the rollback is the cleanup path, and a
            // cancelled rollback leaves the transaction open on a connection
            // about to go back to the pool.
            await scope.FailAsync(CancellationToken.None);

            // Rethrown, not swallowed: AuditLogBehavior — three behaviors out,
            // at step 3 — catches it, audits the failure and rethrows through
            // ExceptionDispatchInfo, and the L1 IExceptionHandler turns it into
            // Problem Details.
            throw;
        }
    }
}
