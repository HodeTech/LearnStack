using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LearnStack.SharedKernel.Identity;
using OpenTelemetry;

namespace LearnStack.Infrastructure.Identity.Federation;

/// <summary>
/// ADR-0099 § Bounded egress: the only fetcher of a tenant-supplied URL. Every fetch and endpoint check validates the
/// URL shape, resolves the host once, refuses any address outside the admitted classes, and connects to a validated
/// address while TLS still verifies the certificate against the original host name (SNI), so DNS rebinding between the
/// check and the connection cannot redirect it. No redirect, cookie, credential or proxy is used; bodies, content types
/// and the time spent are bounded; XML is parsed with DTDs prohibited and no resolver. Nothing remote crosses the port:
/// results are closed statuses and the parsed values the fixed profile consumes.
/// </summary>
internal sealed class FederationEgressFetcher : IFederationEgressFetcher
{
    internal const int DiscoveryLimit = 64 * 1024;
    internal const int DocumentLimit = 256 * 1024;

    /// <summary>
    /// The ceiling for one fetch. ADR-0099 § Bounded egress names four seconds; § Intent-first operations budgets egress
    /// at three seconds in total for discovery then JWKS. The stricter three seconds is the ceiling here, and the
    /// runner's shared budget bounds the whole egress phase (Step 3 decision, recorded in the delivery log).
    /// </summary>
    internal static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(3);

    private static readonly string[] JsonTypes = ["application/json"];
    private static readonly string[] JwksTypes = ["application/json", "application/jwk-set+json"];
    private static readonly string[] SamlTypes = ["application/samlmetadata+xml", "application/xml", "text/xml"];

    private readonly FederationEgressOptions _options;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly X509Certificate2Collection? _trustRoots;
    private readonly Func<IPAddress, IPAddress> _route;

    public FederationEgressFetcher(FederationEgressOptions options)
        : this(options, Dns.GetHostAddressesAsync, null) { }

    /// <summary>
    /// Test seam: a resolver, a custom trust root and the socket address a validated address is dialled at. None disables
    /// the address policy, certificate or host-name validation: the policy runs on the resolved addresses, the custom root
    /// replaces the system store for a chain that must still verify against the original host, and the route lets a
    /// local TLS listener stand in for a validated private address. Production dials the validated address itself.
    /// </summary>
    internal FederationEgressFetcher(FederationEgressOptions options, Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        X509Certificate2Collection? trustRoots, Func<IPAddress, IPAddress>? route = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _trustRoots = trustRoots;
        _route = route ?? (address => address);
    }

    public Task<FederationEgressResult<FederationOidcDiscovery>> FetchOidcDiscoveryAsync(Uri discoveryUrl, CancellationToken cancellationToken = default) =>
        FetchAsync(discoveryUrl, DiscoveryLimit, JsonTypes, (body, url) => FederationDocumentReaders.ReadDiscovery(body, url), cancellationToken);

    public Task<FederationEgressResult<FederationJwksDocument>> FetchJwksAsync(Uri jwksUrl, CancellationToken cancellationToken = default) =>
        FetchAsync(jwksUrl, DocumentLimit, JwksTypes, (body, _) => FederationDocumentReaders.ReadJwks(body), cancellationToken);

    public Task<FederationEgressResult<FederationSamlMetadata>> FetchSamlMetadataAsync(Uri metadataUrl, CancellationToken cancellationToken = default) =>
        FetchAsync(metadataUrl, DocumentLimit, SamlTypes, (body, _) => FederationDocumentReaders.ReadSamlMetadata(body, DocumentLimit), cancellationToken);

