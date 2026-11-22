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

namespace LearnStack.Modules.Identity.Application.DataRequests;

public sealed record RunTenantDataExportCommand(DataExportJobParameters Parameters) : IRequest<Result<None>>;
public sealed record RunOrganizationDataExportCommand(DataExportJobParameters Parameters) : IRequest<Result<None>>;
public sealed record PurgeTenantDataExportCommand(DataExportPurgeParameters Parameters) : IRequest<Result<None>>;
public sealed record PurgeOrganizationDataExportCommand(DataExportPurgeParameters Parameters) : IRequest<Result<None>>;

/// <summary>
/// The four transactional roots of the export workflow
/// (<see href="../../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 2–§ 4</see>):
/// scope-specific declarations sharing one mechanic, run as the system actor under the trusted job policy on the
/// job's own tenant transaction, never in the platform scope.
/// </summary>
/// <remarks>
/// <para>
/// <b>One transaction.</b> The transactional runtime owns exactly one transaction per root, so the export takes
/// <c>requested → fulfilling</c> and then <c>fulfilling → completed</c> (or <c>failed</c>) as separate UPDATEs of
/// that one transaction, each one arrow and one row-version step as the request guard requires. A failure rolls
/// every write back, the request is <c>requested</c> again and the runtime's retry re-runs the whole export, so a
/// retried or resumed job never stores twice; a commit stores the request, its artifact, every chunk and the
/// purge job together.
/// </para>
/// <para>
/// <b>Recorded no-ops.</b> The runtime has no cancel call, so a job whose request is missing, of another kind,
/// cancelled or already terminal completes without changing anything, and its MUST row records that it ran.
/// </para>
/// <para>
/// <b>Fences.</b> The run holds the Identity tenant fence shared from its first read, which the request guard
/// requires of every request write and which orders it against a tenant erasure taking the same fence exclusively
/// (ADR-0107 § 5): an erasure either cancels a still requested export first, or finds it completed and revokes
/// its artifact.
/// </para>
/// </remarks>
public sealed class DataExportJobHandlers(IDataRequestStore store, IIdentityMutationLocks locks, ITenantContext tenant,
    IAuditSubject audit, IClock clock, IGuidFactory ids, IDataExportBundleBuilder bundles, IUserDataParticipantRegistry participants,
    IDataExportArtifactStore artifacts, IDataRequestFulfillmentScheduler scheduler)
    : IRequestHandler<RunTenantDataExportCommand, Result<None>>,
      IRequestHandler<RunOrganizationDataExportCommand, Result<None>>,
      IRequestHandler<PurgeTenantDataExportCommand, Result<None>>,
      IRequestHandler<PurgeOrganizationDataExportCommand, Result<None>>
{
    public Task<Result<None>> Handle(RunTenantDataExportCommand request, CancellationToken cancellationToken) =>
        ExportAsync(request.Parameters, cancellationToken);

    public Task<Result<None>> Handle(RunOrganizationDataExportCommand request, CancellationToken cancellationToken) =>
        ExportAsync(request.Parameters, cancellationToken);

    public Task<Result<None>> Handle(PurgeTenantDataExportCommand request, CancellationToken cancellationToken) =>
        PurgeAsync(request.Parameters, cancellationToken);

    public Task<Result<None>> Handle(PurgeOrganizationDataExportCommand request, CancellationToken cancellationToken) =>
        PurgeAsync(request.Parameters, cancellationToken);

    private async Task<Result<None>> ExportAsync(DataExportJobParameters parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        _ = store.RequireCurrentJob();
        if (parameters.RequestId == Guid.Empty || parameters.RequestId.Version != 7)
        {
            return Done();
        }

        await locks.AcquireAsync(new IdentityMutationLockPlan([], tenant.TenantId, IdentityTenantLockMode.Shared), cancellationToken)
            .ConfigureAwait(false);
        var request = await store.FindLockedAsync(DataSubjectRequestId.From(parameters.RequestId), cancellationToken)
            .ConfigureAwait(false);
        if (request is null)
        {
            return Done();
        }

        audit.Designate(request);
        if (request.Kind != DataRequestKind.Export || request.Status is not (DataRequestStatus.Requested or DataRequestStatus.Fulfilling))
        {
            return Done();
        }

        var now = clock.UtcNow;
        var actor = tenant.UserId ?? UserId.SystemActor;
        if (request.Status == DataRequestStatus.Requested)
        {
            Require(request.StartFulfilling(now, actor));
            await store.SaveAsync(request, cancellationToken).ConfigureAwait(false);
        }

        // Only an earlier transaction of this same request can have stored its artifact; the one-artifact-per-request
        // index makes that the request's artifact, so a resumed run completes instead of storing a second bundle.
        if (await store.FindArtifactOfRequestAsync(request.Id, cancellationToken).ConfigureAwait(false) is null)
        {
            var subject = new UserDataSubject(request.Id.Value, request.Kind, request.Origin, request.SubjectUserId,
                request.Group, request.SnapshotMembershipIds.Select(membership => membership.Value));
            var built = await bundles.BuildAsync(subject, now, cancellationToken).ConfigureAwait(false);
            if (built.IsTooLarge)
            {
                Require(request.Fail(DataRequestFailureReason.ExportTooLarge, now, actor));
                await store.SaveAsync(request, cancellationToken).ConfigureAwait(false);
                return Done();
            }

            using var bundle = built.Bundle!;
            await StoreAsync(request, bundle, now, cancellationToken).ConfigureAwait(false);
        }

        foreach (var participant in participants.Participants)
        {
            var before = request.Version;
            Require(request.RecordParticipantCompleted(participant.Key, now, actor));
            if (request.Version != before)
            {
                await store.SaveAsync(request, cancellationToken).ConfigureAwait(false);
            }
        }

        var retained = await DescribeRetainedAsync(request, cancellationToken).ConfigureAwait(false);
        Require(request.Complete(retained, now, actor));
        await store.SaveAsync(request, cancellationToken).ConfigureAwait(false);
        return Done();
    }

    /// <summary>
    /// Revokes the subject's earlier available artifact in the group, then stores the new one and schedules its purge
    /// at its expiry: a subject holds at most one available artifact per group (ADR-0107 § 4).
    /// </summary>
    private async Task StoreAsync(DataSubjectRequest request, IDataExportBundle bundle, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var earlier in await store.FindAvailableArtifactsAsync(request.SubjectUserId, cancellationToken).ConfigureAwait(false))
        {
            var outcome = await artifacts.PurgeAsync(new DataExportArtifactLocator(earlier.Value, request.Group),
                DataExportArtifactStatus.Revoked, cancellationToken).ConfigureAwait(false);
            if (outcome is not (DataExportArtifactStatus.Revoked or DataExportArtifactStatus.Purged))
            {
                throw new InvalidOperationException("An earlier export artifact could not be withdrawn.");
            }
        }

        var artifactId = ids.NewUuidV7();
        if (artifactId == Guid.Empty || artifactId.Version != 7)
        {
            throw new InvalidOperationException("The identity factory must return a UUIDv7.");
        }

        var stored = await artifacts.PutAsync(new DataExportArtifactPut(artifactId, request.Id.Value, request.SubjectUserId,
                request.Group, bundle.ByteCount, bundle.Truncations), bundle.ReadChunksAsync(cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        if (stored.ArtifactId != artifactId || stored.RequestId != request.Id.Value || stored.ExpiresAt <= now)
        {
            throw new InvalidOperationException("The stored export artifact does not match its request.");
        }

        await scheduler.SchedulePurgeAsync(DataExportArtifactId.From(artifactId), stored.ExpiresAt, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Every participant's retained references for the subject and group, summed per closed kind (ADR-0107 § 6).</summary>
    private async Task<IReadOnlyCollection<RetainedReferenceCount>> DescribeRetainedAsync(DataSubjectRequest request,
        CancellationToken cancellationToken)
    {
        var subject = new UserDataSubject(request.Id.Value, request.Kind, request.Origin, request.SubjectUserId,
            request.Group, request.SnapshotMembershipIds.Select(membership => membership.Value));
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

    private async Task<Result<None>> PurgeAsync(DataExportPurgeParameters parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        _ = store.RequireCurrentJob();
        if (parameters.ArtifactId == Guid.Empty || parameters.ArtifactId.Version != 7)
        {
            return Done();
        }

        var group = new DataRequestScopeGroup(tenant.TenantId, tenant.OrganizationId);
        var outcome = await artifacts.PurgeAsync(new DataExportArtifactLocator(parameters.ArtifactId, group),
            DataExportArtifactStatus.Purged, cancellationToken).ConfigureAwait(false);

        // Absent, revoked or purged: nothing is left to purge. Still available means the database clock has not reached
        // the expiry the job was scheduled for; the runtime retries rather than leave the chunks behind.
        return outcome == DataExportArtifactStatus.Available
            ? throw new InvalidOperationException("The export artifact is not yet due for purge.")
            : Done();
    }

    /// <summary>A transition refused here is a programming error: every precondition was checked under the row lock.</summary>
    private static void Require(Result<None> transition)
    {
        if (transition.IsFailure)
        {
            throw new InvalidOperationException("A data request transition that its own preconditions admitted was refused.");
        }
    }

    private static Result<None> Done() => Result<None>.Ok(None.Value);
}
