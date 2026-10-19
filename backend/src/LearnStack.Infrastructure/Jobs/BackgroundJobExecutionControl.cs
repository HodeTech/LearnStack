using System.Diagnostics.CodeAnalysis;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Infrastructure.Jobs;

internal enum BackgroundJobLeaseLossCause { None, RenewalRefused, Elapsed }

/// <summary>One generation-owned start/business operation and its immutable renewal authority.</summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Only actual operation/callback cleanup may release this generation-owned deadline source.")]
internal sealed partial class BackgroundJobExecutionControl
{
    private readonly object _sync = new();
    private readonly BackgroundJobControl _owner;
    private readonly HangfireGenerationWork.Invocation _work;
    private readonly TimeProvider _time;
    private readonly BackgroundJobRoutingClaim? _claim;
    private readonly BackgroundJobStartPublication? _publication;
    private readonly BackgroundJobCommitGate? _admissionGate;
    private readonly long _issuedAt;
    private readonly CancellationTokenSource _deadlineStop = new();
    private readonly Task _publicationObservation;
    private readonly TaskCompletionSource<Task<bool>> _observersPublished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _observersStarted;
    private Task? _deadlineObservation;
    private Task? _deadlineCancellation;
    private ITimer? _leaseTimer;
    private bool _leaseTimerInitializing;
    private TaskCompletionSource<bool>? _leaseTimerUpdate;
    private long _leaseExpiry;
    private Task<JobStartExecutionResult>? _admission;
    private Task<JobBusinessExecutionResult>? _business;
    private BackgroundJobCapacityCheckpointOperation? _capacityCheckpoint;
    private BackgroundJobExecutionIdentity? _identity;
    private bool _joined, _cleanupFault;
    private int _released;
    private int _renewalClosed;
    private int _firstLeaseLoss;
    private bool _leaseObservationEligible;
    private BackgroundJobCommitGate? _businessGate;

    internal BackgroundJobExecutionControl(BackgroundJobControl owner, HangfireGenerationWork.Invocation work,
        BackgroundJobRoutingClaim claim, BackgroundJobRunGate.RuntimeStop processStop, TimeProvider time)
    {
        _owner = owner;
        _work = work;
        _time = time;
        _claim = claim;
        JobId = claim.JobId;
        _issuedAt = time.GetTimestamp();
        // Publication and its observer exist before any admission operation or
        // synchronous prefix. A late acknowledgement never reopens this gate.
        _publication = new();
        RunGate = new(processStop);
        _admissionGate = RunGate.OpenBoundary(BackgroundJobCheckpointKind.Admission);
        _publicationObservation = ObservePublicationAsync();
    }

    internal BackgroundJobExecutionControl(BackgroundJobControl owner, HangfireGenerationWork.Invocation work,
        BackgroundJobId job, long generation, string queue, BackgroundJobRunGate.RuntimeStop processStop, TimeProvider time)
    {
        _owner = owner;
        _work = work;
        _time = time;
        JobId = job;
        _recoveryGeneration = generation;
        _recoveryQueue = queue;
        _issuedAt = time.GetTimestamp();
        RunGate = new(processStop);
        // Recovery is not a counted admission and never opens Admission or
        // creates a publication observer waiting for a fabricated start receipt.
        _publicationObservation = Task.CompletedTask;
    }

    internal BackgroundJobId JobId { get; }
    internal BackgroundJobRoutingClaim Claim => _claim ?? throw new InvalidOperationException("Recovery has no routing claim.");
    internal BackgroundJobStartPublication Publication => _publication ?? throw new InvalidOperationException("Recovery has no admission publication.");
    internal BackgroundJobRunGate RunGate { get; }
    internal BackgroundJobCommitGate AdmissionGate => _admissionGate ?? throw new InvalidOperationException("Recovery has no admission boundary.");
    internal BackgroundJobCommitGate Gate => Volatile.Read(ref _businessGate) ?? AdmissionGate;
    // Prepare commits only its plan. Its physical ACK cannot stop the lease
    // that still protects Arm, the external call and the later Outcome owner.
    internal bool LeaseWorkSettled => Volatile.Read(ref _businessGate) is
    { Kind: BackgroundJobCheckpointKind.Transactional or BackgroundJobCheckpointKind.Finalize, CommitAcknowledged: true }
        || ExternalLeaseWorkSettled;
    internal BackgroundJobExecutionIdentity? Identity => Volatile.Read(ref _identity);
    internal CancellationToken Token => _work.Token;
    internal bool CleanupFault { get { lock (_sync) return _cleanupFault; } }
    internal bool RenewalRefused => Identity is not null && !LeaseWorkSettled
        && (RunGate.IsClosed || Volatile.Read(ref _renewalClosed) != 0);
    internal bool RenewalPermitted => Identity is not null && !LeaseWorkSettled && !RunGate.IsClosed
        && Volatile.Read(ref _renewalClosed) == 0;

