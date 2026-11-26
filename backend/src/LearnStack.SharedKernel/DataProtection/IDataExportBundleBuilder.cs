using System.Collections.Immutable;

namespace LearnStack.SharedKernel.DataProtection;

/// <summary>
/// Builds one export bundle for a subject and group: one JSON document with a schema version, the request
/// identity, the generation time and one section per registered participant
/// (<see href="../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 3</see>).
/// </summary>
/// <remarks>
/// <para>
/// The builder runs every participant's <see cref="IUserDataParticipant.ExportAsync"/> in registry order on the
/// caller's ambient tenant transaction, each into a bounded section writer. It holds the plaintext in fixed
/// chunk-sized segments, bounded by the bundle ceiling (<c>DataExport:MaxBytes</c>), because the artifact's
/// associated data binds the chunk count, which is not known until the last record is written; it never
/// encrypts, stores or logs the plaintext.
/// </para>
/// <para>
/// A ceiling hit is an outcome, not an exception: the result carries no bundle and the request ends
/// <c>failed</c> with <c>export_too_large</c> (ADR-0107 § 4).
/// </para>
/// </remarks>
public interface IDataExportBundleBuilder
{
    Task<DataExportBundleResult> BuildAsync(UserDataSubject subject, DateTimeOffset generatedAt, CancellationToken cancellationToken);
}

/// <summary>A built bundle or the ceiling refusal; exactly one is present.</summary>
public sealed record DataExportBundleResult
{
    private DataExportBundleResult(IDataExportBundle? bundle) => Bundle = bundle;

    /// <summary>The bundle passed <c>DataExport:MaxBytes</c>; no plaintext is retained.</summary>
    public static DataExportBundleResult TooLarge { get; } = new((IDataExportBundle?)null);

    /// <exception cref="ArgumentNullException">The bundle is missing.</exception>
    public static DataExportBundleResult Built(IDataExportBundle bundle) =>
        new(bundle ?? throw new ArgumentNullException(nameof(bundle)));

    /// <summary>The bundle, which the caller owns and disposes; <see langword="null"/> when <see cref="IsTooLarge"/>.</summary>
    public IDataExportBundle? Bundle { get; }

    public bool IsTooLarge => Bundle is null;

    public override string ToString() => IsTooLarge ? nameof(TooLarge) : nameof(Built);
}

/// <summary>
/// A finished bundle's plaintext, held until it is stored. Disposing it overwrites every buffer with zeros.
/// </summary>
/// <remarks>The plaintext is the subject's personal data; the type never prints it.</remarks>
public interface IDataExportBundle : IDisposable
{
    /// <summary>The UTF-8 size of the whole document.</summary>
    long ByteCount { get; }

    /// <summary>The chunk count the size implies (<see cref="DataRequestLimits.ChunkCountFor"/>).</summary>
    int ChunkCount { get; }

    /// <summary>The collections that stopped at their section cap, as the bundle states them.</summary>
    ImmutableArray<DataExportTruncation> Truncations { get; }

    /// <summary>
    /// The document in order, as chunks of exactly <see cref="DataRequestLimits.ChunkPlaintextBytes"/> but the
    /// last; each chunk is valid only until the bundle is disposed.
    /// </summary>
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(CancellationToken cancellationToken = default);
}
