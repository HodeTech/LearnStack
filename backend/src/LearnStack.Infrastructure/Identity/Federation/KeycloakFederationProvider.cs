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

namespace LearnStack.Infrastructure.Identity.Federation;

/// <summary>
/// ADR-0099's confined Keycloak adapter. It holds the federation admin client (<see cref="KeycloakFederationRoutes.ClientId"/>) alone, takes a fresh
/// client-credentials token per operation, and calls only <see cref="KeycloakFederationRoutes"/>: create for an absent
/// owned alias, read, full-state replace and delete of that alias, and its mapper list. Every read or write first proves
/// the provider object's owner marker names this tenant and connection; a foreign or unreadable object is never touched.
/// The same class implements the operator port (ADR-0099 Amendment 10): one instance serves the tenant runner and
/// another, built with its own resilience pipeline, serves the operator, so a sweep cannot starve a tenant's calls.
/// The client secret is written, never read back, and never returned. Provider responses, messages and exceptions stay
/// inside this namespace: the port sees closed statuses only.
/// </summary>
internal sealed partial class KeycloakFederationProvider : IFederationProvider, IFederationOperator, IDisposable
{
    internal const string SecretKey = "Federation:ClientSecret";
    private const int TokenLimit = 16 * 1024;
    private const int InstanceLimit = 64 * 1024;
    private const int MapperLimit = 256 * 1024;
    private static readonly TimeSpan CallCeiling = TimeSpan.FromSeconds(2.5);

    private readonly KeycloakFederationOptions _options;
    private readonly ISecretProvider _secrets;
    private readonly ResiliencePipeline _pipeline;
    private readonly HttpClient _client;

    internal KeycloakFederationProvider(KeycloakFederationOptions options, ISecretProvider secrets, IProviderResilience<IFederationProvider> resilience)
        : this(options, secrets, resilience, new SocketsHttpHandler()) { }

    /// <summary>Controlled transports are a test seam, never a certificate bypass: a supplied sockets handler is hardened.</summary>
    internal KeycloakFederationProvider(KeycloakFederationOptions options, ISecretProvider secrets, IProviderResilience<IFederationProvider> resilience,
        HttpMessageHandler handler)
        : this(options, secrets, (resilience ?? throw new ArgumentNullException(nameof(resilience))).Pipeline, handler) { }

    /// <summary>The operator's adapter: the same client and routes under its own bulkhead and breaker.</summary>
    internal KeycloakFederationProvider(KeycloakFederationOptions options, ISecretProvider secrets, IProviderResilience<IFederationOperator> resilience)
        : this(options, secrets, resilience, new SocketsHttpHandler()) { }

    internal KeycloakFederationProvider(KeycloakFederationOptions options, ISecretProvider secrets, IProviderResilience<IFederationOperator> resilience,
        HttpMessageHandler handler)
        : this(options, secrets, (resilience ?? throw new ArgumentNullException(nameof(resilience))).Pipeline, handler) { }

