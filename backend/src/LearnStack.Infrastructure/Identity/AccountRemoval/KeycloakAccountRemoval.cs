using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Resilience;
using LearnStack.SharedKernel.Secrets;
using Microsoft.Extensions.Logging;
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
/// the port sees a closed outcome only, and the only thing logged is one closed reason code per refusal.
/// </summary>
/// <remarks>
/// <para>
/// A deletion is idempotent, but the pipeline is configured without retries (see <c>AccountRemovalComposition</c>): the
/// closure's Finalize is itself the repeatable, operator-visible retry, and an automatic repeat after a lost answer would turn
/// <see cref="AccountRemovalOutcome.Uncertain"/> into a quiet <see cref="AccountRemovalOutcome.Absent"/> without the operator
/// ever seeing that an answer was lost.
/// </para>
/// <para>
/// Outcome map. 204 is <see cref="AccountRemovalOutcome.Removed"/>; a 404 that carries the provider's own JSON answer is
/// <see cref="AccountRemovalOutcome.Absent"/>. A missing or unusable secret, a malformed subject, a token endpoint that
/// refuses the client (4xx) or answers a token broader than the confined role, a 401 or 403 on the deletion, a redirect, any
/// other 4xx and a 404 that is not JSON are <see cref="AccountRemovalOutcome.Refused"/>: nothing changed and repeating
/// without repair changes nothing. A 5xx, 429, 408 or 425 on either call, any transport fault on the token call, an open
/// breaker or a full bulkhead, and a fault on the deletion that surely precedes its write (an unresolved name, a failed TLS
/// handshake or proxy tunnel) are <see cref="AccountRemovalOutcome.Unavailable"/>: no deletion was confirmed, and a repeat
/// finds the account absent if one had landed. A timeout, a lost connection, a connection error on the deletion (the transport
/// may have written the request on a reused connection and retried it) or an answer that is neither a deletion nor a refusal
/// after the deletion was handed to the transport is <see cref="AccountRemovalOutcome.Uncertain"/>.
/// </para>
/// <para>
/// A 404 is absence only when it is the provider's own answer. Keycloak 26.7.5 answers a missing user with
/// <c>Content-Type: application/json</c> and <c>{"error":"User not found"}</c> (<c>AccountRemovalProviderTests</c> reads it
/// from the pinned image), so the media type alone separates it from a hop in front of the provider that hides the admin path
/// with an HTML or empty 404; the body is still never read. A hop that answers 404 in JSON is not told apart by status or
/// type: the runbook's deployment check exercises the deletion route for that.
/// </para>
/// </remarks>
internal sealed partial class KeycloakAccountRemoval : IIdentityProviderAccountRemoval, IDisposable
{
    internal const string SecretKey = "AccountRemoval:ClientSecret";
    private const int TokenLimit = 16 * 1024;

    /// <summary>425 Too Early (RFC 8470), which <see cref="HttpStatusCode"/> has no member for.</summary>
    private const HttpStatusCode TooEarly = (HttpStatusCode)425;

    /// <summary>The whole call, token and deletion together; equal to the fixed pipeline timeout the composition root enforces.</summary>
    internal static readonly TimeSpan CallCeiling = TimeSpan.FromSeconds(AccountRemovalAdapterFactory.CallCeilingSeconds);

    /// <summary>
    /// The adapter's own cancellation, one second after the pipeline's timeout; it bounds the call when the pipeline has no
    /// timeout. Were it equal to the pipeline timeout it would start first and win the race: Polly then rethrows a plain
    /// cancellation, which the circuit breaker does not handle and so records as a success, and a hanging provider would
    /// never open the breaker. Set later, it never wins while the pipeline times out: the pipeline's own timeout fires first
    /// and surfaces as the timeout rejection the breaker counts.
    /// </summary>
    internal static readonly TimeSpan Backstop = CallCeiling + TimeSpan.FromSeconds(1);

