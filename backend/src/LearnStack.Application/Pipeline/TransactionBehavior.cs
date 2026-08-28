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
/// <b>What is not here yet.</b> The MUST-class audit write —
/// <c>IAuditStore.WritePendingAsync(unitOfWork, ct)</c> immediately before
/// <c>COMMIT</c>, per
/// <see href="../../../../docs/decisions/0033-audit-durability-model.md">ADR-0033</see>
/// — belongs on this exact line and lands with <c>IAuditStore</c> in
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

        await unitOfWork.BeginTransactionAsync(cancellationToken);

        // First statement inside the transaction, per ADR-0003 Amendment 3. Until
        // Packet 7's TenantResolverMiddleware populates ITenantContext this writes
        // the empty string, and that is correct: the policies read
        // NULLIF(current_setting(...), '')::uuid, so an unresolved context is a
        // NULL predicate and every tenant-owned table returns zero rows.
        await unitOfWork.SetTenantContextAsync(tenantContext, cancellationToken);

        try
        {
            var response = await next();

            if (response.IsFailure)
            {
                // A business-rule failure is not an exception (ADR-0032
                // § Sub-decision 4), and it still must not commit: the handler
                // may have written before deciding it could not finish.
                await unitOfWork.RollbackAsync(CancellationToken.None);
                return response;
            }

            // TODO(2026-08-28, @platform, phase-02a-packet-9): the MUST-class
            // audit write goes here, immediately before the commit —
            // await auditStore.WritePendingAsync(unitOfWork, cancellationToken);
            // It throws on failure, which reaches the catch below and rolls the
            // business write back, which is what ADR-0033 means by fail-closed.
            await unitOfWork.CommitAsync(cancellationToken);
            return response;
        }
        catch
        {
            // CancellationToken.None: the rollback is the cleanup path, and a
            // cancelled rollback leaves the transaction open on a connection
            // about to go back to the pool.
            await unitOfWork.RollbackAsync(CancellationToken.None);

            // Rethrown, not swallowed: AuditLogBehavior one frame out catches it,
            // audits the failure and rethrows through ExceptionDispatchInfo, and
            // the L1 IExceptionHandler turns it into Problem Details.
            throw;
        }
    }
}
