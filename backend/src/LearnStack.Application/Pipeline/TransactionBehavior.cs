using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.Extensions.Logging;

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
/// <b>The MUST-class audit write is the last statement before <c>COMMIT</c></b>, on the
/// owning frame only — <c>IAuditStore.WritePendingAsync(unitOfWork, ct)</c>, per ADR-0033 —
/// so the state change and its record commit together or neither does. The
/// <c>IAuditStateCapture</c> transitions beside it make the commit the only place
/// durability is claimed. Packet 6 shipped the boundary with the line reserved;
/// <see href="../../../../docs/roadmap/phase-02a-kernel-tenancy.md">Packet 9</see> wrote it.
/// </para>
/// </remarks>
public sealed class TransactionBehavior<TRequest, TResponse>(
    IUnitOfWork unitOfWork,
    ITenantContext tenantContext,
    IAuditStore auditStore,
    IAuditStateCapture capture,
    ILogger<TransactionBehavior<TRequest, TResponse>> logger)
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
            // leaving it open for the scope to clean up later. For an unresolved
            // context this writes the empty string, and that is correct: the policies
            // read NULLIF(current_setting(...), '')::uuid, so an unresolved context is
            // a NULL predicate and every tenant-owned table returns zero rows.
            //
            // One exception, and it is the one write whose tenant no context can carry.
            // `tenants` is self-keyed and its policy is WITH CHECK (id = app.tenant_id),
            // so creating a tenant means announcing the tenant being created — an id
            // that names nothing resolvable, because it does not exist yet. Measured
            // against the shipped policy on a throwaway container: unset and empty-string
            // both fail 42501 — the empty-string half is the one a case pins, in
            // A_request_that_does_not_provision_still_fails_closed_when_unresolved, since
            // that is the state this pipeline can actually produce — and
            // the new tenant's own id lets the whole provisioning sequence commit.
            //
            // The !IsResolved term is the load-bearing half, not a defensive one. A
            // caller already authenticated for tenant A who sends a provisioning request
            // naming tenant B falls through to the ordinary path, the transaction is
            // announced with A, and B's insert is refused by the policy. The confused
            // deputy is closed by the database rather than by a check somebody has to
            // remember to write.
            //
            // Announced ONCE either way. A handler announcing a second time would leave
            // a window inside this transaction where app.tenant_id is the empty string —
            // every statement in it silently fail-closed — and would hand every handler
            // in the solution the ability to move the ambient tenant. This stays the
            // only caller, which is what keeps ADR-0040's setter set closed — at eight, since
            // its Amendment 7.
            //
            // WRITE-ONLY, and the asymmetry is the reason. This announces the tenant to
            // PostgreSQL; it does not touch ITenantContextAccessor, which is what the EF
            // query filters read. So inside a provisioning transaction the policies see
            // the new tenant and the filters see the all-zero one, and any EF READ here
            // returns zero rows — silently, the way a context on its own connection would.
            // Nothing reads today: ADR-0042 enumerates the operation as three writes, and
            // inserts are not filtered. A read added here needs the accessor set too,
            // which would make this a fifth writer of a member ADR-0036 Amendment 2 closes
            // at four — so it is a decision, not an edit.
            if (!tenantContext.IsResolved && request is IProvisionsTenant provisioning)
            {
                await unitOfWork.SetProvisioningTenantContextAsync(
                    provisioning.ProvisioningTenantId, cancellationToken);
            }
            else
            {
                await unitOfWork.SetTenantContextAsync(tenantContext, cancellationToken);
            }

            var response = await next();

            if (response.IsFailure)
            {
                // A business-rule failure is not an exception (ADR-0032
                // § Sub-decision 4), and it still must not commit: the handler
                // may have written before deciding it could not finish. It does
                // not mark the unit — see the class remarks.
                await scope.FailAsync(CancellationToken.None);

                // The rows go with it, and AuditLogBehavior's reconcile writes the
                // attempt standalone — a `denied` row is what makes a probe visible, and
                // it cannot ride a transaction that is being discarded.
                if (scope.IsOwner)
                {
                    capture.MarkRolledBack();
                }

                return response;
            }

            // The MUST-class write, immediately before the commit and ON THE OWNING FRAME
            // ONLY. A joiner's CompleteAsync commits nothing, so a joiner writing here
            // would insert a row and then report a durability it does not have
            // (ADR-0044 § 4). The owner flushes every intent in the scope, not only its
            // own.
            //
            // It THROWS on failure, which reaches the catch below, rolls the business
            // write back and answers 503 audit_unavailable. That is what ADR-0033 means by
            // fail-closed: the state change and the record of it commit together or
            // neither does.
            if (scope.IsOwner)
            {
                await auditStore.WritePendingAsync(unitOfWork, cancellationToken);
            }

            committing = true;

            try
            {
                await scope.CompleteAsync(cancellationToken);
            }
            catch (Exception commitFailure)
            {
                // The COMMIT faulted, so the rows' fate is genuinely unknown — the server
                // may have applied it and lost the acknowledgement. Indeterminate prefers
                // a duplicate to a loss: the reconcile writes each row again with a fresh
                // timestamp, and the 23505 that may follow is positive evidence the first
                // one landed.
                //
                // A CANCELLATION is this case, not a separate one. ADR-0032 requires the
                // OperationCanceledException to leave with its type intact, so the mark
                // happens here rather than in a catch filter AuditLogBehavior would have
                // to widen — and the exception is rethrown untouched
                // (ADR-0033 Amendment 4 § 2). Before this, a client disconnecting
                // mid-COMMIT skipped the reconcile entirely and dropped a MUST-class row
                // for an operation that may well have committed.
                if (scope.IsOwner)
                {
                    // REFUSED is not FAULTED, and the unit is the only thing that knows
                    // which. CompleteAsync throws the same way for both — but a unit an
                    // inner frame marked rollback-only is rolled back for real before it
                    // throws, so the outcome is known with certainty and nothing
                    // committed. Labelling that Indeterminate would put a permanent row on
                    // an append-only table saying the COMMIT may have landed, and would
                    // tell the reconcile that a 23505 on its re-write is positive evidence
                    // it did.
                    if (unitOfWork.IsRollbackOnly)
                    {
                        capture.MarkRolledBack();
                    }
                    else
                    {
                        capture.MarkIndeterminate(commitFailure);
                    }
                }

                throw;
            }

            if (scope.IsOwner)
            {
                // The only place durability is claimed, and only the owning frame may say
                // it: CommitAsync returned.
                capture.MarkCommitted();
            }

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
            //
            // Best-effort, for the same reason the commit sits outside this catch:
            // the cleanup must never outrank what it is cleaning up after. A
            // broken connection disposes the NpgsqlTransaction with it, so
            // FailAsync throws ObjectDisposedException — measured, by terminating
            // the backend mid-handler — and an unguarded await would hand the
            // caller, the audit intent and the error tracker that bookkeeping
            // exception with the handler's own nowhere in sight. During a database
            // failover that is every in-flight request at once. Nothing is
            // stranded by swallowing it: RollbackCoreAsync clears the transaction
            // and the depth before it throws, and DisposeAsync still closes the
            // connection in its finally.
            try
            {
                await scope.FailAsync(CancellationToken.None);
            }
            catch (Exception rollbackFailure)
            {
                LogRollbackFailure(logger, typeof(TRequest).Name, rollbackFailure);
            }

            // Rolled back, and the intents survive it: the capture lives in the DI scope
            // rather than in the transaction, which is exactly what lets the reconcile see
            // that the rows it wrote are gone. Not marked when the commit was already in
            // doubt — Indeterminate is the stronger statement and this catch runs for the
            // failures BEFORE the commit.
            if (scope.IsOwner && capture.State != AuditIntentState.Indeterminate)
            {
                capture.MarkRolledBack();
            }

            // Rethrown, not swallowed: AuditLogBehavior — three behaviors out,
            // at step 3 — catches it, audits the failure and rethrows through
            // ExceptionDispatchInfo, and the L1 IExceptionHandler turns it into
            // Problem Details.
            throw;
        }
    }

    // LoggerMessage source-generated delegate (CA1848), matching the house style
    // in AuditLogBehavior and LoggingBehavior.
    private static readonly Action<ILogger, string, Exception?> LogRollbackFailure =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(1, nameof(LogRollbackFailure)),
            "Rolling back the ambient transaction for {RequestName} failed. The original exception is rethrown; this one is recorded here only.");
}
