using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Infrastructure.Audit;

/// <summary>
/// The scoped buffer the audit write path composes its rows from, and the port a handler
/// designates its row's subject through.
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
/// this object is not designed for. Two requests get two instances. Nested requests share
/// this one and run one inside the other, which is what makes a stack of frames a correct
/// model of them.
/// </para>
/// <para>
/// It lives in <c>LearnStack.Infrastructure.Audit</c> rather than in the Audit module,
/// because the interceptor that fills it is attached to <b>every</b> module's
/// <c>DbContext</c> and this assembly references <c>LearnStack.SharedKernel</c> and
/// nothing else
/// (<see href="../../../docs/decisions/0044-audit-write-path.md">ADR-0044 § 11</see>).
/// </para>
/// </remarks>
public sealed class AuditStateCapture : IAuditStateCapture, IAuditSubject
{
    /// <summary>The frame anything declared or captured with no frame open belongs to.</summary>
    private const int RequestFrame = 0;

    private readonly List<CapturedEntityChange> _changes = [];

    /// <summary>The frame each entry of <see cref="_changes"/> was captured in, by index.</summary>
    private readonly List<int> _changeFrames = [];

    private readonly List<AuditIntent> _intents = [];

    /// <summary>The frame each entry of <see cref="_intents"/> was declared in, by index.</summary>
    /// <remarks>
    /// By position and not by <see cref="AuditIntent.Id"/>: the bookkeeping must not rest on
    /// an id being unique within a request, which is a property of whichever
    /// <c>IGuidFactory</c> is registered rather than of this buffer.
    /// </remarks>
    private readonly List<int> _intentFrames = [];

    /// <summary>The open frames, innermost last.</summary>
    private readonly List<int> _openFrames = [];

    private int _framesOpened;

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

    private int CurrentFrame => _openFrames.Count == 0 ? RequestFrame : _openFrames[^1];

    /// <inheritdoc />
    public void Add(CapturedEntityChange change)
    {
        ArgumentNullException.ThrowIfNull(change);

        _changes.Add(change);
        _changeFrames.Add(CurrentFrame);
    }

    /// <inheritdoc />
    public IReadOnlyList<CapturedEntityChange> ChangesOf(AuditIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);

        var position = _intents.FindIndex(declared => ReferenceEquals(declared, intent));

        if (position < 0)
        {
            throw new InvalidOperationException(
                $"The intent for {intent.Operation} is not one this capture holds — it was never "
                + "declared here, or it is a copy from before its frame closed — so there is no "
                + "request whose writes it could describe. Read it from Intents.");
        }

        var frame = _intentFrames[position];
        var own = new List<CapturedEntityChange>();

        for (var index = 0; index < _changes.Count; index++)
        {
            if (_changeFrames[index] == frame)
            {
                own.Add(_changes[index]);
            }
        }

        return own;
    }

    /// <inheritdoc />
    public void OpenFrame() => _openFrames.Add(++_framesOpened);

    /// <inheritdoc />
    public void CloseFrame(AuditIntentResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (_openFrames.Count == 0)
        {
            throw new InvalidOperationException(
                "CloseFrame was called with no frame open. Every AuditLogBehavior invocation "
                + "opens exactly one frame and closes it in its finally.");
        }

        var frame = _openFrames[^1];
        _openFrames.RemoveAt(_openFrames.Count - 1);

        for (var index = 0; index < _intents.Count; index++)
        {
            if (_intentFrames[index] == frame)
            {
                _intents[index] = _intents[index] with { Result = result };
            }
        }
    }

    /// <inheritdoc />
    public void DeclareIntent(AuditIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);

        _intents.Add(intent);
        _intentFrames.Add(CurrentFrame);

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
    public void Designate<TId>(IAggregateRoot<TId> aggregate)
        where TId : struct, IStronglyTypedId<Guid>
    {
        ArgumentNullException.ThrowIfNull(aggregate);

        // Guid's own ToString — the "D" format, invariant by definition — because that is
        // the text AuditChangeTrackerInterceptor renders a Guid-keyed root's entity_id as:
        // the key through its converter, then ToString(). The subject and the capture are
        // compared as text, so they must be spelled one way;
        // Designation_and_capture_render_one_key is the guard.
        Designate(aggregate.GetType(), aggregate.Id.Value.ToString());
    }

    /// <summary>
    /// Binds <paramref name="entityId"/> to the innermost frame's intents whose declared
    /// type <paramref name="aggregateType"/> is.
    /// </summary>
    /// <remarks>
    /// <c>IsAssignableFrom</c> rather than equality, so a runtime subtype of the declared
    /// aggregate — a proxy — designates what its base type declared.
    /// </remarks>
    public void Designate(Type aggregateType, string entityId)
    {
        ArgumentNullException.ThrowIfNull(aggregateType);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);

        var frame = CurrentFrame;

        for (var index = 0; index < _intents.Count; index++)
        {
            var intent = _intents[index];

            if (_intentFrames[index] != frame
                || intent.EntityType is null
                || !intent.EntityType.IsAssignableFrom(aggregateType))
            {
                continue;
            }

            if (intent.SubjectId is null)
            {
                _intents[index] = intent with { SubjectId = entityId };
            }
            else if (!string.Equals(intent.SubjectId, entityId, StringComparison.Ordinal))
            {
                // Not a second opinion the composer could reconcile: one row is about one
                // instance, and a handler naming two has not decided which.
                throw new InvalidOperationException(
                    $"{intent.Operation} already designated {intent.EntityType.Name} "
                    + $"{intent.SubjectId} as its subject; a request's row is about one "
                    + $"instance, and {entityId} would be a second.");
            }
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
        _changeFrames.Clear();
        _intents.Clear();
        _intentFrames.Clear();
        _openFrames.Clear();
        _framesOpened = 0;
        IndeterminateCause = null;
        State = AuditIntentState.None;
    }
}
