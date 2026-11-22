namespace LearnStack.SharedKernel.DataProtection;

/// <summary>
/// The fixed bounds of subject data requests, each owned by the ADR section named beside it.
/// The database CHECKs and guards repeat the same numbers; a change here is a migration.
/// </summary>
public static class DataRequestLimits
{
    /// <summary>The cancellable window before an erasure or closure executes (ADR-0106 § 11).</summary>
    public static readonly TimeSpan CancellationWindow = TimeSpan.FromHours(24);

    /// <summary>How long a completed export artifact stays readable (ADR-0107 § 4).</summary>
    public static readonly TimeSpan ArtifactLifetime = TimeSpan.FromDays(7);

    /// <summary>How long a download ticket authorizes chunk reads (ADR-0107 § 4).</summary>
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(15);

    /// <summary>The most plaintext one encrypted chunk holds (ADR-0107 § 4: 128 KiB).</summary>
    public const int ChunkPlaintextBytes = 128 * 1024;

    /// <summary>AES-256-GCM nonce length; a fresh nonce per chunk (ADR-0107 § 4).</summary>
    public const int NonceBytes = 12;

    /// <summary>AES-256-GCM authentication tag length.</summary>
    public const int TagBytes = 16;

    /// <summary>The key length of every key in the export key ring.</summary>
    public const int KeyBytes = 32;

    /// <summary>The most keys the export key ring may hold (ADR-0107 § 4: one to four).</summary>
    public const int MaximumKeyVersions = 4;

    /// <summary>The longest key-version label, as the invitation envelope ring spells it.</summary>
    public const int MaximumKeyVersionLength = 32;

    /// <summary>The default bundle ceiling, <c>DataExport:MaxBytes</c> (ADR-0107 § 4).</summary>
    public const long DefaultMaximumBundleBytes = 16L * 1024 * 1024;

    /// <summary>The largest bundle ceiling the setting may name (ADR-0107 § 4: 64 MiB).</summary>
    public const long MaximumBundleBytes = 64L * 1024 * 1024;

    /// <summary>The most chunks an artifact can have at the largest ceiling.</summary>
    public const int MaximumChunkCount = (int)(MaximumBundleBytes / ChunkPlaintextBytes);

    /// <summary>The audit section's cap inside one bundle (ADR-0107 § 3: 8 MiB).</summary>
    public const long AuditSectionBytes = 8L * 1024 * 1024;

    /// <summary>The most Memberships one request's frozen snapshot may name.</summary>
    /// <remarks>
    /// A person holds at most one current Membership per group; the rest are Revoked tombstones
    /// of earlier admissions. The bound keeps the snapshot a bounded row, not a list that grows
    /// without limit.
    /// </remarks>
    public const int MaximumSnapshotMemberships = 256;

    /// <summary>The most participants one request's progress record may name.</summary>
    public const int MaximumParticipants = 32;

    /// <summary>The longest participant key.</summary>
    public const int MaximumParticipantKeyLength = 32;

    /// <summary>The number of chunks a bundle of <paramref name="byteCount"/> plaintext bytes occupies.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The bundle is empty or above the largest ceiling.</exception>
    public static int ChunkCountFor(long byteCount) =>
        byteCount is < 1 or > MaximumBundleBytes
            ? throw new ArgumentOutOfRangeException(nameof(byteCount), "The bundle size is outside the supported range.")
            : (int)((byteCount + ChunkPlaintextBytes - 1) / ChunkPlaintextBytes);

    /// <summary>Whether a participant key is a closed lower snake_case token of the bounded length.</summary>
    public static bool IsParticipantKey(string? key) =>
        key is { Length: > 0 and <= MaximumParticipantKeyLength }
        && key[0] is >= 'a' and <= 'z'
        && key.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');

    /// <summary>Whether a key-version label matches the export ring's spelling.</summary>
    public static bool IsKeyVersion(string? version) =>
        version is { Length: > 0 and <= MaximumKeyVersionLength }
        && version.All(character => character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z')
            or (>= '0' and <= '9') or '_' or '-');
}
