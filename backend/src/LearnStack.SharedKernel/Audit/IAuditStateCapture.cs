namespace LearnStack.SharedKernel.Audit;

/// <summary>
/// The scoped buffer the audit write path composes its rows from: the entity snapshots
/// the interceptor took, the intents the behavior declared, and the one signal that
/// says whether they became durable.
/// </summary>
/// <remarks>
/// <para>
/// Registered <b>scoped</b> — one per request — and cleared once, by the outermost
/// behavior, in its <c>finally</c>. A joiner frame must not clear: it would erase the
/// outer request's intents and every snapshot before the owner had committed
/// (<see href="../../../../docs/decisions/0033-audit-durability-model.md">ADR-0033
/// Amendment 2 § 2</see>).
/// </para>
/// <para>
/// Because the lifetime is the DI scope and not the database transaction, a rollback
/// leaves every field of this object intact. That is exactly why
/// <see cref="State"/> must be <em>set</em> by the component that owns the commit and
/// never inferred from what is in here.
/// </para>
/// <para>
/// <b>Frames.</b> Every <c>AuditLogBehavior</c> invocation — outermost or nested — opens a
/// frame before it declares and closes it on its way out. An intent belongs to the frame
/// that declared it, and a captured change to the frame that was innermost when the flush
/// ran, which is the frame whose handler issued the write. That is what lets a nested
/// request's row describe its own writes and not its parent's, and what lets each intent
/// keep the result of its own request
/// (<see href="../../../../docs/decisions/0044-audit-write-path.md">ADR-0044 Amendment 6
/// §§ 1, 3</see>). Anything declared or captured with no frame open belongs to one
/// request-level frame, so a caller that never opens one sees a single frame.
/// </para>
/// <para>
/// A SharedKernel abstraction: it names no EF Core type, because the interceptor that
/// fills it lives on the other side of a boundary the module assemblies may not cross.
/// </para>
/// </remarks>
public interface IAuditStateCapture
{
    /// <summary>Every entity change captured so far, across every flush in the request.</summary>
    IReadOnlyList<CapturedEntityChange> Changes { get; }

    /// <summary>Records one captured change. Called by the interceptor, never by a handler.</summary>
    void Add(CapturedEntityChange change);

    /// <summary>
    /// The intents declared for this request, in declaration order — one per audited
    /// <c>(resource, operation)</c>.
    /// </summary>
    IReadOnlyList<AuditIntent> Intents { get; }

    /// <summary>
    /// Where those intents stand. One state for the request, because they share one
    /// transaction and therefore one fate.
    /// </summary>
    AuditIntentState State { get; }

    /// <summary>
    /// The changes captured in the frame that declared <paramref name="intent"/> — the
    /// ones its own request wrote. What both writers compose a row from.
    /// </summary>
    /// <exception cref="InvalidOperationException">The intent was not declared here.</exception>
    IReadOnlyList<CapturedEntityChange> ChangesOf(AuditIntent intent);

    /// <summary>Opens a frame. <c>AuditLogBehavior</c>, before it declares.</summary>
    void OpenFrame();

    /// <summary>
    /// Closes the innermost frame and records <paramref name="result"/> on every intent it
    /// declared. <c>AuditLogBehavior</c>, in its <c>finally</c>, on every way out.
    /// </summary>
    /// <exception cref="InvalidOperationException">No frame is open.</exception>
    void CloseFrame(AuditIntentResult result);

    /// <summary>Declares an intent in the innermost frame. <c>AuditLogBehavior</c>, step 3.</summary>
    void DeclareIntent(AuditIntent intent);

    /// <summary>
    /// The rows are on the ambient transaction. <b>Not</b> durable — the transaction has
    /// not committed. <c>IAuditStore.WritePendingAsync</c>.
    /// </summary>
    void MarkWrittenInTransaction();

    /// <summary>
    /// <c>CommitAsync</c> returned. The only place durability is claimed, and only the
    /// owning frame may say it.
    /// </summary>
    void MarkCommitted();

    /// <summary>The transaction rolled back. Every row goes with it.</summary>
    void MarkRolledBack();

    /// <summary><c>COMMIT</c> faulted; the server-side outcome is unknown.</summary>
    void MarkIndeterminate(Exception cause);

    /// <summary>Empties the buffer. Once per request, by the outermost frame.</summary>
    void Clear();
}
