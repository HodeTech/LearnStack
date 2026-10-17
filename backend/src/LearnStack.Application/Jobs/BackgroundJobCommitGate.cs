namespace LearnStack.Application.Jobs;

/// <summary>One attempt's sticky cancellation versus owned COMMIT arbitration.</summary>
public sealed class BackgroundJobCommitGate
{
    private const int Open = 0;
    private const int Closed = 1;
    private const int Committing = 2;
    private int _state;
    private int _started;
    private int _acknowledged;
    private int _rolledBack;

    public bool CommitAcknowledged => Volatile.Read(ref _acknowledged) != 0;
    public bool CommitStarted => Volatile.Read(ref _state) == Committing;
    public bool KnownRollback => Volatile.Read(ref _rolledBack) != 0;
    public bool IsClosed => Volatile.Read(ref _state) == Closed;

    /// <summary>A receipt and gate can enter the business executor only once.</summary>
    public bool TryStartExecution() => Interlocked.CompareExchange(ref _started, 1, 0) == 0;

    /// <summary>Revokes the unused entry atomically; a closed commit gate alone cannot prove no start.</summary>
    public bool TrySealUnusedExecution()
    {
        var unused = Interlocked.CompareExchange(ref _started, -1, 0) == 0;
        Close();
        return unused;
    }

    /// <summary>Closes memory permission only; cancellation never operates on a UoW.</summary>
    public void Close() => Interlocked.CompareExchange(ref _state, Closed, Open);

    internal bool TryBeginCommit() => Interlocked.CompareExchange(ref _state, Committing, Open) == Open;
    internal void RecordCommitted() => Volatile.Write(ref _acknowledged, 1);
    internal void RecordRolledBack() => Volatile.Write(ref _rolledBack, 1);
}
