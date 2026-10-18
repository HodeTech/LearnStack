using System.Diagnostics;
using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace LearnStack.Infrastructure.Jobs;

internal enum JobRecipientSubmissionStatus { Accepted, Ineligible, LostClaim, Canceled, Failed, Indeterminate }

internal sealed class JobRecipientSubmissionResult
{
    internal JobRecipientSubmissionResult(JobRecipientSubmissionStatus status, SubmitBackgroundJobRecipientCommand? command,
        bool cleanupFailed, JobApplicationSourceOwner sourceOwner, IReadOnlyList<JobStartCleanupOwnership> retainedCleanup)
    {
        Status = status;
        Command = command;
        CleanupFailed = cleanupFailed;
        SourceOwner = sourceOwner;
        RetainedCleanup = retainedCleanup;
    }

    internal JobRecipientSubmissionStatus Status { get; }
    internal SubmitBackgroundJobRecipientCommand? Command { get; }
    internal bool CleanupFailed { get; }
    internal JobApplicationSourceOwner SourceOwner { get; }
    internal IReadOnlyList<JobStartCleanupOwnership> RetainedCleanup { get; }
    internal Exception? Failure { get; init; }
}

public sealed partial class JobExecutor
{
    /// <summary>The actual owned task; a caller's finite wait does not dispose its live resources.</summary>
    internal async Task<JobRecipientSubmissionResult> ExecuteRecipientAsync(BackgroundJobRecipientClaim claim,
        BackgroundJobLifecycleProjection projection, BackgroundJobCommitGate gate, CancellationToken cancellationToken)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(projection);
        if (gate is not { Kind: BackgroundJobCheckpointKind.RecipientSubmission })
            throw new ArgumentException("Recipient execution requires its exact prebound stage.", nameof(gate));
        options.Validate();
        var telemetry = Observation;
        var stageStarted = telemetry.StartStage();
        var source = JobApplicationSourceOwner.Create();
        var previous = accessor.Current;
        var previousActivity = Activity.Current;
        AsyncServiceScope? scope = null;
        IUnitOfWork? unit = null;
        IDisposable? authority = null;
        IDisposable? sourceBinding = null;
        Activity? activity = null;
        CancellationTokenSource? deadline = null;
        CancellationTokenRegistration? registration = null;
        SubmitBackgroundJobRecipientCommand? command = null;
        Result<BackgroundJobId>? response = null;
        Exception? failure = null;
        var cleanupFailed = false;
        var retained = new List<JobStartCleanupOwnership>();
        try
        {
            if (!gate.TryIssue()) throw new OperationCanceledException("Recipient issuance is closed.", cancellationToken);
            sourceBinding = source.Enter();
            source.RequireFreshScopeReady();
            deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(options.SqlTimeout);
            registration = deadline.Token.UnsafeRegister(StopRecipient, gate);
            deadline.Token.ThrowIfCancellationRequested();
            activity = new Activity("job.recipient_submission").SetIdFormat(ActivityIdFormat.W3C).Start();
            var context = JobTenantContext.ForRecipient(claim.TenantId, claim.OrganizationId,
                activity.Id!, claim.RecurrenceKey.Split('.')[0]);
            accessor.Current = context;
            scope = scopes.CreateAsyncScope();
            unit = scope.Value.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var completion = scope.Value.ServiceProvider.GetRequiredService<BackgroundJobRecipientCompletionParticipant>();
            command = new(claim, gate, completion, projection);
            completion.Arm(command);
            authority = admission.EnterRecipient(command, unit);
            BackgroundJobExecutionAdmission.RequireContext(context);
            deadline.Token.ThrowIfCancellationRequested();
            response = await scope.Value.ServiceProvider.GetRequiredService<ISender>().Send(command, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception caught) when (BackgroundJobControlResources.Catchable(caught)) { failure = caught; }
        finally
        {
            if (scope is { } owned)
                cleanupFailed |= !await CleanupScopeAsync(owned, unit, deadline, retained).ConfigureAwait(false);
            try { activity?.Dispose(); }
            catch
            {
                cleanupFailed = true;
                retained.Add(new(null, null, null, activity, null));
            }
            try { Activity.Current = previousActivity; accessor.Current = previous; }
            catch
            {
                cleanupFailed = true;
                retained.Add(new(scope, unit, deadline, activity, authority, sourceBinding, registration));
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
            if (cleanupFailed) { source.CloseIssuance(); gate.Close(); }
            cleanupFailed |= !await source.JoinAuxiliaryCleanupAsync().ConfigureAwait(false);
            if (registration is { } callback)
            {
                try { await callback.DisposeAsync().ConfigureAwait(false); }
                catch (Exception caught) when (BackgroundJobControlResources.Catchable(caught))
                {
                    cleanupFailed = true;
                    retained.Add(new(null, null, null, null, null, registration: callback));
                }
            }
            try { deadline?.Dispose(); }
            catch
            {
                cleanupFailed = true;
                retained.Add(new(null, null, deadline, null, null));
            }
            if (cleanupFailed) { source.CloseIssuance(); gate.Close(); }
        }
        if (cancellationToken.IsCancellationRequested) gate.Close();
        var status = gate.CommitAcknowledged ? JobRecipientSubmissionStatus.Accepted
            : gate.CommitStarted && !gate.KnownRollback ? JobRecipientSubmissionStatus.Indeterminate
            : command?.Ineligible == true ? JobRecipientSubmissionStatus.Ineligible
            : gate.IsClosed ? JobRecipientSubmissionStatus.Canceled
            : response is { IsFailure: true } ? JobRecipientSubmissionStatus.LostClaim : JobRecipientSubmissionStatus.Failed;
        telemetry.RecordStage(stageStarted, null, "control", JobObservedStage.RecipientSubmission,
            status == JobRecipientSubmissionStatus.Accepted ? JobObservedOutcome.Acknowledged
                : status == JobRecipientSubmissionStatus.Indeterminate ? JobObservedOutcome.UnknownCommit
                : status == JobRecipientSubmissionStatus.Failed ? JobObservedOutcome.Failed : JobObservedOutcome.Refused,
            cleanupFailed || !source.CleanupConfirmed);
        return new(status, command, cleanupFailed || !source.CleanupConfirmed, source, retained.AsReadOnly()) { Failure = failure };
    }

    private static void StopRecipient(object? state) => ((BackgroundJobCommitGate)state!).Close();
}