    public async Task<FederationEgressResult<FederationEndpointAdmission>> CheckEndpointAsync(Uri endpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        using var budget = Budget(cancellationToken);
        try
        {
            var admitted = await AdmitAsync(endpoint, budget.Token).ConfigureAwait(false);
            return admitted.Status switch
            {
                FederationEgressStatus.Admitted => FederationEgressResult.Admitted(FederationEndpointAdmission.Instance),
                FederationEgressStatus.Refused => FederationEgressResult.Refused<FederationEndpointAdmission>(),
                _ => FederationEgressResult.Unavailable<FederationEndpointAdmission>(),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FederationEgressResult.Unavailable<FederationEndpointAdmission>();
        }
    }

    /// <summary>The URL shape, port rule, one resolution and the address-class check; returns the validated addresses.</summary>
    internal async Task<(FederationEgressStatus Status, IPAddress[] Addresses)> AdmitAsync(Uri url, CancellationToken cancellationToken)
    {
        if (!FederationDocumentReaders.Https(url.OriginalString, out var parsed) || parsed!.AbsoluteUri.Length > FederationDocumentReaders.MaximumUrlLength)
            return (FederationEgressStatus.Refused, []);
        var host = parsed.IdnHost.ToLowerInvariant();
        var allowListed = _options.Admits(host, parsed.Port);
        if (parsed.Port != 443 && !allowListed) return (FederationEgressStatus.Refused, []);
        IPAddress[] addresses;
        // The resolver is bounded by the fetch ceiling itself: the platform lookup does not observe the token, so a
        // non-answering resolver would otherwise hold the call past the ceiling. Expiry surfaces as cancellation, which
        // the callers report as unavailable.
        try { addresses = await _resolve(host, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (SocketException) { return (FederationEgressStatus.Unavailable, []); }
        catch (ArgumentException) { return (FederationEgressStatus.Refused, []); }
        if (addresses is not { Length: > 0 }) return (FederationEgressStatus.Unavailable, []);
        // Every resolved address must pass: one private answer among public ones refuses the host.
        var admitted = allowListed
            ? addresses.All(FederationAddressPolicy.IsAllowListable)
            : addresses.All(FederationAddressPolicy.IsGlobal);
        return admitted ? (FederationEgressStatus.Admitted, addresses) : (FederationEgressStatus.Refused, []);
    }

    private async Task<FederationEgressResult<T>> FetchAsync<T>(Uri url, int limit, string[] contentTypes,
        Func<byte[], Uri, T?> read, CancellationToken cancellationToken) where T : class
    {
        ArgumentNullException.ThrowIfNull(url);
        using var suppression = SuppressInstrumentationScope.Begin();
        using var budget = Budget(cancellationToken);
        byte[]? body = null;
        try
        {
            var (status, addresses) = await AdmitAsync(url, budget.Token).ConfigureAwait(false);
            if (status != FederationEgressStatus.Admitted) return Closed<T>(status);
            var (outcome, bytes) = await GetAsync(url, addresses, limit, contentTypes, budget.Token).ConfigureAwait(false);
            body = bytes;
            if (outcome != FederationEgressStatus.Admitted) return Closed<T>(outcome);
            var value = read(body!, url);
            return value is null ? FederationEgressResult.Refused<T>() : FederationEgressResult.Admitted(value);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FederationEgressResult.Unavailable<T>();
        }
        finally
        {
            if (body is not null) CryptographicOperations.ZeroMemory(body);
        }
    }

    private async Task<(FederationEgressStatus, byte[]?)> GetAsync(Uri url, IPAddress[] addresses, int limit, string[] contentTypes,
        CancellationToken cancellationToken)
    {
        using var handler = CreateHandler(addresses);
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = limit };
        using var request = new HttpRequestMessage(HttpMethod.Get, url) { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        request.Headers.Accept.ParseAdd(string.Join(", ", contentTypes));
        request.Headers.AcceptEncoding.ParseAdd("identity");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        HttpResponseMessage response;
        try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException error) when (error.InnerException is AuthenticationException) { return (FederationEgressStatus.Refused, null); }
        catch (HttpRequestException) { return (FederationEgressStatus.Unavailable, null); }
        using (response)
        {
            // A redirect is never followed; anything but 200 is not a document.
            if (response.StatusCode != HttpStatusCode.OK)
                return ((int)response.StatusCode is >= 500 or 429 ? FederationEgressStatus.Unavailable : FederationEgressStatus.Refused, null);
            var type = response.Content.Headers.ContentType?.MediaType;
            if (response.Content.Headers.ContentEncoding.Count != 0 || type is null
                || !contentTypes.Contains(type, StringComparer.OrdinalIgnoreCase)
                || response.Content.Headers.ContentLength is > 0 and var declared && declared > limit)
                return (FederationEgressStatus.Refused, null);
            byte[] buffer = new byte[limit + 1];
            var count = 0;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                while (count < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    count += read;
                }
            }
            catch (HttpRequestException) { CryptographicOperations.ZeroMemory(buffer); return (FederationEgressStatus.Unavailable, null); }
            catch (IOException) { CryptographicOperations.ZeroMemory(buffer); return (FederationEgressStatus.Unavailable, null); }
            if (count > limit) { CryptographicOperations.ZeroMemory(buffer); return (FederationEgressStatus.Refused, null); }
            var body = buffer.AsSpan(0, count).ToArray();
            CryptographicOperations.ZeroMemory(buffer);
            return (FederationEgressStatus.Admitted, body);
        }
    }

    /// <summary>
    /// One handler per fetch: no pooling across validations. The connect callback dials only the validated addresses and
    /// never re-resolves the host; TLS then authenticates the original host name with SNI.
    /// </summary>
    private SocketsHttpHandler CreateHandler(IPAddress[] addresses)
    {
        var tls = new SslClientAuthenticationOptions { EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 };
        if (_trustRoots is { Count: > 0 })
        {
            var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
            policy.CustomTrustStore.AddRange(_trustRoots);
            tls.CertificateChainPolicy = policy;
        }
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            Proxy = null,
            Credentials = null,
            DefaultProxyCredentials = null,
            PreAuthenticate = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = Ceiling,
            MaxConnectionsPerServer = 1,
            MaxResponseHeadersLength = 32,
            PooledConnectionLifetime = TimeSpan.Zero,
            ResponseDrainTimeout = TimeSpan.Zero,
            MaxResponseDrainSize = 0,
            SslOptions = tls,
            ConnectCallback = async (context, token) =>
            {
                foreach (var validated in addresses)
                {
                    var address = _route(validated);
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (SocketException) { socket.Dispose(); }
                    catch { socket.Dispose(); throw; }
                }
                throw new HttpRequestException(HttpRequestError.ConnectionError);
            },
        };
    }

    private static CancellationTokenSource Budget(CancellationToken cancellationToken)
    {
        var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Ceiling);
        return budget;
    }

    private static FederationEgressResult<T> Closed<T>(FederationEgressStatus status) where T : class =>
        status == FederationEgressStatus.Refused ? FederationEgressResult.Refused<T>() : FederationEgressResult.Unavailable<T>();

    public override string ToString() => nameof(FederationEgressFetcher);
}
