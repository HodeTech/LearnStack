using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;

namespace LearnStack.Modules.Identity.Infrastructure.Bindings;

/// <summary>Deployment-pinned configuration; this value neither installs nor changes the stored authority.</summary>
public sealed class IdentityAuthorityRegistration
{
    public IdentityAuthorityRegistration(IdentityAuthorityId authorityId, string issuer)
    {
        if (!authorityId.IsInitialized() || authorityId.Value == Guid.Empty
            || string.IsNullOrEmpty(issuer) || issuer.Length > 2048
            || !Uri.TryCreate(issuer, UriKind.Absolute, out var address)
            || !address.IsWellFormedOriginalString()
            || !string.Equals(address.AbsoluteUri, issuer, StringComparison.Ordinal)
            || !(address.Scheme == Uri.UriSchemeHttps || (address.Scheme == Uri.UriSchemeHttp && address.IsLoopback))
            || address.UserInfo.Length != 0 || address.Query.Length != 0 || address.Fragment.Length != 0
            || !HasUnambiguousPath(address)
            || !address.AbsolutePath.EndsWith("/realms/learnstack", StringComparison.Ordinal))
            throw new ArgumentException("Identity authority configuration is invalid.");
        AuthorityId = authorityId;
        Issuer = issuer;
    }

    public IdentityAuthorityId AuthorityId { get; }
    public string Issuer { get; }

    public bool Matches(GlobalIdentitySubject subject) => subject is not null
        && subject.AuthorityId == AuthorityId && string.Equals(subject.Issuer, Issuer, StringComparison.Ordinal);

    // Keep deployment qualification compatible with the existing API issuer
    // address grammar. Deployment-mode admission remains at the composition root.
    private static bool HasUnambiguousPath(Uri address) =>
        !address.AbsolutePath.Contains("//", StringComparison.Ordinal)
        && address.Segments.Skip(1).All(segment =>
        {
            var decoded = Uri.UnescapeDataString(segment.TrimEnd('/'));
            var pathPart = decoded.Split(';', 2)[0].TrimEnd();
            return pathPart is not ("." or "..") && !decoded.Any(char.IsControl)
                && !decoded.Contains('/') && !decoded.Contains('\\') && !decoded.Contains('%');
        });

    public override string ToString() => nameof(IdentityAuthorityRegistration);
}
