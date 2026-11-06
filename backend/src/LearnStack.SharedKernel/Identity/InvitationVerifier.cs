using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace LearnStack.SharedKernel.Identity;

/// <summary>The single ADR-0093 verifier encoding used by staging and entry; never authentication authority.</summary>
public static class InvitationVerifier
{
    public static bool TryDigest(Guid invitationId, string? code, out byte[] digest)
    {
        digest = [];
        if (invitationId.Version != 7 || code is not { Length: 80 }
            || code[..36] != invitationId.ToString("D") || code[36] != '.'
            || code[37..].Any(value => !(char.IsAsciiLetterOrDigit(value) || value is '-' or '_'))) return false;
        byte[] secret;
        try { secret = Convert.FromBase64String(code[37..].Replace('-', '+').Replace('_', '/') + "="); }
        catch (FormatException) { return false; }
        try
        {
            if (secret.Length != 32 || Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_') != code[37..]) return false;
            digest = Compute(invitationId, secret); return true;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

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
