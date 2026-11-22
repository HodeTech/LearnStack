using System.Collections.Immutable;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.DataProtection;

/// <summary>
/// Custody of an encrypted export artifact: put its chunks, read one chunk, read its metadata, purge
/// it (<see href="../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 4</see>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The default adapter</b> is <c>PostgresDataExportArtifactStore</c> in the Identity module's
/// infrastructure, which owns the artifact tables: AES-256-GCM with a fresh 12-byte nonce
/// per chunk, a key ring of one to four 32-byte keys under <c>DataExport:KeyRing</c> in
/// <c>ISecretProvider</c>, associated data binding format and key version, tenant, request,
/// artifact, chunk index and chunk count, at most <see cref="DataRequestLimits.ChunkPlaintextBytes"/>
/// of plaintext per chunk. An object-storage adapter is owned by
/// <see href="../../../../docs/roadmap/phase-04-cms-media-pages.md">Phase 04</see>; its trigger is that
/// phase shipping <c>IStorageProvider</c>, or a measured need to exceed the ceiling.
/// </para>
/// <para>
/// <b>Plaintext never leaves through this port except one chunk at a time</b>, to the subject's own
/// ticketed retrieval; no ciphertext leaves the API and no URL is a bearer credential. Expiry is a
/// time check made by the reader whether or not the purge ran.
/// </para>
/// <para>
/// Every call runs inside the ambient tenant transaction of the artifact's group as
/// <c>learnstack_app</c>; the store opens no connection of its own.
/// </para>
/// </remarks>
public interface IDataExportArtifactStore
{
    /// <summary>
    /// Encrypts and stores a completed bundle's chunks and records the artifact, <c>available</c>
    /// until <see cref="DataRequestLimits.ArtifactLifetime"/> after now.
    /// </summary>
    Task<DataExportArtifactMetadata> PutAsync(DataExportArtifactPut put,
        IAsyncEnumerable<ReadOnlyMemory<byte>> plaintextChunks, CancellationToken cancellationToken);

