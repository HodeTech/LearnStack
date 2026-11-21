using System.Security.Cryptography;
using System.Text;

namespace LearnStack.SharedKernel.Platform;

/// <summary>The stable, non-reversible handle of one operator at one issuer.</summary>
/// <remarks>
/// The unpadded base64url SHA-256 of <c>learnstack.operator.subject.v1</c>, a line feed, the
/// issuer, a line feed and the opaque <c>sub</c>. Audit rows and configuration grants carry
/// this digest, never the raw subject (ADR-0101 § 9). It is distinct from the identity
/// subject key: an operator is not a <c>users</c> row.
/// </remarks>
public static class PlatformOperatorSubject
{
    private const string Domain = "learnstack.operator.subject.v1";

    /// <summary>Computes the digest for an issuer and subject.</summary>
    public static string Digest(string issuer, string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{Domain}\n{issuer}\n{subject}"));
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
