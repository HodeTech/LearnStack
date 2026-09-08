using LearnStack.SharedKernel.Audit;

namespace LearnStack.Infrastructure.Audit;

/// <summary>
/// The scoped buffer the audit write path composes its rows from.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scoped, and the lifetime is the request rather than the transaction.</b> That is
/// what makes <see cref="State"/> a value someone <em>sets</em> and never one this type
/// infers: a rollback leaves every field here intact, so a buffer that guessed its own
/// state from what it held would report durability for rows the database discarded
/// (<see href="../../../docs/decisions/0033-audit-durability-model.md">ADR-0033
/// Amendment 2 § 2</see>).
/// </para>
/// <para>
/// <b>Not thread-safe, deliberately.</b> One DI scope is one request and one logical
/// thread of execution; a lock here would buy nothing and would suggest a concurrency
/// this object is not designed for. Two requests get two instances.
/// </para>
/// <para>
/// It lives in <c>LearnStack.Infrastructure.Audit</c> rather than in the Audit module,
/// because the interceptor that fills it is attached to <b>every</b> module's
/// <c>DbContext</c> and this assembly references <c>LearnStack.SharedKernel</c> and
/// nothing else
/// (<see href="../../../docs/decisions/0044-audit-write-path.md">ADR-0044 § 11</see>).
/// </para>
/// </remarks>
public sealed class AuditStateCapture : IAuditStateCapture
{
    private readonly List<CapturedEntityChange> _changes = [];
    private readonly List<AuditIntent> _intents = [];

    /// <inheritdoc />
    public IReadOnlyList<CapturedEntityChange> Changes => _changes;

    /// <inheritdoc />
    public IReadOnlyList<AuditIntent> Intents => _intents;

    /// <inheritdoc />
    public AuditIntentState State { get; private set; } = AuditIntentState.None;

    /// <summary>
    /// What faulted the <c>COMMIT</c>, when <see cref="State"/> is
    /// <see cref="AuditIntentState.Indeterminate"/>.
    /// </summary>
    /// <remarks>
    /// Kept so the reconcile step can log the cause beside the row it writes. Not on the
    /// interface: nothing in the write path branches on it, and a port that exposed it
    /// would invite one to.
    /// </remarks>
    public Exception? IndeterminateCause { get; private set; }

    /// <inheritdoc />
    public void Add(CapturedEntityChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        _changes.Add(change);
    }

    /// <inheritdoc />
    public void DeclareIntent(AuditIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);

        _intents.Add(intent);

        // Only out of None. Intents are plural and a joiner declares its own after the
        // owner has already written — moving the state back to Pending there would tell
        // the reconcile step nothing had been written, and it would write the owner's
        // rows a second time.
        if (State == AuditIntentState.None)
        {
            State = AuditIntentState.Pending;
        }
    }

    /// <inheritdoc />
    public void MarkWrittenInTransaction() => State = AuditIntentState.WrittenInTransaction;

    /// <inheritdoc />
    public void MarkCommitted() => State = AuditIntentState.Committed;

    /// <inheritdoc />
    public void MarkRolledBack() => State = AuditIntentState.RolledBack;

    /// <inheritdoc />
    public void MarkIndeterminate(Exception cause)
    {
        ArgumentNullException.ThrowIfNull(cause);

        IndeterminateCause = cause;
        State = AuditIntentState.Indeterminate;
    }

    /// <inheritdoc />
    public void Clear()
    {
        _changes.Clear();
        _intents.Clear();
        IndeterminateCause = null;
        State = AuditIntentState.None;
    }
}
