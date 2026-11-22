using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;

namespace LearnStack.Modules.Identity.Application.DataRequests;

/// <summary>
/// Stores <c>failed</c> with <c>execution_failed</c> for a request whose fulfilment job is dead
/// (<see href="../../../../../docs/decisions/0106-subject-data-requests-authority-and-status.md">ADR-0106 § 8</see>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> ADR-0106 § 8 reads <c>failed</c> from the job phase, so no hook runs in a dead job; but
/// the one-active partial unique index frees a group's slot only for a <em>stored</em> terminal status. A person
/// whose export's job died could otherwise never file another. The creation path calls this before it inserts,
/// and the receipt displays the same answer without writing through <see cref="EffectiveStatus"/>.
/// </para>
/// <para>
/// <b>Transitions, one per save.</b> A dead export's request is <c>requested</c>: its transactional job rolled every
/// write back, and the export never commits <c>fulfilling</c> (<see cref="DataExportJobHandlers"/>). It takes
/// <c>requested → fulfilling</c> and then <c>fulfilling → failed</c> as two saves, because the request guard admits
/// one arrow per UPDATE; both are the aggregate's own transitions. It runs on the caller's tenant transaction under
/// the shared Identity tenant fence that guard requires.
/// </para>
/// </remarks>
public sealed class DataRequestFailureReconciler(IDataRequestStore store, IDataRequestJobPhaseReader phases,
    IIdentityMutationLocks locks, ITenantContext tenant, IClock clock)
{
    /// <summary>
    /// The status a receipt shows: the stored one, or <c>failed</c> for a non-terminal request whose job is dead.
    /// </summary>
    public static DataRequestStatus EffectiveStatus(DataSubjectRequest request, DataRequestJobPhase phase)
    {
        ArgumentNullException.ThrowIfNull(request);
        return EffectiveStatus(request.Kind, request.Status, phase);
    }

    /// <summary>The same answer from a receipt's kind and stored status, for a page that never loads the aggregate.</summary>
    public static DataRequestStatus EffectiveStatus(DataRequestKind kind, DataRequestStatus status, DataRequestJobPhase phase) =>
        !DataRequestCodes.IsTerminal(status) && phase == DataRequestJobPhase.Dead && CanFail(kind, status)
            ? DataRequestStatus.Failed
            : status;

    /// <summary>
    /// Stores <c>failed</c> for a tracked, non-terminal request whose job is dead.
    /// </summary>
    /// <remarks>
    /// The caller loads the request through <see cref="IDataRequestStore.FindLockedAsync"/> on its own group
    /// transaction, so the row is locked against a job that is, after all, still committing.
    /// </remarks>
    /// <returns>Whether the request was moved to <c>failed</c>.</returns>
    public Task<bool> ReconcileAsync(DataSubjectRequest request, CancellationToken cancellationToken) =>
        ReconcileAsync(request, null, cancellationToken);

    /// <summary>
    /// Stores <c>failed</c> for a tracked, non-terminal request whose job is dead, on a transaction whose caller already
    /// holds the Identity tenant fence through <paramref name="heldPlan"/>.
    /// </summary>
    /// <remarks>
    /// A transaction holds one lock plan (<see cref="IIdentityMutationLocks"/>), so a creation path that took the
    /// subject's User fence and the tenant fence before it judges the one-active rule (ADR-0106 § 7) hands its plan in
    /// and this proves the plan is still current instead of acquiring a second one.
    /// </remarks>
    public async Task<bool> ReconcileAsync(DataSubjectRequest request, IdentityMutationLockPlan? heldPlan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.IsTerminal || !CanFail(request) || request.TenantId != tenant.TenantId
            || request.OrganizationId != tenant.OrganizationId)
        {
            return false;
        }

        if (await phases.ReadAsync(request.Id, request.Kind, cancellationToken).ConfigureAwait(false) != DataRequestJobPhase.Dead)
        {
            return false;
        }

        if (heldPlan is null)
        {
            await locks.AcquireAsync(new IdentityMutationLockPlan([], tenant.TenantId, IdentityTenantLockMode.Shared), cancellationToken)
                .ConfigureAwait(false);
        }
        else if (heldPlan.TenantId != tenant.TenantId)
        {
            throw new InvalidOperationException("A held lock plan must fence the request's own tenant.");
        }
        else
        {
            locks.RequireCurrent(heldPlan);
        }

        // Never before the request's own last stamp: a host whose clock lags the one that filed it still orders.
        var now = DataRequestTransitionTime.Of(clock, request);
        var actor = tenant.UserId ?? UserId.SystemActor;
        Require(request.StartFulfilling(now, actor).IsSuccess);
        await store.SaveAsync(request, cancellationToken).ConfigureAwait(false);
        Require(request.Fail(DataRequestFailureReason.ExecutionFailed, now, actor).IsSuccess);
        await store.SaveAsync(request, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Only an export has a fulfilment job in this step, and only a requested one can be dead: its job commits
    /// <c>fulfilling</c> only together with the terminal status.
    /// </summary>
    private static bool CanFail(DataSubjectRequest request) => CanFail(request.Kind, request.Status);

    private static bool CanFail(DataRequestKind kind, DataRequestStatus status) =>
        kind == DataRequestKind.Export && status == DataRequestStatus.Requested;

    private static void Require(bool admitted)
    {
        if (!admitted)
        {
            throw new InvalidOperationException("A failure transition its own preconditions admitted was refused.");
        }
    }
}

/// <summary>
/// When a data request transition is stamped: the later of the clock and the request's own last stamp, so a host whose
/// clock lags the one that filed or last changed the request still orders its transition after it, which the aggregate
/// and the request guard both require. Shared by the export job and the failure reconciler.
/// </summary>
internal static class DataRequestTransitionTime
{
    internal static DateTimeOffset Of(IClock clock, DataSubjectRequest request)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(request);
        var now = clock.UtcNow;
        var floor = request.UpdatedAt ?? request.CreatedAt;
        return now > floor ? now : floor;
    }
}
