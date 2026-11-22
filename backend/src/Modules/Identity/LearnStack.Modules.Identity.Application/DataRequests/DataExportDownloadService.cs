using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;

namespace LearnStack.Modules.Identity.Application.DataRequests;

/// <summary>
/// The ticketed download cores of ADR-0107 § 4: the subject mints a fifteen-minute cursor bound to the session and reads
/// the artifact's chunks through it. The one aggregate this executor writes is the <see cref="DataExportTicket"/>, through
/// <see cref="IDataExportTicketStore"/>; the artifact and its chunks are read through their own ports.
/// </summary>
/// <remarks>
/// A ticket is returned once and stored only as its digest; a chunk's plaintext is handed to the transport, which clears it
/// once written. Neither reaches a log, an exception, an audit row or an idempotency receipt: neither operation is keyed,
/// and the audit capture redacts the ticket's digests.
/// </remarks>
public sealed class DataExportDownloadService(IDataRequestReader reader, IDataExportTicketStore tickets, IDataExportArtifactStore artifacts,
    IDataExportSessionBinding sessions, ITenantContext context, IAuditSubject audit, IGuidFactory ids, IClock clock)
{
    /// <summary>
    /// The most unconsumed, unexpired tickets one artifact may hold at once. A ticket row and its MUST audit row are
    /// permanent receipts, so minting is bounded: a download in progress, a retry and one spare, and the next mint
    /// waits for one to be consumed or to expire (fifteen minutes).
    /// </summary>
    internal const int MaximumOpenTickets = 3;

    private DataRequestScopeGroup Group => new(context.TenantId, context.OrganizationId);

    /// <summary>
    /// Mints a fifteen-minute download cursor bound to the session, over the subject's own readable artifact
    /// (ADR-0107 § 4). Only the subject is ever served: an administrator who filed the export never gets a ticket.
    /// </summary>
    /// <remarks>
    /// Under the artifact's row lock, so concurrent mints count each other: an artifact whose key the ring no longer
    /// holds, or that already holds <see cref="MaximumOpenTickets"/> open tickets, is refused with the same
    /// <c>data_export_unavailable</c> as an artifact that is gone.
    /// </remarks>
    public async Task<Result<DataExportTicketGrant>> MintTicketAsync(DataRequestCaller caller, Guid requestId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.Role != DataRequestRole.Subject)
        {
            return DataRequestService.Fail<DataExportTicketGrant>("lockey_forbidden");
        }

        var request = await DataRequestService.FindVisibleAsync(reader, caller, requestId, cancellationToken).ConfigureAwait(false);
        var found = request is { Kind: DataRequestKind.Export }
            ? await reader.FindArtifactLockedAsync(request.Id, cancellationToken).ConfigureAwait(false)
            : null;
        var current = await caller.RecheckAsync(cancellationToken).ConfigureAwait(false);
        if (current.IsFailure)
        {
            return Result<DataExportTicketGrant>.Fail(current.Error);
        }

        if (request is null || request.Kind != DataRequestKind.Export)
        {
            return DataRequestService.Fail<DataExportTicketGrant>("lockey_not_found");
        }

        if (found is null || !found.Readable || found.Artifact.SubjectUserId != caller.Actor
            || !artifacts.CanOpen(found.Artifact.KeyVersion))
        {
            return DataRequestService.Rule<DataExportTicketGrant>("artifact", "lockey_data_export_unavailable");
        }

        if (await tickets.CountOpenAsync(found.Artifact.Id, cancellationToken).ConfigureAwait(false) >= MaximumOpenTickets)
        {
            return DataRequestService.Rule<DataExportTicketGrant>("artifact", "lockey_data_export_unavailable");
        }

        var binding = sessions.Capture(caller.Actor, context.TenantId, context.OrganizationId);
        if (binding is null)
        {
            return DataRequestService.Fail<DataExportTicketGrant>("lockey_forbidden");
        }

        var (ticket, digest) = DataExportTicketCodec.Mint();
        var minted = DataExportTicket.Mint(DataExportTicketId.From(DataRequestService.NewId(ids)), found.Artifact, digest, binding, clock.UtcNow, caller.Actor);
        if (minted.IsFailure)
        {
            return DataRequestService.Rule<DataExportTicketGrant>("artifact", "lockey_data_export_unavailable");
        }

        audit.Designate(minted.Value);
        await tickets.AddAsync(minted.Value, cancellationToken).ConfigureAwait(false);
        return Result.Ok(new DataExportTicketGrant(ticket, minted.Value.ExpiresAt, found.Artifact.ChunkCount, found.Artifact.ByteCount));
    }

    /// <summary>
    /// Serves one decrypted chunk through a ticket (ADR-0107 § 4). The ticket is a download cursor: while it is unconsumed,
    /// unexpired and presented in the session that minted it, it authorizes the reads of that artifact's chunks; serving
    /// the final chunk (index <c>count - 1</c>) consumes it on the same transaction, after which every read through it is
    /// refused and a resumed or repeated download mints a new ticket. Every refusal is the same <c>not_found</c>.
    /// </summary>
    public async Task<Result<DataExportChunkView>> ReadChunkAsync(DataRequestCaller caller, Guid requestId, string ticket, int index,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var digest = DataExportTicketCodec.TryDigest(ticket);
        if (digest is null || index < 0 || !DataRequestService.ValidId(requestId))
        {
            return DataRequestService.Fail<DataExportChunkView>("lockey_validation_failed");
        }

        if (caller.Role != DataRequestRole.Subject)
        {
            return DataRequestService.Fail<DataExportChunkView>("lockey_forbidden");
        }

        var request = await DataRequestService.FindVisibleAsync(reader, caller, requestId, cancellationToken).ConfigureAwait(false);
        var artifact = request is { Kind: DataRequestKind.Export }
            ? (await reader.ReadArtifactsAsync([request.Id], cancellationToken).ConfigureAwait(false)).GetValueOrDefault(request.Id)
            : null;
        var found = artifact is null ? null
            : await tickets.FindLockedAsync(artifact.Artifact.Id, digest, cancellationToken).ConfigureAwait(false);
        var current = await caller.RecheckAsync(cancellationToken).ConfigureAwait(false);
        if (current.IsFailure)
        {
            return Result<DataExportChunkView>.Fail(current.Error);
        }

        if (found is null)
        {
            return DataRequestService.Fail<DataExportChunkView>("lockey_not_found");
        }

        audit.Designate(found);
        var binding = sessions.Capture(caller.Actor, context.TenantId, context.OrganizationId);
        var now = clock.UtcNow;
        if (binding is null || !found.Matches(digest, binding) || found.CreatedBy != caller.Actor || !found.IsUsable(now)
            || !artifact!.Readable || index >= artifact.Artifact.ChunkCount)
        {
            return DataRequestService.Fail<DataExportChunkView>("lockey_not_found");
        }

        var chunk = await artifacts.ReadChunkAsync(new DataExportArtifactLocator(artifact.Artifact.Id.Value, Group), index, cancellationToken)
            .ConfigureAwait(false);
        if (chunk is null)
        {
            return DataRequestService.Fail<DataExportChunkView>("lockey_not_found");
        }

        var bytes = MemoryMarshal.TryGetArray(chunk.Bytes, out var segment) && segment.Offset == 0 && segment.Count == segment.Array!.Length
            ? segment.Array : chunk.Bytes.ToArray();
        try
        {
            if (chunk.Index == chunk.Count - 1)
            {
                var consumed = found.Consume(now);
                if (consumed.IsFailure)
                {
                    CryptographicOperations.ZeroMemory(bytes);
                    return DataRequestService.Fail<DataExportChunkView>("lockey_not_found");
                }

                await tickets.UpdateAsync(found, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }

        if (!ReferenceEquals(bytes, segment.Array) && segment.Array is { } source)
        {
            CryptographicOperations.ZeroMemory(source);
        }

        return Result.Ok(new DataExportChunkView(chunk.Index, chunk.Count, bytes));
    }
}
