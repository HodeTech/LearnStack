using System.Globalization;
using LearnStack.Api.Common;
using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Pagination;

namespace LearnStack.Api.Identity;

/// <summary>The 202 answer to a data-request creation: the request's initial resource (ADR-0106 § 10).</summary>
public sealed record DataRequestAcceptedResponse(string Id, string Kind, string Status, string Version);

/// <summary>
/// The 202 answer to an erasure's creation: the request's initial resource, <c>scheduled</c> until its 24-hour window
/// ends (ADR-0106 § 10, § 11).
/// </summary>
public sealed record DataErasureAcceptedResponse(string Id, string Kind, string Status, string Version);

/// <summary>
/// The 202 answer to an account closure's filing: the request's initial resource, <c>scheduled</c>; an operator may
/// Begin it from its execution time, and the person may cancel it until that Begin commits (ADR-0106 § 10, § 11;
/// ADR-0107 § 7).
/// </summary>
public sealed record AccountClosureAcceptedResponse(string Id, string Kind, string Status, string Version);

public sealed record DataRequestRetainedResponse(string Kind, long Count);

public sealed record DataExportTruncationResponse(string Collection, long Included, long Total);

/// <summary>An artifact's receipt-safe metadata: no content, no key and no key version.</summary>
public sealed record DataExportArtifactResponse(string Id, string Status, string ExpiresAt, string? PurgedAt, long ByteCount,
    int ChunkCount, IReadOnlyList<DataExportTruncationResponse> Truncations, string Version, bool Readable);

/// <summary>The subject's own request receipt (ADR-0106 § 9): no personal data and no internal identifier but the request's.</summary>
/// <remarks>
/// <see cref="ExecuteAt"/> is a scheduled request's execution time: the end of an erasure's cancellable window, and the
/// earliest time an operator may Begin a closure, which stays cancellable until that Begin commits.
/// </remarks>
public sealed record OwnDataRequestResponse(string Id, string Kind, string Origin, string Status, string CreatedAt, string? UpdatedAt,
    string? CompletedAt, string? LastRefusal, string? FailureReason, IReadOnlyList<DataRequestRetainedResponse> Retained,
    DataExportArtifactResponse? Artifact, string Version, string? ExecuteAt);

public sealed record OwnDataRequestPage(IReadOnlyList<OwnDataRequestResponse> Items, PageInfo PageInfo);

/// <summary>
/// An administrator's view of a request of the group (ADR-0106 § 6, § 13): status and counts only, never content; the
/// frozen Membership identifiers name the subject in the vocabulary the administrator already reads.
/// </summary>
public sealed record DataRequestResponse(string Id, string Kind, string Origin, string Status, string CreatedAt, string? UpdatedAt,
    string? CompletedAt, string? LastRefusal, string? FailureReason, IReadOnlyList<DataRequestRetainedResponse> Retained,
    DataExportArtifactResponse? Artifact, string Version, bool RequestedByYou, IReadOnlyList<string> MembershipIds, string? ExecuteAt);

public sealed record DataRequestPage(IReadOnlyList<DataRequestResponse> Items, PageInfo PageInfo);

/// <summary>One retained group of the subject on the real host: its tenant and organization, or none for the tenant-wide group.</summary>
public sealed record RetainedDataRequestScopeResponse(string TenantId, string? OrganizationId);

public sealed record RetainedDataRequestScopePageResponse(IReadOnlyList<RetainedDataRequestScopeResponse> Items, PageInfo PageInfo);

/// <summary>A minted download cursor (ADR-0107 § 4). The ticket is returned once and never stored, logged or replayed.</summary>
public sealed record DataExportTicketResponse
{
    public DataExportTicketResponse(string ticket, string expiresAt, int chunkCount, long byteCount)
    {
        Ticket = ticket;
        ExpiresAt = expiresAt;
        ChunkCount = chunkCount;
        ByteCount = byteCount;
    }

    [PiiSensitive]
    public string Ticket { get; }

    public string ExpiresAt { get; }
    public int ChunkCount { get; }
    public long ByteCount { get; }

    public override string ToString() => nameof(DataExportTicketResponse);
}

/// <summary>The wire shape of one chunk: <c>index</c>, <c>count</c> and base64 <c>bytes</c> of at most 128 KiB of plaintext.</summary>
public sealed record DataExportChunkResponse(int Index, int Count, [property: PiiSensitive] string Bytes)
{
    public override string ToString() => nameof(DataExportChunkResponse);
}

internal static class DataRequestProjection
{
    internal static OwnDataRequestResponse Own(DataRequestView view) => new(view.RequestId.ToString("D"), view.Kind, view.Origin,
        view.Status, Instant(view.CreatedAt), Instant(view.UpdatedAt), Instant(view.CompletedAt), view.LastRefusal, view.FailureReason,
        Retained(view), Artifact(view.Artifact), AdministrationHttpInput.Version(view.Version), Instant(view.ExecuteAt));

    internal static DataRequestResponse Administration(DataRequestView view) => new(view.RequestId.ToString("D"), view.Kind,
        view.Origin, view.Status, Instant(view.CreatedAt), Instant(view.UpdatedAt), Instant(view.CompletedAt), view.LastRefusal,
        view.FailureReason, Retained(view), Artifact(view.Artifact), AdministrationHttpInput.Version(view.Version), view.RequestedByCaller,
        (view.MembershipIds ?? throw new InvalidOperationException("An administrator view names its Membership snapshot."))
            .Select(id => id.ToString("D")).ToArray(), Instant(view.ExecuteAt));

    internal static DataExportTicketResponse Ticket(DataExportTicketGrant grant) =>
        new(grant.Ticket, Instant(grant.ExpiresAt), grant.ChunkCount, grant.ByteCount);

    private static DataRequestRetainedResponse[] Retained(DataRequestView view) =>
        view.Retained.Select(item => new DataRequestRetainedResponse(item.Kind, item.Count)).ToArray();

    private static DataExportArtifactResponse? Artifact(DataExportArtifactView? artifact) => artifact is null ? null
        : new(artifact.ArtifactId.ToString("D"), artifact.Status, Instant(artifact.ExpiresAt), Instant(artifact.PurgedAt), artifact.ByteCount,
            artifact.ChunkCount, artifact.Truncations.Select(item => new DataExportTruncationResponse(item.Collection, item.Included, item.Total)).ToArray(),
            AdministrationHttpInput.Version(artifact.Version), artifact.Readable);

    internal static string Instant(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string? Instant(DateTimeOffset? value) => value is { } instant ? Instant(instant) : null;
}
