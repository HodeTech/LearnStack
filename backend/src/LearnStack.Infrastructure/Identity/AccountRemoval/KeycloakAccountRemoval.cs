using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Resilience;
using LearnStack.SharedKernel.Secrets;
using OpenTelemetry;
using Polly;
using Polly.Timeout;

namespace LearnStack.Infrastructure.Identity.AccountRemoval;

/// <summary>
/// ADR-0107 § 7's confined Keycloak adapter. It holds the account-removal client
/// (<see cref="KeycloakAccountRemovalRoutes.ClientId"/>, <c>manage-users</c> only) alone, takes a fresh client-credentials
/// token per call, and sends exactly one request: <c>DELETE users/{id}</c> for a canonical user identifier. The client's role
/// can also create and update users, which is why this class has one method, builds no other route and never reads a
/// response body of the deletion. The subject, the client secret and every provider body or message stay inside this class:
/// the port sees a closed outcome only, and nothing is logged.
/// </summary>
/// <remarks>
/// <para>
/// A deletion is idempotent, but the pipeline is configured without retries (see <c>AccountRemovalComposition</c>): the
/// closure's Finalize is itself the repeatable, operator-visible retry, and an automatic repeat after a lost answer would turn
/// <see cref="AccountRemovalOutcome.Uncertain"/> into a quiet <see cref="AccountRemovalOutcome.Absent"/> without the operator
/// ever seeing that an answer was lost.
/// </para>
/// <para>
/// Outcome map. 204 is <see cref="AccountRemovalOutcome.Removed"/>; 404 is <see cref="AccountRemovalOutcome.Absent"/>. A missing or
/// unusable secret, a malformed subject, a token endpoint that refuses the client (4xx) or answers a token broader than the
/// confined role, a 401 or 403 on the deletion, a redirect and any other 4xx are <see cref="AccountRemovalOutcome.Refused"/>:
/// nothing changed and repeating without repair changes nothing. A 5xx or 429 on either call, a refused connection, an open
/// breaker or a full bulkhead, and any fault before the deletion was handed to the transport are
/// <see cref="AccountRemovalOutcome.Unavailable"/>. A timeout, a lost connection or an answer that is neither a deletion nor a
/// refusal after the deletion was handed to the transport is <see cref="AccountRemovalOutcome.Uncertain"/>.
/// </para>
/// </remarks>
internal sealed class KeycloakAccountRemoval : IIdentityProviderAccountRemoval, IDisposable
{
    internal const string SecretKey = "AccountRemoval:ClientSecret";
    private const int TokenLimit = 16 * 1024;

    /// <summary>The whole call, token and deletion together; equal to the fixed pipeline timeout the composition root enforces.</summary>
    internal static readonly TimeSpan CallCeiling = TimeSpan.FromSeconds(AccountRemovalAdapterFactory.CallCeilingSeconds);

    private readonly KeycloakAccountRemovalOptions _options;
    private readonly ISecretProvider _secrets;
    private readonly ResiliencePipeline _pipeline;
    private readonly HttpClient _client;

    internal KeycloakAccountRemoval(KeycloakAccountRemovalOptions options, ISecretProvider secrets,
        IProviderResilience<IIdentityProviderAccountRemoval> resilience)
        : this(options, secrets, resilience, new SocketsHttpHandler()) { }

