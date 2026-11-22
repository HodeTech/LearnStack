using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Secrets;

namespace LearnStack.Infrastructure.DataProtection;

/// <summary>What one sealed export chunk is bound to; the associated data repeats every member.</summary>
public sealed record DataExportChunkBinding
{
    /// <exception cref="ArgumentException">An identity is nil or the position is outside the artifact.</exception>
    public DataExportChunkBinding(Guid tenantId, Guid requestId, Guid artifactId, int index, int count)
    {
        if (tenantId == Guid.Empty || requestId == Guid.Empty || artifactId == Guid.Empty
            || count is < 1 or > DataRequestLimits.MaximumChunkCount || index < 0 || index >= count)
        {
            throw new ArgumentException("A data export chunk binding needs its identities and its position.", nameof(tenantId));
        }

        TenantId = tenantId;
        RequestId = requestId;
        ArtifactId = artifactId;
        Index = index;
        Count = count;
    }

    public Guid TenantId { get; }
    public Guid RequestId { get; }
    public Guid ArtifactId { get; }
    public int Index { get; }
    public int Count { get; }

    public override string ToString() => nameof(DataExportChunkBinding);
}

/// <summary>One sealed chunk as it is stored: the key-ring label, the nonce, the ciphertext and the tag.</summary>
public sealed record DataExportSealedChunk(string KeyVersion, byte[] Nonce, byte[] Ciphertext, byte[] Tag)
{
    public override string ToString() => nameof(DataExportSealedChunk);
}

/// <summary>
/// AES-256-GCM custody of export chunks under the <c>DataExport:KeyRing</c> secret
/// (<see href="../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 4</see>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The key ring mirrors the invitation envelope's.</b> One JSON secret
/// <c>{ "active": "version", "keys": { "version": "base64 of 32 bytes" } }</c>, one to four distinct keys and an
/// active version that is one of them, read once through <see cref="ISecretProvider"/> and fixed for the
/// process. New chunks are sealed under the active key; a chunk is opened under the key its stored label
/// names, so rotation is adding a key, making it active, and removing the old one only after every artifact
/// sealed under it has expired (seven days). Anything else refuses construction, which the host's startup
/// check turns into a refused start.
/// </para>
/// <para>
/// <b>Associated data</b> binds the format version, the key version, the tenant, the request, the artifact,
/// the chunk index and the chunk count, so a chunk moved to another position, artifact, request or tenant, a
/// truncated artifact or a relabelled key fails authentication. A fresh random 12-byte nonce is drawn for
/// every chunk.
/// </para>
/// <para>
/// <b>No secret leaves.</b> Every refusal is one fixed message with no key, label, plaintext or position;
/// <see cref="ToString"/> prints the type name; key buffers are overwritten on <see cref="Dispose"/>, and
/// intermediate plaintext is overwritten on failure.
/// </para>
/// </remarks>
public sealed class DataExportCipher : IDisposable
{
    /// <summary>The secret name; the web launcher's allow-list carries its environment spelling.</summary>
    public const string KeyRingSecret = "DataExport:KeyRing";

    /// <summary>The format version the associated data begins with.</summary>
    public const byte FormatVersion = 1;

    private readonly FrozenDictionary<string, byte[]> _keys;
    private bool _disposed;

    /// <exception cref="InvalidOperationException">The key ring is absent or malformed.</exception>
    public DataExportCipher(ISecretProvider secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            using var ring = JsonDocument.Parse(secrets.GetSecret(KeyRingSecret) ?? throw Refused());
            var root = ring.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2
                || !root.TryGetProperty("active", out var active) || active.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("keys", out var entries) || entries.ValueKind != JsonValueKind.Object)
            {
                throw Refused();
            }

            foreach (var entry in entries.EnumerateObject())
            {
                if (!DataRequestLimits.IsKeyVersion(entry.Name) || entry.Value.ValueKind != JsonValueKind.String
                    || keys.Count == DataRequestLimits.MaximumKeyVersions)
                {
                    throw Refused();
                }

                var key = Convert.FromBase64String(entry.Value.GetString()!);
                if (key.Length != DataRequestLimits.KeyBytes || !keys.TryAdd(entry.Name, key)
                    || keys.Where(other => !ReferenceEquals(other.Value, key))
                        .Any(other => CryptographicOperations.FixedTimeEquals(other.Value, key)))
                {
                    CryptographicOperations.ZeroMemory(key);
                    throw Refused();
                }
            }

