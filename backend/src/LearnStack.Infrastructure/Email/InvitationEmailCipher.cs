using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LearnStack.SharedKernel.Email;
using LearnStack.SharedKernel.Secrets;

namespace LearnStack.Infrastructure.Email;

public sealed record InvitationEmailBinding(Guid SpoolId, Guid TenantId, Guid? OrganizationId,
    Guid InvitationId, Guid GenerationId, Guid JobId);
public sealed record InvitationEmailCiphertext(string KeyVersion, byte[] Nonce, byte[] Ciphertext, byte[] Tag)
{
    public override string ToString() => nameof(InvitationEmailCiphertext);
}

/// <summary>Fixed-for-process secret custody; no fallback keys or plaintext persistence.</summary>
public sealed class InvitationEmailCipher
{
    private readonly Dictionary<string, byte[]> _keys;
    private readonly string _active;
    public const int MaximumBytes = 32 * 1024;

    public InvitationEmailCipher(ISecretProvider secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        try
        {
            using var ring = JsonDocument.Parse(secrets.GetSecret("InvitationEmail:KeyRing") ?? "{}");
            _active = ring.RootElement.GetProperty("active").GetString()!;
            _keys = new(StringComparer.Ordinal);
            foreach (var entry in ring.RootElement.GetProperty("keys").EnumerateObject())
            {
                var key = Convert.FromBase64String(entry.Value.GetString()!);
                if (!ValidVersion(entry.Name) || key.Length != 32 || !_keys.TryAdd(entry.Name, key)) throw Refused();
            }
            if (_keys.Count is < 1 or > 4 || !_keys.ContainsKey(_active)
                || _keys.Values.Select(Convert.ToHexString).Distinct(StringComparer.Ordinal).Count() != _keys.Count) throw Refused();
        }
        catch (Exception failure) when (failure is JsonException or KeyNotFoundException or FormatException or InvalidOperationException or ArgumentException)
        { throw Refused(); }
    }

    public InvitationEmailCiphertext Encrypt(InvitationEmailBinding binding, EmailSubmission message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(message);
        try
        {
            if (plaintext.Length is < 1 or > MaximumBytes) throw Refused();
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            using var cipher = new AesGcm(_keys[_active], 16);
            cipher.Encrypt(nonce, plaintext, ciphertext, tag, AdditionalData(binding, _active));
            return new(_active, nonce, ciphertext, tag);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public EmailSubmission Decrypt(InvitationEmailBinding binding, InvitationEmailCiphertext envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!_keys.TryGetValue(envelope.KeyVersion, out var key) || envelope.Nonce.Length != 12 || envelope.Tag.Length != 16
            || envelope.Ciphertext.Length is < 1 or > MaximumBytes) throw Refused();
        var plaintext = new byte[envelope.Ciphertext.Length];
        try
        {
            using var cipher = new AesGcm(key, 16);
            cipher.Decrypt(envelope.Nonce, envelope.Ciphertext, envelope.Tag, plaintext, AdditionalData(binding, envelope.KeyVersion));
            return JsonSerializer.Deserialize<EmailSubmission>(plaintext) ?? throw Refused();
        }
        catch (Exception failure) when (failure is CryptographicException or JsonException or ArgumentException)
        { throw Refused(); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static byte[] AdditionalData(InvitationEmailBinding binding, string version)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.SpoolId == Guid.Empty || binding.TenantId == Guid.Empty || binding.OrganizationId == Guid.Empty
            || binding.InvitationId == Guid.Empty || binding.GenerationId == Guid.Empty || binding.JobId == Guid.Empty || !ValidVersion(version)) throw Refused();
        using var output = new MemoryStream();
        output.WriteByte(1);
        var text = Encoding.UTF8.GetBytes(version);
        output.WriteByte(checked((byte)text.Length));
        output.Write(text);
        output.Write(binding.SpoolId.ToByteArray(bigEndian: true));
        output.Write(binding.TenantId.ToByteArray(bigEndian: true));
        output.WriteByte(binding.OrganizationId is null ? (byte)0 : (byte)1);
        if (binding.OrganizationId is { } organization) output.Write(organization.ToByteArray(bigEndian: true));
        output.Write(binding.InvitationId.ToByteArray(bigEndian: true));
        output.Write(binding.GenerationId.ToByteArray(bigEndian: true));
        output.Write(binding.JobId.ToByteArray(bigEndian: true));
        return output.ToArray();
    }
    private static bool ValidVersion(string? value) => value is { Length: > 0 and <= 32 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    private static InvalidOperationException Refused() => new("Invitation email custody unavailable.");
}
