using System.Data.Common;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.SharedKernel.Persistence;

/// <summary>
/// The ambient unit of work: <b>one database connection per scope</b>, and the
/// transaction on it.
/// </summary>
/// <remarks>
/// <para>
/// <see href="../../../../docs/decisions/0040-ambient-unit-of-work.md">ADR-0040</see>
/// makes the connection count a correctness property rather than a performance
/// one. <c>SET LOCAL app.tenant_id</c> is connection- <b>and</b>
/// transaction-local, so a <c>DbContext</c> that opened its own connection never
/// saw that statement, and under the corrected Row Level Security policy every
/// read through it returns <b>zero rows</b> — silently, because a policy that
/// filters everything is indistinguishable from a table with no matching data.
/// </para>
/// <para>
/// Every module <c>DbContext</c> resolved in the scope is built on this
/// connection and enlisted in this transaction; <c>IAuditStore</c> and
/// <c>IOutbox</c> reach the same connection through the same seam. There is no
/// <c>SaveChangesAsync</c> here: contexts save themselves, and the unit of work
/// owns only the transaction boundary.
/// </para>
/// <para>
/// <b>Nesting.</b> An application contract may reach a second handler through
/// <c>ISender</c>, so a second <see cref="BeginTransactionAsync"/> on a live
/// transaction is reachable. The outermost call owns the transaction and is the
/// only one whose <see cref="CommitAsync"/> commits; an inner call joins, and its
/// commit resolves its own frame and nothing else. An inner
/// <see cref="RollbackAsync"/> — or an explicit
/// <see cref="MarkRollbackOnly"/> — makes the whole unit unable to commit, so a
/// partial unit is never committed by an outer frame that did not notice.
/// </para>
/// <para>
/// <b>One command at a time.</b> One connection means the ambient transaction
/// cannot be used concurrently; a handler that fans out with
/// <c>Task.WhenAll</c> over two module contexts corrupts the protocol.
/// <c>Modules_Do_Not_Parallelize_Over_The_Ambient_Connection</c> is owed for
/// this by Phase 03, with the first module code that could break it.
/// </para>
/// </remarks>
public interface IUnitOfWork : IAsyncDisposable
{
    /// <summary>
    /// The ambient connection. Opened on first access, never before.
    /// </summary>
    /// <remarks>
    /// A long-running request holds a pooled connection for its whole life,
    /// including across an <c>await</c> on an external provider, which is why it
    /// is acquired on first use rather than at scope start. In practice
    /// <see cref="BeginTransactionAsync"/> is the first use and opens it
    /// asynchronously; reading this property before that opens it synchronously.
    /// </remarks>
    DbConnection Connection { get; }

    /// <summary>
    /// The ambient transaction; <c>null</c> before the first begin and after the
    /// terminal call.
    /// </summary>
    DbTransaction? Transaction { get; }

    /// <summary>True once a transaction has been opened and not yet resolved.</summary>
    bool HasActiveTransaction { get; }

    /// <summary>
    /// Joins the ambient transaction if one is active; otherwise opens it.
    /// </summary>
    /// <returns>
    /// A handle for the frame this call opened. Completing it resolves that
    /// frame; disposing it without completing rolls the unit back, because a
    /// frame that ended without an explicit terminal call has failed and
    /// committing it would commit work nobody claimed was finished.
    /// </returns>
    Task<IUnitOfWorkScope> BeginTransactionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Issues the Row Level Security session variables as the first statement
    /// inside the transaction.
    /// </summary>
    /// <remarks>
    /// It lives here, not in <c>TransactionBehavior</c>, because the statement is
    /// SQL and
    /// <see href="../../../../docs/standards/02-backend-coding.md">Backend Coding
    /// Standards</see> keeps SQL out of the Application layer. A no-op for a
    /// joiner: re-issuing it inside the same transaction would let an inner frame
    /// silently retarget the outer frame's tenant.
    /// </remarks>
    Task SetTenantContextAsync(ITenantContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the innermost open frame. On the outermost frame this commits —
    /// unless the unit is marked rollback-only, in which case it throws rather
    /// than committing a partial unit.
    /// </summary>
    Task CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the innermost open frame by failing it. The unit becomes
    /// rollback-only, and the outermost frame performs the actual
    /// <c>ROLLBACK</c>.
    /// </summary>
    Task RollbackAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the ambient transaction as unable to commit. Irreversible.
    /// </summary>
    /// <remarks>
    /// An inner <c>Result.Fail</c> that an outer handler deliberately absorbs is
    /// not a failure and does not mark it — only an exception, an explicit
    /// <see cref="RollbackAsync"/>, or this call does.
    /// </remarks>
    void MarkRollbackOnly();
}

/// <summary>
/// A handle for one frame of the ambient transaction.
/// </summary>
/// <remarks>
/// Returned by <see cref="IUnitOfWork.BeginTransactionAsync"/> so a caller can
/// write <c>await using</c> and have the frame resolved either way.
/// <c>TransactionBehavior</c> does not use it — it calls
/// <see cref="IUnitOfWork.CommitAsync"/> / <see cref="IUnitOfWork.RollbackAsync"/>
/// explicitly, because which one it calls depends on the <c>Result</c> the
/// handler returned.
/// </remarks>
public interface IUnitOfWorkScope : IAsyncDisposable
{
    /// <summary>
    /// <c>true</c> when this frame opened the transaction, and is therefore the
    /// one whose completion commits.
    /// </summary>
    bool IsOwner { get; }

    /// <summary>
    /// Resolves this frame successfully. On the owning frame that is the commit;
    /// on a joiner it is a no-op, per ADR-0040 § Nesting.
    /// </summary>
    Task CompleteAsync(CancellationToken cancellationToken = default);
}
