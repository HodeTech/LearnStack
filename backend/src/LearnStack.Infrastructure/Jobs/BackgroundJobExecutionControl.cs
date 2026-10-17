using System.Diagnostics.CodeAnalysis;
using LearnStack.Application.Jobs;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>One generation-owned start/business operation and its immutable renewal authority.</summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Only actual operation/callback cleanup may release this generation-owned deadline source.")]
internal sealed class BackgroundJobExecutionControl
{
    private readonly object _sync = new();
    private readonly BackgroundJobControl _owner;
    private readonly HangfireGenerationWork.Invocation _work;
    private readonly TimeProvider _time;
    private readonly long _issuedAt;
    private readonly CancellationTokenSource _deadlineStop = new();
    private readonly Task _publicationObservation;
    private readonly TaskCompletionSource<Task<bool>> _observersPublished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _observersStarted;
    private Task? _deadlineObservation;
    private Task? _deadlineCancellation;
    private ITimer? _leaseTimer;
    private long _leaseExpiry;
    private Task<JobStartExecutionResult>? _admission;
    private Task<JobBusinessExecutionResult>? _business;
    private BackgroundJobCapacityCheckpointOperation? _capacityCheckpoint;
    private BackgroundJobExecutionIdentity? _identity;
    private bool _joined, _cleanupFault;
    private int _released;
    private int _renewalClosed;

    internal BackgroundJobExecutionControl(BackgroundJobControl owner, HangfireGenerationWork.Invocation work,
        BackgroundJobRoutingClaim claim, TimeProvider time)
    {
        _owner = owner;
        _work = work;
        _time = time;
        Claim = claim;
        _issuedAt = time.GetTimestamp();
        // Publication and its observer exist before any admission operation or
        // synchronous prefix. A late acknowledgement never reopens this gate.
        Publication = new();
        Gate = new();
        _publicationObservation = ObservePublicationAsync();
    }

    internal BackgroundJobRoutingClaim Claim { get; }
    internal BackgroundJobStartPublication Publication { get; }
    internal BackgroundJobCommitGate Gate { get; }
    internal BackgroundJobExecutionIdentity? Identity => Volatile.Read(ref _identity);
    internal CancellationToken Token => _work.Token;
    internal bool CleanupFault { get { lock (_sync) return _cleanupFault; } }
    internal bool RenewalRefused => Identity is not null && !Gate.CommitAcknowledged && Volatile.Read(ref _renewalClosed) != 0;
    internal bool RenewalPermitted => Identity is not null && !Gate.CommitAcknowledged
        && Volatile.Read(ref _renewalClosed) == 0;

    internal void RefuseRenewal()
    {
        Interlocked.Exchange(ref _renewalClosed, 1);
        CloseExecution();
    }

    internal bool RecordRenewal(BackgroundJobRenewal renewal, long issuedAt)
    {
        if (!ReferenceEquals(renewal.Identity, Identity))
            throw new InvalidOperationException("Renewal must retain the exact acknowledged execution identity.");
        var remaining = renewal.OwnershipUntil - renewal.DbNow
            - _time.GetElapsedTime(issuedAt, _time.GetTimestamp());
        if (remaining <= TimeSpan.Zero) { RefuseRenewal(); return false; }
        lock (_sync)
        {
            if (_joined || _cleanupFault || Gate.CommitAcknowledged || Volatile.Read(ref _renewalClosed) != 0) return false;
            _leaseExpiry = checked(_time.GetTimestamp() + (long)(remaining.TotalSeconds * _time.TimestampFrequency));
            if (_leaseTimer?.Change(remaining, Timeout.InfiniteTimeSpan) != true)
                throw new InvalidOperationException("The actual lease observation is no longer owned.");
        }
        return true;
    }

    private void LeaseElapsed()
    {
        try
        {
            lock (_sync)
            {
                if (_joined || Gate.CommitAcknowledged || _time.GetTimestamp() < _leaseExpiry) return;
                Interlocked.Exchange(ref _renewalClosed, 1);
                Gate.Close();
            }
            _work.RequestCancellation();
        }
        catch (Exception failure) when (Catchable(failure))
        {
            RetainCleanup();
            CloseExecution();
        }
    }

    internal Task WaitForPublicationAsync() => _publicationObservation;

