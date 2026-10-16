using System.Diagnostics;
using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Identifiers;
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
    ConfirmedStartReceipt? Receipt, bool CleanupFailed)
{
    internal IReadOnlyList<JobStartCleanupOwnership> RetainedCleanup { get; init; } = [];
}

/// <summary>A faulted cleanup keeps its concrete resources; a completed async task alone does not.</summary>
internal sealed class JobStartCleanupOwnership(AsyncServiceScope? scope, IUnitOfWork? unit,
    CancellationTokenSource? deadline, Activity? activity, IDisposable? authority)
{
    internal AsyncServiceScope? Scope { get; } = scope;
    internal IUnitOfWork? Unit { get; } = unit;
    internal CancellationTokenSource? Deadline { get; } = deadline;
    internal Activity? Activity { get; } = activity;
    internal IDisposable? Authority { get; } = authority;
}

/// <summary>Owns framework validation/admission scopes; business activation belongs to the later executor stage.</summary>
public sealed class JobExecutor(IServiceScopeFactory scopes, ITenantContextAccessor accessor,
    BackgroundJobExecutionAdmission admission, BackgroundJobRuntimeOptions options)
{
    public async Task<JobStartExecutionResult> ExecuteAsync(BackgroundJobRoutingClaim claim,
        Func<bool>? startGate = null, CancellationToken cancellationToken = default)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        ArgumentNullException.ThrowIfNull(claim);
        options.Validate();
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
                if (accessor.Current is not null)
                    throw new InvalidOperationException("Envelope validation cannot install tenant authority.");
                BackgroundJobExecutionAdmission.InstallValidatedEnvelope(claim, read.Value);
                accessor.Current = new JobTenantContext(read.Value);
                _ = ActivityContext.TryParse(read.Value.Traceparent, null, isRemote: true, out var parent);
                activity = new Activity("job.admission").SetIdFormat(ActivityIdFormat.W3C)
                    .SetParentId(parent.TraceId, parent.SpanId, parent.TraceFlags).Start();
                scope = scopes.CreateAsyncScope();
                unit = scope.Value.ServiceProvider.GetRequiredService<IUnitOfWork>();
                command = new AdmitBackgroundJobAttemptCommand(claim, read.Value);
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
                        Activity.Current = previousActivity;
                        accessor.Current = previous;
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
        }
        return new(outcome, command?.Receipt, cleanupFailed)
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

    private sealed class JobTenantContext(BackgroundJobEnvelope envelope) : ITenantContext
    {
        public bool IsResolved => true;
        public TenantId TenantId => envelope.TenantId;
        public OrganizationId? OrganizationId => envelope.OrganizationId;
        public UserId? UserId => LearnStack.SharedKernel.Identifiers.UserId.SystemActor;
        public UserId? CausalActorUserId => envelope.CausalActor;
        public TenantContextOrigin? Origin => TenantContextOrigin.Ambient;
        public string? CorrelationId => envelope.Traceparent;
        public string? ModuleName => envelope.DefinitionKey.Split('.')[0];
    }
}
