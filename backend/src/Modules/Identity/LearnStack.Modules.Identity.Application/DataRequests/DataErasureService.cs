using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Invitations;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;

namespace LearnStack.Modules.Identity.Application.DataRequests;

/// <summary>
/// The Identity module's tenant-side erasure of one subject in one scope group
/// (<see href="../../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 5, § 6</see>),
/// which the Identity participant runs inside the erasure job's transaction. It is the one minter of the
/// <see cref="DataErasureWitness"/> the erasure domain operations require.
/// </summary>
/// <remarks>
/// <para>
/// <b>Under the job's evidence.</b> It mints the witness only from the subject's erasure request of the ambient group
/// while that request is <c>fulfilling</c> and while the database shows this transaction holding the subject's User
/// fence and the tenant key, both exclusively: the canonical fences the job took first. Nothing here acquires a fence.
/// </para>
/// <para>
/// <b>In ADR-0107 § 5's order.</b> First the subject's exports in the group: every unexpired artifact is revoked (an
/// expired one purged) and a request not yet stored is cancelled, so an export job that built its bundle before the
/// erasure began finds its own request cancelled under its fence and discards the bundle. Then each Membership of the
/// frozen snapshot loses its role assignments and its profile and becomes the Revoked tombstone; one already erased
/// is left alone, and a Membership admitted after the snapshot is not part of this request. Last, the pending
/// invitations the subject issued in the group are revoked, with the subject as the aggregate actor; offers the subject
/// accepted, or that are addressed to them, stay as the record ADR-0107 § 6 retains.
/// </para>
/// <para>
/// <b>Idempotent.</b> Every step changes only what is not yet erased, so a retried or resumed run repeats nothing.
/// </para>
/// <para>
/// <b>One audit row per kind of change.</b> The erasure may write several instances of a type (the Memberships of the
/// snapshot, the invitations), so it designates the first instance of each it changed and the others travel in that
/// row's <c>changes</c> under their own pointers (ADR-0044 Amendment 6); the job designates its erasure request, beside
/// which a cancelled export request travels the same way.
/// </para>
/// </remarks>
public sealed class DataErasureService(IDataRequestStore requests, IDataExportArtifactStore artifacts, IMembershipWriteStore memberships,
    IInvitationAdministrationStore invitations, IAuditSubject audit, ITenantContext tenant, IClock clock)
{
    /// <summary>How many pending invitations one statement locks and revokes; the loop repeats until none is left.</summary>
    internal const int InvitationPage = 100;

    /// <summary>Erases the subject's tenant-side data in the ambient group for its fulfilling erasure request.</summary>
    /// <exception cref="InvalidOperationException">
    /// The subject's request is not a fulfilling erasure of the ambient group, the fences are not held, or a stored row
    /// contradicts the request's snapshot: the job's attempt fails and rolls back.
    /// </exception>
    public async Task EraseAsync(UserDataSubject subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        UserDataParticipantGuard.RequireGroup(subject, tenant);
        var request = await requests.FindLockedAsync(DataSubjectRequestId.From(subject.RequestId), cancellationToken).ConfigureAwait(false);
        if (request is null || request.Kind != DataRequestKind.Erasure || request.Status != DataRequestStatus.Fulfilling
            || request.SubjectUserId != subject.SubjectUserId || request.Group != subject.Group)
        {
            throw new InvalidOperationException("Erasure runs only for its own fulfilling erasure request.");
        }

        if (!await requests.HoldsErasureFencesAsync(request.SubjectUserId, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Erasure runs only under the subject's and the tenant's exclusive fences.");
        }

        using var source = new FulfillingErasure(request);
        var witness = source.Mint();
        var actor = tenant.UserId ?? UserId.SystemActor;
        await WithdrawExportsAsync(request, actor, cancellationToken).ConfigureAwait(false);
        await EraseMembershipsAsync(witness, request.SnapshotMembershipIds, actor, cancellationToken).ConfigureAwait(false);
        await RevokeIssuedInvitationsAsync(witness, cancellationToken).ConfigureAwait(false);

        // The deferred graph guards (a Membership keeps its profile; the tenant keeps an eligible administrator) run now,
        // inside the job, so a refusal is the attempt's failure rather than an unexplained COMMIT fault.
        await memberships.ValidateGraphAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// ADR-0107 § 5.1: revokes the subject's unexpired artifacts in the group (an expired one is purged, as the artifact
    /// store decides by the database clock) and cancels the export request that is not yet stored.
    /// </summary>
    /// <remarks>
    /// The partial unique index on available artifacts holds at most one per subject and group, so the artifact row of
    /// the erasure's audit names its one instance without a designation.
    /// </remarks>
    private async Task WithdrawExportsAsync(DataSubjectRequest erasure, UserId actor, CancellationToken cancellationToken)
    {
        foreach (var artifact in await requests.FindAvailableArtifactsAsync(erasure.SubjectUserId, cancellationToken).ConfigureAwait(false))
        {
            var outcome = await artifacts.PurgeAsync(new DataExportArtifactLocator(artifact.Value, erasure.Group),
                DataExportArtifactStatus.Revoked, cancellationToken).ConfigureAwait(false);
            if (outcome is not (DataExportArtifactStatus.Revoked or DataExportArtifactStatus.Purged))
            {
                throw new InvalidOperationException("An export artifact could not be withdrawn by the erasure.");
            }
        }

        if (await requests.FindActiveLockedAsync(erasure.SubjectUserId, DataRequestKind.Export, cancellationToken).ConfigureAwait(false)
            is { Status: DataRequestStatus.Requested } export)
        {
            Require(export.Cancel(DataRequestTransitionTime.Of(clock, export), actor));
            await requests.SaveAsync(export, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// ADR-0107 § 5.2: every Membership of the frozen snapshot loses its assignments and profile and becomes the Revoked
    /// tombstone, each saved on its own; the snapshot is at most <see cref="DataRequestLimits.MaximumSnapshotMemberships"/>.
    /// </summary>
    private async Task EraseMembershipsAsync(DataErasureWitness witness, IEnumerable<MembershipId> snapshot, UserId actor,
        CancellationToken cancellationToken)
    {
        var designated = false;
        foreach (var id in snapshot)
        {
            var membership = await memberships.FindAsync(id, cancellationToken).ConfigureAwait(false);
            if (membership is null || membership.TenantId != witness.TenantId || membership.OrganizationId != witness.OrganizationId
                || membership.UserId != witness.SubjectUserId)
            {
                throw new InvalidOperationException("A Membership of the erasure's snapshot is not the subject's in its group.");
            }

            if (membership.IsErased)
            {
                continue;
            }

            var at = clock.UtcNow;
            Require(membership.RemoveAssignmentsForErasure(witness, at, actor));
            Require(membership.ClearProfileForErasure(witness, at, actor));
            Require(membership.RevokeForErasure(witness, at, actor));
            if (!designated)
            {
                audit.Designate(membership);
                designated = true;
            }

            await memberships.UpdateAsync(membership, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// ADR-0107 § 5.3: the pending invitations the subject issued in the group, a bounded page at a time, each revoked with
    /// the subject as the aggregate actor. A revoked page is no longer pending, so the next statement reads the next page.
    /// </summary>
    private async Task RevokeIssuedInvitationsAsync(DataErasureWitness witness, CancellationToken cancellationToken)
    {
        var designated = false;
        while (true)
        {
            var page = await invitations.FindPendingIssuedLockedAsync(witness.SubjectUserId, InvitationPage, cancellationToken)
                .ConfigureAwait(false);
            if (page.Count == 0)
            {
                return;
            }

            var at = clock.UtcNow;
            foreach (var invitation in page)
            {
                Require(invitation.RevokeForErasure(witness, at));
            }

            if (!designated)
            {
                audit.Designate(page[0]);
                designated = true;
            }

            await invitations.UpdateAsync(page[0], cancellationToken).ConfigureAwait(false);
            if (page.Count < InvitationPage)
            {
                return;
            }
        }
    }

    /// <summary>A refusal here is a programming error: the witness, the snapshot and the locks were all checked.</summary>
    private static void Require(Result<None> change)
    {
        if (change.IsFailure)
        {
            throw new InvalidOperationException("An erasure operation its own preconditions admitted was refused.");
        }
    }

    /// <summary>
    /// The one production source of <see cref="DataErasureWitness"/>: current while its erasure request is still the
    /// fulfilling erasure it was minted from and until the erasure that opened it returns.
    /// </summary>
    private sealed class FulfillingErasure(DataSubjectRequest request) : DataErasureWitness.MintingSource, IDisposable
    {
        private bool _closed;

        internal DataErasureWitness Mint() => Capture(request);

        protected override bool IsEvidenceCurrent => !_closed && request.Kind == DataRequestKind.Erasure
            && request.Status == DataRequestStatus.Fulfilling;

        public void Dispose() => _closed = true;
    }
}