    private readonly KeycloakAccountRemovalOptions _options;
    private readonly ISecretProvider _secrets;
    private readonly ResiliencePipeline _pipeline;
    private readonly ILogger _logger;
    private readonly HttpClient _client;

    internal KeycloakAccountRemoval(KeycloakAccountRemovalOptions options, ISecretProvider secrets,
        IProviderResilience<IIdentityProviderAccountRemoval> resilience, ILogger logger)
        : this(options, secrets, resilience, logger, new SocketsHttpHandler()) { }

    /// <summary>Controlled transports are a test seam, never a certificate bypass: a supplied sockets handler is hardened.</summary>
    internal KeycloakAccountRemoval(KeycloakAccountRemovalOptions options, ISecretProvider secrets,
        IProviderResilience<IIdentityProviderAccountRemoval> resilience, ILogger logger, HttpMessageHandler handler)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _pipeline = (resilience ?? throw new ArgumentNullException(nameof(resilience))).Pipeline;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
        // The subject is judged first: it is the cause no repair of the secret can change.
        var configured = _secrets.GetSecret(SecretKey);
        if (!KeycloakAccountRemovalRoutes.TryCanonicalSubject(providerSubject, out var user))
            return Refuse(RefusalReason.SubjectShape);
        if (string.IsNullOrWhiteSpace(configured) || !FederationClientSecret.TryCreate(configured, out var secret))
            return Refuse(RefusalReason.SecretUnusable);
        using var suppression = SuppressInstrumentationScope.Begin();
        using var ceiling = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ceiling.CancelAfter(Backstop);
        var attempt = new Attempt();
        try
        {
            var outcome = await _pipeline.ExecuteAsync(async token =>
            {
                var bearer = await TokenAsync(secret!, attempt, token).ConfigureAwait(false);
                return bearer is null ? AccountRemovalOutcome.Refused : await DeleteAsync(user, bearer, attempt, token).ConfigureAwait(false);
            }, ceiling.Token).ConfigureAwait(false);
            return outcome == AccountRemovalOutcome.Refused ? Refuse(attempt.Refusal) : outcome;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AccountRemovalProviderException fault) { return fault.Uncertain ? AccountRemovalOutcome.Uncertain : AccountRemovalOutcome.Unavailable; }
        // The pipeline's own elapsed timeout, the backstop and a transport fault that escaped: only the attempt says whether the
        // deletion may have reached the provider. Breaker and bulkhead rejections never ran the lambda and fall through the
        // attempt check as unavailable.
        catch (Exception error) when (error is TimeoutRejectedException or OperationCanceledException or HttpRequestException or IOException)
        { return attempt.DeletionHandedToTransport ? AccountRemovalOutcome.Uncertain : AccountRemovalOutcome.Unavailable; }
        catch (ExecutionRejectedException) { return AccountRemovalOutcome.Unavailable; }
    }

    /// <summary>A refusal: the outcome, with its one closed reason code logged and nothing else (no subject, URL or provider text).</summary>
    private AccountRemovalOutcome Refuse(RefusalReason reason)
    {
        LogRefused(_logger, reason switch
        {
            RefusalReason.SubjectShape => "subject_shape",
            RefusalReason.SecretUnusable => "secret_unusable",
            RefusalReason.TokenRefused => "token_refused",
            RefusalReason.TokenMalformed => "token_malformed",
            RefusalReason.TokenNotConfined => "token_not_confined",
            RefusalReason.DeleteUnauthorized => "delete_unauthorized",
            RefusalReason.DeleteRedirect => "delete_redirect",
            RefusalReason.DeleteRejected => "delete_rejected",
            RefusalReason.DeleteNotFoundForeign => "delete_not_found_foreign",
            _ => "unclassified",
        });
        return AccountRemovalOutcome.Refused;
    }

    [LoggerMessage(EventId = 7125, Level = LogLevel.Warning,
        Message = "The account removal adapter refused the deletion: {Reason}.")]
    private static partial void LogRefused(ILogger logger, string reason);

    /// <summary>The bearer token, or null (with the reason on the attempt) when the provider definitively refused the client or issued a broader token.</summary>
    private async Task<string?> TokenAsync(FederationClientSecret secret, Attempt attempt, CancellationToken token)
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
            if (IsTransient(response.StatusCode)) throw new AccountRemovalProviderException(uncertain: false);
            if (response.StatusCode != HttpStatusCode.OK) return Declined(attempt, RefusalReason.TokenRefused);
            if (response.Content.Headers.ContentEncoding.Count != 0 || !IsJson(response)
                || response.Content.Headers.ContentLength > TokenLimit)
                return Declined(attempt, RefusalReason.TokenMalformed);
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
                return count > TokenLimit ? Declined(attempt, RefusalReason.TokenMalformed) : ParseToken(buffer.AsSpan(0, count), attempt);
            }
            catch (HttpRequestException) { throw new AccountRemovalProviderException(uncertain: false); }
            catch (IOException) { throw new AccountRemovalProviderException(uncertain: false); }
            finally { CryptographicOperations.ZeroMemory(buffer); }
        }
    }

    private string? ParseToken(ReadOnlySpan<byte> body, Attempt attempt)
    {
        try
        {
            var reader = new Utf8JsonReader(body, new JsonReaderOptions { MaxDepth = 16 });
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("access_token", out var access)
                || access.ValueKind != JsonValueKind.String || access.GetString() is not { Length: > 0 and <= TokenLimit } bearer
                || !root.TryGetProperty("token_type", out var type) || type.ValueKind != JsonValueKind.String
                || !string.Equals(type.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase))
                return Declined(attempt, RefusalReason.TokenMalformed);
            return NarrowToken(bearer) is { } defect ? Declined(attempt, defect) : bearer;
        }
        catch (JsonException) { return Declined(attempt, RefusalReason.TokenMalformed); }
    }

    /// <summary>
    /// Metadata from the fixed token endpoint, not an incoming-user authentication: the admin API validates the bearer. These
    /// checks refuse a misprovisioned client whose token is broader than <c>manage-users</c> alone: the issuer, the authorized
    /// party, no realm role, and exactly one <c>realm-management</c> role equal to the admin role. Null when the token passes;
    /// otherwise a token that is not a JWT at all is malformed, and one that is not exactly this client's is not confined.
    /// </summary>
    private RefusalReason? NarrowToken(string access)
    {
        var parts = access.Split('.');
        if (parts.Length != 3 || parts.Any(string.IsNullOrEmpty)) return RefusalReason.TokenMalformed;
        byte[] payload;
        try
        {
            var encoded = parts[1].Replace('-', '+').Replace('_', '/');
            payload = Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='));
        }
        catch (FormatException) { return RefusalReason.TokenMalformed; }
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("iss", out var issuer) || issuer.GetString() != _options.Issuer
                || !root.TryGetProperty("azp", out var party) || party.GetString() != KeycloakAccountRemovalRoutes.ClientId)
                return RefusalReason.TokenNotConfined;
            if (root.TryGetProperty("realm_access", out var realm) && realm.ValueKind == JsonValueKind.Object
                && realm.TryGetProperty("roles", out var realmRoles) && realmRoles.ValueKind == JsonValueKind.Array && realmRoles.GetArrayLength() != 0)
                return RefusalReason.TokenNotConfined;
            if (!root.TryGetProperty("resource_access", out var resources) || resources.ValueKind != JsonValueKind.Object
                || resources.EnumerateObject().Count() != 1 || !resources.TryGetProperty("realm-management", out var management)
                || management.ValueKind != JsonValueKind.Object || !management.TryGetProperty("roles", out var roles)
                || roles.ValueKind != JsonValueKind.Array || roles.GetArrayLength() != 1)
                return RefusalReason.TokenNotConfined;
            return roles[0].ValueKind == JsonValueKind.String && roles[0].GetString() == KeycloakAccountRemovalRoutes.AdminRole
                ? null : RefusalReason.TokenNotConfined;
        }
        catch (JsonException) { return RefusalReason.TokenMalformed; }
        catch (InvalidOperationException) { return RefusalReason.TokenMalformed; }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    private async Task<AccountRemovalOutcome> DeleteAsync(string user, string bearer, Attempt attempt, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, KeycloakAccountRemovalRoutes.User(_options, user));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        HttpResponseMessage response;
        // From here the transport may have written the request: a cancellation or timeout is uncertain, not unavailable.
        attempt.DeletionHandedToTransport = true;
        try { response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); }
        catch (HttpRequestException error) { throw new AccountRemovalProviderException(uncertain: !NeverSent(error)); }
        using (response)
        {
            // The body of a deletion answer is never read: the status, and for a 404 the media type, are the answer.
            var status = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.NoContent) return AccountRemovalOutcome.Removed;
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // Absence is the provider's own answer, which is JSON. A 404 in any other type is a hop in front of the provider
                // (a proxy or gateway that hides the admin path) answering for it: the account was never looked up, so it is not absent.
                if (IsJson(response)) return AccountRemovalOutcome.Absent;
                attempt.Refusal = RefusalReason.DeleteNotFoundForeign;
                return AccountRemovalOutcome.Refused;
            }
            if (IsTransient(response.StatusCode)) throw new AccountRemovalProviderException(uncertain: false);
            // A redirect is never followed, and 401, 403 and every other 4xx say the provider will not do this as configured.
            if (status is >= 300 and < 500)
            {
                attempt.Refusal = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? RefusalReason.DeleteUnauthorized
                    : status < 400 ? RefusalReason.DeleteRedirect : RefusalReason.DeleteRejected;
                return AccountRemovalOutcome.Refused;
            }
            // A 1xx or 2xx that is not the deletion's own answer: it may have been applied, and the answer cannot be trusted.
            throw new AccountRemovalProviderException(uncertain: true);
        }
    }

    /// <summary>The provider's own answers are JSON (the token, and Keycloak's 404 for a user it does not hold); the media type is header metadata.</summary>
    private static bool IsJson(HttpResponseMessage response) =>
        string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Answers that say the provider or a hop in front of it could not take the request now: a server fault, a throttle (429), a
    /// request that was not received in time (408) and one refused for its replay risk (425). The last two are defined as not
    /// processed (RFC 9110 15.5.9, RFC 8470), so a repeat is right, and the breaker counts them like the others.
    /// </summary>
    private static bool IsTransient(HttpStatusCode status) =>
        (int)status >= 500 || status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout or TooEarly;

    /// <summary>
    /// Faults that surely precede the write of the request: name resolution, the TLS handshake and a proxy tunnel. A connection
    /// error is not among them: .NET reports it when a reused connection broke after the request was written and the transparent
    /// retry on a new connection then failed to connect, so the deletion may have reached the provider.
    /// </summary>
    private static bool NeverSent(HttpRequestException error) =>
        error.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError
            or HttpRequestError.ProxyTunnelError;

    private static string? Declined(Attempt attempt, RefusalReason reason)
    {
        attempt.Refusal = reason;
        return null;
    }

    public void Dispose() => _client.Dispose();

    public override string ToString() => nameof(KeycloakAccountRemoval);

    /// <summary>One call's state: whether the deletion was handed to the transport (set once, read after a failure) and why it was refused.</summary>
    private sealed class Attempt
    {
        internal bool DeletionHandedToTransport { get; set; }
        internal RefusalReason Refusal { get; set; }
    }

    /// <summary>
    /// The closed set of causes behind <see cref="AccountRemovalOutcome.Refused"/>, each with its own remedy; the runbook lists
    /// the code every one is logged under. <see cref="Unclassified"/> is the default no refusal path leaves unset.
    /// </summary>
    private enum RefusalReason
    {
        Unclassified,
        SubjectShape,
        SecretUnusable,
        TokenRefused,
        TokenMalformed,
        TokenNotConfined,
        DeleteUnauthorized,
        DeleteRedirect,
        DeleteRejected,
        DeleteNotFoundForeign,
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
