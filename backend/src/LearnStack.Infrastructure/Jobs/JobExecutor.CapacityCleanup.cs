using System.Diagnostics;
using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Persistence;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace LearnStack.Infrastructure.Jobs;

public sealed partial class JobExecutor
{
    internal async Task<JobCapacityCleanupResult> ExecuteCapacityCleanupAsync(BackgroundJobCapacityRecoveryReceipt recovery,
        BackgroundJobActualCleanup cleanup, BackgroundJobCommitGate gate, CancellationToken cancellationToken)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        cleanup.RequireRecovery(recovery);
        var telemetry = Observation;
        var stageStarted = telemetry.StartStage();
        var previous = accessor.Current;
        var previousActivity = Activity.Current;
        AsyncServiceScope? scope = null;
        IUnitOfWork? unit = null;
        IDisposable? authority = null;
        IDisposable? sourceBinding = null;
        Activity? activity = null;
        RecordBackgroundJobCapacityCleanupCommand? command = null;
        var cleanupFailed = false;
        var retained = new List<JobStartCleanupOwnership>();
        try
        {
            sourceBinding = cleanup.SourceOwner.Enter();
            cleanup.SourceOwner.RequireFreshScopeReady();
            cancellationToken.ThrowIfCancellationRequested();
            var context = CreateContext(cleanup.Envelope);
            accessor.Current = context;
            _ = ActivityContext.TryParse(cleanup.Envelope.Traceparent, null, isRemote: true, out var parent);
            activity = new Activity("job.capacity_cleanup").SetIdFormat(ActivityIdFormat.W3C)
                .SetParentId(parent.TraceId, parent.SpanId, parent.TraceFlags).Start();
            scope = scopes.CreateAsyncScope();
            unit = scope.Value.ServiceProvider.GetRequiredService<IUnitOfWork>();
            command = new(recovery, cleanup.Evidence, gate);
            authority = admission.EnterCapacityCleanup(command, unit);
            if (!ReferenceEquals(accessor.Current, context))
                throw new InvalidOperationException("Capacity cleanup must retain its exact job context.");
            BackgroundJobExecutionAdmission.RequireContext(context);
            cancellationToken.ThrowIfCancellationRequested();
            _ = await scope.Value.ServiceProvider.GetRequiredService<ISender>()
                .Send(command, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure))
        {
            // Only explicit owner COMMIT facts escape this finite framework boundary.
            if (command?.CommitAcknowledged == true) cleanupFailed = true;
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
            try { Activity.Current = previousActivity; accessor.Current = previous; }
            catch
            {
                cleanupFailed = true;
                retained.Add(new(scope, unit, null, activity, authority, sourceBinding));
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
            if (cleanupFailed) cleanup.SourceOwner.CloseIssuance();
            cleanupFailed |= !await cleanup.SourceOwner.JoinAuxiliaryCleanupAsync().ConfigureAwait(false);
            if (cleanupFailed) cleanup.SourceOwner.CloseIssuance();
        }
        var observed = command?.CommitAcknowledged == true ? JobObservedOutcome.Acknowledged
            : command?.CommitStarted == true && command.KnownRollback != true ? JobObservedOutcome.UnknownCommit
            : JobObservedOutcome.NotCommitted;
        telemetry.RecordStage(stageStarted, cleanup.Envelope.DefinitionKey, cleanup.Envelope.Profile,
            JobObservedStage.CapacityCleanup, observed, cleanupFailed);
        return new(command?.CommitAcknowledged == true, command?.CommitStarted == true,
            command?.KnownRollback == true || command?.CommitStarted != true && !cleanupFailed, cleanupFailed)
        {
            RetainedCleanup = retained.AsReadOnly(),
            SourceOwner = cleanup.SourceOwner,
        };
    }
}
