using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;

namespace LearnStack.Modules.Identity.Application.DataRequests;

/// <summary>
/// The data request rows of the ambient group, read and written on the ambient tenant transaction as
/// <c>learnstack_app</c> (<see href="../../../../../docs/decisions/0106-subject-data-requests-authority-and-status.md">ADR-0106 § 8</see>).
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="DataSubjectRequest"/> write port: it derives <see cref="IAggregateWriteStore{TRoot,TId}"/> so the
/// cross-aggregate census counts every holder. A ticket is written through <see cref="IDataExportTicketStore"/>, an
/// artifact only through <see cref="IDataExportArtifactStore"/>.
/// </para>
/// <para>
/// Every read is confined to the announced tenant and organization, by the predicate and by the row policy, so a
/// request or artifact of another group is absent rather than refused. Writes go through tracked aggregates only:
/// each transition is saved on its own, because the request and artifact guards admit exactly one arrow and one
/// row-version step per UPDATE.
/// </para>
/// </remarks>
public interface IDataRequestStore : IAggregateWriteStore<DataSubjectRequest, DataSubjectRequestId>
{
    /// <summary>The executing data-request job: refuses a caller that is not one of the module's export or erasure job roots.</summary>
    BackgroundJobId RequireCurrentJob();

    /// <summary>The request in the ambient group, locked for update; <see langword="null"/> when absent.</summary>
    Task<DataSubjectRequest?> FindLockedAsync(DataSubjectRequestId requestId, CancellationToken cancellationToken);

