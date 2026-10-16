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
    ConfirmedStartReceipt? Receipt, bool CleanupFailed);

/// <summary>Owns framework validation/admission scopes; business activation belongs to the later executor stage.</summary>
public sealed class JobExecutor(IServiceScopeFactory scopes, ITenantContextAccessor accessor,
    BackgroundJobExecutionAdmission admission)
{
    public async Task<JobStartExecutionResult> ExecuteAsync(BackgroundJobRoutingClaim claim,
        CancellationToken cancellationToken = default)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        ArgumentNullException.ThrowIfNull(claim);
        var previous = accessor.Current;
        var previousActivity = Activity.Current;
        AsyncServiceScope? scope = null;
        IDisposable? authority = null;
        Activity? activity = null;
        AdmitBackgroundJobAttemptCommand? command = null;
        IUnitOfWork? unit = null;
        var outcome = JobStartExecutionOutcome.Failed;
        var cleanupFailed = false;
        try
        {
            accessor.Current = null;
            authority = admission.EnterFramework(claim);
            cancellationToken.ThrowIfCancellationRequested();
            scope = scopes.CreateAsyncScope();
            unit = scope.Value.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var read = await scope.Value.ServiceProvider.GetRequiredService<ISender>()
                .Send(new ReadBackgroundJobEnvelopeQuery(claim), cancellationToken).ConfigureAwait(false);
            if (read.IsFailure)
            {
                outcome = JobStartExecutionOutcome.InvalidEnvelope;
            }
            else
            {
                // The validated read owns no service subsequently used by admission or business work.
                cleanupFailed = !await CleanupScopeAsync(scope.Value, unit).ConfigureAwait(false);
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
                var started = await scope.Value.ServiceProvider.GetRequiredService<ISender>()
                    .Send(command, cancellationToken).ConfigureAwait(false);
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
                    : exception is OperationCanceledException && cancellationToken.IsCancellationRequested
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
                if (scope is { } owned) cleanupFailed |= !await CleanupScopeAsync(owned, unit).ConfigureAwait(false);
            }
            catch
            {
                cleanupFailed = true;
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
                }
                finally
                {
                    Activity.Current = previousActivity;
                    accessor.Current = previous;
                    authority?.Dispose();
                }
            }
        }
        return new(outcome, command?.Receipt, cleanupFailed);
    }

    private static async Task<bool> CleanupScopeAsync(AsyncServiceScope scope, IUnitOfWork? unit)
    {
        try
        {
            await scope.DisposeAsync().ConfigureAwait(false);
            return true;
        }
        catch
        {
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
