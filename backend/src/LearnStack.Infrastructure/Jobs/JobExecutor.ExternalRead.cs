using System.Diagnostics;
using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace LearnStack.Infrastructure.Jobs;

public sealed partial class JobExecutor
{
    internal Task<JobExternalReadResult> ReadExternalStateAsync(ConfirmedStartReceipt original, CancellationToken token) =>
        ReadExternalStateCoreAsync(original, null, token);

    internal Task<JobExternalReadResult> ReadExternalStateAsync(BackgroundJobExternalRecoveryBinding recovery, CancellationToken token) =>
        ReadExternalStateCoreAsync(null, recovery, token);

    /// <summary>
    /// This is the actual task, not a finite-wait observer. Its invocation owner
    /// retains it and the supplied private cancellation until actual completion.
    /// </summary>
    private async Task<JobExternalReadResult> ReadExternalStateCoreAsync(ConfirmedStartReceipt? original,
        BackgroundJobExternalRecoveryBinding? recovery, CancellationToken token)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        if ((original is null) == (recovery is null)) throw new ArgumentException("An external read has one exact origin.");
        var request = original is not null ? new ReadBackgroundJobExternalStateQuery(original)
            : new ReadBackgroundJobExternalStateQuery(recovery!);
        var source = request.Authority.SourceOwner;
        var previous = accessor.Current;
        var previousActivity = Activity.Current;
        AsyncServiceScope? scope = null;
        IUnitOfWork? unit = null;
        IDisposable? authority = null;
        IDisposable? sourceBinding = null;
        Activity? activity = null;
        CancellationTokenRegistration? registration = null;
        Result<BackgroundJobExternalSnapshot>? response = null;
        Exception? failure = null;
        var cleanupFailed = false;
        var retained = new List<JobStartCleanupOwnership>();
        try
        {
            token.ThrowIfCancellationRequested();
            registration = token.UnsafeRegister(StopExternalRead, request.Authority.Run);
            if (request.Authority.Run.IsClosed) throw new OperationCanceledException("External reading is closed.", token);
            sourceBinding = source.Enter();
            source.RequireFreshScopeReady();
            var context = original is not null ? CreateContext(original.Envelope)
                : JobTenantContext.ForRecoveryRead(recovery!.Receipt.TenantId, recovery.Receipt.OrganizationId,
                    recovery.Receipt.JobId, recovery.Receipt.DefinitionKey.Split('.')[0]);
            accessor.Current = context;
            if (original is not null)
            {
                _ = ActivityContext.TryParse(original.Envelope.Traceparent, null, isRemote: true, out var parent);
                activity = new Activity("job.external_read").SetIdFormat(ActivityIdFormat.W3C)
                    .SetParentId(parent.TraceId, parent.SpanId, parent.TraceFlags);
                activity.Start();
            }
            scope = scopes.CreateAsyncScope();
            unit = scope.Value.ServiceProvider.GetRequiredService<IUnitOfWork>();
            authority = admission.EnterExternalCheckpoint(request, unit);
            BackgroundJobExecutionAdmission.RequireContext(context);
            token.ThrowIfCancellationRequested();
            response = await scope.Value.ServiceProvider.GetRequiredService<ISender>().Send(request, token).ConfigureAwait(false);
        }
        catch (Exception caught) when (BackgroundJobControlResources.Catchable(caught)) { failure = caught; }
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
            if (cleanupFailed) { source.CloseIssuance(); request.Authority.Run.Close(); }
            cleanupFailed |= !await source.JoinAuxiliaryCleanupAsync().ConfigureAwait(false);
            // Cancellation remains effective through every actual cleanup step,
            // including arbitrary Activity observers and auxiliary task joins.
            if (registration is { } callback)
            {
                try { await callback.DisposeAsync().ConfigureAwait(false); }
                catch (Exception caught) when (BackgroundJobControlResources.Catchable(caught))
                {
                    cleanupFailed = true;
                    retained.Add(new(null, null, null, null, null, registration: callback));
                }
            }
            if (cleanupFailed) { source.CloseIssuance(); request.Authority.Run.Close(); }
        }
        // Covers pre-cancelled entry and cancellation after deregistration.
        // A real read acknowledgement remains historical fact after this stop.
        if (token.IsCancellationRequested) request.Authority.Run.Close();
        JobExternalReadHandle? handle = null;
        if (failure is null && !cleanupFailed && source.CleanupConfirmed && request.CommitAcknowledged
            && response is { IsSuccess: true } && ReferenceEquals(response.Value, request.RecordedSnapshot))
            handle = original is not null ? new(original, request) : new(recovery!, request);
        var status = request.CommitAcknowledged ? JobExternalReadStatus.Completed
            : request.Authority.Run.IsClosed || token.IsCancellationRequested
                ? JobExternalReadStatus.Refused : JobExternalReadStatus.Failed;
        return new(status, handle, cleanupFailed, request, retained.AsReadOnly());
    }

    private static void StopExternalRead(object? state) => ((BackgroundJobRunGate)state!).Close();
}
