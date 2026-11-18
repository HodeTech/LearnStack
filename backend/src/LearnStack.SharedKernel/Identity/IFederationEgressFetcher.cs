using System.Collections.Immutable;

namespace LearnStack.SharedKernel.Identity;

public enum FederationEgressStatus
{
    /// <summary>The guarded fetch or address check succeeded.</summary>
    Admitted = 1,
    /// <summary>The address class, scheme, port, size, content type or document shape refused it.</summary>
    Refused = 2,
    /// <summary>The fetcher is not configured, timed out or could not reach the host.</summary>
    Unavailable = 3,
}

/// <summary>A closed egress result; no raw body, address or remote message crosses the port.</summary>
public sealed record FederationEgressResult<T> where T : class
{
    internal FederationEgressResult(FederationEgressStatus status, T? value) { Status = status; Value = value; }
    public FederationEgressStatus Status { get; }
    public T? Value { get; }
    public override string ToString() => nameof(FederationEgressResult<T>);
}

public static class FederationEgressResult
{
    public static FederationEgressResult<T> Admitted<T>(T value) where T : class =>
        new(FederationEgressStatus.Admitted, value ?? throw new ArgumentNullException(nameof(value)));
    public static FederationEgressResult<T> Refused<T>() where T : class => new(FederationEgressStatus.Refused, null);
    public static FederationEgressResult<T> Unavailable<T>() where T : class => new(FederationEgressStatus.Unavailable, null);
}

/// <summary>The endpoints an OIDC discovery document names; the issuer is its <c>issuer</c> member.</summary>
public sealed record FederationOidcDiscovery(string Issuer, Uri AuthorizationEndpoint, Uri TokenEndpoint, Uri JwksEndpoint,
    Uri? UserInfoEndpoint)
{
    public override string ToString() => nameof(FederationOidcDiscovery);
}

/// <summary>A fetched JWKS reduced to the count of usable signing keys.</summary>
public sealed record FederationJwksDocument(int SigningKeyCount)
{
    public override string ToString() => nameof(FederationJwksDocument);
}

/// <summary>The SAML identity-provider metadata values the fixed profile consumes.</summary>
public sealed record FederationSamlMetadata(string EntityId, Uri SingleSignOnEndpoint, ImmutableArray<string> SigningCertificates)
{
    public override string ToString() => nameof(FederationSamlMetadata);
}

/// <summary>Confirmation that an endpoint passed the address-class guard.</summary>
public sealed record FederationEndpointAdmission
{
    public static FederationEndpointAdmission Instance { get; } = new();
    private FederationEndpointAdmission() { }
}

/// <summary>
/// ADR-0099 § Bounded egress: the only fetcher of a tenant-supplied URL. HTTPS only, globally routable addresses
/// (or the operator allow-list), no redirects, credentials or proxy, bounded bodies and content types. Keycloak is
/// never handed a URL to fetch. The guard runs at Configure, test start and Activate.
/// </summary>
public interface IFederationEgressFetcher
{
    Task<FederationEgressResult<FederationOidcDiscovery>> FetchOidcDiscoveryAsync(Uri discoveryUrl, CancellationToken cancellationToken = default);
    Task<FederationEgressResult<FederationJwksDocument>> FetchJwksAsync(Uri jwksUrl, CancellationToken cancellationToken = default);
    Task<FederationEgressResult<FederationSamlMetadata>> FetchSamlMetadataAsync(Uri metadataUrl, CancellationToken cancellationToken = default);

    /// <summary>Applies the address-class guard to an endpoint the provider will contact, without fetching it.</summary>
    Task<FederationEgressResult<FederationEndpointAdmission>> CheckEndpointAsync(Uri endpoint, CancellationToken cancellationToken = default);
}

/// <summary>The fail-closed default until the guarded fetcher exists: every call is unavailable, and nothing is fetched.</summary>
public sealed class UnconfiguredFederationEgressFetcher : IFederationEgressFetcher
{
    public Task<FederationEgressResult<FederationOidcDiscovery>> FetchOidcDiscoveryAsync(Uri discoveryUrl, CancellationToken cancellationToken = default) =>
        Unavailable<FederationOidcDiscovery>(discoveryUrl);
    public Task<FederationEgressResult<FederationJwksDocument>> FetchJwksAsync(Uri jwksUrl, CancellationToken cancellationToken = default) =>
        Unavailable<FederationJwksDocument>(jwksUrl);
    public Task<FederationEgressResult<FederationSamlMetadata>> FetchSamlMetadataAsync(Uri metadataUrl, CancellationToken cancellationToken = default) =>
        Unavailable<FederationSamlMetadata>(metadataUrl);
    public Task<FederationEgressResult<FederationEndpointAdmission>> CheckEndpointAsync(Uri endpoint, CancellationToken cancellationToken = default) =>
        Unavailable<FederationEndpointAdmission>(endpoint);

    private static Task<FederationEgressResult<T>> Unavailable<T>(Uri url) where T : class
    {
        ArgumentNullException.ThrowIfNull(url);
        return Task.FromResult(FederationEgressResult.Unavailable<T>());
    }
}
