using System.Diagnostics;
using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace LearnStack.Infrastructure.Jobs;

public sealed partial class JobExecutor
{
    internal Task<JobExternalCheckpointResult> RecordExternalArmAsync(ConfirmedStartReceipt original,
        JobExternalReadHandle read, BackgroundJobCommitGate gate, CancellationToken token)
    {
        var snapshot = read.Require(original);
        if (snapshot.PlanJson is null || snapshot.PlanFingerprint is null || snapshot.OperationId is null
            || snapshot.ExecuteIntentRecorded || snapshot.NoStartRecorded || snapshot.OutcomeJson is not null)
            throw new InvalidOperationException("Arm requires its original prepared app-read plan.");
        var command = new RecordBackgroundJobArmCommand(original, gate, snapshot.PlanFingerprint, snapshot.OperationId.Value);
        return WriteExternalCheckpointAsync(command, null, snapshot.Envelope!, token);
    }

    internal Task<JobExternalCheckpointResult> RecordExternalOutcomeAsync(ConfirmedStartReceipt original,
        JobExternalReadHandle read, BackgroundJobCommitGate gate, BackgroundJobRetainedExternalObservation observation,
        CancellationToken token)
    {
        var snapshot = read.Require(original);
        RequireCompletedObservation(snapshot, original.AttemptId, observation);
        var command = new RecordBackgroundJobOutcomeCommand(original, gate, observation.PlanFingerprint, observation.OperationId,
            observation.OutcomeJson!, observation.State, observation.FailurePolicy, observation.QuiescenceId!.Value,
            observation.QuiescenceKind!.Value);
        return WriteExternalCheckpointAsync(null, command, snapshot.Envelope!, token);
    }

    internal Task<JobExternalCheckpointResult> RecordExternalOutcomeAsync(BackgroundJobExternalRecoveryBinding recovery,
        JobExternalReadHandle read, BackgroundJobCommitGate gate, BackgroundJobRetainedExternalObservation observation,
        CancellationToken token)
    {
        var snapshot = read.Require(recovery);
        RequireCompletedObservation(snapshot, recovery.Receipt.AttemptId, observation);
        var command = new RecordBackgroundJobOutcomeCommand(recovery, gate, observation.PlanFingerprint, observation.OperationId,
            observation.OutcomeJson!, observation.State, observation.FailurePolicy, observation.QuiescenceId!.Value,
            observation.QuiescenceKind!.Value);
        return WriteExternalCheckpointAsync(null, command, snapshot.Envelope!, token);
    }

    internal Task<JobExternalCheckpointResult> RecordExternalNoStartAsync(BackgroundJobExternalRecoveryBinding recovery,
        JobExternalReadHandle read, BackgroundJobCommitGate gate, CancellationToken token)
    {
        var snapshot = read.Require(recovery);
        if (recovery.Receipt.Purpose != BackgroundJobExternalRecoveryPurpose.Reconcile)
            throw new InvalidOperationException("Ordinary no-start requires reconciliation purpose.");
        return WriteExternalCheckpointAsync(null, RecordBackgroundJobOutcomeCommand.ForNoStart(recovery, gate),
            snapshot.Envelope!, token);
    }

    internal Task<JobExternalCheckpointResult> RecordExternalTerminalNoStartAsync(BackgroundJobExternalRecoveryBinding recovery,
        JobExternalReadHandle read, BackgroundJobCommitGate gate, CancellationToken token)
    {
        var snapshot = read.Require(recovery);
        if (recovery.Receipt.Purpose != BackgroundJobExternalRecoveryPurpose.TerminalNoStart
            || (snapshot.PlanJson is null ? snapshot.PlanFingerprint is not null || snapshot.OperationId is not null
                : recovery.Receipt.TerminalReason is not ("retired_definition" or "invalid_envelope")
                    || snapshot.PlanFingerprint is null || snapshot.OperationId is null)
            || snapshot.ExecuteIntentRecorded || snapshot.OutcomeJson is not null || snapshot.OutcomeState is not null
            || snapshot.FailurePolicy is not null || snapshot.SelectedNormalAttemptId is not null
            || snapshot.FinalizationStatus is not null || snapshot.FinalizationReason is not null)
            throw new InvalidOperationException("Terminal no-start requires its exact acknowledged unarmed cleanup read.");
        return WriteExternalCheckpointAsync(null, RecordBackgroundJobOutcomeCommand.ForTerminalNoStart(recovery, gate),
            snapshot.Envelope!, token);
    }

