using System.Diagnostics;

namespace LearnStack.Application.Jobs;

/// <summary>One exact stage's issuance and physical COMMIT, bound to its sticky run.</summary>
public sealed class BackgroundJobCommitGate
{
    private const int Open = 0;
    private const int Closed = 1;
    private const int Committing = 2;
    private int _state;
    private int _started;
    private int _acknowledged;
    private int _rolledBack;
    private int _issued;
    private BackgroundJobCheckpointConfirmation? _confirmation;
    private sealed record ArmCommitWindow(long ObservedAt, TimeSpan Remaining);
    private ArmCommitWindow? _armCommitWindow;

    internal BackgroundJobCommitGate(BackgroundJobRunGate run, BackgroundJobCheckpointKind kind)
    {
        Run = run;
        Kind = kind;
    }

    public BackgroundJobRunGate Run { get; }
    public BackgroundJobCheckpointKind Kind { get; }

    public bool CommitAcknowledged => Volatile.Read(ref _acknowledged) != 0;
    public bool CommitStarted => Volatile.Read(ref _state) == Committing;
    public bool KnownRollback => Volatile.Read(ref _rolledBack) != 0;
    public bool CommitConfirmed => Volatile.Read(ref _confirmation) is not null;
    public BackgroundJobCheckpointConfirmation? Confirmation => Volatile.Read(ref _confirmation);
    public bool IsClosed => Volatile.Read(ref _state) == Closed || Run.IsClosed;
    /// <summary>Historical ACK alone cannot authorize a provider after the observed lease/deadline window.</summary>
    public bool HasLiveAcknowledgedArm => Kind == BackgroundJobCheckpointKind.Arm && CommitStarted
        && CommitAcknowledged && !KnownRollback && Volatile.Read(ref _armCommitWindow) is { } window
        && Stopwatch.GetElapsedTime(window.ObservedAt) < window.Remaining;
    public bool TryIssue() => Run.TryIssue(this);

    /// <summary>A receipt and gate can enter the business executor only once.</summary>
    public bool TryStartExecution() => TryIssue() && Interlocked.CompareExchange(ref _started, 1, 0) == 0;

    /// <summary>Revokes the unused entry atomically; a closed commit gate alone cannot prove no start.</summary>
    public bool TrySealUnusedExecution()
    {
        var unused = Interlocked.CompareExchange(ref _started, -1, 0) == 0;
        Close();
        return unused;
    }

    /// <summary>Closes memory permission only; cancellation never operates on a UoW.</summary>
    public void Close() => Run.Close();

    internal bool TryBeginCommit() => Run.TryBeginCommit(this);
    internal bool TryBeginCheckpointCommit(long observedAt, TimeSpan remaining) =>
        Run.TryBeginCheckpointCommit(this, observedAt, remaining);
    internal bool TryIssueOwned() => Volatile.Read(ref _state) == Open
        && Interlocked.CompareExchange(ref _issued, 1, 0) == 0;
    internal bool TryBeginCommitOwned() => Volatile.Read(ref _issued) == 1
        && Interlocked.CompareExchange(ref _state, Committing, Open) == Open;
    internal bool TryBeginCheckpointCommitOwned(long observedAt, TimeSpan remaining)
    {
        if (Volatile.Read(ref _issued) != 1 || Volatile.Read(ref _state) != Open) return false;
        if (Kind == BackgroundJobCheckpointKind.Arm
            && Interlocked.CompareExchange(ref _armCommitWindow, new(observedAt, remaining), null) is not null)
            return false;
        return TryBeginCommitOwned();
    }
    internal void CloseOwned() => Interlocked.CompareExchange(ref _state, Closed, Open);
    internal void RecordCommitted() => Volatile.Write(ref _acknowledged, 1);
    internal void RecordRolledBack() => Volatile.Write(ref _rolledBack, 1);
    internal void RecordConfirmation(BackgroundJobCheckpointConfirmation confirmation)
    {
        if (!ReferenceEquals(confirmation.Gate, this) || KnownRollback || !CommitStarted)
            throw new InvalidOperationException("Historical confirmation belongs to its exact admitted COMMIT boundary.");
        Interlocked.CompareExchange(ref _confirmation, confirmation, null);
    }
}
