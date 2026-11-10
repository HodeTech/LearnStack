using System.Security.Cryptography;
using System.Text;

namespace LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;

/// <summary>Canonical confirmation digest of the exact authored notice, never its display fallback.</summary>
public static class MembershipFieldConsentNotice
{
    private static readonly byte[] Domain = Encoding.UTF8.GetBytes("learnstack.membership-field-consent.notice.v1\0");

    public static string ComputeHash(string notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Domain);
        hash.AppendData(Encoding.UTF8.GetBytes(notice));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
