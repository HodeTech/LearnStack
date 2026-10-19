using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Infrastructure.Jobs;

internal sealed partial class BackgroundJobControl
{
    internal Task<bool> FinishNoPlanPrepareAsync(BackgroundJobExecutionControl execution, JobBusinessExecutionResult result)
    {
        if (execution.Identity is not { } identity)
            throw new InvalidOperationException("A NoPlan completion retains its original ordinary identity.");
        return Enqueue(new(execution, RequestKind.NoPlanTerminal, null, null, _time.GetTimestamp())
        {
            NoPlanResult = result,
            NoPlanOrigin = PostgresBackgroundJobControlStore.NoPlanTerminalRequestOrigin(
                identity, BackgroundJobTerminalReason.PermanentFailure),
        });
    }

    private async Task ProcessNoPlanTerminalRequestAsync(CompletionRequest request)
    {
        if (request.Execution.Identity is not { } identity || request.NoPlanResult is not { } result
            || request.NoPlanOrigin is not { } origin)
            throw new InvalidOperationException("NoPlan completion lost its immutable ordinary result or status origin.");
        if (request.Probe is { } probe)
        {
            if (probe != origin)
            {
                SealControl();
                FinishRequest(request, false);
                return;
            }
            var publication = new TaskCompletionSource<Task<OwnedBackgroundJobControl<BackgroundJobProbeResult>>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var actual = publication.Task.Unwrap();
            lock (_sync)
            {
                if (_stopping) { FinishRequest(request, true); return; }
                request.NoPlanProbeTask = actual;
                _controlOperation = request;
            }
            try { publication.TrySetResult(_store.ProbeCommitAsync(probe, _stop.Token)); }
            catch (Exception failure) when (Catchable(failure)) { publication.TrySetException(failure); }
            OwnedBackgroundJobControl<BackgroundJobProbeResult> observation;
            try { observation = await actual.ConfigureAwait(false); }
            catch (Exception failure) when (Catchable(failure))
            {
                SealControl();
                FinishRequest(request, false);
                return;
            }
            _controlProbe = observation;
            if (!observation.CleanupConfirmed) { SealControl(); return; }
            _controlProbe = null;
            _controlOperation = null;
            if (observation.Failed || observation.Value is null
                || observation.Value.Disposition is not (BackgroundJobProbeDisposition.ControlCommitted
                    or BackgroundJobProbeDisposition.ChangedOwner or BackgroundJobProbeDisposition.RolledBack))
            {
                lock (_sync) { _controlFault = true; _unknown++; request.Due = Later(_bounds.RecoveryProbeInterval); }
                return;
            }
            if (observation.Value.Disposition != BackgroundJobProbeDisposition.RolledBack)
            {
                // Changed ownership ends only this origin's authority. It is
                // not a claim that the old request committed or released work.
                FinishRequest(request, true);
                return;
            }
            request.Probe = null;
        }

        // Publish the exact task before the store's synchronous prefix. Failed
        // cleanup retains it; uncertain COMMIT permits only the original probe
        // until its locked history positively proves rollback.
        var statusPublication = new TaskCompletionSource<Task<OwnedBackgroundJobControl<BackgroundJobControlChange>>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var statusTask = statusPublication.Task.Unwrap();
        lock (_sync)
        {
            if (_stopping) { FinishRequest(request, true); return; }
            request.NoPlanStatusTask = statusTask;
            _controlOperation = request;
        }
        try
        {
            statusPublication.TrySetResult(_store.RequestNoPlanExternalTerminalAsync(identity,
                BackgroundJobTerminalReason.PermanentFailure, _stop.Token, result));
        }
        catch (Exception failure) when (Catchable(failure)) { statusPublication.TrySetException(failure); }
        OwnedBackgroundJobControl<BackgroundJobControlChange> operation;
        try { operation = await statusTask.ConfigureAwait(false); }
        catch (Exception failure) when (Catchable(failure))
        {
            SealControl();
            FinishRequest(request, false);
            return;
        }
        _controlOperation = operation;
        if (!await QualifyControlAsync(operation).ConfigureAwait(false)) return;
        if (operation.Probes.Count > 1 || operation.Probes.Any(candidate => candidate != origin)
            || operation.CommitUncertain && operation.Probes.Count != 1)
        {
            SealControl();
            FinishRequest(request, false);
            return;
        }
        if (!RetainFailedRequest(request, operation)) FinishRequest(request, true);
        _controlOperation = null;
    }
}
