using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Idempotency;

/// <summary>Canonical exclusive capacity key, separate from every business lock family.</summary>
public static class IdempotencyTenantCapacityKey
{
    public static long ForTenant(TenantId tenantId)
    {
        if (!tenantId.IsInitialized() || tenantId.Value == Guid.Empty || tenantId == TenantId.PlatformSentinel)
            throw new ArgumentException("Idempotency capacity tenant is invalid.", nameof(tenantId));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, Encoding.UTF8.GetBytes("learnstack.idempotency.capacity.v1"));
        Span<byte> identifier = stackalloc byte[16];
        tenantId.Value.TryWriteBytes(identifier, bigEndian: true, out _);
        Append(hash, identifier);
        return BinaryPrimitives.ReadInt64BigEndian(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)value.Length));
        hash.AppendData(length);
        hash.AppendData(value);
    }
}
