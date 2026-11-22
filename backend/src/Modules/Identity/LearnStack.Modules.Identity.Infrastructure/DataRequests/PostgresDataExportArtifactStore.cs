using LearnStack.Infrastructure.DataProtection;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;
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
/// <b>Time.</b> Readability and the revoke-or-purge decision use the database clock, as the guards do, so an
/// artifact past its expiry is unreadable whether or not its purge ran.
/// </para>
/// </remarks>
public sealed class PostgresDataExportArtifactStore(IdentityDbContext db, IUnitOfWork unit, ITenantContext tenant,
    DataExportCipher cipher, IClock clock, IAuditSubject audit) : IDataExportArtifactStore
{
    private static readonly TimeSpan StatementBudget = TimeSpan.FromSeconds(5);

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

        var created = DataExportArtifact.Create(DataExportArtifactId.From(put.ArtifactId), request, put.ByteCount,
            cipher.ActiveKeyVersion, put.Truncations, clock.UtcNow, tenant.UserId ?? UserId.SystemActor);
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
        Guid requestId;
        int count;
        DataExportSealedChunk sealedChunk;
        using (var budget = Budget(cancellationToken))
        {
            try
            {
                await using var row = await command.ExecuteReaderAsync(budget.Token).ConfigureAwait(false);
                if (!await row.ReadAsync(budget.Token).ConfigureAwait(false))
                {
                    return null;
                }

                requestId = row.GetGuid(0);
                count = row.GetInt32(1);
                sealedChunk = new DataExportSealedChunk(row.GetString(2), (byte[])row[3], (byte[])row[4], (byte[])row[5]);
            }
            catch (NpgsqlException)
            {
                throw Unavailable();
            }
        }

        var plaintext = cipher.Open(new DataExportChunkBinding(tenant.TenantId.Value, requestId, artifact.ArtifactId, index, count),
            sealedChunk);
        return new DataExportPlaintextChunk(index, count, plaintext);
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
            throw Unavailable();
        }

        await using (var delete = Command("""
            DELETE FROM public.data_export_chunks WHERE tenant_id = @tenant AND artifact_id = @artifact
            """))
        {
            delete.Parameters.AddWithValue("artifact", artifact.ArtifactId);
            await ExecuteAsync(token => delete.ExecuteNonQueryAsync(token), cancellationToken).ConfigureAwait(false);
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
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
        await using var command = new NpgsqlCommand("SELECT clock_timestamp()",
            (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!)
        { CommandTimeout = 5 };
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

    private NpgsqlCommand Command(string sql)
    {
        var command = new NpgsqlCommand(sql, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("tenant", tenant.TenantId.Value);
        command.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid, (object?)tenant.OrganizationId?.Value ?? DBNull.Value);
        return command;
    }

    private async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> statement, CancellationToken cancellationToken)
    {
        using var budget = Budget(cancellationToken);
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

    private static CancellationTokenSource Budget(CancellationToken cancellationToken)
    {
        var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(StatementBudget);
        return budget;
    }

    private void RequireGroup(DataRequestScopeGroup group, bool writable)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (group.TenantId != tenant.TenantId || group.OrganizationId != tenant.OrganizationId
            || !unit.HasActiveTransaction || unit.IsRollbackOnly
            || writable && unit.Mode != TransactionMode.ReadWrite
            || unit.GetTenantContextAnnouncement(unit.Transaction) is not { } announcement
            || announcement.TenantId != group.TenantId || announcement.OrganizationId != group.OrganizationId)
        {
            throw Refused();
        }
    }

    private static DataExportArtifactMetadata Metadata(DataExportArtifact artifact, bool readable) =>
        new(artifact.Id.Value, artifact.RequestId.Value, artifact.Status, artifact.ByteCount, artifact.ChunkCount,
            artifact.KeyVersion, artifact.ExpiresAt, artifact.PurgedAt, artifact.Truncations, readable);

    private static InvalidOperationException Refused() => new("Data export artifact custody refused.");

    private static InvalidOperationException Unavailable() => new("Data export artifact storage unavailable.");
}
