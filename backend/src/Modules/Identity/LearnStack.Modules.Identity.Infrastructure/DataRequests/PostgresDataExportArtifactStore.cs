using LearnStack.Infrastructure.DataProtection;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Modules.Identity.Infrastructure.DataRequests;

/// <summary>
/// The default <see cref="IDataExportArtifactStore"/>: encrypted chunks in <c>data_export_chunks</c> and the
/// receipt-safe metadata in <c>data_export_artifacts</c>
/// (<see href="../../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 4</see>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Ambient only.</b> Every call runs on the caller's announced tenant transaction as <c>learnstack_app</c> and
/// opens no connection; the locator's group must be the announced one, so the row policy and this check agree.
/// </para>
/// <para>
/// <b>The metadata is an aggregate, the ciphertext is not.</b> The artifact row is written through the tracked
/// <see cref="DataExportArtifact"/>, so its creation and transitions reach the audit capture; the chunks are
/// written, read and deleted with fixed parameterized statements and are never tracked, so ciphertext cannot reach
/// an audit snapshot. The database guards admit a chunk only for an available artifact and the deferred
/// completeness check refuses a commit in which an available artifact lacks a chunk or a revoked or purged one
/// keeps one.
/// </para>
/// <para>
/// <b>Bounded memory.</b> <see cref="PutAsync"/> seals and inserts each plaintext chunk as it arrives and keeps
/// none; a read decrypts one chunk.
/// </para>
/// <para>
/// <b>Time.</b> The artifact is stamped, and its readability and the revoke-or-purge decision are judged, by the
/// database clock the guards use, so an artifact past its expiry is unreadable whether or not its purge ran and a
/// revocation never meets a creation stamp from a clock ahead of the database's.
/// </para>
/// <para>
/// <b>Failures.</b> A statement that fails or outlives its five-second budget leaves the owner rollback-only and is
/// the retryable <see cref="InfrastructureException"/>; a custody refusal of a stored chunk (a key the ring no longer
/// holds, or a chunk that fails authentication) is logged with identifiers only and answered as an absence, so the
/// subject meets the one uniform refusal and no secret or position leaves. The first refusal of an artifact under a
/// key label in this process is <see cref="LogLevel.Critical"/> and its repeats (every further chunk, every retry)
/// are <see cref="LogLevel.Warning"/>, through <see cref="DataExportCustodyRefusals"/>.
/// </para>
/// </remarks>
public sealed class PostgresDataExportArtifactStore(IdentityDbContext db, IUnitOfWork unit, ITenantContext tenant,
    DataExportCipher cipher, IAuditSubject audit, DataExportCustodyRefusals refusals, ILogger<PostgresDataExportArtifactStore> logger)
    : IDataExportArtifactStore
{
    private static readonly EventId CustodyRefusedEvent = new(3193, "DataExportChunkCustodyRefused");

    private static readonly Action<ILogger, Guid, Guid, int, string, Exception?> LogCustodyRefused =
        LoggerMessage.Define<Guid, Guid, int, string>(LogLevel.Critical, CustodyRefusedEvent,
            "Data export chunk custody refused: tenant {TenantId}, artifact {ArtifactId}, chunk {ChunkIndex}, key version {KeyVersion}.");

    private static readonly Action<ILogger, Guid, Guid, int, string, Exception?> LogCustodyRefusedAgain =
        LoggerMessage.Define<Guid, Guid, int, string>(LogLevel.Warning, CustodyRefusedEvent,
            "Data export chunk custody refused again: tenant {TenantId}, artifact {ArtifactId}, chunk {ChunkIndex}, key version {KeyVersion}; reported at critical severity before.");

    /// <inheritdoc />
    public bool CanOpen(string keyVersion) => cipher.HoldsKey(keyVersion);

    public async Task<DataExportArtifactMetadata> PutAsync(DataExportArtifactPut put, IAsyncEnumerable<ReadOnlyMemory<byte>> plaintextChunks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(put);
        ArgumentNullException.ThrowIfNull(plaintextChunks);
        RequireGroup(put.Group, writable: true);
        var requestId = DataSubjectRequestId.From(put.RequestId);
        var request = await db.DataSubjectRequests.SingleOrDefaultAsync(candidate => candidate.Id == requestId
                && candidate.TenantId == tenant.TenantId && candidate.OrganizationId == tenant.OrganizationId, cancellationToken)
            .ConfigureAwait(false);
        if (request is null || request.SubjectUserId != put.SubjectUserId)
        {
            throw Refused();
        }

        // The database clock stamps the artifact, as the guards judge it; the request's own last stamp is the floor the
        // aggregate requires, so a request clock ahead of the database's still orders.
        var now = await DatabaseNowAsync(cancellationToken).ConfigureAwait(false);
        var floor = request.UpdatedAt ?? request.CreatedAt;
        var created = DataExportArtifact.Create(DataExportArtifactId.From(put.ArtifactId), request, put.ByteCount,
            cipher.ActiveKeyVersion, put.Truncations, now > floor ? now : floor, tenant.UserId ?? UserId.SystemActor);
        if (created.IsFailure || created.Value.ChunkCount != put.ChunkCount)
        {
            throw Refused();
        }

        var artifact = created.Value;
        db.DataExportArtifacts.Add(artifact);
        audit.Designate(artifact);
        await SaveAsync(cancellationToken).ConfigureAwait(false);

        var index = 0;
        long stored = 0;
        await foreach (var chunk in plaintextChunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var expected = index < put.ChunkCount - 1
                ? DataRequestLimits.ChunkPlaintextBytes
                : put.ByteCount - ((long)(put.ChunkCount - 1) * DataRequestLimits.ChunkPlaintextBytes);
            if (index >= put.ChunkCount || chunk.Length != expected)
            {
                throw Refused();
            }

            var sealedChunk = cipher.Seal(new DataExportChunkBinding(tenant.TenantId.Value, put.RequestId, put.ArtifactId,
                index, put.ChunkCount), chunk.Span);
            await InsertChunkAsync(put.ArtifactId, index, sealedChunk, cancellationToken).ConfigureAwait(false);
            stored += chunk.Length;
            index++;
        }

        if (index != put.ChunkCount || stored != put.ByteCount)
        {
            throw Refused();
        }

        return Metadata(artifact, readable: true);
    }

    public async Task<DataExportArtifactMetadata?> ReadMetadataAsync(DataExportArtifactLocator artifact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        RequireGroup(artifact.Group, writable: false);
        var id = DataExportArtifactId.From(artifact.ArtifactId);
        var found = await db.DataExportArtifacts.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == id
                && candidate.TenantId == tenant.TenantId && candidate.OrganizationId == tenant.OrganizationId, cancellationToken)
            .ConfigureAwait(false);
        if (found is null)
        {
            return null;
        }

        var now = await DatabaseNowAsync(cancellationToken).ConfigureAwait(false);
        return Metadata(found, found.Status == DataExportArtifactStatus.Available && now < found.ExpiresAt);
    }

    public async Task<DataExportPlaintextChunk?> ReadChunkAsync(DataExportArtifactLocator artifact, int index, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        RequireGroup(artifact.Group, writable: false);
        if (index is < 0 or >= DataRequestLimits.MaximumChunkCount)
        {
            return null;
        }

        await using var command = Command("""
            SELECT a.request_id, a.chunk_count, a.key_version, c.nonce, c.ciphertext, c.tag
            FROM public.data_export_artifacts AS a
            JOIN public.data_export_chunks AS c ON c.tenant_id = a.tenant_id AND c.artifact_id = a.id
            WHERE a.tenant_id = @tenant AND a.organization_id IS NOT DISTINCT FROM @organization AND a.id = @artifact
              AND c.chunk_index = @index AND a.status = 'available' AND clock_timestamp() < a.expires_at
            """);
        command.Parameters.AddWithValue("artifact", artifact.ArtifactId);
        command.Parameters.AddWithValue("index", index);
        var stored = await ExecuteAsync(async token =>
        {
            await using var row = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await row.ReadAsync(token).ConfigureAwait(false))
            {
                return (StoredChunk?)null;
            }

            return new StoredChunk(row.GetGuid(0), row.GetInt32(1),
                new DataExportSealedChunk(row.GetString(2), (byte[])row[3], (byte[])row[4], (byte[])row[5]));
        }, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return null;
        }

        byte[] plaintext;
        try
        {
            plaintext = cipher.Open(new DataExportChunkBinding(tenant.TenantId.Value, stored.RequestId, artifact.ArtifactId, index,
                stored.Count), stored.Chunk);
        }
        catch (ObjectDisposedException)
        {
            // The host is stopping and the key ring has been cleared: retryable, nothing was disclosed.
            throw Unavailable();
        }
        catch (Exception failure) when (failure is InvalidOperationException or ArgumentException)
        {
            // A key the ring no longer holds, or a chunk moved, altered or sealed under another binding: the stored bundle
            // cannot be served. The label is a non-secret ring version, never key material.
            var label = DataRequestLimits.IsKeyVersion(stored.Chunk.KeyVersion) ? stored.Chunk.KeyVersion : "invalid";
            (refusals.IsFirst(artifact.ArtifactId, label) ? LogCustodyRefused : LogCustodyRefusedAgain)(
                logger, tenant.TenantId.Value, artifact.ArtifactId, index, label, null);
            return null;
        }

        return new DataExportPlaintextChunk(index, stored.Count, plaintext);
    }

    public async Task<DataExportArtifactStatus?> PurgeAsync(DataExportArtifactLocator artifact, DataExportArtifactStatus outcome,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (outcome is not (DataExportArtifactStatus.Revoked or DataExportArtifactStatus.Purged))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), "A purge records the artifact revoked or purged.");
        }

        RequireGroup(artifact.Group, writable: true);
        await using (var lockRow = Command("""
            SELECT id FROM public.data_export_artifacts
            WHERE id = @artifact AND tenant_id = @tenant AND organization_id IS NOT DISTINCT FROM @organization FOR UPDATE
            """))
        {
            lockRow.Parameters.AddWithValue("artifact", artifact.ArtifactId);
            if (await ExecuteAsync(token => lockRow.ExecuteScalarAsync(token), cancellationToken).ConfigureAwait(false) is null)
            {
                return null;
            }
        }

        var id = DataExportArtifactId.From(artifact.ArtifactId);
        var found = await db.DataExportArtifacts.SingleAsync(candidate => candidate.Id == id
                && candidate.TenantId == tenant.TenantId && candidate.OrganizationId == tenant.OrganizationId, cancellationToken)
            .ConfigureAwait(false);
        if (found.Status is DataExportArtifactStatus.Revoked or DataExportArtifactStatus.Purged)
        {
            return found.Status;
        }

        // The database clock decides, as the guard does: revoke before the expiry, purge after it.
        var now = await DatabaseNowAsync(cancellationToken).ConfigureAwait(false);
        var expired = now >= found.ExpiresAt;
        if (!expired && outcome == DataExportArtifactStatus.Purged)
        {
            return found.Status;
        }

        var actor = tenant.UserId ?? UserId.SystemActor;
        var transition = expired ? found.Purge(now, actor) : found.Revoke(now, actor);
        if (transition.IsFailure)
        {
            // The clocks disagree at the boundary: a later attempt decides again.
            unit.MarkRollbackOnly();
            throw Unavailable();
        }

        // The transition is stored first: the chunk guard deletes ciphertext only of an artifact that is no longer
        // available, and the deferred completeness check demands that none is left when the transaction commits.
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await using (var delete = Command("""
            DELETE FROM public.data_export_chunks WHERE tenant_id = @tenant AND artifact_id = @artifact
            """))
        {
            delete.Parameters.AddWithValue("artifact", artifact.ArtifactId);
            await ExecuteAsync(token => delete.ExecuteNonQueryAsync(token), cancellationToken).ConfigureAwait(false);
        }

        return found.Status;
    }

    private async Task InsertChunkAsync(Guid artifactId, int index, DataExportSealedChunk chunk, CancellationToken cancellationToken)
    {
        await using var command = Command("""
            INSERT INTO public.data_export_chunks (tenant_id, organization_id, artifact_id, chunk_index, nonce, ciphertext, tag)
            VALUES (@tenant, @organization, @artifact, @index, @nonce, @ciphertext, @tag)
            """);
        command.Parameters.AddWithValue("artifact", artifactId);
        command.Parameters.AddWithValue("index", index);
        command.Parameters.AddWithValue("nonce", NpgsqlDbType.Bytea, chunk.Nonce);
        command.Parameters.AddWithValue("ciphertext", NpgsqlDbType.Bytea, chunk.Ciphertext);
        command.Parameters.AddWithValue("tag", NpgsqlDbType.Bytea, chunk.Tag);
        await ExecuteAsync(token => command.ExecuteNonQueryAsync(token), cancellationToken).ConfigureAwait(false);
    }

    private async Task<DateTimeOffset> DatabaseNowAsync(CancellationToken cancellationToken)
    {
        await using var command = DataRequestSql.Statement(unit, "SELECT clock_timestamp()");
        var value = await ExecuteAsync(token => command.ExecuteScalarAsync(token), cancellationToken).ConfigureAwait(false);
        return value switch
        {
            DateTime utc when utc.Kind == DateTimeKind.Utc => new DateTimeOffset(utc, TimeSpan.Zero),
            DateTimeOffset instant => instant.ToUniversalTime(),
            _ => throw Unavailable(),
        };
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            unit.MarkRollbackOnly();
            throw Unavailable();
        }
    }

    private NpgsqlCommand Command(string sql) => DataRequestSql.Command(unit, tenant, sql);

    private async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> statement, CancellationToken cancellationToken)
    {
        using var budget = DataRequestSql.Budget(cancellationToken);
        try
        {
            return await statement(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            unit.MarkRollbackOnly();
            throw Unavailable();
        }
        catch (NpgsqlException)
        {
            unit.MarkRollbackOnly();
            throw Unavailable();
        }
    }

    private void RequireGroup(DataRequestScopeGroup group, bool writable)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (group.TenantId != tenant.TenantId || group.OrganizationId != tenant.OrganizationId
            || !DataRequestOwner.IsAnnounced(unit, tenant, writable))
        {
            throw Refused();
        }
    }

    private static DataExportArtifactMetadata Metadata(DataExportArtifact artifact, bool readable) =>
        new(artifact.Id.Value, artifact.RequestId.Value, artifact.Status, artifact.ByteCount, artifact.ChunkCount,
            artifact.KeyVersion, artifact.ExpiresAt, artifact.PurgedAt, artifact.Truncations, readable);

    private static InvalidOperationException Refused() => new("Data export artifact custody refused.");

    private static InfrastructureException Unavailable() => new("Data export artifact storage unavailable.");

    private sealed record StoredChunk(Guid RequestId, int Count, DataExportSealedChunk Chunk)
    {
        public override string ToString() => nameof(StoredChunk);
    }
}