    internal void RefuseRenewal()
    {
        ObserveLeaseLoss(BackgroundJobLeaseLossCause.RenewalRefused);
        Interlocked.Exchange(ref _renewalClosed, 1);
        CloseExecution();
    }

    // An already-visible stop/join/settlement wins at this observation point.
    // The cause CAS is not a universal ordering primitive for the process gate.
    private void ObserveLeaseLoss(BackgroundJobLeaseLossCause cause)
    {
        lock (_sync)
        {
            if (cause is not (BackgroundJobLeaseLossCause.RenewalRefused or BackgroundJobLeaseLossCause.Elapsed)
                || !_leaseObservationEligible || _joined || _cleanupFault
                || LeaseWorkSettled || RunGate.IsClosed || Volatile.Read(ref _renewalClosed) != 0
                || Interlocked.CompareExchange(ref _firstLeaseLoss, (int)cause, 0) != 0) return;
        }
        // Fixed Interlocked cells only; never acquire the parent's state lock
        // or notify a metric listener from a lease callback.
        _owner.ObserveLeaseLoss(cause);
    }

    internal bool RecordRenewal(BackgroundJobRenewal renewal, long issuedAt)
    {
        if (!ReferenceEquals(renewal.Identity, Identity))
            throw new InvalidOperationException("Renewal must retain the exact acknowledged execution identity.");
        ITimer? timer = null;
        TaskCompletionSource<bool>? update = null;
        TimeSpan remaining = default;
        long desiredExpiry = 0;
        lock (_sync)
        {
            if (_joined || _cleanupFault || RunGate.IsClosed || LeaseWorkSettled || Volatile.Read(ref _renewalClosed) != 0
                || _leaseTimerUpdate is { Task.IsCompleted: false }) return false;
            var now = _time.GetTimestamp();
            remaining = renewal.OwnershipUntil - renewal.DbNow - _time.GetElapsedTime(issuedAt, now);
            var permitted = remaining > TimeSpan.Zero && now < _leaseExpiry
                && (Identity is not { IsRecovery: true } || _recoveryBinding is { } binding
                    && renewal.RecoveryLease is { } evidence && RunGate.TryAcceptExternalRecoveryRenewal(binding, evidence));
            // Waiting for the Run lock also spends the native wait budget.
            now = _time.GetTimestamp();
            remaining = renewal.OwnershipUntil - renewal.DbNow - _time.GetElapsedTime(issuedAt, now);
            if (permitted && remaining > TimeSpan.Zero && now < _leaseExpiry && _leaseTimer is { } actual)
            {
                desiredExpiry = checked(now + (long)(remaining.TotalSeconds * _time.TimestampFrequency));
                timer = actual;
                update = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _leaseTimerUpdate = update; // Retained before the synchronous Change prefix.
            }
            else
            {
                if (remaining <= TimeSpan.Zero || now >= _leaseExpiry) ObserveLeaseLoss(BackgroundJobLeaseLossCause.Elapsed);
                Interlocked.Exchange(ref _renewalClosed, 1);
                RunGate.Close();
            }
        }
        if (update is null) { _work.ExpireWait(); return false; }
        var applied = false;
        var clean = false;
        var stop = false;
        try
        {
            clean = timer!.Change(remaining, Timeout.InfiniteTimeSpan);
            lock (_sync)
            {
                if (!clean)
                {
                    _cleanupFault = true;
                    Interlocked.Exchange(ref _renewalClosed, 1);
                    RunGate.Close();
                    stop = true;
                }
                else if (!_joined && !_cleanupFault && !RunGate.IsClosed && !LeaseWorkSettled
                    && Volatile.Read(ref _renewalClosed) == 0)
                {
                    var now = _time.GetTimestamp();
                    if (now < _leaseExpiry && now < desiredExpiry)
                    {
                        _leaseExpiry = desiredExpiry;
                        applied = true;
                    }
                    else
                    {
                        ObserveLeaseLoss(BackgroundJobLeaseLossCause.Elapsed);
                        Interlocked.Exchange(ref _renewalClosed, 1);
                        RunGate.Close();
                        stop = true;
                    }
                }
            }
        }
        catch (Exception failure) when (Catchable(failure))
        {
            lock (_sync)
            {
                _cleanupFault = true;
                Interlocked.Exchange(ref _renewalClosed, 1);
                RunGate.Close();
            }
            stop = true;
            clean = false;
        }
        finally
        {
            // Successful return/cleanup is distinct from applying a renewal:
            // stop or business ACK may have won while Change was in flight.
            update.TrySetResult(clean);
        }
        if (stop) _work.ExpireWait();
        return applied;
    }

