namespace LearnStack.Infrastructure.Identity.Federation;

/// <summary>
/// Composition-owned provider identity for the federation adapter: the tenant realm issuer only. HTTPS is mandatory; a
/// plain-HTTP loopback issuer is admitted only when the composition root declares the Development environment
/// (ADR-0099 § Ownership and the confined adapter). No caller, tenant or provider value selects the target.
/// </summary>
public sealed class KeycloakFederationOptions
{
    private const string RealmSuffix = "/realms/learnstack";

    public KeycloakFederationOptions(string issuer, bool allowLocalDevelopmentHttp = false)
    {
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var address) || address.UserInfo.Length != 0 || address.Query.Length != 0
            || address.Fragment.Length != 0
            || !(address.Scheme == Uri.UriSchemeHttps || allowLocalDevelopmentHttp && address.IsLoopback && address.Scheme == Uri.UriSchemeHttp)
            || !address.AbsolutePath.TrimEnd('/').EndsWith(RealmSuffix, StringComparison.Ordinal))
            throw new ArgumentException("Federation provider configuration is invalid.");
        Issuer = address.AbsoluteUri.TrimEnd('/');
        var root = Issuer[..^RealmSuffix.Length];
        AdminRealm = new Uri(root + "/admin/realms/learnstack/");
        TokenEndpoint = new Uri(Issuer + "/protocol/openid-connect/token");
    }

    public string Issuer { get; }
    internal Uri AdminRealm { get; }
    internal Uri TokenEndpoint { get; }
    public override string ToString() => nameof(KeycloakFederationOptions);
}
