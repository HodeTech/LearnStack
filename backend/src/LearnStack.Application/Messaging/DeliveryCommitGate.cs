namespace LearnStack.Application.Messaging;

/// <summary>Sticky arbitration between delivery cancellation and physical commit admission.</summary>
public sealed class DeliveryCommitGate
{
    private const int Open = 0;
    private const int Closed = 1;
    private const int Committing = 2;
    private int _state;
    private int _commitAcknowledged;
    private int _executionStarted;

    /// <summary>Definitive durability, independent of later owned resource cleanup.</summary>
    public bool CommitAcknowledged => Volatile.Read(ref _commitAcknowledged) != 0;

    /// <summary>Admission has won; an unacknowledged commit may require durable reconciliation.</summary>
    public bool CommitStarted => Volatile.Read(ref _state) == Committing;

    /// <summary>Cancellation won admission; a foreign cancellation exception alone does not close it.</summary>
    public bool IsClosed => Volatile.Read(ref _state) == Closed;

    /// <summary>Binds this receipt to one executor invocation, including attempts canceled before entry.</summary>
    public bool TryStartExecution() => Interlocked.CompareExchange(ref _executionStarted, 1, 0) == 0;

    /// <summary>Cancellation closes an open gate; it never operates on the transaction.</summary>
    public void Close() => Interlocked.CompareExchange(ref _state, Closed, Open);

    internal bool TryBeginCommit() => Interlocked.CompareExchange(ref _state, Committing, Open) == Open;

    internal void RecordCommitted() => Volatile.Write(ref _commitAcknowledged, 1);
}