    /// <summary>The artifact's metadata, or <see langword="null"/> when the group holds no such artifact.</summary>
    Task<DataExportArtifactMetadata?> ReadMetadataAsync(DataExportArtifactLocator artifact, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the store's key ring still holds the key a chunk sealed under <paramref name="keyVersion"/> opens with;
    /// a ticket is minted only for an artifact this answers true for, so a retired key is a refusal before any read.
    /// </summary>
    bool CanOpen(string keyVersion);

    /// <summary>Decrypts one chunk of an available, unexpired artifact.</summary>
    /// <returns>
    /// <see langword="null"/> when the group holds no such artifact or chunk, or the artifact is revoked,
    /// purged or past its expiry by the database clock, whether or not the purge ran. A chunk the store cannot
    /// open (its key left the ring, or it fails authentication because the stored ciphertext is corrupt or
    /// forged) is also answered <see langword="null"/>, after the store records it at critical severity with
    /// identifiers only: the reader meets the one uniform refusal and nothing about the failure leaves.
    /// </returns>
    /// <exception cref="LearnStack.SharedKernel.Errors.InfrastructureException">The storage failed or outlived its statement budget.</exception>
    Task<DataExportPlaintextChunk?> ReadChunkAsync(DataExportArtifactLocator artifact, int index,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes every chunk and records the artifact <see cref="DataExportArtifactStatus.Revoked"/>
    /// (subject, administrator, newer export or erasure) or <see cref="DataExportArtifactStatus.Purged"/>
    /// (expiry); the metadata row stays as the receipt.
    /// </summary>
    /// <remarks>
    /// Revocation is admitted only before the artifact's expiry and the purge only after it, both by the
    /// database clock (ADR-0107 § 4). A revocation asked for once the expiry has passed purges instead, so a
    /// caller that wants the chunks gone always gets them gone; a purge asked for before the expiry changes
    /// nothing. Repeating either on an artifact that already holds no chunk changes nothing.
    /// </remarks>
    /// <returns>The artifact's status afterwards, or <see langword="null"/> when the group holds no such artifact.</returns>
    Task<DataExportArtifactStatus?> PurgeAsync(DataExportArtifactLocator artifact, DataExportArtifactStatus outcome,
        CancellationToken cancellationToken);
}

/// <summary>Names one artifact inside its group.</summary>
public sealed record DataExportArtifactLocator
{
    /// <exception cref="ArgumentException">The artifact identity is nil.</exception>
    public DataExportArtifactLocator(Guid artifactId, DataRequestScopeGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (artifactId == Guid.Empty)
        {
            throw new ArgumentException("An artifact locator needs the artifact identity.", nameof(artifactId));
        }

        ArtifactId = artifactId;
        Group = group;
    }

    public Guid ArtifactId { get; }
    public DataRequestScopeGroup Group { get; }

    public override string ToString() => nameof(DataExportArtifactLocator);
}

/// <summary>What a put records about the bundle it stores.</summary>
public sealed record DataExportArtifactPut
{
    /// <exception cref="ArgumentException">A member is unassigned or the size is outside the supported range.</exception>
    public DataExportArtifactPut(Guid artifactId, Guid requestId, UserId subjectUserId,
        DataRequestScopeGroup group, long byteCount, IEnumerable<DataExportTruncation>? truncations = null)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (artifactId == Guid.Empty || requestId == Guid.Empty)
        {
            throw new ArgumentException("A put needs the artifact and request identities.", nameof(artifactId));
        }

        if (!StronglyTypedId.IsAssigned(subjectUserId) || subjectUserId == UserId.SystemActor)
        {
            throw new ArgumentException("A put needs a real subject.", nameof(subjectUserId));
        }

        ChunkCount = DataRequestLimits.ChunkCountFor(byteCount);
        Truncations = DataExportTruncation.Normalize(truncations);
        ArtifactId = artifactId;
        RequestId = requestId;
        SubjectUserId = subjectUserId;
        Group = group;
        ByteCount = byteCount;
    }

    public Guid ArtifactId { get; }
    public Guid RequestId { get; }
    public UserId SubjectUserId { get; }
    public DataRequestScopeGroup Group { get; }

    /// <summary>The plaintext bundle size.</summary>
    public long ByteCount { get; }

    /// <summary>The chunk count the size implies.</summary>
    public int ChunkCount { get; }

    /// <summary>The bundle's truncation statement, which the receipt keeps (ADR-0107 § 3); empty when nothing was capped.</summary>
    public ImmutableArray<DataExportTruncation> Truncations { get; }

    public override string ToString() => nameof(DataExportArtifactPut);
}

/// <summary>An artifact's receipt-safe metadata: no plaintext, no ciphertext, no key.</summary>
/// <remarks>
/// <see cref="Status"/> is the stored lifecycle; <see cref="IsReadable"/> adds the time check against the
/// database clock, so an artifact still stored <c>available</c> after its expiry is reported unreadable.
/// </remarks>
public sealed record DataExportArtifactMetadata(Guid ArtifactId, Guid RequestId, DataExportArtifactStatus Status,
    long ByteCount, int ChunkCount, string KeyVersion, DateTimeOffset ExpiresAt, DateTimeOffset? PurgedAt,
    ImmutableArray<DataExportTruncation> Truncations, bool IsReadable);

/// <summary>One decrypted chunk of a ticketed download.</summary>
/// <remarks>The bytes are the subject's personal data; the type never prints them.</remarks>
public sealed record DataExportPlaintextChunk
{
    /// <exception cref="ArgumentOutOfRangeException">The index, count or length is outside the bounds.</exception>
    public DataExportPlaintextChunk(int index, int count, ReadOnlyMemory<byte> bytes)
    {
        if (count is < 1 or > DataRequestLimits.MaximumChunkCount || index < 0 || index >= count
            || bytes.Length is < 1 or > DataRequestLimits.ChunkPlaintextBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "The chunk is outside the artifact's bounds.");
        }

        Index = index;
        Count = count;
        Bytes = bytes;
    }

    public int Index { get; }
    public int Count { get; }
    [PiiSensitive]
    public ReadOnlyMemory<byte> Bytes { get; }

    public override string ToString() => nameof(DataExportPlaintextChunk);
}