    internal Task<JobExternalCheckpointResult> SelectStoredExternalOutcomeAsync(BackgroundJobExternalRecoveryBinding recovery,
        JobExternalReadHandle read, BackgroundJobCommitGate gate, CancellationToken token)
    {
        var snapshot = read.Require(recovery);
        if (snapshot.PlanJson is null || snapshot.PlanFingerprint is null || snapshot.OperationId is null
            || snapshot.OutcomeJson is null || snapshot.OutcomeState is not { } state
            || snapshot.NoStartRecorded || !snapshot.ExecuteIntentRecorded
            || snapshot.SelectedNormalAttemptId is not null
            || BackgroundJobExternalPolicy.Select(state, snapshot.FailurePolicy, recovery.Receipt.NormalAttemptOrdinal)
                .Disposition != BackgroundJobKnownOutcomeDisposition.Finalize)
            throw new InvalidOperationException("Stored selection requires the exact completed final-selecting app-read facts.");
        return WriteExternalCheckpointAsync(null, RecordBackgroundJobOutcomeCommand.ForStoredCompleted(recovery, gate,
            snapshot.PlanFingerprint, snapshot.OperationId.Value), snapshot.Envelope!, token);
    }

    private static void RequireCompletedObservation(BackgroundJobExternalSnapshot snapshot,
        BackgroundJobAttemptId attempt, BackgroundJobRetainedExternalObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!observation.IsCompleted || observation.OutcomeJson is null || observation.QuiescenceId is null
            || observation.QuiescenceKind is null || observation.JobId != snapshot.Envelope!.JobId
            || observation.AttemptId != attempt || observation.PlanFingerprint != snapshot.PlanFingerprint
            || observation.OperationId != snapshot.OperationId)
            throw new InvalidOperationException("Outcome requires the exact completed observation of its app-read operation.");
    }

    /// <summary>
    /// Actual execution of the two sealed write requests only. The native owner
    /// supplies and retains its private cancellation and independently bounds its wait.
    /// </summary>
    private async Task<JobExternalCheckpointResult> WriteExternalCheckpointAsync(RecordBackgroundJobArmCommand? arm,
        RecordBackgroundJobOutcomeCommand? outcome, BackgroundJobEnvelope envelope,
        CancellationToken token)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        if ((arm is null) == (outcome is null)) throw new ArgumentException("An external checkpoint has one exact write.");
        var telemetry = Observation;
        var stageStarted = telemetry.StartStage();
        var metadata = arm?.Authority ?? outcome!.Authority;
        var gate = arm?.Gate ?? outcome!.Gate;
        var source = metadata.SourceOwner;
        var previous = accessor.Current;
        var previousActivity = Activity.Current;
        AsyncServiceScope? scope = null;
        IUnitOfWork? unit = null;
        IDisposable? authority = null;
        IDisposable? sourceBinding = null;
        Activity? activity = null;
        CancellationTokenRegistration? registration = null;
        Result<None>? response = null;
        var issued = false;
        var cleanupFailed = false;
        var retained = new List<JobStartCleanupOwnership>();
        try
        {
            token.ThrowIfCancellationRequested();
            registration = token.UnsafeRegister(StopExternalRead, metadata.Run);
            if (!metadata.MatchesEnvelope(envelope) || metadata.Run.IsClosed)
                throw new InvalidOperationException("The external checkpoint requires its open, exact app-read origin.");
            sourceBinding = source.Enter();
            source.RequireFreshScopeReady();
            var context = CreateContext(envelope);
            accessor.Current = context;
            _ = ActivityContext.TryParse(envelope.Traceparent, null, isRemote: true, out var parent);
            activity = telemetry.CreateActivity(JobTraceOperation.ExternalCheckpoint, parent);
            activity.Start();
            scope = scopes.CreateAsyncScope();
            unit = scope.Value.ServiceProvider.GetRequiredService<IUnitOfWork>();
            authority = arm is not null ? admission.EnterExternalCheckpoint(arm, unit)
                : admission.EnterExternalCheckpoint(outcome!, unit);
            BackgroundJobExecutionAdmission.RequireContext(context);
            token.ThrowIfCancellationRequested();
            if (!gate.TryIssue()) throw new OperationCanceledException("External checkpoint issuance is closed.", token);
            issued = true;
            var sender = scope.Value.ServiceProvider.GetRequiredService<ISender>();
            response = arm is not null ? await sender.Send(arm, token).ConfigureAwait(false)
                : await sender.Send(outcome!, token).ConfigureAwait(false);
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure))
        {
            // Preserve the exact request/gate and any physical ACK. Neither a
            // fault nor positive cleanup decides an unacknowledged COMMIT.
        }
        finally
        {
            if (scope is { } owned)
                cleanupFailed |= !await CleanupScopeAsync(owned, unit, null, retained).ConfigureAwait(false);
            try { activity?.Dispose(); }
            catch
            {
                cleanupFailed = true;
                retained.Add(new(null, null, null, activity, null));
            }
            try { Activity.Current = previousActivity; }
            catch
            {
                cleanupFailed = true;
                retained.Add(new(scope, unit, null, activity, authority, sourceBinding, registration));
            }
            try { accessor.Current = previous; }
            catch
            {
                cleanupFailed = true;
                retained.Add(new(scope, unit, null, activity, authority, sourceBinding, registration));
            }
            try { authority?.Dispose(); }
            catch
            {
                cleanupFailed = true;
                retained.Add(new(null, null, null, null, authority));
            }
            try { sourceBinding?.Dispose(); }
            catch
            {
                cleanupFailed = true;
                retained.Add(new(null, null, null, null, null, sourceBinding));
            }
            if (cleanupFailed) { source.CloseIssuance(); metadata.Run.Close(); }
            cleanupFailed |= !await source.JoinAuxiliaryCleanupAsync().ConfigureAwait(false);
            if (registration is { } callback)
            {
                try { await callback.DisposeAsync().ConfigureAwait(false); }
                catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure))
                {
                    cleanupFailed = true;
                    retained.Add(new(null, null, null, null, null, registration: callback));
                }
            }
            if (cleanupFailed) { source.CloseIssuance(); metadata.Run.Close(); }
        }
        if (token.IsCancellationRequested) metadata.Run.Close();
        var acknowledged = arm?.CommitAcknowledged ?? outcome!.CommitAcknowledged;
        var status = acknowledged ? JobExternalCheckpointStatus.Completed
            : !issued ? JobExternalCheckpointStatus.Refused
            : gate.CommitStarted && !gate.KnownRollback ? JobExternalCheckpointStatus.Indeterminate
            : metadata.Run.IsClosed || token.IsCancellationRequested || response is { IsFailure: true }
                ? JobExternalCheckpointStatus.Refused : JobExternalCheckpointStatus.Failed;
        var stage = arm is not null ? JobObservedStage.Arm
            : outcome!.IsStoredCompleted ? JobObservedStage.SelectedOutcome
            : gate.Kind == BackgroundJobCheckpointKind.NoStart ? JobObservedStage.NoStart : JobObservedStage.Outcome;
        telemetry.RecordStage(stageStarted, envelope.DefinitionKey, envelope.Profile, stage,
            status == JobExternalCheckpointStatus.Completed ? JobObservedOutcome.Acknowledged
                : status == JobExternalCheckpointStatus.Indeterminate ? JobObservedOutcome.UnknownCommit
                : status == JobExternalCheckpointStatus.Refused ? JobObservedOutcome.Refused : JobObservedOutcome.Failed, cleanupFailed);
        return new(status, arm, outcome, issued, cleanupFailed, retained.AsReadOnly());
    }
}