    /// <summary>The subject's artifacts in the ambient group that are still stored <c>available</c>.</summary>
    Task<IReadOnlyList<DataExportArtifactId>> FindAvailableArtifactsAsync(UserId subject, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the subject holds a readable artifact in the ambient group, of any origin: stored <c>available</c> and
    /// before its expiry by the database clock (ADR-0107 § 4).
    /// </summary>
    Task<bool> HoldsReadableArtifactAsync(UserId subject, CancellationToken cancellationToken);

    /// <summary>Saves the changes of a request this scope loaded and tracks.</summary>
    Task SaveAsync(DataSubjectRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// The subject's Memberships of any status in the ambient group, Revoked tombstones included: exactly the set the
    /// creation guard demands as the frozen snapshot (ADR-0106 § 8). At most
    /// <see cref="DataRequestLimits.MaximumSnapshotMemberships"/> plus one.
    /// </summary>
    Task<IReadOnlyList<MembershipId>> ReadSubjectMembershipsAsync(UserId subject, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the subject holds an Active or Suspended Membership in the ambient group: the current row an administrator's
    /// erasure is refused for (ADR-0106 § 3). Read under the subject's User fence, which every admission also takes.
    /// </summary>
    Task<bool> HoldsCurrentMembershipAsync(UserId subject, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the subject is the tenant's only eligible tenant-wide administrator (ADR-0106 § 7, ADR-0107 § 7): they hold
    /// an Active, undeleted, tenant-wide Membership with a tenant administrator Role that keeps every kernel grant, and no
    /// other person does. The same predicate the deferred complete-graph guard enforces at commit.
    /// </summary>
    /// <remarks>
    /// The guard also requires the administrator's User to be Active, which the application role cannot read for another
    /// person (the <c>users</c> policy admits only the session's own subject); no path moves a User out of Active before
    /// P03-10 Step 3's closure, which owns extending this read.
    /// </remarks>
    Task<bool> IsSoleEligibleTenantAdministratorAsync(UserId subject, CancellationToken cancellationToken);

    /// <summary>
    /// Whether this transaction holds the subject's User fence and the ambient tenant's fence, both exclusively, as the
    /// database sees them: the canonical fences an erasure runs under (ADR-0107 § 5).
    /// </summary>
    Task<bool> HoldsErasureFencesAsync(UserId subject, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the subject holds, in the ambient group, a closure's fan-out erasure that is <c>completed</c>. Only that
    /// closure's Finalize completes one, in the transaction that moves the person to Closed, so its presence says the group's
    /// part of the closure is done (ADR-0107 § 7).
    /// </summary>
    Task<bool> HoldsCompletedFanOutAsync(UserId subject, CancellationToken cancellationToken);

    /// <summary>The subject's non-terminal request of the kind in the ambient group, tracked and locked for update.</summary>
    Task<DataSubjectRequest?> FindActiveLockedAsync(UserId subject, DataRequestKind kind, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts a new request of the ambient group. Any answer but <see cref="DataRequestInsertion.Added"/> leaves the
    /// transaction rollback-only with nothing stored: the one-active partial unique index refused it (ADR-0106 § 7), or the
    /// request guard refused its subject because the person's User is not in the one status the request's origin allows
    /// (ADR-0107 § 7).
    /// </summary>
    Task<DataRequestInsertion> TryAddAsync(DataSubjectRequest request, CancellationToken cancellationToken);

    /// <inheritdoc cref="SaveAsync"/>
    Task IAggregateWriteStore<DataSubjectRequest, DataSubjectRequestId>.UpdateAsync(DataSubjectRequest aggregate,
        CancellationToken cancellationToken) => SaveAsync(aggregate, cancellationToken);
}

/// <summary>How the insertion of a new request ended; every refusal stored nothing.</summary>
public enum DataRequestInsertion
{
    /// <summary>The request is stored.</summary>
    Added = 1,

    /// <summary>A request of the kind is already active for the subject in the group (ADR-0106 § 7).</summary>
    AlreadyActive = 2,

    /// <summary>
    /// The request guard refused the subject, judged under the subject's User fence: Closing for a closure's fan-out
    /// erasure, Active for every other request (ADR-0107 § 7). A filing admitted while the person was Active meets it when
    /// a closure's Begin or Finalize committed first, and an administrator's when the person's account is closing.
    /// </summary>
    SubjectNotAdmitted = 3,
}

/// <summary>
/// The <see cref="DataExportTicket"/> write port (ADR-0107 § 4): a minted ticket is inserted with its digests only,
/// and the one later change is its consumption.
/// </summary>
public interface IDataExportTicketStore : IAggregateWriteStore<DataExportTicket, DataExportTicketId>
{
    /// <summary>The artifact's ticket with this digest in the ambient group, tracked and locked for update.</summary>
    Task<DataExportTicket?> FindLockedAsync(DataExportArtifactId artifactId, string digest, CancellationToken cancellationToken);

    /// <summary>
    /// The artifact's tickets in one statement: those of <paramref name="sessionBindingDigest"/> that are neither
    /// consumed nor past their expiry by the database clock, and every ticket ever minted over it. The caller holds
    /// the artifact's row lock, so concurrent mints are counted one after the other.
    /// </summary>
    Task<DataExportTicketCounts> CountAsync(DataExportArtifactId artifactId, string sessionBindingDigest, CancellationToken cancellationToken);
}

/// <summary>An artifact's tickets: those open in one session binding, and all ever minted.</summary>
public sealed record DataExportTicketCounts(int OpenInBinding, int Minted);

/// <summary>
/// Reads of the ambient group's requests, artifacts and Memberships for the receipts, pages and handler cores
/// (<see href="../../../../../docs/decisions/0106-subject-data-requests-authority-and-status.md">ADR-0106 § 9, § 13</see>).
/// </summary>
/// <remarks>
/// Every read names the announced tenant <b>and</b> <c>organization_id IS NOT DISTINCT FROM</c> the announced
/// organization: the canonical row policy also shows an organization context the tenant-wide group's rows, so the
/// predicate, not the policy, keeps one group's requests out of another's pages. Nothing is written here; the one
/// locking read, <see cref="FindArtifactLockedAsync"/>, retains the artifact row for the revocation or ticket mint
/// that follows on the same transaction, as the Role lock read does.
/// </remarks>
public interface IDataRequestReader
{
    /// <summary>
    /// The group's requests newest first, strictly older than <paramref name="before"/> when given: only the
    /// subject's when <paramref name="subject"/> is given, otherwise every export and erasure (never a closure).
    /// </summary>
    /// <remarks>
    /// A page reads only the columns its receipt shows: never the progress document, and the Membership snapshot only
    /// for the administrator's page (no <paramref name="subject"/>), the one view that displays it.
    /// </remarks>
    Task<IReadOnlyList<DataRequestReceiptRow>> ListAsync(UserId? subject, Guid? before, int take, CancellationToken cancellationToken);

    /// <summary>One request of the group; <see langword="null"/> when the group holds no such request.</summary>
    Task<DataSubjectRequest?> FindAsync(DataSubjectRequestId requestId, CancellationToken cancellationToken);

    /// <summary>The artifacts of the given requests in the group, each with its readability by the database clock.</summary>
    Task<IReadOnlyDictionary<DataSubjectRequestId, DataRequestArtifactRow>> ReadArtifactsAsync(
        IReadOnlyCollection<DataSubjectRequestId> requestIds, CancellationToken cancellationToken);

    /// <summary>The request's export artifact in the ambient group, tracked and locked for update; <see langword="null"/> when none.</summary>
    Task<DataRequestArtifactRow?> FindArtifactLockedAsync(DataSubjectRequestId requestId, CancellationToken cancellationToken);

    /// <summary>The person who holds or held the Membership, of any status, in the ambient group; <see langword="null"/> otherwise.</summary>
    Task<UserId?> FindMembershipSubjectAsync(MembershipId membership, CancellationToken cancellationToken);
}

/// <summary>An artifact and whether it is readable now: stored <c>available</c> and before its expiry by the database clock.</summary>
public sealed record DataRequestArtifactRow(DataExportArtifact Artifact, bool Readable);

/// <summary>
/// The columns a request's receipt shows, read without the aggregate: a page projects these and nothing more.
/// <see cref="MembershipSnapshotJson"/> is <see langword="null"/> when the read did not select it (the subject's own
/// pages, which never display it).
/// </summary>
public sealed record DataRequestReceiptRow(DataSubjectRequestId Id, DataRequestKind Kind, DataRequestOrigin Origin,
    DataRequestStatus Status, UserId SubjectUserId, UserId? RequesterUserId, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt,
    DateTimeOffset? CompletedAt, DateTimeOffset? ExecuteAt, DataRequestRefusal? LastRefusal, DataRequestFailureReason? FailureReason,
    string ResultJson, string? MembershipSnapshotJson, long Version)
{
    /// <summary>The receipt columns of a loaded request, its snapshot included.</summary>
    public static DataRequestReceiptRow From(DataSubjectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(request.Id, request.Kind, request.Origin, request.Status, request.SubjectUserId, request.RequesterUserId,
            request.CreatedAt, request.UpdatedAt, request.CompletedAt, request.ExecuteAt, request.LastRefusal, request.FailureReason,
            request.ResultJson, request.MembershipSnapshotJson, request.Version);
    }

    public override string ToString() => nameof(DataRequestReceiptRow);
}

/// <summary>
/// Where a data request's fulfilment job stands, read from the job's own row
/// (<see href="../../../../../docs/decisions/0106-subject-data-requests-authority-and-status.md">ADR-0106 § 8</see>).
/// </summary>
public enum DataRequestJobPhase
{
    /// <summary>No fulfilment job of the request is visible in its group.</summary>
    Unavailable = 1,

    /// <summary>Queued, running or retrying: the job may still finish.</summary>
    Pending = 2,

    /// <summary>The job finished with its business outcome committed.</summary>
    Completed = 3,

    /// <summary>Quarantined or dead-lettered: the job ends without a business outcome and runs no hook.</summary>
    Dead = 4,
}

/// <summary>
/// Reads the phase of a request's fulfilment job without writing anything, so a receipt can show <c>failed</c>
/// for a dead job before the reconciler has stored it.
/// </summary>
/// <remarks>
/// Keyed by the request's typed identity and kind, never by the aggregate: it reads, and the group is the announced one.
/// </remarks>
public interface IDataRequestJobPhaseReader
{
    /// <summary>The phase of the request's fulfilment job; a kind with no fulfilment job is <see cref="DataRequestJobPhase.Unavailable"/>.</summary>
    Task<DataRequestJobPhase> ReadAsync(DataSubjectRequestId requestId, DataRequestKind kind, CancellationToken cancellationToken);

    /// <summary>
    /// The phases of a page of the group's requests of one kind in one statement; a request with no job, or of a kind with no
    /// fulfilment job, is <see cref="DataRequestJobPhase.Unavailable"/>.
    /// </summary>
    Task<IReadOnlyDictionary<DataSubjectRequestId, DataRequestJobPhase>> ReadManyAsync(IReadOnlyCollection<DataSubjectRequestId> requestIds,
        DataRequestKind kind, CancellationToken cancellationToken);
}