    internal void HoldAdmission(Task<JobStartExecutionResult> task)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_sync)
        {
            if (_admission is not null || _joined) throw new InvalidOperationException("Job control owns one admission task.");
            _admission = task;
        }
    }

    internal void HoldBusiness(Task<JobBusinessExecutionResult> task)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_sync)
        {
            if (_business is not null || _joined) throw new InvalidOperationException("Job control owns one business task.");
            _business = task;
        }
    }

    internal void HoldCapacityCheckpoint(BackgroundJobCapacityCheckpointOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_sync)
        {
            if (_joined || _cleanupFault || !ReferenceEquals(operation.Cleanup.Execution, this)
                || _capacityCheckpoint is { } previous && (!previous.Completion.IsCompletedSuccessfully
                    || previous.Completion.Result.CleanupFailed))
                throw new InvalidOperationException("Capacity cleanup retains one actual checkpoint until positive cleanup.");
            _capacityCheckpoint = operation;
        }
    }

    // Fixed-memory closure is separate from the owned asynchronous cancellation
    // relay. A generation-local drain never calls this for already admitted work.
    internal void CloseExecution()
    {
        Gate.Close();
        _work.RequestCancellation();
    }

    private async Task ObservePublicationAsync()
    {
        try
        {
            var receipt = await Publication.Completion.ConfigureAwait(false);
            if (receipt is null) return;
            var identity = BackgroundJobExecutionIdentity.FromReceipt(receipt);
            // Debit from before framework read/admission issuance. This is
            // conservative when admission cleanup is slow and cannot grant a
            // fresh business budget beyond the durable database deadline.
            var remaining = receipt.Deadline - receipt.AdmittedAt
                - _time.GetElapsedTime(_issuedAt, _time.GetTimestamp());
            if (remaining <= TimeSpan.Zero) { Volatile.Write(ref _identity, identity); CloseExecution(); return; }
            var leaseRemaining = receipt.OwnershipUntil - receipt.AdmittedAt
                - _time.GetElapsedTime(_issuedAt, _time.GetTimestamp());
            if (leaseRemaining <= TimeSpan.Zero) { Volatile.Write(ref _identity, identity); RefuseRenewal(); return; }
            lock (_sync)
                _leaseExpiry = checked(_time.GetTimestamp() + (long)(leaseRemaining.TotalSeconds * _time.TimestampFrequency));
            _leaseTimer = _time.CreateTimer(static state => ((BackgroundJobExecutionControl)state!).LeaseElapsed(),
                this, leaseRemaining, Timeout.InfiniteTimeSpan);
            _deadlineObservation = ObserveDeadlineAsync(remaining);
            Volatile.Write(ref _identity, identity);
        }
        catch (Exception failure) when (Catchable(failure))
        {
            RetainCleanup();
            CloseExecution();
        }
    }

    private async Task ObserveDeadlineAsync(TimeSpan remaining)
    {
        try
        {
            await Task.Delay(remaining, _time, _deadlineStop.Token).ConfigureAwait(false);
            CloseExecution();
        }
        catch (OperationCanceledException) when (_deadlineStop.IsCancellationRequested) { }
        catch (Exception failure) when (Catchable(failure))
        {
            RetainCleanup();
            CloseExecution();
        }
    }

    /// <summary>Joins publication/deadline ownership; the generation still joins its private callbacks afterwards.</summary>
    internal async Task<bool> JoinAsync(bool operationCleanupConfirmed)
    {
        // Serialize permanent renewal closure with an already-running renewal
        // before disposing its timer. Only actual cleanup below publishes joined.
        lock (_sync) Interlocked.Exchange(ref _renewalClosed, 1);
        if (_admission is { IsCompletedSuccessfully: true }) _admission.Result.SourceOwner.CloseIssuance();
        if (!operationCleanupConfirmed) { RetainCleanup(); return false; }
        try
        {
            if (_admission is not { IsCompletedSuccessfully: true } || _admission.Result.CleanupFailed
                || _business is not null && (!_business.IsCompletedSuccessfully || _business.Result.CleanupFailed))
            {
                RetainCleanup();
                return false;
            }
            if (_capacityCheckpoint is { } checkpoint && (await checkpoint.Completion.ConfigureAwait(false)).CleanupFailed)
            { RetainCleanup(); return false; }
            if (!await JoinBusinessObserversAsync().ConfigureAwait(false) || !_admission.Result.SourceOwner.CleanupConfirmed)
            { RetainCleanup(); return false; }
            lock (_sync)
            {
                if (_cleanupFault) return false;
                _joined = true;
                return true;
            }
        }
        catch (Exception failure) when (Catchable(failure))
        {
            RetainCleanup();
            return false;
        }
    }

    internal Task<bool> JoinBusinessObserversAsync()
    {
        lock (_sync)
        {
            // Shares RecordRenewal's lock: a late batch cannot rearm a disposed timer.
            Interlocked.Exchange(ref _renewalClosed, 1);
            if (_observersStarted) return _observersPublished.Task.Unwrap();
            _observersStarted = true;
        }
        var actual = JoinBusinessObserversCoreAsync();
        _observersPublished.TrySetResult(actual);
        return _observersPublished.Task.Unwrap();
    }

    private async Task<bool> JoinBusinessObserversCoreAsync()
    {
        try
        {
            await _publicationObservation.ConfigureAwait(false);
            _deadlineCancellation = _deadlineStop.CancelAsync();
            await _deadlineCancellation.ConfigureAwait(false);
            if (_deadlineObservation is not null) await _deadlineObservation.ConfigureAwait(false);
            if (_leaseTimer is not null) await _leaseTimer.DisposeAsync().ConfigureAwait(false);
            _deadlineStop.Dispose();
            lock (_sync) return !_cleanupFault;
        }
        catch (Exception failure) when (Catchable(failure))
        {
            RetainCleanup();
            return false;
        }
    }

    /// <summary>Only the generation's positive final private-cleanup witness forgets this job identity.</summary>
    internal void ReleaseAfterPrivateCleanup()
    {
        lock (_sync)
        {
            if (!_joined || _cleanupFault || Interlocked.Exchange(ref _released, 1) != 0)
                throw new InvalidOperationException("Job execution control requires one positive actual cleanup witness.");
        }
        _owner.Release(this);
    }

    private void RetainCleanup() { lock (_sync) _cleanupFault = true; }
    private static bool Catchable(Exception failure) => BackgroundJobControlResources.Catchable(failure);
}