            ActiveKeyVersion = active.GetString()!;
            if (keys.Count < 1 || !keys.ContainsKey(ActiveKeyVersion))
            {
                throw Refused();
            }

            _keys = keys.ToFrozenDictionary(StringComparer.Ordinal);
        }
        catch (Exception failure) when (failure is JsonException or FormatException or InvalidOperationException
            or ArgumentException or KeyNotFoundException)
        {
            foreach (var key in keys.Values)
            {
                CryptographicOperations.ZeroMemory(key);
            }

            throw Refused();
        }
    }

    /// <summary>The label new chunks are sealed under; a label, never key material.</summary>
    public string ActiveKeyVersion { get; } = string.Empty;

    /// <summary>Seals one chunk of at most <see cref="DataRequestLimits.ChunkPlaintextBytes"/> under the active key.</summary>
    /// <exception cref="InvalidOperationException">The chunk is empty or too large, or the cipher is disposed.</exception>
    public DataExportSealedChunk Seal(DataExportChunkBinding binding, ReadOnlySpan<byte> plaintext)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (plaintext.Length is < 1 or > DataRequestLimits.ChunkPlaintextBytes)
        {
            throw Refused();
        }

        var nonce = RandomNumberGenerator.GetBytes(DataRequestLimits.NonceBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[DataRequestLimits.TagBytes];
        using var cipher = new AesGcm(_keys[ActiveKeyVersion], DataRequestLimits.TagBytes);
        cipher.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(binding, ActiveKeyVersion));
        return new DataExportSealedChunk(ActiveKeyVersion, nonce, ciphertext, tag);
    }

    /// <summary>Opens one sealed chunk; the caller owns the returned plaintext and overwrites it when done.</summary>
    /// <exception cref="InvalidOperationException">
    /// The key version is unknown, a length is wrong, or authentication failed: the chunk was moved, altered or
    /// sealed under a different binding.
    /// </exception>
    public byte[] Open(DataExportChunkBinding binding, DataExportSealedChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(chunk);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (chunk.KeyVersion is null || !_keys.TryGetValue(chunk.KeyVersion, out var key)
            || chunk.Nonce is not { Length: DataRequestLimits.NonceBytes } || chunk.Tag is not { Length: DataRequestLimits.TagBytes }
            || chunk.Ciphertext is not { Length: >= 1 and <= DataRequestLimits.ChunkPlaintextBytes })
        {
            throw Refused();
        }

        var plaintext = new byte[chunk.Ciphertext.Length];
        try
        {
            using var cipher = new AesGcm(key, DataRequestLimits.TagBytes);
            cipher.Decrypt(chunk.Nonce, chunk.Ciphertext, chunk.Tag, plaintext, AssociatedData(binding, chunk.KeyVersion));
            return plaintext;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw Refused();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var key in _keys.Values)
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public override string ToString() => nameof(DataExportCipher);

    /// <summary>The associated data: format, key version, tenant, request, artifact, index and count.</summary>
    internal static byte[] AssociatedData(DataExportChunkBinding binding, string keyVersion)
    {
        if (!DataRequestLimits.IsKeyVersion(keyVersion))
        {
            throw Refused();
        }

        var label = Encoding.ASCII.GetBytes(keyVersion);
        var data = new byte[1 + 1 + label.Length + 16 + 16 + 16 + 4 + 4];
        var span = data.AsSpan();
        span[0] = FormatVersion;
        span[1] = (byte)label.Length;
        label.CopyTo(span[2..]);
        var offset = 2 + label.Length;
        binding.TenantId.TryWriteBytes(span.Slice(offset, 16), bigEndian: true, out _);
        binding.RequestId.TryWriteBytes(span.Slice(offset + 16, 16), bigEndian: true, out _);
        binding.ArtifactId.TryWriteBytes(span.Slice(offset + 32, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteInt32BigEndian(span.Slice(offset + 48, 4), binding.Index);
        BinaryPrimitives.WriteInt32BigEndian(span.Slice(offset + 52, 4), binding.Count);
        return data;
    }

    private static InvalidOperationException Refused() => new("Data export custody unavailable.");
}
