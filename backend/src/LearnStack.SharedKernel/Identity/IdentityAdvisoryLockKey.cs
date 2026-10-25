using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Identity;

/// <summary>Stable database lock keys; collisions serialize unrelated work and never grant authority.</summary>
public static class IdentityAdvisoryLockKey
{
    public static long ForSubject(GlobalIdentitySubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        IdentitySubjectKey.Append(hash, Encoding.UTF8.GetBytes("learnstack.identity.lock.v1"));
        IdentitySubjectKey.Append(hash, Encoding.UTF8.GetBytes("subject"));
        Span<byte> authority = stackalloc byte[16];
        subject.AuthorityId.Value.TryWriteBytes(authority, bigEndian: true, out _);
        IdentitySubjectKey.Append(hash, authority);
        IdentitySubjectKey.Append(hash, subject.SubjectKey.ToByteArray());
        return BinaryPrimitives.ReadInt64BigEndian(hash.GetHashAndReset());
    }
}
