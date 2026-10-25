using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Identity;

/// <summary>Immutable, linkable subject digest. This value supplies neither secrecy nor admission authority.</summary>
public sealed class IdentitySubjectKey : IEquatable<IdentitySubjectKey>
{
    private readonly byte[] _bytes;

    private IdentitySubjectKey(byte[] bytes)
    {
        _bytes = bytes.ToArray();
        Hex = Convert.ToHexStringLower(_bytes);
    }

    public string Hex { get; }

    public byte[] ToByteArray() => _bytes.ToArray();

    public static IdentitySubjectKey FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 32) throw new ArgumentException("An identity subject key requires exactly 32 bytes.");
        return new IdentitySubjectKey(bytes.ToArray());
    }

    public static IdentitySubjectKey Compute(IdentityAuthorityId authorityId, string subject)
    {
        if (!authorityId.IsInitialized() || authorityId.Value == Guid.Empty
            || string.IsNullOrWhiteSpace(subject) || subject.Length > 255)
            throw new ArgumentException("Identity subject-key inputs are invalid.");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, Encoding.UTF8.GetBytes("learnstack.identity.subject.v1"));
        Span<byte> authority = stackalloc byte[16];
        authorityId.Value.TryWriteBytes(authority, bigEndian: true, out _);
        Append(hash, authority);
        Append(hash, Encoding.UTF8.GetBytes(subject));
        return new IdentitySubjectKey(hash.GetHashAndReset());
    }

    internal static void Append(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)value.Length));
        hash.AppendData(length);
        hash.AppendData(value);
    }

    public bool Equals(IdentitySubjectKey? other) => other is not null
        && string.Equals(Hex, other.Hex, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is IdentitySubjectKey other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Hex);

    public override string ToString() => nameof(IdentitySubjectKey);
}