    /// <summary>Controlled transports are a test seam, never a certificate bypass: a supplied sockets handler is hardened.</summary>
    internal KeycloakAccountRemoval(KeycloakAccountRemovalOptions options, ISecretProvider secrets,
        IProviderResilience<IIdentityProviderAccountRemoval> resilience, HttpMessageHandler handler)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _pipeline = (resilience ?? throw new ArgumentNullException(nameof(resilience))).Pipeline;
        ArgumentNullException.ThrowIfNull(handler);
        if (handler is SocketsHttpHandler sockets)
        {
            if (sockets.SslOptions.RemoteCertificateValidationCallback is not null)
                throw new InvalidOperationException("Account removal provider certificate validation cannot be replaced.");
            sockets.AllowAutoRedirect = false; sockets.UseCookies = false; sockets.UseProxy = false; sockets.Proxy = null;
            sockets.Credentials = null; sockets.DefaultProxyCredentials = null; sockets.PreAuthenticate = false;
            sockets.AutomaticDecompression = DecompressionMethods.None;
            sockets.ConnectTimeout = TimeSpan.FromSeconds(2.5); sockets.MaxResponseHeadersLength = 32;
            sockets.ResponseDrainTimeout = TimeSpan.Zero; sockets.MaxResponseDrainSize = 0;
        }
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = TokenLimit };
    }

    public async Task<AccountRemovalOutcome> RemoveAsync(string providerSubject, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providerSubject);
        // Both refusals precede any network effect: a malformed subject can never be sent, and an unusable secret never leaves.
        var configured = _secrets.GetSecret(SecretKey);
        if (!KeycloakAccountRemovalRoutes.TryCanonicalSubject(providerSubject, out var user) || string.IsNullOrWhiteSpace(configured)
            || !FederationClientSecret.TryCreate(configured, out var secret))
            return AccountRemovalOutcome.Refused;
        using var suppression = SuppressInstrumentationScope.Begin();
        using var ceiling = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ceiling.CancelAfter(CallCeiling);
        var phase = new Phase();
        try
        {
            return await _pipeline.ExecuteAsync(async token =>
            {
                var bearer = await TokenAsync(secret!, token).ConfigureAwait(false);
                return bearer is null ? AccountRemovalOutcome.Refused : await DeleteAsync(user, bearer, phase, token).ConfigureAwait(false);
            }, ceiling.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AccountRemovalProviderException fault) { return fault.Uncertain ? AccountRemovalOutcome.Uncertain : AccountRemovalOutcome.Unavailable; }
        // The pipeline's own elapsed timeout, the ceiling and a transport fault that escaped: only the phase says whether the
        // deletion may have reached the provider. Breaker and bulkhead rejections never ran the lambda and fall through the
        // phase check as unavailable.
        catch (Exception error) when (error is TimeoutRejectedException or OperationCanceledException or HttpRequestException or IOException)
        { return phase.DeletionHandedToTransport ? AccountRemovalOutcome.Uncertain : AccountRemovalOutcome.Unavailable; }
        catch (ExecutionRejectedException) { return AccountRemovalOutcome.Unavailable; }
    }

    /// <summary>The bearer token, or null when the provider definitively refused the client or issued a broader token.</summary>
    private async Task<string?> TokenAsync(FederationClientSecret secret, CancellationToken token)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = KeycloakAccountRemovalRoutes.ClientId,
            ["client_secret"] = secret.Reveal(),
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, KeycloakAccountRemovalRoutes.Token(_options)) { Content = content };
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        HttpResponseMessage response;
        try { response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); }
        catch (HttpRequestException) { throw new AccountRemovalProviderException(uncertain: false); }
        using (response)
        {
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new AccountRemovalProviderException(uncertain: false);
            if (response.StatusCode != HttpStatusCode.OK) return null;
            if (response.Content.Headers.ContentEncoding.Count != 0
                || !string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
                || response.Content.Headers.ContentLength > TokenLimit)
                return null;
            var buffer = new byte[TokenLimit + 1];
            var count = 0;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                while (count < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(count), token).ConfigureAwait(false);
                    if (read == 0) break;
                    count += read;
                }
                return count > TokenLimit ? null : ParseToken(buffer.AsSpan(0, count));
            }
            catch (HttpRequestException) { throw new AccountRemovalProviderException(uncertain: false); }
            catch (IOException) { throw new AccountRemovalProviderException(uncertain: false); }
            finally { CryptographicOperations.ZeroMemory(buffer); }
        }
    }

    private string? ParseToken(ReadOnlySpan<byte> body)
    {
        try
        {
            var reader = new Utf8JsonReader(body, new JsonReaderOptions { MaxDepth = 16 });
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("access_token", out var access)
                || access.ValueKind != JsonValueKind.String || access.GetString() is not { Length: > 0 and <= TokenLimit } bearer
                || !root.TryGetProperty("token_type", out var type) || type.ValueKind != JsonValueKind.String
                || !string.Equals(type.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase) || !NarrowToken(bearer))
                return null;
            return bearer;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Metadata from the fixed token endpoint, not an incoming-user authentication: the admin API validates the bearer. These
    /// checks refuse a misprovisioned client whose token is broader than <c>manage-users</c> alone: the issuer, the authorized
    /// party, no realm role, and exactly one <c>realm-management</c> role equal to the admin role.
    /// </summary>
    private bool NarrowToken(string access)
    {
        var parts = access.Split('.');
        if (parts.Length != 3 || parts.Any(string.IsNullOrEmpty)) return false;
        byte[] payload;
        try
        {
            var encoded = parts[1].Replace('-', '+').Replace('_', '/');
            payload = Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='));
        }
        catch (FormatException) { return false; }
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("iss", out var issuer) || issuer.GetString() != _options.Issuer
                || !root.TryGetProperty("azp", out var party) || party.GetString() != KeycloakAccountRemovalRoutes.ClientId)
                return false;
            if (root.TryGetProperty("realm_access", out var realm) && realm.ValueKind == JsonValueKind.Object
                && realm.TryGetProperty("roles", out var realmRoles) && realmRoles.ValueKind == JsonValueKind.Array && realmRoles.GetArrayLength() != 0)
                return false;
            if (!root.TryGetProperty("resource_access", out var resources) || resources.ValueKind != JsonValueKind.Object
                || resources.EnumerateObject().Count() != 1 || !resources.TryGetProperty("realm-management", out var management)
                || management.ValueKind != JsonValueKind.Object || !management.TryGetProperty("roles", out var roles)
                || roles.ValueKind != JsonValueKind.Array || roles.GetArrayLength() != 1)
                return false;
            return roles[0].ValueKind == JsonValueKind.String && roles[0].GetString() == KeycloakAccountRemovalRoutes.AdminRole;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    private async Task<AccountRemovalOutcome> DeleteAsync(string user, string bearer, Phase phase, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, KeycloakAccountRemovalRoutes.User(_options, user));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        HttpResponseMessage response;
        // From here the transport may have written the request: a cancellation or timeout is uncertain, not unavailable.
        phase.DeletionHandedToTransport = true;
        try { response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); }
        catch (HttpRequestException error) { throw new AccountRemovalProviderException(uncertain: !NeverSent(error)); }
        using (response)
        {
            // The body of a deletion answer is never read: the status alone is the answer, and a body is provider text.
            var status = (int)response.StatusCode;
            switch (response.StatusCode)
            {
                case HttpStatusCode.NoContent: return AccountRemovalOutcome.Removed;
                case HttpStatusCode.NotFound: return AccountRemovalOutcome.Absent;
                case HttpStatusCode.TooManyRequests: throw new AccountRemovalProviderException(uncertain: false);
            }
            if (status >= 500) throw new AccountRemovalProviderException(uncertain: false);
            // A redirect is never followed, and 401, 403 and every other 4xx say the provider will not do this as configured.
            if (status is >= 300 and < 500) return AccountRemovalOutcome.Refused;
            // A 1xx or 2xx that is not the deletion's own answer: it may have been applied, and the answer cannot be trusted.
            throw new AccountRemovalProviderException(uncertain: true);
        }
    }

    /// <summary>Faults that happen before any request byte is written: name resolution, connecting, TLS and a proxy tunnel.</summary>
    private static bool NeverSent(HttpRequestException error) =>
        error.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError
            or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError;

    public void Dispose() => _client.Dispose();

    public override string ToString() => nameof(KeycloakAccountRemoval);

    /// <summary>Whether the deletion was handed to the transport in the current attempt; set once, read after a failure.</summary>
    private sealed class Phase
    {
        internal bool DeletionHandedToTransport { get; set; }
    }
}

/// <summary>
/// A closed adapter fault, thrown so the resilience breaker counts it. It carries no provider body, status text, transport
/// message, subject or secret; the flag says whether the deletion may have been applied.
/// </summary>
internal sealed class AccountRemovalProviderException : ProviderException
{
    internal AccountRemovalProviderException(bool uncertain)
        : base("keycloak", "Account removal provider call failed.", isClientError: false)
    {
        Uncertain = uncertain;
    }

    internal bool Uncertain { get; }
}
