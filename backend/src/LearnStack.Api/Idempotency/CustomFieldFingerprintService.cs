using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using LearnStack.Api.Common;
using LearnStack.SharedKernel.Idempotency;
using LearnStack.SharedKernel.Secrets;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Api.Idempotency;

/// <summary>Only the new closed mutation descriptors select this keyed format.</summary>
internal sealed class CustomFieldFingerprintService(ISecretProvider secrets, IConfiguration configuration,
    ICustomFieldIdempotencyMetadataReader metadata)
{
    internal async Task<string> ComputeAsync(HttpContext http, ITenantContext scope,
        ProtectedAdministrationDescriptor descriptor, long? expected, CancellationToken cancellationToken)
    {
        if (!IdempotencyFilter.TryReadKey(http, out var key) || !scope.IsResolved || scope.UserId is null)
            throw new CustomFieldFingerprintUnavailableException();
        var incumbent = await metadata.ReadKeyMetadataAsync(scope.TenantId, key, cancellationToken);
        using var ring = ReadRing();
        var selected = incumbent.KeyId ?? ring.Current;
        if (!ring.Keys.TryGetValue(selected, out var material)) throw new CustomFieldFingerprintUnavailableException();
        var body = await AdministrationHttpInput.ReadBodyBytesAsync(http);
        using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, material);
        Append("learnstack.idempotency.custom-fields.v1");
        Append(scope.TenantId.Value.ToString("D"));
        Append(scope.OrganizationId?.Value.ToString("D") ?? string.Empty);
        Append(scope.UserId.Value.Value.ToString("D"));
        Append(descriptor.Operation.ToString());
        Append(expected is { } version ? EntityTag.For(version) : string.Empty);
        Append(http.Request.Method);
        Append(http.Request.PathBase + http.Request.Path);
        Append(http.Request.QueryString.Value ?? string.Empty);
        AppendBytes(body);
        return CustomFieldIdempotencyFingerprint.Prefix + selected + ":" + Convert.ToHexStringLower(hash.GetHashAndReset());

        void Append(string value) => AppendBytes(Encoding.UTF8.GetBytes(value));
        void AppendBytes(ReadOnlySpan<byte> value)
        {
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
            hash.AppendData(length);
            hash.AppendData(value);
        }
    }

    private KeyRing ReadRing()
    {
        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            var current = secrets.GetSecret("Idempotency:CustomFields:CurrentKeyId");
            // Only nonsecret IDs are enumerated from configuration. Every key's
            // material and the current ID still pass through ISecretProvider.
            var ids = configuration.GetSection("Idempotency:CustomFields:Keys").GetChildren().Select(child => child.Key).ToArray();
            if (!CustomFieldIdempotencyFingerprint.ValidKeyId(current) || ids.Length is < 1 or > 4
                || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length || !ids.Contains(current, StringComparer.Ordinal))
                throw new CustomFieldFingerprintUnavailableException();
            foreach (var id in ids)
            {
                if (!CustomFieldIdempotencyFingerprint.ValidKeyId(id)) throw new CustomFieldFingerprintUnavailableException();
                var encoded = secrets.GetSecret("Idempotency:CustomFields:Keys:" + id);
                var bytes = new byte[32];
                if (encoded is null || !Convert.TryFromBase64String(encoded, bytes, out var count) || count != bytes.Length
                    || Convert.ToBase64String(bytes) != encoded || keys.Values.Any(prior => CryptographicOperations.FixedTimeEquals(prior, bytes)))
                {
                    CryptographicOperations.ZeroMemory(bytes);
                    throw new CustomFieldFingerprintUnavailableException();
                }
                keys.Add(id, bytes);
            }
            return new KeyRing(current!, keys);
        }
        catch
        {
            foreach (var bytes in keys.Values) CryptographicOperations.ZeroMemory(bytes);
            throw new CustomFieldFingerprintUnavailableException();
        }
    }

    private sealed class KeyRing(string current, Dictionary<string, byte[]> keys) : IDisposable
    {
        internal string Current { get; } = current;
        internal Dictionary<string, byte[]> Keys { get; } = keys;
        public void Dispose() { foreach (var bytes in Keys.Values) CryptographicOperations.ZeroMemory(bytes); }
    }
}

internal sealed class CustomFieldFingerprintUnavailableException() : Exception("Custom field replay is unavailable.");
