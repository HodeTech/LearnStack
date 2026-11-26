using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace LearnStack.Modules.Identity.Application.DataRequests;

/// <summary>
/// The opaque download ticket of ADR-0107 § 4: 32 random bytes, spelled as 43 unpadded base64url characters, and
/// stored only as a domain-separated SHA-256 digest.
/// </summary>
/// <remarks>
/// Neither the ticket nor its bytes are kept after the response that returns it; the digest alone is stored, and the
/// session binding (<c>IDataExportSessionBinding</c>) is a second digest the ticket row also holds.
/// </remarks>
public static class DataExportTicketCodec
{
    /// <summary>The ticket's entropy.</summary>
    public const int TicketBytes = 32;

    /// <summary>The exact length of the wire spelling.</summary>
    public const int TicketLength = 43;

    private static readonly byte[] Domain = Encoding.ASCII.GetBytes("learnstack.data_export.ticket.v1");

    /// <summary>Mints a fresh ticket and its digest. The caller returns the ticket once and stores only the digest.</summary>
    public static (string Ticket, string Digest) Mint()
    {
        Span<byte> secret = stackalloc byte[TicketBytes];
        try
        {
            RandomNumberGenerator.Fill(secret);
            return (Encode(secret), Digest(secret));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>The digest of a presented ticket, or <see langword="null"/> when it is not a canonical ticket spelling.</summary>
    public static string? TryDigest(string? ticket)
    {
        if (ticket is not { Length: TicketLength }
            || ticket.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
        {
            return null;
        }

        Span<byte> secret = stackalloc byte[TicketBytes];
        try
        {
            var padded = ticket.Replace('-', '+').Replace('_', '/') + "=";
            if (!Convert.TryFromBase64String(padded, secret, out var written) || written != TicketBytes
                || !string.Equals(Encode(secret), ticket, StringComparison.Ordinal))
            {
                return null;
            }

            return Digest(secret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private static string Encode(ReadOnlySpan<byte> secret) =>
        Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Digest(ReadOnlySpan<byte> secret)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, Domain.Length);
        hash.AppendData(length);
        hash.AppendData(Domain);
        hash.AppendData(secret);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
