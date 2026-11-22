using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Idempotency;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Identity.Application.Contracts.DataRequests;

// The live-session data-request API of ADR-0106 and ADR-0107 § 4: one request type per operation and policy kind.
// The subject's own operations carry no permission key (OwnDataRequest); the administrator's are Protected on the
// existing identity.membership.admin alternatives. The retained-entry operations of ADR-0106 § 9 add thin request
// types of their own over the same handler cores.

/// <summary>The receipt of a request mutation: the request and its version after the change.</summary>
public sealed record DataRequestMutationResult(Guid RequestId, long Version);

/// <summary>The receipt of an artifact revocation: the request it belongs to and the artifact's version after it.</summary>
public sealed record DataExportArtifactMutationResult(Guid RequestId, Guid ArtifactId, long Version);

/// <summary>One capped collection of a bundle: what the bundle states, never a value (ADR-0107 § 3).</summary>
public sealed record DataExportTruncationView(string Collection, long Included, long Total);

/// <summary>One closed retained-reference kind and its count (ADR-0107 § 6); never a row identifier.</summary>
public sealed record DataRequestRetainedView(string Kind, long Count);

/// <summary>
/// An export artifact's receipt-safe metadata: no plaintext, no ciphertext, no key and no key version.
/// <see cref="Readable"/> is the time check against the database clock (ADR-0107 § 4).
/// </summary>
public sealed record DataExportArtifactView(Guid ArtifactId, string Status, DateTimeOffset ExpiresAt, DateTimeOffset? PurgedAt,
    long ByteCount, int ChunkCount, IReadOnlyList<DataExportTruncationView> Truncations, long Version, bool Readable);

/// <summary>
/// A request's receipt (ADR-0106 § 9): identity, kind, origin, status, timestamps, the recorded refusal or failure
/// code, the closed retained-reference kinds and counts, and the artifact's metadata. No personal data, no bundle
/// content. <see cref="MembershipIds"/> is the frozen snapshot and is shown only to an administrator.
/// </summary>
public sealed record DataRequestView(Guid RequestId, string Kind, string Origin, string Status, DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt, DateTimeOffset? CompletedAt, DateTimeOffset? ExecuteAt, string? LastRefusal, string? FailureReason,
    IReadOnlyList<DataRequestRetainedView> Retained, DataExportArtifactView? Artifact, long Version, bool RequestedByCaller,
    IReadOnlyList<Guid>? MembershipIds);

/// <summary>A minted download cursor: the opaque ticket, returned exactly once, and what it authorizes.</summary>
/// <remarks>The ticket is a bearer value only together with the session it is bound to; it never prints.</remarks>
public sealed record DataExportTicketGrant
{
    public DataExportTicketGrant(string ticket, DateTimeOffset expiresAt, int chunkCount, long byteCount)
    {
        Ticket = ticket;
        ExpiresAt = expiresAt;
        ChunkCount = chunkCount;
        ByteCount = byteCount;
    }

    [PiiSensitive]
    public string Ticket { get; }

    public DateTimeOffset ExpiresAt { get; }
    public int ChunkCount { get; }
    public long ByteCount { get; }

    public override string ToString() => nameof(DataExportTicketGrant);
}

/// <summary>
/// One decrypted chunk of a ticketed download (ADR-0107 § 4). The owner of the bytes clears them once they are
/// written; the type never prints them.
/// </summary>
public sealed record DataExportChunkView
{
    public DataExportChunkView(int index, int count, byte[] bytes)
    {
        Index = index;
        Count = count;
        Bytes = bytes;
    }

    public int Index { get; }
    public int Count { get; }

    [PiiSensitive]
    public byte[] Bytes { get; }

    public override string ToString() => nameof(DataExportChunkView);
}

// ---- The subject, through a live session (OperationPolicyKind.OwnDataRequest) ----

/// <summary>The person files an export of their own data in the session's group (ADR-0106 § 3).</summary>
public sealed record CreateOwnDataExportCommand : IRequest<Result<DataRequestMutationResult>>;

/// <summary>The person's own requests in the session's group, newest first.</summary>
public sealed record ListOwnDataRequestsQuery(string? Cursor = null, int Limit = 20) : IRequest<Result<Page<DataRequestView>>>;

/// <summary>The person's own request receipt in the session's group.</summary>
public sealed record GetOwnDataRequestQuery(Guid RequestId) : IRequest<Result<DataRequestView>>;

/// <summary><c>requested → cancelled</c> by the subject (ADR-0106 § 8, § 11).</summary>
public sealed record CancelOwnDataRequestCommand(Guid RequestId, long ExpectedVersion) : IRequest<Result<DataRequestMutationResult>>;

/// <summary>The subject revokes an unexpired export artifact of their own request, which purges it at once (ADR-0107 § 4).</summary>
public sealed record RevokeOwnDataExportCommand(Guid RequestId, long ExpectedArtifactVersion)
    : IRequest<Result<DataExportArtifactMutationResult>>;

/// <summary>Mints a fifteen-minute download cursor over the subject's available artifact (ADR-0107 § 4).</summary>
public sealed record MintOwnDataExportTicketCommand(Guid RequestId) : IRequest<Result<DataExportTicketGrant>>;

