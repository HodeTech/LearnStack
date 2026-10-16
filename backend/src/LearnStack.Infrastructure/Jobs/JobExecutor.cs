using System.Diagnostics;
using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace LearnStack.Infrastructure.Jobs;

public enum JobStartExecutionOutcome
{
    Started,
    LostClaim,
    InvalidEnvelope,
    Canceled,
    Failed,
    Indeterminate,
}

public sealed record JobStartExecutionResult(JobStartExecutionOutcome Outcome,
    ConfirmedStartReceipt? Receipt, bool CleanupFailed, JobApplicationSourceOwner SourceOwner)
{
    internal IReadOnlyList<JobStartCleanupOwnership> RetainedCleanup { get; init; } = [];
}

/// <summary>A faulted cleanup keeps its concrete resources; a completed async task alone does not.</summary>
internal sealed class JobStartCleanupOwnership(AsyncServiceScope? scope, IUnitOfWork? unit,
    CancellationTokenSource? deadline, Activity? activity, IDisposable? authority, IDisposable? sourceBinding = null)
{
    internal AsyncServiceScope? Scope { get; } = scope;
    internal IUnitOfWork? Unit { get; } = unit;
    internal CancellationTokenSource? Deadline { get; } = deadline;
    internal Activity? Activity { get; } = activity;
    internal IDisposable? Authority { get; } = authority;
    internal IDisposable? SourceBinding { get; } = sourceBinding;
}

