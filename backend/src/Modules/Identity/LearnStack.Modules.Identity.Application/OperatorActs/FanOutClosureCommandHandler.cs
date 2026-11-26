using LearnStack.Modules.Identity.Application.Contracts.OperatorActs;
using LearnStack.Modules.Identity.Application.DataRequests;
using LearnStack.Modules.Identity.Application.Seeding;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;

namespace LearnStack.Modules.Identity.Application.OperatorActs;

/// <summary>
/// One participant group's half of a closure (<c>fan_out_closure</c>, ADR-0107 § 7): in the group's own announced
/// transaction, under the closing person's exclusive User fence and the group tenant's shared fence, it creates the
/// person's fan-out erasure and enqueues its job, or adopts the erasure the group already has.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the group holds decides.</b> The active erasure of the person in the group is read under the fences, after a
/// dead job's request has been stored failed the way every creation path stores it (ADR-0106 § 8):
/// </para>
/// <list type="bullet">
/// <item>none, and a <c>completed</c> fan-out erasure of the person: the group's part of the closure is done, because only
/// the closure's Finalize completes one, in the transaction that moves the person to Closed. A repeated Begin that reaches
/// the group after that adopts it with nothing written and nothing designated, so its row names no one
/// (<see cref="ClosureGroupOutcome.Adopted"/>);</item>
/// <item>none otherwise, or only terminal ones: a fan-out erasure is created, <c>fulfilling</c>, with no window, the system actor's,
/// and its job is enqueued on the same transaction (<see cref="ClosureGroupOutcome.Started"/>);</item>
/// <item>a fan-out still <c>fulfilling</c>: adopted, and its job enqueued again unless one is still in flight, so a job that
/// died is retried by repeating Begin (<see cref="ClosureGroupOutcome.Adopted"/>);</item>
/// <item>any erasure awaiting the operator's privileged step: adopted as it is (<see cref="ClosureGroupOutcome.Adopted"/>);</item>
/// <item>the person's or an administrator's erasure still in its window: cancelled by the system actor, then replaced by
/// the fan-out in the same transaction, the cancellation saved first so the one-active index sees it
/// (<see cref="ClosureGroupOutcome.Started"/>);</item>
/// <item>such an erasure past its window: its own job runs it, so the group waits for a repeated Begin
/// (<see cref="ClosureGroupOutcome.Waiting"/>).</item>
/// </list>
/// <para>
/// <b>The database is the witness.</b> The request guard admits the fan-out's birth in <c>fulfilling</c> only for a subject
/// whose User is Closing, read under the User fence this act holds; a refused insertion rolls the whole group back, the
/// cancellation of an erasure it replaced included. The frozen snapshot is every Membership the person holds in the group,
/// of any status, which the guard also demands.
/// </para>
/// <para>
/// <b>Refusals are the group's failure.</b> A group whose tenant or organization no longer allows ordinary work, a
/// person not Closing, a lock that cannot be taken in time and a lost race each return a failure result, which the
/// fan-out reports as a failed group and a repeated Begin retries. One MUST row names the created or adopted request; an
/// erasure the fan-out cancelled travels in that row's changes.
/// </para>
/// </remarks>
internal sealed class FanOutClosureCommandHandler(
    IDataRequestStore store,
    IDataRequestJobPhaseReader phases,
    DataRequestFailureReconciler reconciler,
    IDataRequestFulfillmentScheduler scheduler,
    IIdentityScopeLifecycleReader lifecycle,
    ITenantContext context,
    IAuditSubject audit,
    OperatorActGuard guard,
    IGuidFactory ids,
    IClock clock) : IRequestHandler<FanOutClosureCommand, Result<ClosureGroupOutcome>>
{
    public async Task<Result<ClosureGroupOutcome>> Handle(FanOutClosureCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The group is the context the executor built for this act: its tenant, and exactly the organization the command
        // names, so an act sent into another group's context refuses before it reads anything.
        if (!context.IsResolved || context.OrganizationId != request.OrganizationId || !SeedFoundationGate.ValidUser(request.SubjectUserId))
            return DataRequestRules.Rule<ClosureGroupOutcome>("$", "lockey_validation_failed");
        if (!await SeedFoundationGate.AllowsCurrentLifecycleAsync(context, lifecycle, cancellationToken).ConfigureAwait(false))
            return DataRequestRules.Fail<ClosureGroupOutcome>("lockey_not_found");

        // ADR-0107 § 7: the person's exclusive User fence, which the request guard requires for the insertion, then the
        // group tenant's fence, shared as every creation takes it; a running erasure holds it exclusively.
        var subject = request.SubjectUserId;
        var plan = new IdentityMutationLockPlan([subject], context.TenantId, IdentityTenantLockMode.Shared);
        var admission = await guard.AdmitAsync(request, plan, cancellationToken).ConfigureAwait(false);
        if (admission.IsFailure)
            return Result<ClosureGroupOutcome>.Fail(admission.Error);
        try
        {
            if (!await SeedFoundationGate.AllowsCurrentLifecycleAsync(context, lifecycle, cancellationToken).ConfigureAwait(false))
                return DataRequestRules.Fail<ClosureGroupOutcome>("lockey_not_found");

            var active = await store.FindActiveLockedAsync(subject, DataRequestKind.Erasure, cancellationToken).ConfigureAwait(false);
            if (active is not null && await reconciler.ReconcileAsync(active, plan, cancellationToken).ConfigureAwait(false))
            {
                // A person's erasure whose job died past its window is now stored failed, which frees the group's slot.
                active = null;
            }

            if (active is null && await store.HoldsCompletedFanOutAsync(subject, cancellationToken).ConfigureAwait(false))
            {
                // Finalize completed this group's fan-out and closed the person: a Begin repeated across that commit finds
                // the group done. Nothing is designated, so the act's row names neither the person nor a request.
                guard.MarkReplayed();
                return Result.Ok(ClosureGroupOutcome.Adopted);
            }

            switch (active)
            {
                case null:
                    return await StartAsync(request, admission.Value, null, cancellationToken).ConfigureAwait(false);
                case { Status: DataRequestStatus.AwaitingPrivilegedStep }:
                    audit.Designate(active);
                    guard.MarkReplayed();
                    return Result.Ok(ClosureGroupOutcome.Adopted);
                case { Origin: DataRequestOrigin.ClosureFanout, Status: DataRequestStatus.Fulfilling }:
                    return await ResumeAsync(active, admission.Value, cancellationToken).ConfigureAwait(false);
                case { Origin: not DataRequestOrigin.ClosureFanout, Status: DataRequestStatus.Scheduled, ExecuteAt: { } due }
                    when DataRequestTransitionTime.Of(clock, active) < due:
                    return await StartAsync(request, admission.Value, active, cancellationToken).ConfigureAwait(false);
                default:
                    // A person's or an administrator's erasure that its own job runs now: once it awaits its privileged step,
                    // a repeated Begin adopts it.
                    audit.Designate(active);
                    guard.MarkReplayed();
                    return Result.Ok(ClosureGroupOutcome.Waiting);
            }
        }
        catch (IdentityMutationLockException)
        {
            return DataRequestRules.Fail<ClosureGroupOutcome>("lockey_dependency_unavailable");
        }
    }

    /// <summary>
    /// Creates the group's fan-out erasure and enqueues its job, after cancelling <paramref name="replaced"/> when the group
    /// held a person's or an administrator's erasure still in its window.
    /// </summary>
    private async Task<Result<ClosureGroupOutcome>> StartAsync(FanOutClosureCommand request, OperatorActAdmission admission,
        DataSubjectRequest? replaced, CancellationToken cancellationToken)
    {
        var subject = request.SubjectUserId;
        var memberships = await store.ReadSubjectMembershipsAsync(subject, cancellationToken).ConfigureAwait(false);
        if (memberships.Count == 0)
            return DataRequestRules.Fail<ClosureGroupOutcome>("lockey_not_found");
        if (memberships.Count > DataRequestLimits.MaximumSnapshotMemberships)
            return DataRequestRules.Rule<ClosureGroupOutcome>("$", "lockey_snapshot_too_large");

        var created = DataSubjectRequest.FanOutErasure(DataSubjectRequestId.From(DataRequestRules.NewId(ids)), subject,
            new DataRequestScopeGroup(context.TenantId, context.OrganizationId), memberships, clock.UtcNow);
        if (created.IsFailure)
            return Result<ClosureGroupOutcome>.Fail(created.Error);
        var fanOut = created.Value;
        audit.Designate(fanOut);

        if (replaced is not null)
        {
            // The system actor cancels the erasure the closure supersedes; it is saved before the insertion so the one-active
            // index frees the group's slot first (ADR-0107 § 7).
            if (replaced.Cancel(DataRequestTransitionTime.Of(clock, replaced), UserId.SystemActor).IsFailure)
                return DataRequestRules.Rule<ClosureGroupOutcome>("$", "lockey_data_request_not_cancellable");
            if (guard.RequireCurrent(admission) is { IsFailure: true } lost)
                return Result<ClosureGroupOutcome>.Fail(lost.Error);
            await store.SaveAsync(replaced, cancellationToken).ConfigureAwait(false);
        }

        if (guard.RequireCurrent(admission) is { IsFailure: true } changed)
            return Result<ClosureGroupOutcome>.Fail(changed.Error);
        var insertion = await store.TryAddAsync(fanOut, cancellationToken).ConfigureAwait(false);
        if (insertion != DataRequestInsertion.Added)
        {
            return insertion == DataRequestInsertion.SubjectNotAdmitted
                ? DataRequestRules.Rule<ClosureGroupOutcome>("$", "lockey_subject_not_admitted")
                : DataRequestRules.AlreadyActive<ClosureGroupOutcome>();
        }

        await scheduler.SubmitAsync(Fulfillment(fanOut), cancellationToken).ConfigureAwait(false);
        return Result.Ok(ClosureGroupOutcome.Started);
    }

    /// <summary>
    /// Adopts the group's fan-out erasure that is still <c>fulfilling</c>, and enqueues its job again unless one is still
    /// queued, running or retrying: a job that died is how a group's erasure stays <c>fulfilling</c>.
    /// </summary>
    private async Task<Result<ClosureGroupOutcome>> ResumeAsync(DataSubjectRequest fanOut, OperatorActAdmission admission,
        CancellationToken cancellationToken)
    {
        audit.Designate(fanOut);
        if (await phases.ReadAsync(fanOut.Id, DataRequestKind.Erasure, cancellationToken).ConfigureAwait(false) == DataRequestJobPhase.Pending)
        {
            guard.MarkReplayed();
            return Result.Ok(ClosureGroupOutcome.Adopted);
        }

        if (guard.RequireCurrent(admission) is { IsFailure: true } changed)
            return Result<ClosureGroupOutcome>.Fail(changed.Error);
        await scheduler.SubmitAsync(Fulfillment(fanOut), cancellationToken).ConfigureAwait(false);
        return Result.Ok(ClosureGroupOutcome.Adopted);
    }

    private static DataRequestFulfillment Fulfillment(DataSubjectRequest request) =>
        new(request.Id.Value, request.Kind, request.Status, request.Group, request.ExecuteAt, request.Origin);
}
