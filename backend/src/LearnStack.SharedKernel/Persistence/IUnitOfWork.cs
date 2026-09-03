using System.Data.Common;
using LearnStack.SharedKernel.Identifiers;
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
/// only one whose terminal call touches the database; an inner call joins, and
/// its commit or rollback resolves its own frame and nothing else.
/// </para>
/// <para>
/// What escalates a frame's failure to the whole unit is
/// <see cref="MarkRollbackOnly"/>, and only two things call it: an exception —
/// <c>TransactionBehavior</c> marks the unit before rolling back on one — and a
/// caller doing so deliberately. An inner <c>Result.Fail</c> that an outer
/// handler absorbs is not one of them, per ADR-0040 § Nesting: the outer handler
/// took responsibility for it, and its own work still commits.
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
    /// A handle for the frame this call opened. Resolving it through the handle
    /// rather than through <see cref="CommitAsync"/> is what makes a leaked inner
    /// frame loud: the handle knows its own depth and refuses to resolve while a
    /// frame opened after it is still open. Disposing it unresolved rolls the unit
    /// back, because a frame that ended without a terminal call has failed and
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
    /// Whether a sanctioned setter has announced the tenant on
    /// <paramref name="transaction"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read by <c>TenantContextGuardInterceptor</c>, which refuses any command a module
    /// <c>DbContext</c> issues on an unannounced transaction. Without it the failure is
    /// an empty result set — safe, because the policy predicate is <c>NULL</c>, and
    /// silent, which is the outage.
    /// </para>
    /// <para>
    /// <b>It takes the command's transaction rather than returning a bare flag</b>, and
    /// the reference check against this unit's own live transaction is the load-bearing
    /// half. Measured on Npgsql 10: a pooled data source hands back the <i>same</i>
    /// <c>NpgsqlTransaction</c> instance across sequential open/begin/dispose cycles, so
    /// anything keyed on the transaction object would vouch for a later transaction on
    /// the strength of an earlier one's announcement.
    /// </para>
    /// <para>
    /// There is deliberately no writer on this interface. The only thing that may mark a
    /// transaction is the code that issues the <c>set_config</c> pair, and it does so
    /// after the round trip returns — a failed announcement leaves the transaction
    /// unmarked. A module that could set the flag could silence the guard.
    /// </para>
    /// </remarks>
    bool IsTenantContextIssuedOn(DbTransaction? transaction);

    /// <summary>
    /// Announces the tenant a provisioning request is about to create.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one write whose tenant no context can supply.</b> <c>tenants</c> is
    /// self-keyed and its policy is <c>WITH CHECK (id = app.tenant_id)</c>, so creating a
    /// tenant requires announcing the tenant being created. No resolved
    /// <c>ITenantContext</c> can carry that id — it names a tenant that does not exist —
    /// and the empty string an unresolved context writes fails the check identically to
    /// announcing nothing: measured, 42501 both ways.
    /// </para>
    /// <para>
    /// <b>It does not widen the setter set.</b>
    /// <see href="../../../../docs/decisions/0040-ambient-unit-of-work.md">ADR-0040
    /// Amendment 3</see> closes that set at seven, and <c>TransactionBehavior</c> remains
    /// the only caller of this as it is of its sibling — this is the same setter
    /// announcing a different value for one request shape, not an eighth.
    /// </para>
    /// <para>
    /// <b>Every way of misusing it throws rather than degrading.</b> No open transaction,
    /// a joiner (<i>not</i> the silent early return the ambient setter uses — a joiner
    /// that thought it announced would fail three frames away with 42501), a transaction
    /// already announced, or an id that is uninitialized or all-zero. The
    /// already-announced guard is what keeps the only reachable transition
    /// unannounced → the new tenant: nothing can retarget a live transaction.
    /// </para>
    /// </remarks>
    Task SetProvisioningTenantContextAsync(
        TenantId tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the innermost open frame. On the outermost frame this commits —
    /// unless the unit is marked rollback-only, in which case it throws rather
    /// than committing a partial unit.
    /// </summary>
    /// <remarks>
    /// Frame-blind: it resolves whatever frame is innermost, so a caller that
    /// leaked one silently downgrades its own commit to a no-op.
    /// <see cref="IUnitOfWorkScope.CompleteAsync"/> is the guarded form and is
    /// what <c>TransactionBehavior</c> uses; this exists for a caller that has no
    /// handle to hand.
    /// </remarks>
    Task CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the innermost open frame by failing it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A joiner's failure does not poison the unit.</b> ADR-0040 § Nesting:
    /// "an inner <c>Result.Fail</c> that the outer handler deliberately absorbs
    /// is <i>not</i> a failure and does not mark it — only an exception, or an
    /// explicit <see cref="MarkRollbackOnly"/>, does." So this resolves the
    /// caller's frame and, on the outermost one, performs the actual
    /// <c>ROLLBACK</c>; escalating to the whole unit is
    /// <see cref="MarkRollbackOnly"/>'s job, and the exception path calls it.
    /// </para>
    /// <para>
    /// <b>It is cleanup, so it never throws over the thing it is cleaning up
    /// after.</b> On a unit with nothing left to resolve — the state a faulted
    /// <see cref="CommitAsync"/> leaves behind — this is a no-op. The alternative
    /// was measured: it replaced every commit-time exception with
    /// "no transaction frame is open", including the
    /// <c>OperationCanceledException</c> that three separate ADR-0032 behaviours
    /// key on.
    /// </para>
    /// </remarks>
    Task RollbackAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the unit as unable to commit. Irreversible, for the life of the unit
    /// of work — not just of the current transaction.
    /// </summary>
    /// <remarks>
    /// An inner <c>Result.Fail</c> that an outer handler deliberately absorbs is
    /// not a failure and does not mark it; an exception is, and
    /// <c>TransactionBehavior</c> calls this before rolling back on one. Once
    /// marked, <see cref="CommitAsync"/> throws and
    /// <see cref="BeginTransactionAsync"/> refuses to open a new transaction on
    /// the same unit — a scope that needs a fresh one takes a fresh scope, which
    /// is the model.
    /// </remarks>
    void MarkRollbackOnly();
}

/// <summary>
/// A handle for one frame of the ambient transaction.
/// </summary>
/// <remarks>
/// <para>
/// Returned by <see cref="IUnitOfWork.BeginTransactionAsync"/>. It carries the
/// depth of the frame it opened, which is the whole reason to prefer it over the
/// frame-blind <see cref="IUnitOfWork.CommitAsync"/>: a caller that resolves
/// through the handle cannot silently resolve someone else's frame, and a leaked
/// inner frame becomes an exception at the outer frame's terminal call rather
/// than a success that wrote nothing.
/// </para>
/// <para>
/// Two terminal calls rather than one, because which one a caller makes depends
/// on the outcome it is reporting — <c>TransactionBehavior</c> chooses by the
/// <c>Result</c> the handler returned.
/// </para>
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
    /// <exception cref="InvalidOperationException">
    /// A frame opened after this one is still open. Resolving out of order would
    /// commit nothing and report success.
    /// </exception>
    Task CompleteAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves this frame by failing it. On the owning frame that is the
    /// <c>ROLLBACK</c>; on a joiner it declines this frame without making the
    /// unit unable to commit, which is ADR-0040 § Nesting's rule about an
    /// absorbed inner failure.
    /// </summary>
    Task FailAsync(CancellationToken cancellationToken = default);
}
