using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;

namespace LearnStack.Modules.Identity.Application.DataRequests;

/// <summary>Whom an operation serves: the request's subject, or the administrator who filed it.</summary>
public enum DataRequestRole
{
    /// <summary>The person the request is about; reaches only their own requests.</summary>
    Subject = 1,

    /// <summary>A holder of <c>identity.membership.admin</c> covering the group; mutates only the requests they filed.</summary>
    Administrator = 2,
}

/// <summary>
/// The caller a handler core serves, independent of how its context was established: the live-session handlers
/// build it from <see cref="DataRequestAuthorization"/>, and a retained-entry path from its own witness. The core
/// calls <see cref="RecheckAsync"/> once its fences are held, so authority lost while waiting refuses the act.
/// </summary>
public sealed class DataRequestCaller
{
    private readonly Func<CancellationToken, Task<Result<None>>> _recheck;

    public DataRequestCaller(UserId actor, DataRequestRole role, Func<CancellationToken, Task<Result<None>>> recheck)
    {
        ArgumentNullException.ThrowIfNull(recheck);
        if (!StronglyTypedId.IsAssigned(actor) || actor == UserId.SystemActor || !Enum.IsDefined(role))
        {
            throw new ArgumentException("A data request caller is a real person in a closed role.", nameof(actor));
        }

        Actor = actor;
        Role = role;
        _recheck = recheck;
    }

    public UserId Actor { get; }
    public DataRequestRole Role { get; }

    public Task<Result<None>> RecheckAsync(CancellationToken cancellationToken) => _recheck(cancellationToken);

    public override string ToString() => nameof(DataRequestCaller);
}