/// <summary>Owns separate framework admission and ordinary business scopes through actual cleanup.</summary>
public sealed partial class JobExecutor(IServiceScopeFactory scopes, ITenantContextAccessor accessor,
    BackgroundJobExecutionAdmission admission, BackgroundJobRuntimeOptions options)
{
    internal IServiceScopeFactory ScopeFactory => scopes;

    public Task<JobStartExecutionResult> ExecuteAsync(BackgroundJobRoutingClaim claim,
        Func<bool>? startGate = null, CancellationToken cancellationToken = default) =>
        ExecutePublishedAsync(claim, new BackgroundJobStartPublication(), startGate, cancellationToken);

    internal async Task<JobStartExecutionResult> ExecutePublishedAsync(BackgroundJobRoutingClaim claim,
        BackgroundJobStartPublication publication, Func<bool>? startGate, CancellationToken cancellationToken)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(publication);
        options.Validate();
        var sourceOwner = JobApplicationSourceOwner.Create();
        var sourceBinding = sourceOwner.Enter();
        var previous = accessor.Current;
        var previousActivity = Activity.Current;
        AsyncServiceScope? scope = null;
        IDisposable? authority = null;
        Activity? activity = null;
        AdmitBackgroundJobAttemptCommand? command = null;
        IUnitOfWork? unit = null;
        CancellationTokenSource? sqlDeadline = null;
        var outcome = JobStartExecutionOutcome.Failed;
        var cleanupFailed = false;
        var retainedCleanup = new List<JobStartCleanupOwnership>();
        try
        {
            accessor.Current = null;
            authority = admission.EnterFramework(claim, startGate);
            cancellationToken.ThrowIfCancellationRequested();
            sourceOwner.RequireFreshScopeReady();
            scope = scopes.CreateAsyncScope();
            unit = scope.Value.ServiceProvider.GetRequiredService<IUnitOfWork>();
            sqlDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sqlDeadline.CancelAfter(options.SqlTimeout);
            var read = await scope.Value.ServiceProvider.GetRequiredService<ISender>()
                .Send(new ReadBackgroundJobEnvelopeQuery(claim), sqlDeadline.Token).ConfigureAwait(false);
            if (read.IsFailure)
            {
                outcome = JobStartExecutionOutcome.InvalidEnvelope;
            }
            else
            {
                // The validated read owns no service subsequently used by admission or business work.
                cleanupFailed = !await CleanupScopeAsync(scope.Value, unit, sqlDeadline, retainedCleanup).ConfigureAwait(false);
                sqlDeadline.Dispose();
                sqlDeadline = null;
                scope = null;
                unit = null;
                if (cleanupFailed)
                    throw new InvalidOperationException("The validated read scope did not clean up successfully.");
                sourceOwner.RequireFreshScopeReady();
                if (accessor.Current is not null)
                    throw new InvalidOperationException("Envelope validation cannot install tenant authority.");
                BackgroundJobExecutionAdmission.InstallValidatedEnvelope(claim, read.Value);
                accessor.Current = CreateContext(read.Value);
                _ = ActivityContext.TryParse(read.Value.Traceparent, null, isRemote: true, out var parent);
                activity = new Activity("job.admission").SetIdFormat(ActivityIdFormat.W3C)
                    .SetParentId(parent.TraceId, parent.SpanId, parent.TraceFlags).Start();
                scope = scopes.CreateAsyncScope();
                unit = scope.Value.ServiceProvider.GetRequiredService<IUnitOfWork>();
                command = new AdmitBackgroundJobAttemptCommand(claim, read.Value);
                publication.Bind(command);
                // Each exact framework transaction has its own bound, including
                // announcement and COMMIT. Keep its source through actual cleanup.
                sqlDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                sqlDeadline.CancelAfter(options.SqlTimeout);
                var started = await scope.Value.ServiceProvider.GetRequiredService<ISender>()
                    .Send(command, sqlDeadline.Token).ConfigureAwait(false);
                outcome = started.IsSuccess && command.Receipt is not null
                    ? JobStartExecutionOutcome.Started : JobStartExecutionOutcome.LostClaim;
            }
        }
        catch (Exception exception)
        {
            if (command?.Receipt is not null)
            {
                outcome = JobStartExecutionOutcome.Started;
                cleanupFailed = true;
            }
            else
            {
                outcome = command?.CommitStarted == true && unit?.IsRollbackOnly != true
                    ? JobStartExecutionOutcome.Indeterminate
                    : exception is OperationCanceledException
                        && (cancellationToken.IsCancellationRequested || sqlDeadline?.IsCancellationRequested == true)
                        ? JobStartExecutionOutcome.Canceled
                        : exception is ArgumentException && command is null
                            ? JobStartExecutionOutcome.InvalidEnvelope : JobStartExecutionOutcome.Failed;
            }
            // Only finite outcomes leave this boundary; payloads, exception text and raw types do not.
        }
        finally
        {
            try
            {
                if (scope is { } owned)
                    cleanupFailed |= !await CleanupScopeAsync(owned, unit, sqlDeadline, retainedCleanup).ConfigureAwait(false);
            }
            catch
            {
                cleanupFailed = true;
                retainedCleanup.Add(new(scope, unit, sqlDeadline, null, null));
            }
            finally
            {
                try
                {
                    activity?.Dispose();
                }
                catch
                {
                    cleanupFailed = true;
                    retainedCleanup.Add(new(null, null, null, activity, null));
                }
                finally
                {
                    try
                    {
                        try
                        {
                            Activity.Current = previousActivity;
                            accessor.Current = previous;
                        }
                        catch
                        {
                            cleanupFailed = true;
                            retainedCleanup.Add(new(null, unit, null, activity, authority));
                        }
                        try { authority?.Dispose(); }
                        catch
                        {
                            cleanupFailed = true;
                            retainedCleanup.Add(new(null, null, null, null, authority));
                        }
                    }
                    finally
                    {
                        try { sqlDeadline?.Dispose(); }
                        catch
                        {
                            cleanupFailed = true;
                            retainedCleanup.Add(new(null, null, sqlDeadline, null, null));
                        }
                    }
                }
            }
            if (command?.Receipt is null || cleanupFailed || !sourceOwner.CleanupConfirmed) sourceOwner.CloseIssuance();
            cleanupFailed |= !sourceOwner.CleanupConfirmed;
            try { sourceBinding.Dispose(); }
            catch
            {
                cleanupFailed = true;
                retainedCleanup.Add(new(null, null, null, null, null, sourceBinding));
            }
            publication.SettleNoReceipt();
        }
        return new(outcome, command?.Receipt, cleanupFailed, sourceOwner)
        {
            RetainedCleanup = retainedCleanup.AsReadOnly(),
        };
    }

    private static async Task<bool> CleanupScopeAsync(AsyncServiceScope scope, IUnitOfWork? unit,
        CancellationTokenSource? deadline, List<JobStartCleanupOwnership> retained)
    {
        try
        {
            await scope.DisposeAsync().ConfigureAwait(false);
            return true;
        }
        catch
        {
            // DI can stop before every service is disposed, and the fallback
            // below can itself fail. Retain both actual owners in the result;
            // async state machines are allowed to clear locals on completion.
            retained.Add(new(scope, unit, deadline, null, null));
            // DI stops at the first throwing disposable and marks the scope disposed.
            // Retain the exact unit before handler resolution so a later service cannot
            // strand its connection. Its idempotent cleanup is awaited under the same
            // tenant, Activity and sticky authority, including after acknowledged COMMIT.
            if (unit is not null)
            {
                try
                {
                    await unit.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Both faults remain one finite cleanup diagnostic; COMMIT evidence survives.
                }
            }
            return false;
        }
    }

    private static JobTenantContext CreateContext(BackgroundJobEnvelope envelope) => JobTenantContext.FromValidatedJob(
        envelope.TenantId, envelope.OrganizationId, envelope.JobId, envelope.ParentJobId, envelope.IncomingEventId,
        envelope.CausalActor, envelope.Traceparent, envelope.DefinitionKey.Split('.')[0]);
}
