using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Identity.Application.DataRequests;

/// <summary>
/// The default <see cref="IDataRequestEligibility"/>: structure only
/// (<see href="../../../../../docs/decisions/0106-subject-data-requests-authority-and-status.md">ADR-0106 § 7</see>).
/// </summary>
/// <remarks>
/// <para>
/// <b>What it judges.</b> The subject holds a Membership of any status in the group (a Revoked tombstone counts);
/// at creation no non-terminal request of the same kind, subject and group exists, and at execution none but the
/// executing one; an erasure of the tenant-wide group is refused while the subject is the tenant's only eligible
/// tenant-wide administrator; and the registered <see cref="IDataRequestHoldSource"/> reports no hold. It reads on the
/// caller's announced group transaction as <c>learnstack_app</c> and confers no authority.
/// </para>
/// <para>
/// <b>Only the tenant-wide group can remove an administrator.</b> A tenant administrator Membership is tenant-wide, so an
/// organization group's erasure never touches one and is never refused for it.
/// </para>
/// <para>
/// <b>A dead job is reconciled by the caller, first.</b> ADR-0106 § 8 reads <c>failed</c> from the job phase, but the
/// one-active index frees the slot only for a stored terminal status; the creation path locks the existing request
/// and stores its failure through <see cref="DataRequestFailureReconciler"/> under the fences it already holds before
/// it asks, so this judgment sees the stored outcome. The partial unique index remains the race's backstop.
/// </para>
/// <para>
/// <b>No closure yet.</b> The closure's structure is P03-10 Step 3's; asked about that kind this refuses to answer rather
/// than call it eligible.
/// </para>
/// </remarks>
public sealed class DataRequestEligibility(IDataRequestStore store, IDataRequestHoldSource holds, ITenantContext tenant)
    : IDataRequestEligibility
{
    public async Task<DataRequestEligibilityDecision> EvaluateAsync(DataRequestEligibilityQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!tenant.IsResolved || query.Group.TenantId != tenant.TenantId || query.Group.OrganizationId != tenant.OrganizationId)
        {
            throw new InvalidOperationException("Eligibility is judged only inside the request's own announced group.");
        }

        if (query.Kind == DataRequestKind.Closure)
        {
            throw new InvalidOperationException("Closure eligibility lands with its own step (ADR-0107 § 7).");
        }

        var memberships = await store.ReadSubjectMembershipsAsync(query.SubjectUserId, cancellationToken).ConfigureAwait(false);
        if (memberships.Count == 0)
        {
            return DataRequestEligibilityDecision.Refused(DataRequestIneligibility.NoMembership);
        }

        var active = await store.FindActiveLockedAsync(query.SubjectUserId, query.Kind, cancellationToken).ConfigureAwait(false);
        if (active is not null && (query.Stage == DataRequestEligibilityStage.Creation || active.Id.Value != query.RequestId))
        {
            return DataRequestEligibilityDecision.Refused(DataRequestIneligibility.RequestAlreadyActive);
        }

        if (query.Kind == DataRequestKind.Erasure && query.Group.IsTenantWide
            && await store.IsSoleEligibleTenantAdministratorAsync(query.SubjectUserId, cancellationToken).ConfigureAwait(false))
        {
            return DataRequestEligibilityDecision.Refused(DataRequestIneligibility.BlockedLastAdministrator);
        }

        return await holds.HasHoldAsync(new DataRequestHoldQuery(query.Kind, query.SubjectUserId, query.Group), cancellationToken)
            .ConfigureAwait(false)
            ? DataRequestEligibilityDecision.Refused(DataRequestIneligibility.BlockedHold)
            : DataRequestEligibilityDecision.Eligible;
    }
}
