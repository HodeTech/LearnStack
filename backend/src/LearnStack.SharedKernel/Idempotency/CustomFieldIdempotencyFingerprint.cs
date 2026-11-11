namespace LearnStack.SharedKernel.Idempotency;

/// <summary>Closed persisted wire format. This parser never returns a body digest.</summary>
public static class CustomFieldIdempotencyFingerprint
{
    public const string Prefix = "cfh1:";

    public static bool ValidKeyId(string? id) => id is { Length: >= 1 and <= 32 }
        && id.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    public static bool TryReadKeyId(string fingerprint, out string? keyId)
    {
        keyId = null;
        if (!fingerprint.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var separator = fingerprint.IndexOf(':', Prefix.Length);
        if (separator < 0 || fingerprint.Length - separator - 1 != 64
            || !fingerprint.AsSpan(separator + 1).ContainsOnlyLowerHex()) return false;
        var candidate = fingerprint[Prefix.Length..separator];
        if (!ValidKeyId(candidate)) return false;
        keyId = candidate;
        return true;
    }

    public static bool IsHistorical(string fingerprint) => fingerprint.Length == 64
        && fingerprint.AsSpan().ContainsOnlyLowerHex();

    private static bool ContainsOnlyLowerHex(this ReadOnlySpan<char> text)
    {
        foreach (var character in text)
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }
}

/// <summary>Only presence and a nonsecret key ID; no response, fingerprint or retention reset.</summary>
public sealed record CustomFieldIdempotencyKeyMetadata(bool Exists, string? KeyId);

public interface ICustomFieldIdempotencyMetadataReader
{
    Task<CustomFieldIdempotencyKeyMetadata> ReadKeyMetadataAsync(
        LearnStack.SharedKernel.Identifiers.TenantId tenantId, string key, CancellationToken cancellationToken);
}
