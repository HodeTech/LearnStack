using System.Collections.Immutable;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;

namespace LearnStack.Modules.Identity.Domain;

/// <summary>
/// The receipt-safe metadata of one encrypted export bundle
/// (<see href="../../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 4</see>).
/// </summary>
/// <remarks>
/// <para>
/// The ciphertext is not here: its chunks are plain storage rows written and read by the artifact
/// store, and never pass through change tracking. This row holds the size, the chunk count, the key
/// version and the lifecycle, and stays as the receipt after its chunks are gone.
/// </para>
/// <para>
/// <b>Lifecycle.</b> <c>available</c> until its expiry; <c>revoked</c> by the subject, the requesting
/// administrator, a newer export or an erasure, which purges the chunks at once; <c>expired</c> once
/// observed past its expiry; <c>purged</c> by the delayed purge job. Expiry is a time check, so an
/// unpurged expired artifact is already unreadable.
/// </para>
/// <para>
/// The group and subject mirror the request's: a subject has at most one <c>available</c> artifact
/// per group, which a partial unique index enforces.
/// </para>
/// </remarks>
[TenantOwned]
[OrganizationScoped]
public sealed class DataExportArtifact : AuditableEntity<DataExportArtifactId>, IAggregateRoot<DataExportArtifactId>, IOrganizationScoped
{
    private DataExportArtifact() { }

    public TenantId TenantId { get; private set; }
    public OrganizationId? OrganizationId { get; private set; }
    public DataSubjectRequestId RequestId { get; private set; }
    public UserId SubjectUserId { get; private set; }
    public DataExportArtifactStatus Status { get; private set; }

    /// <summary>The plaintext bundle size.</summary>
    public long ByteCount { get; private set; }

    public int ChunkCount { get; private set; }

    /// <summary>The key-ring version the chunks were sealed under; a label, never key material.</summary>
    public string KeyVersion { get; private set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>When the chunks were deleted, by revocation or by the purge.</summary>
    public DateTimeOffset? PurgedAt { get; private set; }

    /// <summary>
    /// The bundle's truncation statement as canonical JSON, set at creation and never changed: the receipt
    /// says a section was capped exactly as the bundle does (ADR-0107 § 3). <c>{}</c> when nothing was capped.
    /// </summary>
    public string TruncationJson { get; private set; } = DataRequestDocuments.EmptyObject;

    public ImmutableArray<DataExportTruncation> Truncations => DataRequestDocuments.ReadTruncation(TruncationJson);

    /// <summary>
    /// Records the artifact of an export that is fulfilling: the group and subject come from the request,
    /// never from the caller.
    /// </summary>
    public static Result<DataExportArtifact> Create(DataExportArtifactId id, DataSubjectRequest request, long byteCount,
        string keyVersion, DateTimeOffset at, UserId actor) =>
        Create(id, request, byteCount, keyVersion, [], at, actor);

    /// <summary>
    /// Records the artifact of an export that is fulfilling, with the bundle's truncation statement.
    /// </summary>
    public static Result<DataExportArtifact> Create(DataExportArtifactId id, DataSubjectRequest request, long byteCount,
        string keyVersion, IReadOnlyCollection<DataExportTruncation> truncations, DateTimeOffset at, UserId actor)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(truncations);
        var truncation = DataRequestDocuments.WriteTruncation(truncations);
        if (truncation is null || !StronglyTypedId.IsAssigned(id) || id.Value.Version != 7
            || request.Kind != DataRequestKind.Export || request.Status != DataRequestStatus.Fulfilling || request.IsDeleted
            || byteCount is < 1 or > DataRequestLimits.MaximumBundleBytes || !DataRequestLimits.IsKeyVersion(keyVersion)
            || !StronglyTypedId.IsAssigned(actor) || at == default || at.Offset != TimeSpan.Zero
            || at < (request.UpdatedAt ?? request.CreatedAt) || at > DateTimeOffset.MaxValue - DataRequestLimits.ArtifactLifetime)
        {
            return Result<DataExportArtifact>.Fail(IdentityFailures.BusinessRuleError());
        }

        var artifact = new DataExportArtifact
        {
            Id = id,
            TenantId = request.TenantId,
            OrganizationId = request.OrganizationId,
            RequestId = request.Id,
            SubjectUserId = request.SubjectUserId,
            Status = DataExportArtifactStatus.Available,
            ByteCount = byteCount,
            ChunkCount = DataRequestLimits.ChunkCountFor(byteCount),
            KeyVersion = keyVersion,
            TruncationJson = truncation,
            ExpiresAt = at + DataRequestLimits.ArtifactLifetime,
        };
        artifact.MarkCreated(at, actor);
        return Result<DataExportArtifact>.Ok(artifact);
    }

    /// <summary>Whether a chunk may be read at <paramref name="now"/>: available and unexpired.</summary>
    public bool IsReadable(DateTimeOffset now) => Status == DataExportArtifactStatus.Available && now < ExpiresAt;

    /// <summary><c>available → revoked</c> before expiry; the caller deletes the chunks in the same transaction.</summary>
    public Result<None> Revoke(DateTimeOffset at, UserId actor) =>
        Transition(Status == DataExportArtifactStatus.Available && at < ExpiresAt, DataExportArtifactStatus.Revoked, purged: true, at, actor);

    /// <summary><c>available → expired</c>, once its expiry has passed and before the purge.</summary>
    /// <remarks>
    /// No Step 1 path calls it: expiry is a time check (<see cref="IsReadable"/>), receipts display <c>expired</c>
    /// without a write, and the purge job moves an expired artifact straight to <c>purged</c>. It is kept because the
    /// stored lifecycle of ADR-0107 § 4 and the artifact guard admit the arrow, so the aggregate and the database stay
    /// one machine; the operator inspection of P03-10 Step 2 (ADR-0107 § 8, the expired artifacts not yet purged) is
    /// the first caller that may record an observed expiry, and Phase 11's retention owns the rest.
    /// </remarks>
    public Result<None> Expire(DateTimeOffset at, UserId actor) =>
        Transition(Status == DataExportArtifactStatus.Available && at >= ExpiresAt, DataExportArtifactStatus.Expired, purged: false, at, actor);

    /// <summary><c>available</c> or <c>expired → purged</c> after expiry; the caller deletes the chunks in the same transaction.</summary>
    public Result<None> Purge(DateTimeOffset at, UserId actor) =>
        Transition(Status is DataExportArtifactStatus.Available or DataExportArtifactStatus.Expired && at >= ExpiresAt,
            DataExportArtifactStatus.Purged, purged: true, at, actor);

    private Result<None> Transition(bool admitted, DataExportArtifactStatus next, bool purged, DateTimeOffset at, UserId actor)
    {
        if (!admitted || IsDeleted || Version == long.MaxValue || !StronglyTypedId.IsAssigned(actor)
            || at == default || at.Offset != TimeSpan.Zero || at < (UpdatedAt ?? CreatedAt))
        {
            return IdentityFailures.BusinessRule();
        }

        MarkUpdated(at, actor);
        Status = next;
        if (purged)
        {
            PurgedAt = at;
        }

        return Result<None>.Ok(None.Value);
    }
}