/// <summary>
/// The handler cores of the data-request API
/// (<see href="../../../../../docs/decisions/0106-subject-data-requests-authority-and-status.md">ADR-0106</see>,
/// <see href="../../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 4</see>):
/// one core per operation, each confined to the announced group and to the caller's role.
/// </summary>
/// <remarks>
/// <para>
/// <b>The group is the context's.</b> Every row is read and written in the announced tenant and exactly the announced
/// organization or none; a request of another group is absent, never refused, so its existence is not disclosed.
/// </para>
/// <para>
/// <b>Roles.</b> A subject reaches only requests about themselves. An administrator reads every export and erasure
/// of the group (status and counts only, never content, never a closure) and cancels or revokes only what they filed.
/// The subject also revokes an artifact an administrator's export produced; nobody else does.
/// </para>
/// <para>
/// <b>Secrets.</b> A ticket is returned once and stored only as its digest; a chunk's plaintext is handed to the
/// transport, which clears it once written. Neither reaches a log, an exception, an audit row or an idempotency
/// receipt: the ticket operations are not keyed, and the audit capture redacts the ticket's digests.
/// </para>
/// </remarks>
public sealed class DataRequestService(IDataRequestStore store, IDataRequestReader reader, IDataRequestJobPhaseReader phases,
    DataRequestFailureReconciler reconciler, IDataRequestEligibility eligibility, IDataRequestFulfillmentScheduler scheduler,
    IDataExportArtifactStore artifacts, IIdentityMutationLocks locks, ITenantContext context, IAuditSubject audit, IGuidFactory ids,
    IClock clock)
{
    private DataRequestScopeGroup Group => new(context.TenantId, context.OrganizationId);

    /// <summary>
    /// Files an export of <paramref name="subject"/>'s data in the announced group under the subject's exclusive User
    /// fence and the shared tenant fence, with the frozen Membership snapshot, and submits its job on the same
    /// transaction (ADR-0106 § 7, § 8, § 10; ADR-0107 § 2). The subject's own export withdraws their previous artifact
    /// when it stores its own; an administrator's is refused while the subject holds a readable artifact in the group.
    /// </summary>
    public async Task<Result<DataRequestMutationResult>> CreateExportAsync(DataRequestCaller caller, UserId subject,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!StronglyTypedId.IsAssigned(subject) || subject == UserId.SystemActor
            || (caller.Role == DataRequestRole.Subject) != (subject == caller.Actor))
        {
            // The subject files only for themselves; an administrator never for themselves (ADR-0106 § 3).
            return Rule<DataRequestMutationResult>("$", "lockey_business_rule_violation");
        }

        try
        {
            var plan = new IdentityMutationLockPlan([subject], context.TenantId, IdentityTenantLockMode.Shared);
            await locks.AcquireAsync(plan, cancellationToken).ConfigureAwait(false);
            var current = await caller.RecheckAsync(cancellationToken).ConfigureAwait(false);
            if (current.IsFailure)
            {
                return Result<DataRequestMutationResult>.Fail(current.Error);
            }

            // ADR-0106 § 8: a dead job's request is stored failed under these fences before the one-active rule is
            // judged, so a person whose export died is never locked out.
            if (await store.FindActiveLockedAsync(subject, DataRequestKind.Export, cancellationToken).ConfigureAwait(false) is { } existing)
            {
                await reconciler.ReconcileAsync(existing, plan, cancellationToken).ConfigureAwait(false);
            }

            var origin = caller.Role == DataRequestRole.Subject ? DataRequestOrigin.Self : DataRequestOrigin.Administrator;
            var decision = await eligibility.EvaluateAsync(new DataRequestEligibilityQuery(DataRequestKind.Export, origin, subject,
                Group, DataRequestEligibilityStage.Creation), cancellationToken).ConfigureAwait(false);
            if (!decision.IsEligible)
            {
                return decision.Reason switch
                {
                    DataRequestIneligibility.NoMembership => Fail<DataRequestMutationResult>("lockey_not_found"),
                    DataRequestIneligibility.RequestAlreadyActive => AlreadyActive<DataRequestMutationResult>(),
                    DataRequestIneligibility.BlockedHold => Rule<DataRequestMutationResult>("$", "lockey_blocked_hold"),
                    _ => Rule<DataRequestMutationResult>("$", "lockey_blocked_last_administrator"),
                };
            }

            // An administrator's export never replaces the subject's own readable artifact (a new export withdraws the
            // previous one): while the subject holds one in the group, of any origin, it is refused. One active export
            // per subject and group means no export can store an artifact between this read and the insert below.
            if (caller.Role == DataRequestRole.Administrator
                && await store.HoldsReadableArtifactAsync(subject, cancellationToken).ConfigureAwait(false))
            {
                return Rule<DataRequestMutationResult>("$", "lockey_subject_export_available");
            }

            var memberships = await store.ReadSubjectMembershipsAsync(subject, cancellationToken).ConfigureAwait(false);
            if (memberships.Count == 0)
            {
                return Fail<DataRequestMutationResult>("lockey_not_found");
            }

            if (memberships.Count > DataRequestLimits.MaximumSnapshotMemberships)
            {
                return Rule<DataRequestMutationResult>("$", "lockey_business_rule_violation");
            }

            var id = NewId(ids);
            var now = clock.UtcNow;
            var created = caller.Role == DataRequestRole.Subject
                ? DataSubjectRequest.FileForSelf(DataSubjectRequestId.From(id), DataRequestKind.Export, subject, Group, memberships, now)
                : DataSubjectRequest.FileForSubject(DataSubjectRequestId.From(id), DataRequestKind.Export, caller.Actor, subject, Group,
                    memberships, now);
            if (created.IsFailure)
            {
                return Result<DataRequestMutationResult>.Fail(created.Error);
            }

            var request = created.Value;
            audit.Designate(request);
            locks.RequireCurrent(plan);
            if (!await store.TryAddAsync(request, cancellationToken).ConfigureAwait(false))
            {
                return AlreadyActive<DataRequestMutationResult>();
            }

            await scheduler.SubmitAsync(request.Id.Value, request.Kind, request.Status, request.Group, cancellationToken).ConfigureAwait(false);
            return Result.Ok(new DataRequestMutationResult(request.Id.Value, request.Version));
        }
        catch (IdentityMutationLockException)
        {
            return Fail<DataRequestMutationResult>("lockey_dependency_unavailable");
        }
    }

    /// <summary>
    /// An administrator files an export for the person who holds or held <paramref name="membershipId"/>, of any status,
    /// in the announced group (ADR-0106 § 3): a Revoked tombstone still names its person. A Membership of another group
    /// is absent.
    /// </summary>
    public async Task<Result<DataRequestMutationResult>> CreateExportForMembershipAsync(DataRequestCaller caller, Guid membershipId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.Role != DataRequestRole.Administrator)
        {
            return Fail<DataRequestMutationResult>("lockey_forbidden");
        }

        if (!ValidId(membershipId))
        {
            return Fail<DataRequestMutationResult>("lockey_validation_failed");
        }

        var subject = await reader.FindMembershipSubjectAsync(MembershipId.From(membershipId), cancellationToken).ConfigureAwait(false);
        return subject is { } person
            ? await CreateExportAsync(caller, person, cancellationToken).ConfigureAwait(false)
            : Fail<DataRequestMutationResult>("lockey_not_found");
    }

    /// <summary>The caller's page of the group's requests, newest first, with each one's displayed status.</summary>
    public async Task<Result<Page<DataRequestView>>> ListAsync(DataRequestCaller caller, string operation, string? cursor, int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        var scope = DataRequestCursorCodec.Scope(operation, caller.Actor, context.TenantId, context.OrganizationId);
        Guid? before = null;
        if (limit is < 1 or > CursorPagination.MaxLimit)
        {
            return Fail<Page<DataRequestView>>("lockey_validation_failed");
        }

        if (cursor is not null)
        {
            if (!DataRequestCursorCodec.TryDecode(cursor, scope, out var anchor))
            {
                return Fail<Page<DataRequestView>>("lockey_validation_failed");
            }

            before = anchor;
        }

        var rows = await reader.ListAsync(caller.Role == DataRequestRole.Subject ? caller.Actor : null, before, limit + 1, cancellationToken)
            .ConfigureAwait(false);
        var page = rows.Take(limit).ToArray();
        var views = await ViewsAsync(caller, page, cancellationToken).ConfigureAwait(false);
        var current = await caller.RecheckAsync(cancellationToken).ConfigureAwait(false);
        if (current.IsFailure)
        {
            return Result<Page<DataRequestView>>.Fail(current.Error);
        }

        var next = rows.Count > limit ? DataRequestCursorCodec.Encode(scope, page[^1].Id.Value) : null;
        return Result.Ok(new Page<DataRequestView>(views, new PageInfo(next, null, next is not null, false)));
    }

    /// <summary>One request's receipt, or <c>not_found</c> when the caller's role does not reach it in this group.</summary>
    public async Task<Result<DataRequestView>> GetAsync(DataRequestCaller caller, Guid requestId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var request = await FindVisibleAsync(caller, requestId, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return Fail<DataRequestView>("lockey_not_found");
        }

        audit.Designate(request);
        var view = (await ViewsAsync(caller, [DataRequestReceiptRow.From(request)], cancellationToken).ConfigureAwait(false))[0];
        var current = await caller.RecheckAsync(cancellationToken).ConfigureAwait(false);
        return current.IsFailure ? Result<DataRequestView>.Fail(current.Error) : Result.Ok(view);
    }

    /// <summary>Disclosure of an original request a keyed replay names: the caller must still reach it.</summary>
    public async Task<Result<None>> ReadReceiptAsync(DataRequestCaller caller, Guid requestId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var request = await FindVisibleAsync(caller, requestId, cancellationToken).ConfigureAwait(false);
        var current = await caller.RecheckAsync(cancellationToken).ConfigureAwait(false);
        if (current.IsFailure)
        {
            return current;
        }

        if (request is null || !Mutates(caller, request))
        {
            return Fail<None>("lockey_not_found");
        }

        audit.Designate(request);
        return Result.Ok(None.Value);
    }

    /// <summary><c>requested → cancelled</c> under the shared tenant fence the request guard requires (ADR-0106 § 8, § 11).</summary>
    public async Task<Result<DataRequestMutationResult>> CancelAsync(DataRequestCaller caller, Guid requestId, long expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!ValidId(requestId) || expectedVersion < 0)
        {
            return Fail<DataRequestMutationResult>("lockey_validation_failed");
        }

        try
        {
            var plan = new IdentityMutationLockPlan([], context.TenantId, IdentityTenantLockMode.Shared);
            await locks.AcquireAsync(plan, cancellationToken).ConfigureAwait(false);
            var request = await store.FindLockedAsync(DataSubjectRequestId.From(requestId), cancellationToken).ConfigureAwait(false);
            var current = await caller.RecheckAsync(cancellationToken).ConfigureAwait(false);
            if (current.IsFailure)
            {
                return Result<DataRequestMutationResult>.Fail(current.Error);
            }

            if (request is null || !Mutates(caller, request))
            {
                return Fail<DataRequestMutationResult>("lockey_not_found");
            }

            audit.Designate(request);
            if (request.Version != expectedVersion)
            {
                return Fail<DataRequestMutationResult>("lockey_concurrency_conflict");
            }

            var cancelled = request.Cancel(clock.UtcNow, caller.Actor);
            if (cancelled.IsFailure)
            {
                return Result<DataRequestMutationResult>.Fail(cancelled.Error);
            }

            locks.RequireCurrent(plan);
            await store.SaveAsync(request, cancellationToken).ConfigureAwait(false);
            return Result.Ok(new DataRequestMutationResult(request.Id.Value, request.Version));
        }
        catch (IdentityMutationLockException)
        {
            return Fail<DataRequestMutationResult>("lockey_dependency_unavailable");
        }
    }

    /// <summary>
    /// Revokes an unexpired artifact, which deletes its chunks at once (ADR-0107 § 4). The subject revokes any
    /// artifact about them; an administrator only one an export they filed produced.
    /// </summary>
    public async Task<Result<DataExportArtifactMutationResult>> RevokeAsync(DataRequestCaller caller, Guid requestId,
        long expectedArtifactVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!ValidId(requestId) || expectedArtifactVersion < 0)
        {
            return Fail<DataExportArtifactMutationResult>("lockey_validation_failed");
        }

        var request = await FindVisibleAsync(caller, requestId, cancellationToken).ConfigureAwait(false);
        var found = request is not null && request.Kind == DataRequestKind.Export && Mutates(caller, request)
            ? await reader.FindArtifactLockedAsync(request.Id, cancellationToken).ConfigureAwait(false)
            : null;
        var current = await caller.RecheckAsync(cancellationToken).ConfigureAwait(false);
        if (current.IsFailure)
        {
            return Result<DataExportArtifactMutationResult>.Fail(current.Error);
        }

        if (found is null)
        {
            return Fail<DataExportArtifactMutationResult>("lockey_not_found");
        }

        var artifact = found.Artifact;
        audit.Designate(artifact);
        if (artifact.Version != expectedArtifactVersion)
        {
            return Fail<DataExportArtifactMutationResult>("lockey_concurrency_conflict");
        }

        if (!found.Readable)
        {
            return Rule<DataExportArtifactMutationResult>("artifact", "lockey_data_export_unavailable");
        }

        var outcome = await artifacts.PurgeAsync(new DataExportArtifactLocator(artifact.Id.Value, Group), DataExportArtifactStatus.Revoked,
            cancellationToken).ConfigureAwait(false);
        return outcome is DataExportArtifactStatus.Revoked or DataExportArtifactStatus.Purged
            ? Result.Ok(new DataExportArtifactMutationResult(request!.Id.Value, artifact.Id.Value, artifact.Version))
            : Fail<DataExportArtifactMutationResult>("lockey_not_found");
    }

    private Task<DataSubjectRequest?> FindVisibleAsync(DataRequestCaller caller, Guid requestId, CancellationToken cancellationToken) =>
        FindVisibleAsync(reader, caller, requestId, cancellationToken);

    /// <summary>The request when the caller's role reaches it in the announced group; <see langword="null"/> otherwise.</summary>
    internal static async Task<DataSubjectRequest?> FindVisibleAsync(IDataRequestReader reader, DataRequestCaller caller, Guid requestId,
        CancellationToken cancellationToken)
    {
        if (!ValidId(requestId))
        {
            return null;
        }

        var request = await reader.FindAsync(DataSubjectRequestId.From(requestId), cancellationToken).ConfigureAwait(false);
        return request is not null && Reaches(caller, request) ? request : null;
    }

    /// <summary>A subject reaches requests about them; an administrator every export and erasure of the group, never a closure.</summary>
    private static bool Reaches(DataRequestCaller caller, DataSubjectRequest request) => caller.Role == DataRequestRole.Subject
        ? request.SubjectUserId == caller.Actor
        : request.Kind != DataRequestKind.Closure;

    /// <summary>A subject mutates requests about them; an administrator only an export or erasure they filed.</summary>
    private static bool Mutates(DataRequestCaller caller, DataSubjectRequest request) => caller.Role == DataRequestRole.Subject
        ? request.SubjectUserId == caller.Actor
        : request.Kind != DataRequestKind.Closure && request.Origin == DataRequestOrigin.Administrator
            && request.RequesterUserId == caller.Actor;

    private async Task<DataRequestView[]> ViewsAsync(DataRequestCaller caller, DataRequestReceiptRow[] requests,
        CancellationToken cancellationToken)
    {
        if (requests.Length == 0)
        {
            return [];
        }

        // Only a stored requested export can be displayed failed (DataRequestFailureReconciler.EffectiveStatus): a
        // completed, failed or cancelled one never changes, so its job is not read.
        var phase = await phases.ReadManyAsync(requests
            .Where(request => request.Kind == DataRequestKind.Export && request.Status == DataRequestStatus.Requested)
            .Select(request => request.Id).ToArray(), cancellationToken).ConfigureAwait(false);
        var stored = await reader.ReadArtifactsAsync(requests.Select(request => request.Id).ToArray(), cancellationToken).ConfigureAwait(false);
        return requests.Select(request => View(caller, request, phase.GetValueOrDefault(request.Id, DataRequestJobPhase.Unavailable),
            stored.GetValueOrDefault(request.Id))).ToArray();
    }

    /// <summary>
    /// A receipt shows the status the reconciler would store: <c>failed</c> for a request whose job is dead, without writing
    /// (ADR-0106 § 8). No personal data, no content; the Membership snapshot only to an administrator.
    /// </summary>
    internal static DataRequestView View(DataRequestCaller caller, DataRequestReceiptRow request, DataRequestJobPhase phase,
        DataRequestArtifactRow? artifact)
    {
        var status = DataRequestFailureReconciler.EffectiveStatus(request.Kind, request.Status, phase);
        var failure = request.FailureReason
            ?? (status == DataRequestStatus.Failed && request.Status != DataRequestStatus.Failed ? DataRequestFailureReason.ExecutionFailed : null);
        return new DataRequestView(request.Id.Value, DataRequestCodes.Kinds.ToCode(request.Kind), DataRequestCodes.Origins.ToCode(request.Origin),
            DataRequestCodes.Statuses.ToCode(status), request.CreatedAt, request.UpdatedAt, request.CompletedAt, request.ExecuteAt,
            request.LastRefusal is { } refusal ? DataRequestCodes.Refusals.ToCode(refusal) : null,
            failure is { } reason ? DataRequestCodes.FailureReasons.ToCode(reason) : null,
            DataRequestDocuments.ReadResult(request.ResultJson)
                .Select(entry => new DataRequestRetainedView(DataRequestCodes.RetainedKinds.ToCode(entry.Key), entry.Value))
                .OrderBy(entry => entry.Kind, StringComparer.Ordinal).ToArray(),
            artifact is null ? null : new DataExportArtifactView(artifact.Artifact.Id.Value,
                DataRequestCodes.ArtifactStatuses.ToCode(artifact.Readable || artifact.Artifact.Status != DataExportArtifactStatus.Available
                    ? artifact.Artifact.Status : DataExportArtifactStatus.Expired),
                artifact.Artifact.ExpiresAt, artifact.Artifact.PurgedAt, artifact.Artifact.ByteCount, artifact.Artifact.ChunkCount,
                artifact.Artifact.Truncations.Select(item => new DataExportTruncationView(item.Collection, item.Included, item.Total)).ToArray(),
                artifact.Artifact.Version, artifact.Readable),
            request.Version,
            request.RequesterUserId == caller.Actor,
            caller.Role == DataRequestRole.Administrator
                ? DataRequestDocuments.ReadMemberships(request.MembershipSnapshotJson
                        ?? throw new InvalidOperationException("An administrator's receipt reads its Membership snapshot."))
                    .Select(member => member.Value).ToArray()
                : null);
    }

    internal static Guid NewId(IGuidFactory factory)
    {
        var id = factory.NewUuidV7();
        return id != Guid.Empty && id.Version == 7 ? id : throw new InvalidOperationException("The identity factory must return a UUIDv7.");
    }

    internal static bool ValidId(Guid id) => id != Guid.Empty && id.Version == 7;

    private static Result<T> AlreadyActive<T>() => Rule<T>("$", "lockey_request_already_active");

    /// <summary>A 409 with the reason on the field it concerns; the status map is closed, so the code stays the business rule's.</summary>
    internal static Result<T> Rule<T>(string field, string reason) => Result<T>.Fail(new Error(
        LocalizedMessage.Of("lockey_business_rule_violation"),
        new Dictionary<string, IReadOnlyList<LocalizedMessage>>(StringComparer.Ordinal) { [field] = [LocalizedMessage.Of(reason)] }));

    internal static Result<T> Fail<T>(string key) => Result<T>.Fail(new Error(LocalizedMessage.Of(key)));
}
