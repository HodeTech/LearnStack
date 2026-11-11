using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Infrastructure.Identity.ProviderEvents;

/// <summary>Composition-owned source identity, never a provider-selected tenant or caller URL.</summary>
public sealed class IdentityEventProviderOptions
{
    public IdentityEventProviderOptions(IdentityAuthorityId authorityId, string issuer, string realmId,
        bool allowLocalDevelopmentHttp = false)
    {
        if (!StronglyTypedId.IsAssigned(authorityId) || string.IsNullOrWhiteSpace(realmId) || realmId.Length > 255
            || realmId.Any(char.IsControl) || !Uri.TryCreate(issuer, UriKind.Absolute, out var address)
            || address.UserInfo.Length != 0 || address.Query.Length != 0 || address.Fragment.Length != 0
            || !(address.Scheme == Uri.UriSchemeHttps
                || allowLocalDevelopmentHttp && address.IsLoopback && address.Scheme == Uri.UriSchemeHttp)
            || !address.AbsolutePath.TrimEnd('/').EndsWith("/realms/learnstack", StringComparison.Ordinal))
            throw new ArgumentException("Identity event source configuration is invalid.");
        AuthorityId = authorityId; Issuer = address.AbsoluteUri.TrimEnd('/'); RealmId = realmId;
    }
    public IdentityAuthorityId AuthorityId { get; }
    public string Issuer { get; }
    public string RealmId { get; }
    public override string ToString() => nameof(IdentityEventProviderOptions);
}