    private void LeaseElapsed()
    {
        try
        {
            lock (_sync)
            {
                if (_joined || LeaseWorkSettled || _time.GetTimestamp() < _leaseExpiry) return;
                ObserveLeaseLoss(BackgroundJobLeaseLossCause.Elapsed);
                Interlocked.Exchange(ref _renewalClosed, 1);
                RunGate.Close();
            }
            _work.ExpireWait();
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
        RunGate.Close();
        _work.ExpireWait();
    }

    private async Task ObservePublicationAsync()
    {
        try
        {
            var receipt = await Publication.Completion.ConfigureAwait(false);
            if (receipt is null) return;
            receipt.BindRun(RunGate);
            _businessGate = receipt.Stage switch
            {
                BackgroundJobExecutionStage.Transactional => RunGate.TryOpenBoundary(BackgroundJobCheckpointKind.Transactional),
                BackgroundJobExecutionStage.Prepare => RunGate.TryOpenBoundary(BackgroundJobCheckpointKind.Prepare),
                BackgroundJobExecutionStage.Finalize => RunGate.TryOpenBoundary(BackgroundJobCheckpointKind.Finalize),
                null when receipt.Envelope.Profile == "external" && receipt.LedgerStage == BackgroundJobLedgerStage.Normal => null,
                _ => throw new InvalidOperationException("Job publication returned an invalid execution stage."),
            };
            var identity = BackgroundJobExecutionIdentity.FromReceipt(receipt);
            lock (_sync) _leaseObservationEligible = true;
            // Debit from before framework read/admission issuance. This is
            // conservative when admission cleanup is slow and cannot grant a
            // fresh business budget beyond the durable database deadline.
            var remaining = receipt.Deadline - receipt.AdmittedAt
                - _time.GetElapsedTime(_issuedAt, _time.GetTimestamp());
            if (remaining <= TimeSpan.Zero) { Volatile.Write(ref _identity, identity); CloseExecution(); return; }
            var leaseRemaining = receipt.OwnershipUntil - receipt.AdmittedAt
                - _time.GetElapsedTime(_issuedAt, _time.GetTimestamp());
            if (leaseRemaining <= TimeSpan.Zero)
            {
                Volatile.Write(ref _identity, identity);
                ObserveLeaseLoss(BackgroundJobLeaseLossCause.Elapsed);
                RefuseRenewal();
                return;
            }
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
        lock (_sync)
        {
            Interlocked.Exchange(ref _renewalClosed, 1);
            if (_leaseTimerInitializing)
            {
                _cleanupFault = true;
                RunGate.Close();
                return false;
            }
        }
        if (_admission is { IsCompletedSuccessfully: true }) _admission.Result.SourceOwner.CloseIssuance();
        _recoverySource?.CloseIssuance();
        if (!operationCleanupConfirmed) { RetainCleanup(); return false; }
        try
        {
            if ((_claim is not null
                    ? _admission is not { IsCompletedSuccessfully: true, Result.CleanupFailed: false }
                    : !RecoveryOperationsCleaned)
                || _business is not null && (!_business.IsCompletedSuccessfully || _business.Result.CleanupFailed)
                || !ExternalOperationsCleaned)
            {
                RetainCleanup();
                return false;
            }
            if (_capacityCheckpoint is { } checkpoint && (await checkpoint.Completion.ConfigureAwait(false)).CleanupFailed)
            { RetainCleanup(); return false; }
            if (!await JoinBusinessObserversAsync().ConfigureAwait(false)
                || _admission is not null && !_admission.Result.SourceOwner.CleanupConfirmed
                || _recoverySource is not null && !_recoverySource.CleanupConfirmed)
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
            Task<bool>? timerUpdate;
            lock (_sync) timerUpdate = _leaseTimerUpdate?.Task;
            if (timerUpdate is not null && !await timerUpdate.ConfigureAwait(false)) return false;
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