/// <summary>Reads one decrypted chunk through a ticket; the ticket travels only in a request body.</summary>
public sealed record ReadOwnDataExportChunkCommand : IRequest<Result<DataExportChunkView>>
{
    public ReadOwnDataExportChunkCommand(Guid requestId, string ticket, int index)
    {
        RequestId = requestId;
        Ticket = ticket;
        Index = index;
    }

    public Guid RequestId { get; }

    [PiiSensitive]
    public string Ticket { get; }

    public int Index { get; }

    public override string ToString() => nameof(ReadOwnDataExportChunkCommand);
}

/// <summary>Authorizes only disclosure of a stored receipt for the subject's own original request.</summary>
public sealed record ReadOwnDataRequestReceiptQuery(Guid RequestId) : IRequest<Result<None>>, IIdempotencyReceiptRead;

// ---- An administrator of the group (Protected, identity.membership.admin at Tenant or Organization) ----

/// <summary>
/// A holder of <c>identity.membership.admin</c> covering the group files an export for the person who holds or held
/// the named Membership of any status in the group (ADR-0106 § 3); never for themselves.
/// </summary>
public sealed record CreateDataExportForSubjectCommand(Guid MembershipId) : IRequest<Result<DataRequestMutationResult>>;

/// <summary>The group's export and erasure requests: status and counts only, never content.</summary>
public sealed record ListDataRequestsQuery(string? Cursor = null, int Limit = 20) : IRequest<Result<Page<DataRequestView>>>;

/// <summary>One request of the group: status and counts only, never content.</summary>
public sealed record GetDataRequestQuery(Guid RequestId) : IRequest<Result<DataRequestView>>;

/// <summary><c>requested → cancelled</c> by the administrator who filed the request.</summary>
public sealed record CancelDataRequestCommand(Guid RequestId, long ExpectedVersion) : IRequest<Result<DataRequestMutationResult>>;

/// <summary>The administrator who filed an export revokes its unexpired artifact, which purges it at once.</summary>
public sealed record RevokeDataExportCommand(Guid RequestId, long ExpectedArtifactVersion)
    : IRequest<Result<DataExportArtifactMutationResult>>;

/// <summary>Authorizes only disclosure of a stored receipt for the administrator's original request.</summary>
public sealed record ReadDataRequestReceiptQuery(Guid RequestId) : IRequest<Result<None>>, IIdempotencyReceiptRead;

// ---- The subject, through the retained own scope (OperationPolicyKind.OwnDataRequestEntry, ADR-0106 § 9) ----
// Each type acts in exactly the group the subject's own retained-scope witness proved, never in a Membership's
// context: a Suspended person, a person whose Membership was revoked and a group where only a request survives are
// all served. The group a type names is a selection the witness must agree with, never authority of its own.

/// <summary>One page of the subject's retained groups on the real host, in the function's order.</summary>
/// <remarks><see cref="NextCursor"/> is the last group's key: <c>tenant</c> for the tenant-wide group, else the organization.</remarks>
public sealed record RetainedDataRequestScopePage(IReadOnlyList<DataRequestScopeGroup> Items, string? NextCursor);

/// <summary>Discovers the groups on the real host where the subject holds or held a Membership or has a request.</summary>
public sealed record ListRetainedDataRequestScopesQuery(string? Cursor = null, int Limit = 20) : IRequest<Result<RetainedDataRequestScopePage>>;

/// <summary>The subject's own requests in one retained group, newest first.</summary>
/// <param name="OrganizationId">The selected group's organization, or <see langword="null"/> for the tenant-wide group.</param>
public sealed record ListRetainedDataRequestsQuery(OrganizationId? OrganizationId, string? Cursor = null, int Limit = 20)
    : IRequest<Result<Page<DataRequestView>>>;

/// <summary>The subject files an export of their own data in one retained group (ADR-0106 § 3, § 9).</summary>
public sealed record CreateRetainedDataExportCommand(OrganizationId? OrganizationId) : IRequest<Result<DataRequestMutationResult>>;

/// <summary><c>requested → cancelled</c> by the subject in one retained group (ADR-0106 § 8, § 11).</summary>
public sealed record CancelRetainedDataRequestCommand(OrganizationId? OrganizationId, Guid RequestId, long ExpectedVersion)
    : IRequest<Result<DataRequestMutationResult>>;

/// <summary>Mints a fifteen-minute download cursor over the subject's available artifact in one retained group.</summary>
public sealed record MintRetainedDataExportTicketCommand(OrganizationId? OrganizationId, Guid RequestId) : IRequest<Result<DataExportTicketGrant>>;

/// <summary>Reads one decrypted chunk through a ticket in one retained group; the ticket travels only in a request body.</summary>
public sealed record ReadRetainedDataExportChunkCommand : IRequest<Result<DataExportChunkView>>
{
    public ReadRetainedDataExportChunkCommand(OrganizationId? organizationId, Guid requestId, string ticket, int index)
    {
        OrganizationId = organizationId;
        RequestId = requestId;
        Ticket = ticket;
        Index = index;
    }

    public OrganizationId? OrganizationId { get; }

    public Guid RequestId { get; }

    [PiiSensitive]
    public string Ticket { get; }

    public int Index { get; }

    public override string ToString() => nameof(ReadRetainedDataExportChunkCommand);
}

/// <summary>Authorizes only disclosure of a stored receipt for the subject's own original retained-entry request.</summary>
public sealed record ReadRetainedDataRequestReceiptQuery(Guid RequestId) : IRequest<Result<None>>, IIdempotencyReceiptRead;
