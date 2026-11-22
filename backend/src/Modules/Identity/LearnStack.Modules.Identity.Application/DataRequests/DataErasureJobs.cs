using System.Diagnostics;
using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;
using Microsoft.Extensions.Logging;

namespace LearnStack.Modules.Identity.Application.DataRequests;

public sealed record RunTenantDataErasureCommand(DataErasureJobParameters Parameters) : IRequest<Result<None>>;
public sealed record RunOrganizationDataErasureCommand(DataErasureJobParameters Parameters) : IRequest<Result<None>>;

/// <summary>
/// The two transactional roots of the tenant-side erasure
/// (<see href="../../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 1, § 2, § 5</see>):
/// scope-specific declarations sharing one mechanic, run as the system actor under the trusted job policy on the job's
/// own tenant transaction, never in the platform scope. The request store is the one write port; every other change is
/// the participants'.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fences first, then the row.</b> The run reads the request without a lock to learn its subject, takes the subject's
/// exclusive User fence and then the tenant key exclusively (ADR-0107 § 5, the canonical order of ADR-0074), and only
/// then locks and re-reads the request. A cancellation holds the shared tenant fence, so it either committed before the
/// run took its fences, and the run is a recorded no-op, or it waits for the run to commit.
/// </para>
/// <para>
/// <b>Asked twice.</b> A <c>scheduled</c> erasure becomes <c>fulfilling</c> only once its execution time has passed by
/// the run's clock and the database's; a run that wakes early throws, so the runtime retries it rather than drop it.
/// Eligibility is judged again once it is <c>fulfilling</c> (ADR-0106 § 7): a hold or a sole administrator ends it
/// <c>blocked</c> with its code, and that is the run's committed business outcome.
/// </para>
/// <para>
/// <b>One transaction.</b> The transactional runtime owns exactly one transaction per root attempt, so every
/// participant's erasure runs on it in the registry's ordinal order, each recorded in the request's progress as it
/// finishes; the request then records the tenant side's retained report and waits for the operator's privileged step.
/// A failure rolls every write back and the runtime's retry runs the whole erasure again, so a committed erasure is
/// complete and a half-erased group never commits. ADR-0107 § 1's transaction per participant holds while only the
/// Identity participant writes; a resumed <c>fulfilling</c> request skips the participants its progress records.
/// </para>
/// <para>
/// <b>Recorded no-ops.</b> The runtime has no cancel call, so a job whose request is missing, of another kind,
/// cancelled or past its tenant side completes without changing anything, and its MUST rows record that it ran.
/// </para>
/// <para>
/// <b>Signals.</b> One log line per run carries the request identifier, the outcome and the duration; never a subject,
/// a group or a participant's content. A run its host cancelled is not a failure.
/// </para>
/// </remarks>
public sealed partial class DataErasureJobHandlers(IDataRequestStore store, IDataRequestReader reader, IIdentityMutationLocks locks,
    ITenantContext tenant, IAuditSubject audit, IClock clock, IDataRequestEligibility eligibility,
    IUserDataParticipantRegistry participants, ILogger<DataErasureJobHandlers> logger)
    : IRequestHandler<RunTenantDataErasureCommand, Result<None>>,
      IRequestHandler<RunOrganizationDataErasureCommand, Result<None>>
{
    public Task<Result<None>> Handle(RunTenantDataErasureCommand request, CancellationToken cancellationToken) =>
        ObserveAsync(request.Parameters, cancellationToken);

    public Task<Result<None>> Handle(RunOrganizationDataErasureCommand request, CancellationToken cancellationToken) =>
        ObserveAsync(request.Parameters, cancellationToken);

    /// <summary>Runs the erasure and records its outcome; a failure is recorded and rethrown unchanged.</summary>
    private async Task<Result<None>> ObserveAsync(DataErasureJobParameters parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var started = Stopwatch.GetTimestamp();
        string outcome;
        try
        {
            outcome = await EraseAsync(parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (RecordFailure(parameters.RequestId, started, failure, cancellationToken))
        {
            throw; // Unreachable: the filter records and declines, so the exception propagates unwound.
        }

        var duration = Stopwatch.GetElapsedTime(started);
        LogFinished(logger, parameters.RequestId, outcome, duration.TotalMilliseconds);
        return Result<None>.Ok(None.Value);
    }

    /// <summary>Records a failed attempt and declines the exception; a run its host cancelled records nothing.</summary>
    private bool RecordFailure(Guid requestId, long started, Exception failure, CancellationToken cancellationToken)
    {
        if (DataExportJobHandlers.IsHostCancellation(failure, cancellationToken))
        {
            return false;
        }

        // The exception's type only: a message or a provider's detail is not a closed value.
        var duration = Stopwatch.GetElapsedTime(started);
        LogFailed(logger, requestId, failure.GetType().Name, duration.TotalMilliseconds);
        return false;
    }

    private async Task<string> EraseAsync(DataErasureJobParameters parameters, CancellationToken cancellationToken)
    {
        _ = store.RequireCurrentJob();
        if (parameters.RequestId == Guid.Empty || parameters.RequestId.Version != 7)
        {
            return "noop_invalid_parameters";
        }

        var requestId = DataSubjectRequestId.From(parameters.RequestId);
        var observed = await reader.FindAsync(requestId, cancellationToken).ConfigureAwait(false);
        if (observed is null)
        {
            return "noop_missing";
        }

        audit.Designate(observed);
        if (observed.Kind != DataRequestKind.Erasure)
        {
            return "noop_not_erasure";
        }

        if (!Runs(observed.Status))
        {
            return "noop_" + DataRequestCodes.Statuses.ToCode(observed.Status);
        }

        // ADR-0107 § 5: the subject's exclusive User fence, then the tenant key exclusively, before any row lock.
        var plan = new IdentityMutationLockPlan([observed.SubjectUserId], tenant.TenantId, IdentityTenantLockMode.Exclusive);
        await locks.AcquireAsync(plan, cancellationToken).ConfigureAwait(false);
        var request = await store.FindLockedAsync(requestId, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return "noop_missing";
        }

        if (!Runs(request.Status))
        {
            // Cancelled, or ended by another run, while this one waited for its fences.
            return "noop_" + DataRequestCodes.Statuses.ToCode(request.Status);
        }

        var actor = tenant.UserId ?? UserId.SystemActor;
        if (request.Status == DataRequestStatus.Scheduled)
        {
            var at = DataRequestTransitionTime.Of(clock, request);
            if (request.ExecuteAt is not { } due || at < due)
            {
                // The window has not ended by this run's clock: a run that woke early is retried, never dropped.
                LogNotDue(logger, request.Id.Value);
                throw new InvalidOperationException("The erasure is not yet due.");
            }

            locks.RequireCurrent(plan);
            Require(request.StartFulfilling(at, actor));
            await store.SaveAsync(request, cancellationToken).ConfigureAwait(false);
            var decision = await eligibility.EvaluateAsync(new DataRequestEligibilityQuery(request.Kind, request.Origin,
                request.SubjectUserId, request.Group, DataRequestEligibilityStage.Execution, request.Id.Value), cancellationToken)
                .ConfigureAwait(false);
            if (!decision.IsEligible)
            {
                // ADR-0106 § 7: a refusal at execution ends the erasure blocked with its code, and that commits. Any other
                // reason is a broken invariant here: the request's own Membership snapshot and active slot hold it.
                var refusal = decision.Refusal ?? throw new InvalidOperationException("An executing erasure lost its structure.");
                Require(request.Block(refusal, DataRequestTransitionTime.Of(clock, request), actor));
                await store.SaveAsync(request, cancellationToken).ConfigureAwait(false);
                return "blocked_" + DataRequestCodes.Refusals.ToCode(refusal);
            }
        }

        var subject = new UserDataSubject(request.Id.Value, request.Kind, request.Origin, request.SubjectUserId, request.Group,
            request.SnapshotMembershipIds.Select(membership => membership.Value));
        foreach (var participant in participants.Participants)
        {
            if (request.Progress.ContainsKey(participant.Key))
            {
                continue;
            }

            await DataExportJobHandlers.ParticipateAsync(async () =>
            {
                await participant.EraseAsync(subject, cancellationToken).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);

            // A participant's own reads may have refreshed the shared change tracker; the request row is still this run's.
            request = await RequireAsync(requestId, cancellationToken).ConfigureAwait(false);
            locks.RequireCurrent(plan);
            Require(request.RecordParticipantCompleted(participant.Key, DataRequestTransitionTime.Of(clock, request), actor));
            await store.SaveAsync(request, cancellationToken).ConfigureAwait(false);
        }

        // The retained report is the post-erasure state, read on the same transaction after every write (ADR-0107 § 6).
        var retained = await DataExportJobHandlers.ParticipateAsync(() => DescribeRetainedAsync(subject, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        request = await RequireAsync(requestId, cancellationToken).ConfigureAwait(false);
        locks.RequireCurrent(plan);
        Require(request.RecordRetained(retained, DataRequestTransitionTime.Of(clock, request), actor));
        await store.SaveAsync(request, cancellationToken).ConfigureAwait(false);
        Require(request.AwaitPrivilegedStep(DataRequestTransitionTime.Of(clock, request), actor));
        await store.SaveAsync(request, cancellationToken).ConfigureAwait(false);
        return "awaiting_privileged_step";
    }

    /// <summary>
    /// A <c>scheduled</c> erasure runs from the start; a <c>fulfilling</c> one, such as a closure's fan-out erasure that
    /// starts there (ADR-0107 § 7), resumes past the participants its progress records. Every other status is the job's
    /// recorded no-op.
    /// </summary>
    private static bool Runs(DataRequestStatus status) => status is DataRequestStatus.Scheduled or DataRequestStatus.Fulfilling;

    private async Task<DataSubjectRequest> RequireAsync(DataSubjectRequestId requestId, CancellationToken cancellationToken) =>
        await store.FindLockedAsync(requestId, cancellationToken).ConfigureAwait(false) is { Status: DataRequestStatus.Fulfilling } request
            ? request
            : throw new InvalidOperationException("The fulfilling erasure request changed under its own fences.");

    /// <summary>Every participant's retained references for the subject and group, summed per closed kind (ADR-0107 § 6).</summary>
    private async Task<IReadOnlyCollection<RetainedReferenceCount>> DescribeRetainedAsync(UserDataSubject subject,
        CancellationToken cancellationToken)
    {
        var totals = new SortedDictionary<RetainedReferenceKind, long>();
        foreach (var participant in participants.Participants)
        {
            foreach (var retained in await participant.DescribeRetainedAsync(subject, cancellationToken).ConfigureAwait(false))
            {
                totals[retained.Kind] = checked(totals.GetValueOrDefault(retained.Kind) + retained.Count);
            }
        }

        return [.. totals.Select(total => new RetainedReferenceCount(total.Key, total.Value))];
    }

    /// <summary>A transition refused here is a programming error: every precondition was checked under the fences.</summary>
    private static void Require(Result<None> transition)
    {
        if (transition.IsFailure)
        {
            throw new InvalidOperationException("A data request transition that its own preconditions admitted was refused.");
        }
    }

    [LoggerMessage(EventId = 7511, Level = LogLevel.Information,
        Message = "Data erasure job for request {RequestId} finished ({Outcome}) in {DurationMilliseconds} ms.")]
    private static partial void LogFinished(ILogger logger, Guid requestId, string outcome, double durationMilliseconds);

    [LoggerMessage(EventId = 7512, Level = LogLevel.Warning,
        Message = "Data erasure {RequestId} is not yet due; the runtime retries.")]
    private static partial void LogNotDue(ILogger logger, Guid requestId);

    [LoggerMessage(EventId = 7513, Level = LogLevel.Warning,
        Message = "Data erasure {RequestId} attempt failed ({FailureType}) after {DurationMilliseconds} ms; its transaction rolls back for the runtime's retry.")]
    private static partial void LogFailed(ILogger logger, Guid requestId, string failureType, double durationMilliseconds);
}
