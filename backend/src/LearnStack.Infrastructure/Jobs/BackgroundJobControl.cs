using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Infrastructure.Jobs;

public sealed record BackgroundJobControlHealth(int Executions, int PendingOutcomes,
    bool RenewalHealthy, bool RecoveryHealthy, bool TerminalHealthy, bool CleanupConfirmed,
    long Renewals, long Retries, long Terminalized, long UnknownOutcomes)
{
    public bool? HasQuarantinedJobs { get; init; }
    public bool? HasQuarantinedControl { get; init; }
    public bool? HasDeferredWork { get; init; }
    public bool ObservationHealthy { get; init; }
    public BackgroundJobDurableObservation? Observation { get; init; }
    public long? ObservationTimestamp { get; init; }
    public bool ObservationFailed { get; init; }
    public long? RenewalTimestamp { get; init; }
    public long RenewalsConfirmed { get; init; }
    public long LeaseLossRenewalRefused { get; init; }
    public long LeaseLossElapsed { get; init; }
    public BackgroundJobLateSettlementSnapshot? LateSettlements { get; init; }
    public long? RecoveryTimestamp { get; init; }
    public long? TerminalTimestamp { get; init; }
    public BackgroundJobRecurrenceHealth? Recurrence { get; init; }
}

/// <summary>Independent finite renewal, recovery and terminal lanes; none invokes module work.</summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The facade owns actual lane completion and the private cancellation source, including retained cleanup.")]
internal sealed partial class BackgroundJobControl
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, BackgroundJobExecutionControl> _executions = [];
    private readonly List<CompletionRequest> _requests = [];
    private readonly PostgresBackgroundJobControlStore _store;
    private readonly PostgresBackgroundJobTerminalStore _terminal;
    private readonly JobExecutor _executor;
    private readonly BackgroundJobRecurrenceControl _recurrence;
    private readonly BackgroundJobRuntimeGate _gate;
    private readonly BackgroundJobRuntimeOptions _bounds;
    private readonly TimeProvider _time;
    private readonly BackgroundJobObservationManifest _observationManifest;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _stopRequested = Signal();
    private readonly TaskCompletionSource<Task[]> _loopsPublished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completion;
    private readonly Task _stopRelay, _completionObservation;
    // These exact slots retain returned failed-cleanup resources. A probe has
    // its own one slot; neither lane may recursively acquire another probe.
    private OwnedBackgroundJobControl<IReadOnlyList<BackgroundJobRenewal>>? _renewalOperation;
    private BackgroundJobRenewalOperation? _renewalFlight;
    private object? _controlOperation, _terminalOperation;
    private OwnedBackgroundJobControl<BackgroundJobProbeResult>? _controlProbe, _terminalProbe;
    private PendingCommitCursor? _pendingControl, _pendingTerminal;
    private BackgroundJobTerminalNoStartOperation? _terminalNoStart;
    private Activity? _terminalActivity;
    private Task? _cancellation;
    private bool _started, _stopping, _cleanupFault, _renewalFault, _controlFault, _terminalFault;
    private bool _controlSealed;
    private long? _renewalProgress, _controlProgress, _terminalProgress;
    private long _renewals, _renewalsConfirmed, _retries, _terminalized, _unknown;
    private long _leaseLossRenewalRefused, _leaseLossElapsed;

    internal BackgroundJobControl(PrivilegedDataSource source, BackgroundJobDefinitionRegistry definitions,
        JobExecutor executor, BackgroundJobRuntimeGate gate, BackgroundJobRuntimeOptions bounds,
        IGuidFactory ids, TimeProvider time, BackgroundJobRecurrenceRegistry? recurrences = null,
        BackgroundJobLifecycleProjection? projection = null)
    {
        _store = new(source, definitions, bounds, ids, gate);
        _terminal = new(source, executor.ScopeFactory, bounds, ids, gate);
        _executor = executor;
        _gate = gate;
        _bounds = bounds;
        _time = time;
        var recurrenceRegistry = recurrences ?? BackgroundJobRecurrenceRegistry.CreateEmpty();
        _observationManifest = BackgroundJobObservationManifest.Create(definitions, recurrenceRegistry);
        _recurrence = new(source, definitions, recurrenceRegistry,
            projection, executor, gate, bounds, ids, time);
        _completion = new(this, TaskCreationOptions.RunContinuationsAsynchronously);
        _stopRelay = RelayStopAsync();
        _completionObservation = ObserveCompletionAsync();
    }

    internal Task Completion => _completion.Task;
    internal BackgroundJobControlHealth Snapshot
    {
        get
        {
            lock (_sync)
                return new(_executions.Count, _requests.Count + PendingCount(_pendingControl) + PendingCount(_pendingTerminal)
                    + (_terminalNoStart?.PendingOutcomes ?? 0),
                    !_cleanupFault && !_renewalFault && !_executions.Values.Any(item => item.RenewalRefused) && Fresh(_renewalProgress),
                    !_cleanupFault && !_controlFault && _pendingControl is null
                        && !_requests.Any(item => item.Checkpoint is { TimelyReturn.IsCompletedSuccessfully: true } checkpoint
                            && !checkpoint.TimelyReturn.Result) && Fresh(_controlProgress),
                    !_cleanupFault && !_terminalFault && _pendingTerminal is null
                        && (_terminalNoStart?.Healthy ?? true) && Fresh(_terminalProgress), Completion.IsCompletedSuccessfully,
                    _renewals, _retries, _terminalized, _unknown)
                {
                    HasQuarantinedJobs = _observation?.HasQuarantinedJobs,
                    HasQuarantinedControl = _observation?.HasQuarantinedControl,
                    HasDeferredWork = _observation?.HasDeferredWork,
                    ObservationHealthy = ObservationHealthy(),
                    Observation = _observation,
                    ObservationTimestamp = _observationObservedAt,
                    ObservationFailed = _observationFault,
                    RenewalTimestamp = _renewalProgress,
                    RenewalsConfirmed = _renewalsConfirmed,
                    LeaseLossRenewalRefused = Interlocked.Read(ref _leaseLossRenewalRefused),
                    LeaseLossElapsed = Interlocked.Read(ref _leaseLossElapsed),
                    LateSettlements = CaptureLateSettlements(),
                    RecoveryTimestamp = _controlProgress,
                    TerminalTimestamp = _terminalProgress,
                    Recurrence = _recurrence.Snapshot,
                };
        }
    }

    internal void ObserveLeaseLoss(BackgroundJobLeaseLossCause cause)
    {
        switch (cause)
        {
            case BackgroundJobLeaseLossCause.RenewalRefused: Interlocked.Increment(ref _leaseLossRenewalRefused); break;
            case BackgroundJobLeaseLossCause.Elapsed: Interlocked.Increment(ref _leaseLossElapsed); break;
        }
    }

    internal BackgroundJobExecutionControl? TryBegin(BackgroundJobRoutingClaim claim,
        HangfireGenerationWork.Invocation work)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        lock (_sync)
        {
            if (_stopping || !_gate.TryAdmit() || !work.TryAdmitContinuation()
                || RetainedIds().Contains(claim.JobId.Value) || _executions.Count >= 64) return null;
            var execution = new BackgroundJobExecutionControl(this, work, claim, _gate.RunStop, _time);
            _executions.Add(claim.JobId.Value, execution);
            return execution;
        }
    }

    internal BackgroundJobExecutionControl? TryBeginRecovery(BackgroundJobId job, long generation, string queue,
        HangfireGenerationWork.Invocation work)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        lock (_sync)
        {
            if (_stopping || !_gate.TryAdmit() || !work.TryAdmitContinuation()
                || RetainedIds().Contains(job.Value) || _executions.Count >= 64) return null;
            var execution = new BackgroundJobExecutionControl(this, work, job, generation, queue, _gate.RunStop, _time);
            _executions.Add(job.Value, execution);
            return execution;
        }
    }

    internal bool CanRoute(BackgroundJobId job)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        lock (_sync)
            return !_stopping && _gate.TryAdmit() && _executions.Count < 64 && !RetainedIds().Contains(job.Value);
    }

    internal bool TryBeginExternalStatus(BackgroundJobExecutionControl execution, out CancellationToken token)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        lock (_sync)
        {
            token = default;
            if (_stopping || !_gate.TryAdmit() || !_executions.TryGetValue(execution.JobId.Value, out var actual)
                || !ReferenceEquals(actual, execution)) return false;
            // Pure status accounting has its own bounded SQL task after actual
            // probe cleanup. This token grants no new app/provider/Run entry.
            token = _stop.Token;
            return true;
        }
    }

    internal void Release(BackgroundJobExecutionControl execution)
    {
        lock (_sync)
        {
            if (!_executions.TryGetValue(execution.JobId.Value, out var actual)
                || !ReferenceEquals(actual, execution))
                throw new InvalidOperationException("Only the actual execution's final cleanup releases its identity.");
            _executions.Remove(execution.JobId.Value);
        }
    }

    internal void Start()
    {
        lock (_sync)
        {
            if (_started) throw new InvalidOperationException("Job control starts once.");
            if (_stopping) return;
            _started = true;
        }
        var loops = new List<Task>();
        try
        {
            // The facade already roots this owner before any synchronous store
            // prefix. Separate tasks prevent held terminal audit from stopping
            // renewal, recovery, bridge or native healthy business admission.
            if (ExecutionContext.IsFlowSuppressed()) ScheduleLoops(loops);
            else { using (ExecutionContext.SuppressFlow()) ScheduleLoops(loops); }
        }
        catch (Exception failure) when (Catchable(failure)) { RetainCleanup(); }
        finally { _loopsPublished.TrySetResult(loops.ToArray()); }
    }

    private void ScheduleLoops(List<Task> loops)
    {
        _recurrence.Start();
        loops.Add(Task.Run(RunRenewalAsync));
        loops.Add(Task.Run(RunControlAsync));
        loops.Add(Task.Run(RunTerminalAsync));
    }

    internal void RequestStop()
    {
        lock (_sync)
        {
            _stopping = true;
            if (!_started) _loopsPublished.TrySetResult([]);
        }
        _recurrence.RequestStop();
        _stopRequested.TrySetResult();
    }

    internal Task<bool> FinishAdmissionAsync(BackgroundJobExecutionControl execution, JobStartExecutionResult result,
        BackgroundJobActualCleanup? cleanup = null)
    {
        if (result.CleanupFailed) return Task.FromResult(false);
        if (cleanup is not null)
            return Enqueue(new(execution, RequestKind.Admission,
                result.Outcome == JobStartExecutionOutcome.Indeterminate
                    ? BackgroundJobCommitProbe.ForAdmission(execution.Claim) with { CapacityGroup = cleanup.Envelope.CapacityGroup } : null,
                new(BackgroundJobFailureDisposition.Retry, "unexpected_fault", null), _time.GetTimestamp(), cleanup));
        if (result.ValidatedEnvelope?.CapacityGroup is not null && result.Outcome is JobStartExecutionOutcome.Started or JobStartExecutionOutcome.Indeterminate)
            return Task.FromResult(false);
        return result.Outcome switch
        {
            JobStartExecutionOutcome.Indeterminate => Enqueue(new(execution, RequestKind.Admission,
                BackgroundJobCommitProbe.ForAdmission(execution.Claim), null, _time.GetTimestamp())),
            JobStartExecutionOutcome.InvalidEnvelope => Enqueue(new(execution, RequestKind.InvalidEnvelope,
                null, null, _time.GetTimestamp())),
            JobStartExecutionOutcome.CapacityDeferred => Enqueue(new(execution, RequestKind.CapacityDeferred,
                null, null, _time.GetTimestamp())),
            _ => Task.FromResult(true),
        };
    }

    internal Task<bool> FinishBusinessAsync(BackgroundJobExecutionControl execution, JobBusinessExecutionResult result,
        BackgroundJobActualCleanup? cleanup = null)
    {
        if (result.CleanupFailed) return Task.FromResult(false);
        if (result.CommitAcknowledged) return Task.FromResult(true);
        if (execution.Identity is not { } identity) return Task.FromResult(false);
        if (identity.Profile == "external") return execution.FinishExternalFinalizeAsync(result, _store);
        execution.Gate.Close();
        if (identity.CapacityGroup is not null)
        {
            if (cleanup is null) return Task.FromResult(false);
            return Enqueue(new(execution, RequestKind.Business,
                result.DurableOutcomeUncertain ? BackgroundJobCommitProbe.ForBusiness(identity) : null,
                result.Decision, _time.GetTimestamp(), cleanup));
        }
        if (result.DurableOutcomeUncertain)
            return Enqueue(new(execution, RequestKind.Business, BackgroundJobCommitProbe.ForBusiness(identity),
                new(BackgroundJobFailureDisposition.Retry, "unexpected_fault", null), _time.GetTimestamp()));
        if (result.KnownRollback && result.Decision.Disposition is BackgroundJobFailureDisposition.Retry or BackgroundJobFailureDisposition.Terminal)
            return Enqueue(new(execution, RequestKind.Business, null, result.Decision, _time.GetTimestamp()));
        // Owned deadline/shutdown leaves the counted receipt for fenced recovery;
        // a canceled wait is never a permanent business refusal.
        return Task.FromResult(true);
    }

    internal Task<bool> FinishFinalizerAsync(BackgroundJobExecutionControl execution, JobBusinessExecutionResult result,
        OwnedBackgroundJobControl<BackgroundJobCheckpointHistoryObservation>? history) =>
        Enqueue(new(execution, RequestKind.Finalizer, null, null, _time.GetTimestamp())
        { FinalizerResult = result, FinalizerHistory = history });

    internal Task<bool> ExecuteExternalNormalAsync(BackgroundJobExecutionControl execution) =>
        execution.ExecuteExternalNormalAsync(_executor, _store);

    internal Task<bool> ExecuteExternalRecoveryAsync(BackgroundJobExecutionControl execution) =>
        execution.ExecuteExternalRecoveryAsync(_executor, _store);

    private Task<bool> Enqueue(CompletionRequest request)
    {
        lock (_sync)
        {
            if (_stopping)
            {
                request.Cleanup?.SourceOwner.CloseIssuance();
                return Task.FromResult(true); // Actual work is done; no new control issuance on host stop.
            }
            if (_requests.Count >= 128)
            {
                request.Cleanup?.SourceOwner.CloseIssuance();
                _cleanupFault = true;
                return Task.FromResult(false);
            }
            _requests.Add(request);
            return request.Completion.Task;
        }
    }

    private async Task RunRenewalAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            BackgroundJobExecutionControl[] active;
            lock (_sync) active = _executions.Values.Where(item => item.RenewalPermitted).ToArray();
            try
            {
                if (active.Length > 0)
                {
                    _renewalFlight = new(_store, active, _bounds, _time, _stop.Token);
                    _renewalFlight.Start();
                    var timely = await _renewalFlight.TimelyReturn.ConfigureAwait(false);
                    if (!timely)
                    {
                        foreach (var execution in active) execution.RefuseRenewal();
                        lock (_sync) _renewalFault = true;
                    }
                    // A finite wait is not actual batch/connection cleanup. This
                    // lane issues nothing else until the original task returns.
                    _renewalOperation = await _renewalFlight.Completion.ConfigureAwait(false);
                    // A later cleanup fault cannot withdraw the physical ACK.
                    // It still prevents accepting these leases or releasing resources.
                    if (_renewalOperation.CommitAcknowledged)
                        lock (_sync) _renewals += _renewalOperation.Value?.Count ?? 0;
                    if (_renewalOperation.CommitAcknowledged && !_renewalOperation.Failed)
                    {
                        var rows = _renewalOperation.Value ?? [];
                        var renewed = rows.Select(row => row.Identity).ToHashSet();
                        foreach (var row in rows)
                        {
                            var execution = active.Single(item => ReferenceEquals(item.Identity, row.Identity));
                            if (!execution.LeaseWorkSettled) execution.RecordRenewal(row, _renewalFlight.IssuedAt);
                        }
                        foreach (var execution in active)
                            if (!renewed.Contains(execution.Identity!) && !execution.LeaseWorkSettled) execution.RefuseRenewal();
                    }
                    else
                    {
                        foreach (var execution in active) execution.RefuseRenewal();
                        lock (_sync) _renewalFault = true;
                        if (_renewalOperation.CommitUncertain)
                        {
                            lock (_sync) _unknown++;
                            foreach (var probe in _renewalOperation.Probes)
                            {
                                var execution = active.Single(item => item.JobId.Value == probe.JobId);
                                _ = Enqueue(new(execution, RequestKind.Renewal, probe, null, _time.GetTimestamp()));
                            }
                        }
                    }
                    if (!_renewalOperation.CleanupConfirmed) { RetainCleanup(); return; }
                    lock (_sync) _renewalFault = !timely || _renewalOperation.Failed;
                    _renewalOperation = null;
                    _renewalFlight = null;
                }
                lock (_sync)
                {
                    if (active.Length == 0) _renewalFault = _executions.Values.Any(item => item.RenewalRefused);
                    _renewalProgress = _time.GetTimestamp();
                }
                await Task.Delay(_bounds.RenewalInterval, _time, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception failure) when (Catchable(failure))
            {
                foreach (var execution in active) execution.RefuseRenewal();
                lock (_sync) _renewalFault = true;
                if (_renewalFlight is not null && _renewalOperation?.CleanupConfirmed != true)
                { RetainCleanup(); return; }
                _renewalOperation = null;
                _renewalFlight = null;
                await PauseAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task RunControlAsync()
    {
        var lane = 0;
        var outcomeTurn = true;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (ObservationReadDue() && !await RefreshObservationAsync().ConfigureAwait(false)) return;
                CompletionRequest? request;
                lock (_sync) request = outcomeTurn ? _requests.FirstOrDefault(item => _time.GetTimestamp() >= item.Due) : null;
                outcomeTurn = !outcomeTurn;
                if (request is not null)
                {
                    await ProcessRequestAsync(request).ConfigureAwait(false);
                    lock (_sync)
                    {
                        if (_controlSealed) return;
                        if (_requests.Remove(request)) _requests.Add(request);
                    }
                }
                else
                {
                    PendingCommitCursor? pending;
                    lock (_sync) pending = _pendingControl;
                    if (pending is not null)
                    {
                        if (!await ProbePendingAsync(pending, terminal: false).ConfigureAwait(false)) return;
                    }
                    else
                    {
                        var selected = (BackgroundJobControlLane)lane;
                        lane = (lane + 1) % Enum.GetValues<BackgroundJobControlLane>().Length;
                        var operation = await _store.RunRecoveryQuantumAsync(selected, RetainedIds(), _stop.Token).ConfigureAwait(false);
                        _controlOperation = operation;
                        if (operation.CommitAcknowledged)
                            lock (_sync) _retries += operation.Probes.Count(IsRetryTransition);
                        if (!await QualifyControlAsync(operation, retainPendingProbes: true).ConfigureAwait(false)) return;
                        _controlOperation = null;
                    }
                }
                lock (_sync) _controlProgress = _time.GetTimestamp();
                await PauseAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception failure) when (Catchable(failure))
            {
                lock (_sync) _controlFault = true;
                await PauseAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessRequestAsync(CompletionRequest request)
    {
        if (request.Kind == RequestKind.NoPlanTerminal)
        {
            await ProcessNoPlanTerminalRequestAsync(request).ConfigureAwait(false);
            return;
        }
        if (request.Kind == RequestKind.Finalizer)
        {
            await ProcessFinalizerRequestAsync(request).ConfigureAwait(false);
            return;
        }
        if (request.Cleanup is not null)
        {
            await ProcessCapacityRequestAsync(request).ConfigureAwait(false);
            return;
        }
        if (request.Probe is { } probe)
        {
            _controlProbe = await _store.ProbeCommitAsync(probe, _stop.Token).ConfigureAwait(false);
            if (!_controlProbe.CleanupConfirmed) { SealControl(); return; }
            if (_controlProbe.Failed || _controlProbe.Value is not { Disposition: not (BackgroundJobProbeDisposition.Unresolved or BackgroundJobProbeDisposition.Missing) } resolution)
            {
                lock (_sync) { _controlFault = true; _unknown++; request.Due = Later(_bounds.RecoveryProbeInterval); }
                _controlProbe = null;
                return;
            }
            ObserveRequestHistory(request, probe, _controlProbe);
            _controlProbe = null;
            if (request.Kind != RequestKind.Business || resolution.Disposition != BackgroundJobProbeDisposition.RolledBack)
            {
                FinishRequest(request, true);
                return;
            }
            request.Probe = null; // Actual row lock established the original business rollback.
        }
        if (request.Kind == RequestKind.CapacityDeferred)
        {
            var operation = await _store.DeferCapacityAsync(request.Execution.Claim, _stop.Token).ConfigureAwait(false);
            _controlOperation = operation;
            if (!await QualifyControlAsync(operation).ConfigureAwait(false)) return;
            if (!RetainFailedRequest(request, operation)) FinishRequest(request, true);
        }
        else if (request.Kind == RequestKind.InvalidEnvelope)
        {
            var operation = await _store.RequestInvalidEnvelopeAsync(request.Execution.Claim, _stop.Token).ConfigureAwait(false);
            _controlOperation = operation;
            if (!await QualifyControlAsync(operation).ConfigureAwait(false)) return;
            if (!RetainFailedRequest(request, operation)) FinishRequest(request, true);
        }
        else if (request.Kind == RequestKind.Business && request.Execution.Identity is { } identity && request.Decision is { } decision)
        {
            var operation = await _store.FinishKnownRollbackAsync(identity, decision, _stop.Token).ConfigureAwait(false);
            _controlOperation = operation;
            if (operation.CommitAcknowledged && operation.Value is { Phase: "pending" }) ObserveRequestRetry(request);
            if (!await QualifyControlAsync(operation).ConfigureAwait(false)) return;
            if (!RetainFailedRequest(request, operation))
            {
                FinishRequest(request, true);
            }
        }
        else FinishRequest(request, true);
        _controlOperation = null;
    }

    private async Task ProcessFinalizerRequestAsync(CompletionRequest request)
    {
        if (request.Probe is { } probe)
        {
            _controlProbe = await _store.ProbeCommitAsync(probe, _stop.Token).ConfigureAwait(false);
            if (!_controlProbe.CleanupConfirmed) { SealControl(); return; }
            var observation = _controlProbe;
            _controlProbe = null;
            ObserveRequestHistory(request, probe, observation);
            if (observation.Failed || observation.Value is null
                || observation.Value.Disposition is BackgroundJobProbeDisposition.Unresolved or BackgroundJobProbeDisposition.Missing)
            {
                lock (_sync) { _controlFault = true; _unknown++; request.Due = Later(_bounds.RecoveryProbeInterval); }
                return;
            }
            if (observation.Value.Disposition != BackgroundJobProbeDisposition.RolledBack)
            {
                // Changed owner is only the end of this old authority. It is
                // never interpreted as the original finalizer/status success.
                FinishRequest(request, true);
                return;
            }
            request.Probe = null;
        }
        if (request.Execution.Identity is not { } identity || request.FinalizerResult is not { } result)
            throw new InvalidOperationException("Finalizer request lost its exact ordinary origin.");
        // Preserve the concrete actual task before its first asynchronous wait.
        // Replacement is allowed only after the previous result cleaned and,
        // when COMMIT was uncertain, its exact locked probe proved rollback.
        var publication = new TaskCompletionSource<Task<OwnedBackgroundJobControl<BackgroundJobControlChange>>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var actual = publication.Task.Unwrap();
        lock (_sync)
        {
            if (_stopping) { FinishRequest(request, true); return; }
            // Stop must see the retained slot before any synchronous SQL prefix.
            request.FinalizerTask = actual;
            _controlOperation = request;
        }
        try { publication.TrySetResult(_store.FinishExternalFinalizeAsync(identity, result, request.FinalizerHistory, _stop.Token)); }
        catch (Exception failure) when (Catchable(failure)) { publication.TrySetException(failure); }
        OwnedBackgroundJobControl<BackgroundJobControlChange> operation;
        try { operation = await actual.ConfigureAwait(false); }
        catch (Exception failure) when (Catchable(failure))
        {
            // The actual failed task remains on the retained request slot.
            SealControl();
            FinishRequest(request, false);
            return;
        }
        _controlOperation = operation;
        if (operation.CommitAcknowledged && operation.Value is { Phase: "finalize_pending" }) ObserveRequestRetry(request);
        if (!await QualifyControlAsync(operation).ConfigureAwait(false)) return;
        if (operation.CommitUncertain && operation.Probes.Count != 1)
        {
            SealControl();
            FinishRequest(request, false);
            return;
        }
        if (!RetainFailedRequest(request, operation))
        {
            FinishRequest(request, true);
        }
        _controlOperation = null;
    }

    private bool RetainFailedRequest(CompletionRequest request, OwnedBackgroundJobControl<BackgroundJobControlChange> operation)
    {
        if (!operation.CommitUncertain && !operation.Failed) return false;
        // Positive resource cleanup permits another bounded control operation;
        // it does not settle or replace the original business decision.
        if (operation.CommitUncertain) request.Probe = operation.Probes.SingleOrDefault();
        request.Due = Later(_bounds.RecoveryProbeInterval);
        return true;
    }

    private async Task ProcessCapacityRequestAsync(CompletionRequest request)
    {
        var cleanup = request.Cleanup!;
        if (request.Probe is { } probe)
        {
            _controlProbe = await _store.ProbeCommitAsync(probe, _stop.Token).ConfigureAwait(false);
            if (!_controlProbe.CleanupConfirmed) { SealControl(); return; }
            var result = _controlProbe.Value;
            if (_controlProbe.Failed || result is null
                || result.Disposition is BackgroundJobProbeDisposition.Unresolved or BackgroundJobProbeDisposition.Missing)
            {
                lock (_sync) { _controlFault = true; _unknown++; request.Due = Later(_bounds.RecoveryProbeInterval); }
                _controlProbe = null;
                return;
            }
            ObserveRequestHistory(request, probe, _controlProbe);
            _controlProbe = null;
            request.Probe = null;
            if (probe.Boundary == BackgroundJobCommitBoundary.Admission
                && result.Disposition == BackgroundJobProbeDisposition.ChangedOwner)
            {
                // Changed current authority is not absence of the original
                // admission. Keep its locked historical obligation intact.
                request.Probe = probe;
                request.Due = Later(_bounds.RecoveryProbeInterval);
                return;
            }
            if (probe.Boundary == BackgroundJobCommitBoundary.TransactionalCleanup)
            {
                request.CleanupCommitted = result.Disposition == BackgroundJobProbeDisposition.CapacityCleanupCommitted;
                if (request.CleanupCommitted && !result.CurrentRecoveryAuthority
                    && (result.WakeGeneration != probe.WakeGeneration || result.CurrentAttemptId != probe.AttemptId
                        || result.Phase is "pending" or "terminal_pending" or "completed"))
                {
                    FinishRequest(request, true); // Locked settled history and actual changed routing ownership.
                    return;
                }
                if (!result.CurrentRecoveryAuthority) request.Recovery = null;
                if (!request.CleanupCommitted) request.Checkpoint = null;
            }
            else if (probe.Boundary == BackgroundJobCommitBoundary.CapacityRecovery)
            {
                request.Recovery = result.RecoveryReceipt;
                request.Acquisition = null;
            }
            else if (result.Disposition is BackgroundJobProbeDisposition.BusinessCompleted or BackgroundJobProbeDisposition.TerminalCompleted
                || probe.Boundary == BackgroundJobCommitBoundary.Admission && result.Disposition is BackgroundJobProbeDisposition.RolledBack
                    or BackgroundJobProbeDisposition.AdmissionReleased)
            {
                FinishRequest(request, true);
                return;
            }
            else if (probe.Boundary == BackgroundJobCommitBoundary.Control && result.Disposition is BackgroundJobProbeDisposition.ControlCommitted
                or BackgroundJobProbeDisposition.ChangedOwner)
            {
                FinishRequest(request, true);
                return;
            }
        }
        if (request.Checkpoint is { } checkpoint && !request.CleanupCommitted)
        {
            if (!checkpoint.Completion.IsCompleted)
            {
                lock (_sync)
                {
                    if (checkpoint.TimelyReturn.IsCompletedSuccessfully && !checkpoint.TimelyReturn.Result) _controlFault = true;
                    request.Due = Later(_bounds.ControlPoll);
                }
                return;
            }
            var result = await checkpoint.Completion.ConfigureAwait(false);
            if (result.CleanupFailed) { SealControl(); return; }
            if (result.CommitAcknowledged) request.CleanupCommitted = true;
            else if (result.DurableOutcomeUncertain)
            {
                request.Probe = BackgroundJobCommitProbe.ForCapacityCleanup(checkpoint.Recovery, cleanup.Evidence);
                request.Due = Later(_bounds.RecoveryProbeInterval);
                lock (_sync) { _controlFault = true; _unknown++; }
                return;
            }
            else
            {
                // A returned task alone is insufficient for replacement. The
                // operation result above positively joined its real cleanup.
                request.Checkpoint = null;
                request.Probe = BackgroundJobCommitProbe.ForCapacityCleanup(checkpoint.Recovery, cleanup.Evidence);
                request.Due = Later(_bounds.RecoveryProbeInterval);
                if (!result.KnownRollback) { SealControl(); return; }
                return;
            }
        }
        if (request.Recovery is null)
        {
            var acquired = await _store.AcquireCapacityRecoveryAsync(cleanup, _stop.Token).ConfigureAwait(false);
            request.Acquisition = acquired; // Retain the private pre-ACK candidate across uncertainty.
            _controlOperation = acquired;
            if (!await QualifyControlAsync(acquired).ConfigureAwait(false)) return;
            _controlOperation = null;
            if (acquired.CommitUncertain)
            {
                request.Probe = acquired.Probes.SingleOrDefault();
                request.Due = Later(_bounds.RecoveryProbeInterval);
                return;
            }
            request.Recovery = acquired.Value;
            // Keep the last cleaned failure in this bounded slot until its
            // replacement is published or recovery authority is confirmed.
            if (request.Recovery is not null) request.Acquisition = null;
            if (request.Recovery is null)
            {
                // A peer may settle routing after the last historical probe
                // unlocked. The original generation can then never be acquired
                // again; recheck exact committed history instead of spinning.
                if (request.CleanupCommitted && request.Checkpoint is { } committed)
                    request.Probe = BackgroundJobCommitProbe.ForCapacityCleanup(committed.Recovery, cleanup.Evidence);
                request.Due = Later(_bounds.RecoveryProbeInterval);
                return;
            }
        }
        cleanup.RequireRecovery(request.Recovery);
        if (!request.CleanupCommitted)
        {
            lock (_sync)
            {
                if (_stopping || !_gate.TryAdmit()) return;
                var operation = new BackgroundJobCapacityCheckpointOperation(_executor, request.Recovery, cleanup,
                    _gate.RunStop, _bounds, _time);
                request.Execution.HoldCapacityCheckpoint(operation);
                request.Checkpoint = operation;
                operation.Start();
                request.Due = Later(_bounds.ControlPoll);
            }
            return;
        }
        var decision = request.Decision!;
        var finished = await _store.FinishCapacityRecoveryAsync(request.Recovery, decision, _stop.Token).ConfigureAwait(false);
        _controlOperation = finished;
        if (finished.CommitAcknowledged && finished.Value is { Phase: "pending" }) ObserveRequestRetry(request);
        if (!await QualifyControlAsync(finished).ConfigureAwait(false)) return;
        _controlOperation = null;
        if (RetainFailedRequest(request, finished)) return;
        if (finished.Value is not null)
        {
            FinishRequest(request, true);
        }
        else
        {
            // Expired/taken-over recovery authority cannot route. Preserve the
            // historical checkpoint fact while obtaining fresh exact authority.
            request.Probe = BackgroundJobCommitProbe.ForCapacityCleanup(request.Recovery, cleanup.Evidence);
            request.Due = Later(_bounds.RecoveryProbeInterval);
        }
    }

    private Task<bool> QualifyControlAsync<T>(OwnedBackgroundJobControl<T> operation, bool retainPendingProbes = false)
    {
        lock (_sync) { _controlFault = operation.Failed; if (operation.CommitUncertain) _unknown++; }
        if (!operation.CleanupConfirmed)
        {
            // Keep the original actual resources and its immutable probes in
            // the main slot. No disposal failure grants replacement issuance.
            SealControl();
            return Task.FromResult(false);
        }
        if (retainPendingProbes && operation.CommitUncertain && operation.Probes.Count != 0)
        {
            lock (_sync)
            {
                if (_pendingControl is not null || operation.Probes.Count > BackgroundJobControlLimits.MaximumProbeBatch)
                { _cleanupFault = true; _controlSealed = true; return Task.FromResult(false); }
                _pendingControl = new(operation.Probes, _time.GetTimestamp());
            }
        }
        return Task.FromResult(true);
    }

    private async Task RunTerminalAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (!await AdvanceTerminalNoStartAsync().ConfigureAwait(false)) return;
                PendingCommitCursor? pending;
                lock (_sync) pending = _pendingTerminal;
                if (pending is not null)
                {
                    if (!await ProbePendingAsync(pending, terminal: true).ConfigureAwait(false)) return;
                    lock (_sync) _terminalProgress = _time.GetTimestamp();
                    await PauseAsync().ConfigureAwait(false);
                    continue;
                }
                var acquired = await _terminal.TryClaimTerminalAsync(RetainedIds(), _stop.Token).ConfigureAwait(false);
                _terminalOperation = acquired;
                if (!await QualifyTerminalAsync(acquired).ConfigureAwait(false)) return;
                if (acquired.Value is { } claim)
                {
                    _terminalActivity = _executor.Observation.CreateActivity(JobTraceOperation.Terminal, default);
                    _terminalActivity.Start();
                    var operation = await _terminal.TryTerminalizeAsync(claim, _terminalActivity.Id!, _stop.Token).ConfigureAwait(false);
                    _terminalOperation = operation;
                    if (operation.CommitAcknowledged && operation.Value is not null) lock (_sync) _terminalized++;
                    if (!await QualifyTerminalAsync(operation).ConfigureAwait(false)) return;
                    if (operation.KnownRollback && operation.Failed)
                    {
                        var backoff = await _terminal.BackoffKnownAuditRollbackAsync(claim, _stop.Token).ConfigureAwait(false);
                        _terminalOperation = backoff;
                        if (!await QualifyTerminalAsync(backoff).ConfigureAwait(false)) return;
                    }
                    _terminalActivity.Dispose();
                    _terminalActivity = null;
                }
                _terminalOperation = null;
                lock (_sync) _terminalProgress = _time.GetTimestamp();
                await PauseAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception failure) when (Catchable(failure))
            {
                lock (_sync) _terminalFault = true;
                if (_terminalActivity is not null) { RetainCleanup(); return; }
                await PauseAsync().ConfigureAwait(false);
            }
        }
    }

    private Task<bool> QualifyTerminalAsync<T>(OwnedBackgroundJobControl<T> operation)
    {
        lock (_sync) { _terminalFault = operation.Failed; if (operation.CommitUncertain) _unknown++; }
        if (!operation.CleanupConfirmed)
        {
            // The original result retains both failed cleanup and unresolved
            // durable facts. Neither a caller timeout nor cleanup is a probe.
            RetainCleanup();
            return Task.FromResult(false);
        }
        if (operation.CommitUncertain && operation.Probes.Count != 0)
        {
            lock (_sync)
            {
                if (_pendingTerminal is not null || operation.Probes.Count != 1)
                { _cleanupFault = true; _terminalFault = true; return Task.FromResult(false); }
                _pendingTerminal = new(operation.Probes, _time.GetTimestamp());
            }
        }
        return Task.FromResult(true);
    }

    private async Task<bool> ProbePendingAsync(PendingCommitCursor cursor, bool terminal)
    {
        BackgroundJobCommitProbe probe;
        lock (_sync)
        {
            if (_time.GetTimestamp() < cursor.Due) return true;
            probe = cursor.Probes[cursor.Index];
        }
        // This is the lane's one fresh probe slot. A failed read probe never
        // produces another probe or consumes a business attempt.
        var observed = await _store.ProbeCommitAsync(probe, _stop.Token).ConfigureAwait(false);
        if (terminal) _terminalProbe = observed; else _controlProbe = observed;
        if (!observed.CleanupConfirmed)
        {
            if (terminal) RetainCleanup(); else SealControl();
            return false;
        }
        lock (_sync)
        {
            if (observed.Failed || observed.Value is null
                || observed.Value.Disposition is BackgroundJobProbeDisposition.Unresolved or BackgroundJobProbeDisposition.Missing)
            {
                cursor.Due = Later(_bounds.RecoveryProbeInterval);
                if (terminal) _terminalFault = true; else _controlFault = true;
            }
            else
            {
                if (terminal && observed.Value.Disposition == BackgroundJobProbeDisposition.TerminalCompleted) _terminalized++;
                if (!terminal && observed.Value.Disposition == BackgroundJobProbeDisposition.ControlCommitted
                    && IsRetryTransition(probe)) _retries++;
                cursor.Index++;
                cursor.Due = _time.GetTimestamp();
                if (cursor.Index == cursor.Probes.Count)
                {
                    if (terminal) { _pendingTerminal = null; _terminalFault = false; }
                    else { _pendingControl = null; _controlFault = false; }
                }
            }
        }
        // Only the positively cleaned concrete read is forgotten. Its failed
        // or missing result leaves the original cursor and job exclusions live.
        if (terminal) _terminalProbe = null; else _controlProbe = null;
        return true;
    }

    private async Task RelayStopAsync()
    {
        await _stopRequested.Task.ConfigureAwait(false);
        BackgroundJobExecutionControl[] executions;
        CompletionRequest[] requests;
        BackgroundJobTerminalNoStartOperation? terminalNoStart;
        lock (_sync)
        {
            executions = _executions.Values.ToArray();
            requests = _requests.ToArray();
            terminalNoStart = _terminalNoStart;
        }
        terminalNoStart?.RequestStop();
        foreach (var execution in executions) execution.CloseExecution();
        // Business work has already returned before its completion request is
        // queued. Host stop leaves its durable lease for fenced future recovery.
        foreach (var request in requests) request.Checkpoint?.RequestStop();
        try { _cancellation = _stop.CancelAsync(); await _cancellation.ConfigureAwait(false); }
        catch (Exception failure) when (Catchable(failure)) { RetainCleanup(); }
        foreach (var request in requests)
        {
            var clean = true;
            if (request.Checkpoint is { } checkpoint)
            {
                var result = await checkpoint.Completion.ConfigureAwait(false);
                clean = !result.CleanupFailed;
            }
            if (request.FinalizerTask is { } finalizer)
            {
                try { clean &= (await finalizer.ConfigureAwait(false)).CleanupConfirmed; }
                catch (Exception failure) when (Catchable(failure)) { clean = false; }
            }
            if (request.NoPlanStatusTask is { } noPlanStatus)
            {
                try { clean &= (await noPlanStatus.ConfigureAwait(false)).CleanupConfirmed; }
                catch (Exception failure) when (Catchable(failure)) { clean = false; }
            }
            if (request.NoPlanProbeTask is { } noPlanProbe)
            {
                try { clean &= (await noPlanProbe.ConfigureAwait(false)).CleanupConfirmed; }
                catch (Exception failure) when (Catchable(failure)) { clean = false; }
            }
            if (!clean) RetainCleanup();
            FinishRequest(request, clean);
        }
    }

    private async Task ObserveCompletionAsync()
    {
        try
        {
            var loops = await _loopsPublished.Task.ConfigureAwait(false);
            await Task.WhenAll(loops).ConfigureAwait(false);
            await _stopRelay.ConfigureAwait(false);
            await _recurrence.Completion.ConfigureAwait(false);
            BackgroundJobTerminalNoStartOperation? terminalNoStart;
            lock (_sync) terminalNoStart = _terminalNoStart;
            if (terminalNoStart is not null
                && !await terminalNoStart.JoinAfterLaneStoppedAsync().ConfigureAwait(false)) RetainCleanup();
            _stop.Dispose();
            lock (_sync) { if (!_cleanupFault) _completion.TrySetResult(); }
        }
        catch (Exception failure) when (Catchable(failure)) { RetainCleanup(); }
    }

    private Guid[] RetainedIds()
    {
        lock (_sync) return _executions.Keys.Concat(_requests.Select(request => request.Execution.JobId.Value))
            .Concat(_pendingControl?.Probes.Select(probe => probe.JobId) ?? [])
            .Concat(_pendingTerminal?.Probes.Select(probe => probe.JobId) ?? [])
            .Concat(_terminalNoStart?.RetainedIds ?? [])
            .Concat(RetainedOperationIds(_controlOperation)).Concat(RetainedOperationIds(_terminalOperation))
            .Distinct().ToArray();
    }

    private static IEnumerable<Guid> RetainedOperationIds(object? operation) => operation switch
    {
        OwnedBackgroundJobControl<BackgroundJobControlChange> { CleanupConfirmed: false } owned => owned.Probes.Select(probe => probe.JobId),
        OwnedBackgroundJobControl<IReadOnlyList<BackgroundJobControlChange>> { CleanupConfirmed: false } owned => owned.Probes.Select(probe => probe.JobId),
        OwnedBackgroundJobControl<BackgroundJobTerminalClaim> { CleanupConfirmed: false } owned => owned.Probes.Select(probe => probe.JobId),
        _ => [],
    };

    private void FinishRequest(CompletionRequest request, bool cleanup)
    {
        request.Cleanup?.SourceOwner.CloseIssuance();
        lock (_sync) _requests.Remove(request);
        request.Completion.TrySetResult(cleanup);
    }

    private void ObserveRequestRetry(CompletionRequest request)
    {
        lock (_sync)
        {
            if (request.RetryObserved) return;
            request.RetryObserved = true;
            _retries++;
        }
    }

    private void ObserveRequestHistory(CompletionRequest request, BackgroundJobCommitProbe probe,
        OwnedBackgroundJobControl<BackgroundJobProbeResult> observation)
    {
        if (!observation.CleanupConfirmed || observation.Failed || observation.Value is not { } value) return;
        if (value.Disposition == BackgroundJobProbeDisposition.ControlCommitted && IsRetryTransition(probe))
            ObserveRequestRetry(request);
        else if (request.Kind == RequestKind.Renewal && probe.Boundary == BackgroundJobCommitBoundary.Renewal
            && value.Disposition == BackgroundJobProbeDisposition.Renewed)
            lock (_sync) _renewalsConfirmed++;
    }

    // Clearing an expired routing token leaves the same generation pending.
    // It is bridge repair, not a new counted business/finalizer retry.
    private static bool IsRetryTransition(BackgroundJobCommitProbe probe) =>
        probe.Boundary is BackgroundJobCommitBoundary.Control or BackgroundJobCommitBoundary.ExternalKnownRetry
            or BackgroundJobCommitBoundary.ExternalFinalizeDisposition
        && probe.ExpectedPhase is "pending" or "finalize_pending"
        && probe.ExpectedGeneration is { } generation && generation > 0 && generation - 1 == probe.WakeGeneration;

    internal void ObserveExternalRetry() { lock (_sync) _retries++; }

    private async Task PauseAsync()
    {
        try { await Task.Delay(_bounds.ControlPoll, _time, _stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private long Later(TimeSpan delay) => checked(_time.GetTimestamp() + (long)(delay.TotalSeconds * _time.TimestampFrequency));
    private bool Fresh(long? timestamp) => timestamp is { } value && _time.GetElapsedTime(value, _time.GetTimestamp()) <= _bounds.ProgressTimeout;
    private static int PendingCount(PendingCommitCursor? cursor) => cursor is null ? 0 : cursor.Probes.Count - cursor.Index;
    private void RetainCleanup() { lock (_sync) _cleanupFault = true; }
    private void SealControl() { lock (_sync) { _controlSealed = true; _cleanupFault = true; _controlFault = true; } }
    private static bool Catchable(Exception failure) => BackgroundJobControlResources.Catchable(failure);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private enum RequestKind { Admission, Business, InvalidEnvelope, Renewal, CapacityDeferred, Finalizer, NoPlanTerminal }
    private sealed class CompletionRequest(BackgroundJobExecutionControl execution, RequestKind kind,
        BackgroundJobCommitProbe? probe, BackgroundJobFailureDecision? decision, long due,
        BackgroundJobActualCleanup? cleanup = null)
    {
        internal BackgroundJobExecutionControl Execution { get; } = execution;
        internal RequestKind Kind { get; } = kind;
        internal BackgroundJobCommitProbe? Probe { get; set; } = probe;
        internal BackgroundJobFailureDecision? Decision { get; } = decision;
        internal BackgroundJobActualCleanup? Cleanup { get; } = cleanup;
        internal BackgroundJobCapacityRecoveryReceipt? Recovery { get; set; }
        internal OwnedBackgroundJobControl<BackgroundJobCapacityRecoveryReceipt>? Acquisition { get; set; }
        internal BackgroundJobCapacityCheckpointOperation? Checkpoint { get; set; }
        internal JobBusinessExecutionResult? FinalizerResult { get; init; }
        internal OwnedBackgroundJobControl<BackgroundJobCheckpointHistoryObservation>? FinalizerHistory { get; init; }
        internal Task<OwnedBackgroundJobControl<BackgroundJobControlChange>>? FinalizerTask { get; set; }
        internal JobBusinessExecutionResult? NoPlanResult { get; init; }
        internal BackgroundJobCommitProbe? NoPlanOrigin { get; init; }
        internal Task<OwnedBackgroundJobControl<BackgroundJobControlChange>>? NoPlanStatusTask { get; set; }
        internal Task<OwnedBackgroundJobControl<BackgroundJobProbeResult>>? NoPlanProbeTask { get; set; }
        internal bool CleanupCommitted { get; set; }
        internal bool RetryObserved { get; set; }
        internal long Due { get; set; } = due;
        internal TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class PendingCommitCursor(IReadOnlyList<BackgroundJobCommitProbe> probes, long due)
    {
        internal System.Collections.ObjectModel.ReadOnlyCollection<BackgroundJobCommitProbe> Probes { get; } = Array.AsReadOnly(probes.ToArray());
        internal int Index { get; set; }
        internal long Due { get; set; } = due;
    }
}