    private KeycloakFederationProvider(KeycloakFederationOptions options, ISecretProvider secrets, ResiliencePipeline pipeline,
        HttpMessageHandler handler)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        ArgumentNullException.ThrowIfNull(handler);
        if (handler is SocketsHttpHandler sockets)
        {
            if (sockets.SslOptions.RemoteCertificateValidationCallback is not null)
                throw new InvalidOperationException("Federation provider certificate validation cannot be replaced.");
            sockets.AllowAutoRedirect = false; sockets.UseCookies = false; sockets.UseProxy = false; sockets.Proxy = null;
            sockets.Credentials = null; sockets.DefaultProxyCredentials = null; sockets.PreAuthenticate = false;
            sockets.AutomaticDecompression = DecompressionMethods.None;
            sockets.ConnectTimeout = CallCeiling; sockets.MaxResponseHeadersLength = 32;
            sockets.ResponseDrainTimeout = TimeSpan.Zero; sockets.MaxResponseDrainSize = 0;
        }
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = MapperLimit };
    }

    public async Task<FederationProviderResult> ApplyAsync(FederationBrokerWrite write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        var state = write.State;
        // An OIDC broker is never created or kept without a secret: a secretless create would write a broker no test passes.
        return await RunAsync(FederationProviderResult.Unavailable, async (bearer, token) =>
        {
            var current = await ReadOwnedAsync(state.Owner, bearer, token).ConfigureAwait(false);
            if (current.Kind == Owned.Foreign) return FederationProviderResult.Foreign;
            if (current.Kind == Owned.Unreadable) return FederationProviderResult.Foreign;
            if (current.Kind == Owned.Present && current.Protocol != state.Protocol) return FederationProviderResult.Refused;
            if (current.Kind == Owned.Absent && state.Protocol == FederationBrokerProtocol.Oidc && write.Secret is null)
                return FederationProviderResult.Refused;
            if (current.Kind == Owned.Present && RefusedChange(current.State!, state, write.Secret is not null))
                return FederationProviderResult.Refused;
            var representation = KeycloakFederationProfile.Representation(state, write.Secret);
            var creating = current.Kind == Owned.Absent;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(representation);
            try
            {
                using var content = new ByteArrayContent(bytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                var status = await SendAsync(creating ? HttpMethod.Post : HttpMethod.Put,
                    creating ? KeycloakFederationRoutes.Instances(_options) : KeycloakFederationRoutes.Instance(_options, state.Owner),
                    content, bearer, 0, token).ConfigureAwait(false);
                return status.Code switch
                {
                    HttpStatusCode.Created when creating => FederationProviderResult.Applied,
                    HttpStatusCode.NoContent when !creating => FederationProviderResult.Applied,
                    HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.NotFound or HttpStatusCode.Forbidden
                        or HttpStatusCode.Unauthorized => FederationProviderResult.Refused,
                    _ => throw Fault(),
                };
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FederationBrokerReadback> ReadAsync(FederationBrokerOwner owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return await RunAsync(FederationBrokerReadback.Unavailable, async (bearer, token) =>
        {
            var current = await ReadOwnedAsync(owner, bearer, token).ConfigureAwait(false);
            switch (current.Kind)
            {
                case Owned.Absent: return FederationBrokerReadback.Absent;
                case Owned.Foreign or Owned.Unreadable: return FederationBrokerReadback.Foreign;
            }
            var mappers = await SendAsync(HttpMethod.Get, KeycloakFederationRoutes.Mappers(_options, owner), null, bearer, MapperLimit, token)
                .ConfigureAwait(false);
            if (mappers.Code != HttpStatusCode.OK || mappers.Body is null) throw Fault();
            int count;
            try
            {
                using var document = JsonDocument.Parse(mappers.Body, new JsonDocumentOptions { MaxDepth = 16 });
                if (document.RootElement.ValueKind != JsonValueKind.Array) throw Fault();
                count = document.RootElement.GetArrayLength();
            }
            catch (JsonException) { throw Fault(); }
            return FederationBrokerReadback.Present(current.State!, count, current.Conforms);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FederationProviderResult> DeleteAsync(FederationBrokerOwner owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return await RunAsync(FederationProviderResult.Unavailable, async (bearer, token) =>
        {
            var current = await ReadOwnedAsync(owner, bearer, token).ConfigureAwait(false);
            switch (current.Kind)
            {
                case Owned.Absent: return FederationProviderResult.Applied;
                case Owned.Foreign or Owned.Unreadable: return FederationProviderResult.Foreign;
            }
            var deleted = await SendAsync(HttpMethod.Delete, KeycloakFederationRoutes.Instance(_options, owner), null, bearer, 0, token)
                .ConfigureAwait(false);
            return deleted.Code switch
            {
                HttpStatusCode.NoContent or HttpStatusCode.NotFound => FederationProviderResult.Applied,
                HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                    => FederationProviderResult.Refused,
                _ => throw Fault(),
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The adapter's own defence for two rules, compared with what the provider holds now: the issuer or SAML entity
    /// identifier never changes in place (ADR-0099 § Ownership; a new connection is required), and a write without a fresh
    /// secret keeps the provider's stored secret, so it may not change the token endpoint, client identifier or client
    /// authentication the stored secret would then be sent with (ADR-0099 Amendment 3). Either refuses before any write.
    /// </summary>
    private static bool RefusedChange(FederationBrokerState held, FederationBrokerState desired, bool freshSecret) =>
        (held.Configuration, desired.Configuration) switch
        {
            (FederationOidcConfiguration current, FederationOidcConfiguration wanted) =>
                !string.Equals(current.Issuer, wanted.Issuer, StringComparison.Ordinal)
                || !freshSecret && (!string.Equals(current.TokenEndpoint.AbsoluteUri, wanted.TokenEndpoint.AbsoluteUri, StringComparison.Ordinal)
                    || !string.Equals(current.ClientId, wanted.ClientId, StringComparison.Ordinal)
                    || current.ClientAuthentication != wanted.ClientAuthentication),
            (FederationSamlConfiguration current, FederationSamlConfiguration wanted) =>
                !string.Equals(current.EntityId, wanted.EntityId, StringComparison.Ordinal),
            _ => true,
        };

    /// <summary>
    /// One port call: the admin secret is checked before any network effect, then the resilience pipeline (retry disabled,
    /// finite timeout, breaker, bulkhead) runs a fresh token and the operation. Transport faults surface inside as
    /// <see cref="FederationProviderException"/> so the breaker counts them, and leave as the port's unavailable status.
    /// A caller's cancellation propagates; the runner records it as uncertain.
    /// </summary>
    private async Task<T> RunAsync<T>(T unavailable, Func<string, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        if (!FederationClientSecret.TryCreate(_secrets.GetSecret(SecretKey), out var secret)) return unavailable;
        using var suppression = SuppressInstrumentationScope.Begin();
        using var ceiling = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ceiling.CancelAfter(CallCeiling);
        try
        {
            return await _pipeline.ExecuteAsync(async token =>
            {
                var bearer = await TokenAsync(secret!, token).ConfigureAwait(false);
                return await operation(bearer, token).ConfigureAwait(false);
            }, ceiling.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (FederationProviderException) { return unavailable; }
        catch (ExecutionRejectedException) { return unavailable; }
        catch (OperationCanceledException) { return unavailable; }
        catch (HttpRequestException) { return unavailable; }
        catch (IOException) { return unavailable; }
    }

    private async Task<string> TokenAsync(FederationClientSecret secret, CancellationToken token)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = KeycloakFederationRoutes.ClientId,
            ["client_secret"] = secret.Reveal(),
        });
        var response = await SendAsync(HttpMethod.Post, KeycloakFederationRoutes.Token(_options), content, null, TokenLimit, token).ConfigureAwait(false);
        if (response.Code != HttpStatusCode.OK || response.Body is null) throw Fault(isClientError: (int)response.Code is >= 400 and < 500);
        try
        {
            using var document = JsonDocument.Parse(response.Body, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("access_token", out var access)
                || access.ValueKind != JsonValueKind.String || access.GetString() is not { Length: > 0 and <= 16 * 1024 } bearer
                || !root.TryGetProperty("token_type", out var type) || type.ValueKind != JsonValueKind.String
                || !string.Equals(type.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase) || !NarrowAdminToken(bearer))
                throw Fault(isClientError: true);
            return bearer;
        }
        catch (JsonException) { throw Fault(); }
        finally { CryptographicOperations.ZeroMemory(response.Body); }
    }

    /// <summary>
    /// Metadata from the fixed token endpoint, not an incoming-user authentication: the admin API validates the bearer.
    /// These checks refuse a misprovisioned client whose token is broader than <c>manage-identity-providers</c>.
    /// </summary>
    private bool NarrowAdminToken(string access)
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
                || !root.TryGetProperty("azp", out var party) || party.GetString() != KeycloakFederationRoutes.ClientId)
                return false;
            if (root.TryGetProperty("realm_access", out var realm) && realm.ValueKind == JsonValueKind.Object
                && realm.TryGetProperty("roles", out var realmRoles) && realmRoles.ValueKind == JsonValueKind.Array && realmRoles.GetArrayLength() != 0)
                return false;
            if (!root.TryGetProperty("resource_access", out var resources) || resources.ValueKind != JsonValueKind.Object
                || resources.EnumerateObject().Count() != 1 || !resources.TryGetProperty("realm-management", out var management)
                || management.ValueKind != JsonValueKind.Object || !management.TryGetProperty("roles", out var roles)
                || roles.ValueKind != JsonValueKind.Array || roles.GetArrayLength() != 1)
                return false;
            return roles[0].ValueKind == JsonValueKind.String && roles[0].GetString() == KeycloakFederationRoutes.AdminRole;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    private async Task<OwnedBroker> ReadOwnedAsync(FederationBrokerOwner owner, string bearer, CancellationToken token)
    {
        var response = await SendAsync(HttpMethod.Get, KeycloakFederationRoutes.Instance(_options, owner), null, bearer, InstanceLimit, token)
            .ConfigureAwait(false);
        if (response.Code == HttpStatusCode.NotFound) return new OwnedBroker(Owned.Absent, null, false, default);
        if (response.Code != HttpStatusCode.OK || response.Body is null) throw Fault();
        try
        {
            using var document = JsonDocument.Parse(response.Body, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            // The marker is checked before anything else is interpreted: a foreign broker is reported, never parsed further.
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("config", out var config) || config.ValueKind != JsonValueKind.Object
                || !config.TryGetProperty(KeycloakFederationProfile.OwnerKey, out var marker) || marker.ValueKind != JsonValueKind.String
                || !string.Equals(marker.GetString(), KeycloakFederationProfile.OwnerMarker(owner), StringComparison.Ordinal))
                return new OwnedBroker(Owned.Foreign, null, false, default);
            var read = KeycloakFederationProfile.Read(root, owner);
            return read is { } owned
                ? new OwnedBroker(Owned.Present, owned.State, owned.Conforms, owned.State.Protocol)
                : new OwnedBroker(Owned.Unreadable, null, false, default);
        }
        catch (JsonException) { throw Fault(); }
        finally { CryptographicOperations.ZeroMemory(response.Body); }
    }

    /// <summary>
    /// One bounded exchange: no redirect is followed, a body is read only for the declared JSON type and never beyond its
    /// limit, and no transport or provider text leaves this method.
    /// </summary>
    private async Task<(HttpStatusCode Code, byte[]? Body)> SendAsync(HttpMethod method, Uri uri, HttpContent? content, string? bearer, int limit,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        if (limit > 0) request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        HttpResponseMessage response;
        try { response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); }
        catch (HttpRequestException) { throw Fault(); }
        using (response)
        {
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests) throw Fault();
            if (limit == 0 || !response.IsSuccessStatusCode) return (response.StatusCode, null);
            if (response.Content.Headers.ContentEncoding.Count != 0
                || !string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
                || response.Content.Headers.ContentLength > limit)
                throw Fault();
            var buffer = new byte[limit + 1];
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
                if (count > limit) throw Fault();
                return (response.StatusCode, buffer.AsSpan(0, count).ToArray());
            }
            catch (HttpRequestException) { throw Fault(); }
            catch (IOException) { throw Fault(); }
            finally { CryptographicOperations.ZeroMemory(buffer); }
        }
    }

    private static FederationProviderException Fault(bool isClientError = false) => new(isClientError);

    public void Dispose() => _client.Dispose();

    public override string ToString() => nameof(KeycloakFederationProvider);

    private enum Owned { Absent, Present, Foreign, Unreadable }

    private readonly record struct OwnedBroker(Owned Kind, FederationBrokerState? State, bool Conforms, FederationBrokerProtocol Protocol);
}

/// <summary>A closed adapter fault: no provider body, status text or transport message is carried.</summary>
internal sealed class FederationProviderException : ProviderException
{
    internal FederationProviderException(bool isClientError)
        : base("keycloak", "Federation provider call failed.", isClientError)
    {
    }
}
