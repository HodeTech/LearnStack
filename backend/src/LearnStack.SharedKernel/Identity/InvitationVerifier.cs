using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace LearnStack.SharedKernel.Identity;

/// <summary>The single ADR-0093 verifier encoding used by staging and entry; never authentication authority.</summary>
public static class InvitationVerifier
{
    public static byte[] Compute(Guid invitationId, ReadOnlySpan<byte> secret)
    {
        if (invitationId.Version != 7 || secret.Length != 32)
            throw new ArgumentException("Invitation verifier input refused.");
        var domain = Encoding.UTF8.GetBytes("learnstack.identity.invitation.v1");
        Span<byte> material = stackalloc byte[12 + domain.Length + 16 + 32];
        BinaryPrimitives.WriteUInt32BigEndian(material, (uint)domain.Length);
        domain.CopyTo(material[4..]);
        var offset = 4 + domain.Length;
        BinaryPrimitives.WriteUInt32BigEndian(material[offset..], 16);
        invitationId.TryWriteBytes(material[(offset + 4)..], bigEndian: true, out _);
        offset += 20;
        BinaryPrimitives.WriteUInt32BigEndian(material[offset..], 32);
        secret.CopyTo(material[(offset + 4)..]);
        try { return SHA256.HashData(material); }
        finally { CryptographicOperations.ZeroMemory(material); }
    }
}
