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
    private DataRequestScopeGroup Group => new(context.TenantId, context.OrganizationId);

    /// <summary>
    /// Mints a fifteen-minute download cursor bound to the session, over the subject's own readable artifact
    /// (ADR-0107 § 4). Only the subject is ever served: an administrator who filed the export never gets a ticket.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Under the artifact's row lock, so concurrent mints count each other. An artifact that is gone, unreadable or
    /// whose key the ring no longer holds is refused with <c>data_export_unavailable</c>: only a new export helps. A
    /// ticket row and its MUST audit row are permanent receipts, so minting is bounded and a bound reached is refused
    /// with <c>data_export_ticket_limit</c>: at most <see cref="DataRequestLimits.MaximumOpenTicketsPerBinding"/> open
    /// tickets per session binding (wait for one to be consumed or to expire), and at most
    /// <see cref="DataRequestLimits.MaximumTicketsPerArtifact"/> ever per artifact (final: file a new export).
    /// </para>
    /// <para>
    /// Each call records one <see cref="DataExportTelemetry.DownloadCounterName"/> measurement.
    /// </para>
    /// </remarks>
    public async Task<Result<DataExportTicketGrant>> MintTicketAsync(DataRequestCaller caller, Guid requestId,
        CancellationToken cancellationToken)
    {
        var (result, outcome) = await MintCoreAsync(caller, requestId, cancellationToken).ConfigureAwait(false);
        DataExportTelemetry.Download(DataExportDownloadOperation.Mint, outcome);
        return result;
    }

    private async Task<(Result<DataExportTicketGrant> Result, DataExportDownloadOutcome Outcome)> MintCoreAsync(DataRequestCaller caller,
        Guid requestId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.Role != DataRequestRole.Subject)
        {
            return Refused(DataRequestService.Fail<DataExportTicketGrant>("lockey_forbidden"));
        }

        var request = await DataRequestService.FindVisibleAsync(reader, caller, requestId, cancellationToken).ConfigureAwait(false);
        var found = request is { Kind: DataRequestKind.Export }
            ? await reader.FindArtifactLockedAsync(request.Id, cancellationToken).ConfigureAwait(false)
            : null;
        var current = await caller.RecheckAsync(cancellationToken).ConfigureAwait(false);
        if (current.IsFailure)
        {
            return Refused(Result<DataExportTicketGrant>.Fail(current.Error));
        }

        if (request is null || request.Kind != DataRequestKind.Export)
        {
            return Refused(DataRequestService.Fail<DataExportTicketGrant>("lockey_not_found"));
        }

        if (found is null || !found.Readable || found.Artifact.SubjectUserId != caller.Actor)
        {
            return Refused(Unavailable());
        }

        if (!artifacts.CanOpen(found.Artifact.KeyVersion))
        {
            return (Unavailable(), DataExportDownloadOutcome.CustodyFailed);
        }

        var binding = sessions.Capture(caller.Actor, context.TenantId, context.OrganizationId);
        if (binding is null)
        {
            return Refused(DataRequestService.Fail<DataExportTicketGrant>("lockey_forbidden"));
        }

        var counts = await tickets.CountAsync(found.Artifact.Id, binding, cancellationToken).ConfigureAwait(false);
        if (counts.OpenInBinding >= DataRequestLimits.MaximumOpenTicketsPerBinding
            || counts.Minted >= DataRequestLimits.MaximumTicketsPerArtifact)
        {
            return (DataRequestService.Rule<DataExportTicketGrant>("artifact", "lockey_data_export_ticket_limit"),
                DataExportDownloadOutcome.Limit);
        }

        var (ticket, digest) = DataExportTicketCodec.Mint();
        var minted = DataExportTicket.Mint(DataExportTicketId.From(DataRequestService.NewId(ids)), found.Artifact, digest, binding, clock.UtcNow, caller.Actor);
        if (minted.IsFailure)
        {
            return Refused(Unavailable());
        }

        audit.Designate(minted.Value);
        await tickets.AddAsync(minted.Value, cancellationToken).ConfigureAwait(false);
        return (Result.Ok(new DataExportTicketGrant(ticket, minted.Value.ExpiresAt, found.Artifact.ChunkCount, found.Artifact.ByteCount)),
            DataExportDownloadOutcome.Served);
    }

    private static Result<DataExportTicketGrant> Unavailable() =>
        DataRequestService.Rule<DataExportTicketGrant>("artifact", "lockey_data_export_unavailable");

    private static (Result<T> Result, DataExportDownloadOutcome Outcome) Refused<T>(Result<T> refusal) =>
        (refusal, DataExportDownloadOutcome.Refused);

    /// <summary>
    /// Serves one decrypted chunk through a ticket (ADR-0107 § 4). The ticket is a download cursor: while it is unconsumed,
    /// unexpired and presented in the session that minted it, it authorizes the reads of that artifact's chunks; serving
    /// the final chunk (index <c>count - 1</c>) consumes it on the same transaction, after which every read through it is
    /// refused and a resumed or repeated download mints a new ticket. Every refusal is the same <c>not_found</c>; a
    /// chunk the store cannot open is counted as <c>custody_failed</c> in
    /// <see cref="DataExportTelemetry.DownloadCounterName"/>, every other refusal as <c>refused</c>.
    /// </summary>
    public async Task<Result<DataExportChunkView>> ReadChunkAsync(DataRequestCaller caller, Guid requestId, string ticket, int index,
        CancellationToken cancellationToken)
    {
        var (result, outcome) = await ReadChunkCoreAsync(caller, requestId, ticket, index, cancellationToken).ConfigureAwait(false);
        DataExportTelemetry.Download(DataExportDownloadOperation.Chunk, outcome);
        return result;
    }

    private async Task<(Result<DataExportChunkView> Result, DataExportDownloadOutcome Outcome)> ReadChunkCoreAsync(DataRequestCaller caller,
        Guid requestId, string ticket, int index, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var digest = DataExportTicketCodec.TryDigest(ticket);
        if (digest is null || index < 0 || !DataRequestService.ValidId(requestId))
        {
            return Refused(DataRequestService.Fail<DataExportChunkView>("lockey_validation_failed"));
        }

        if (caller.Role != DataRequestRole.Subject)
        {
            return Refused(DataRequestService.Fail<DataExportChunkView>("lockey_forbidden"));
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
            return Refused(Result<DataExportChunkView>.Fail(current.Error));
        }

        if (found is null)
        {
            return Refused(DataRequestService.Fail<DataExportChunkView>("lockey_not_found"));
        }

        audit.Designate(found);
        var binding = sessions.Capture(caller.Actor, context.TenantId, context.OrganizationId);
        var now = clock.UtcNow;
        if (binding is null || !found.Matches(digest, binding) || found.CreatedBy != caller.Actor || !found.IsUsable(now)
            || !artifact!.Readable || index >= artifact.Artifact.ChunkCount)
        {
            return Refused(DataRequestService.Fail<DataExportChunkView>("lockey_not_found"));
        }

        var chunk = await artifacts.ReadChunkAsync(new DataExportArtifactLocator(artifact.Artifact.Id.Value, Group), index, cancellationToken)
            .ConfigureAwait(false);
        if (chunk is null)
        {
            // A readable artifact's chunk inside its count that the store answers as absent could not be opened: its key
            // left the ring or its ciphertext fails authentication (the store logs it). The subject sees not_found.
            return (DataRequestService.Fail<DataExportChunkView>("lockey_not_found"), DataExportDownloadOutcome.CustodyFailed);
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
                    return Refused(DataRequestService.Fail<DataExportChunkView>("lockey_not_found"));
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

        return (Result.Ok(new DataExportChunkView(chunk.Index, chunk.Count, bytes)), DataExportDownloadOutcome.Served);
    }
}
