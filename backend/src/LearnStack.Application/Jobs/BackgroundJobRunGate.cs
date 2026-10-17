using System.Diagnostics;

namespace LearnStack.Application.Jobs;

/// <summary>The finite short transaction boundaries within an owned job invocation.</summary>
public enum BackgroundJobCheckpointKind
{
    Admission,
    Transactional,
    Prepare,
    Arm,
    Outcome,
    NoStart,
    Finalize,
    TransactionalCleanup,
    RecipientSubmission,
}

/// <summary>
/// Sticky memory permission for one invocation. Stage acknowledgements describe
/// history; they never reopen stopped work or substitute for actual resource cleanup.
/// </summary>
public sealed class BackgroundJobRunGate
{
    private readonly object _sync = new();
    private readonly RuntimeStop _runtime;
    private BackgroundJobCommitGate? _current;
    private bool _stopped;
    private int _externalEntry;
    private BackgroundJobExternalRecoveryBinding? _recovery;

    public BackgroundJobRunGate(RuntimeStop runtime) =>
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    public bool IsClosed { get { lock (_sync) return _stopped || !_runtime.IsOpen; } }

    public void Close()
    {
        lock (_sync)
        {
            _stopped = true;
            _current?.CloseOwned();
        }
    }

    /// <summary>Creates a fresh boundary; no uncertain predecessor is silently acknowledged.</summary>
    public BackgroundJobCommitGate OpenBoundary(BackgroundJobCheckpointKind kind)
        => TryOpenBoundary(kind) ?? throw new InvalidOperationException("The job run cannot open this checkpoint boundary.");

    public BackgroundJobCommitGate? TryOpenBoundary(BackgroundJobCheckpointKind kind)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        lock (_sync)
        {
            if (_stopped || !_runtime.TryAdmit()
                || _recovery is not null && kind is not (BackgroundJobCheckpointKind.Outcome or BackgroundJobCheckpointKind.NoStart)
                || _recovery?.Receipt.Purpose == BackgroundJobExternalRecoveryPurpose.TerminalNoStart
                    && kind != BackgroundJobCheckpointKind.NoStart
                || _current is { } previous && !previous.CommitAcknowledged && !previous.CommitConfirmed && !previous.KnownRollback)
                return null;
            return _current = new(this, kind);
        }
    }

    internal bool TryIssue(BackgroundJobCommitGate boundary)
    {
        lock (_sync)
            return ReferenceEquals(_current, boundary) && !_stopped && _runtime.TryAdmit()
                && boundary.TryIssueOwned();
    }

    internal bool TryBeginCommit(BackgroundJobCommitGate boundary)
    {
        lock (_sync)
            return ReferenceEquals(_current, boundary) && !_stopped && _runtime.TryAdmit()
                && boundary.TryBeginCommitOwned();
    }

    internal bool TryBeginCheckpointCommit(BackgroundJobCommitGate boundary, long observedAt, TimeSpan remaining)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_current, boundary) || _stopped || !_runtime.TryAdmit()) return false;
            // Check inside the same lock as the COMMIT CAS: waiting for that
            // lock cannot borrow a formerly live lease observation.
            if (remaining <= TimeSpan.Zero || Stopwatch.GetElapsedTime(observedAt) >= remaining)
            {
                _stopped = true;
                boundary.CloseOwned();
                return false;
            }
            return boundary.TryBeginCheckpointCommitOwned(observedAt, remaining);
        }
    }

    public bool TryIssueExternalExecute(BackgroundJobCommitGate arm)
    {
        ArgumentNullException.ThrowIfNull(arm);
        lock (_sync)
        {
            if (_stopped || _recovery is not null || _externalEntry != 0 || !_runtime.TryAdmit()
                || !ReferenceEquals(_current, arm) || !ReferenceEquals(arm.Run, this)
                || arm.Kind != BackgroundJobCheckpointKind.Arm
                || !arm.HasLiveAcknowledgedArm && arm.Confirmation?.AllowsOriginalExecute(arm) != true || arm.KnownRollback)
                return false;
            _externalEntry = 1;
            return true;
        }
    }

    public bool TrySealUnusedExternalExecute()
    {
        lock (_sync)
        {
            if (_externalEntry != 0) return false;
            _externalEntry = -1;
            return true;
        }
    }

    internal void BindExternalRecovery(BackgroundJobExternalRecoveryBinding binding, RuntimeStop runtime)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(runtime, _runtime) || _stopped || !_runtime.TryAdmit()
                || _current is not null || _recovery is not null || _externalEntry != 0)
                throw new InvalidOperationException("Recovery requires a fresh run on its original process boundary.");
            if (!binding.HasLiveLeaseWindow)
            {
                _stopped = true;
                throw new InvalidOperationException("The observed recovery lease has expired before binding.");
            }
            _recovery = binding;
        }
    }

    public bool TryIssueExternalReconcile(BackgroundJobExternalRecoveryBinding recovery)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        lock (_sync)
        {
            if (_stopped || _externalEntry != 0 || !_runtime.TryAdmit()
                || recovery.Receipt.Purpose != BackgroundJobExternalRecoveryPurpose.Reconcile
                || !ReferenceEquals(_recovery, recovery) || !ReferenceEquals(recovery.Run, this)
                || !recovery.SourceOwner.CleanupConfirmed || _current is not null)
                return false;
            // This is the last CAS before the real adapter method. In particular,
            // a held constructor or Run-lock wait cannot borrow an earlier check.
            if (!recovery.HasLiveLeaseWindow)
            {
                _stopped = true;
                return false;
            }
            _externalEntry = 1;
            return true;
        }
    }

    public bool TryAcceptExternalRecoveryRenewal(BackgroundJobExternalRecoveryBinding recovery,
        BackgroundJobExternalRecoveryRenewal renewal)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(renewal);
        lock (_sync)
        {
            if (_stopped || !_runtime.TryAdmit() || !ReferenceEquals(_recovery, recovery)
                || recovery.Receipt.Purpose != BackgroundJobExternalRecoveryPurpose.Reconcile
                || !ReferenceEquals(recovery.Run, this)) return false;
            if (!recovery.HasLiveLeaseWindow)
            {
                _stopped = true;
                _current?.CloseOwned();
                return false;
            }
            // No numeric setter or new receipt: the actual SQL/ACK/cleanup
            // witness must match this exact still-live acquisition and process.
            var accepted = recovery.TryAcceptRenewal(renewal, _runtime);
            // Validation can straddle expiry after the fast check above. The
            // binding's late refusal must leave the same sticky stopped state.
            if (!accepted && !recovery.HasLiveLeaseWindow)
            {
                _stopped = true;
                _current?.CloseOwned();
            }
            return accepted;
        }
    }

#pragma warning disable CA1034 // This callback-free process cell is created only by the closed runtime owner.
    public sealed class RuntimeStop
    {
        private int _closed;
        public bool IsOpen => Volatile.Read(ref _closed) == 0;
        public bool TryAdmit() => Interlocked.CompareExchange(ref _closed, 0, 0) == 0;
        public void Close() => Interlocked.Exchange(ref _closed, 1);
    }
#pragma warning restore CA1034
}
