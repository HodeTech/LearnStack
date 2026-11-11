using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using LearnStack.SharedKernel.Errors;
using OpenTelemetry;

namespace LearnStack.Infrastructure.Identity.ProviderEvents;

internal sealed partial class KeycloakIdentityEventProvider
{
    private const string ClientId = "learnstack-event-reader";

    private async Task<byte[]?> ReadAuthenticatedAsync(Owner owner, string relative, CancellationToken token)
    {
        using var suppression = SuppressInstrumentationScope.Begin();
        // Each bounded read uses fresh client credentials. Secret rotation or
        // disable is not hidden by an adapter-cached bearer token.
        var access = await ReadTokenAsync(owner, token).ConfigureAwait(false);
        const string suffix = "/realms/learnstack";
        var endpoint = _options.Issuer[..^suffix.Length] + "/admin/realms/learnstack/" + relative;
        var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        return await ReadBodyAsync(owner, request, MaximumBodyBytes, token).ConfigureAwait(false);
    }

    private async Task<string> ReadTokenAsync(Owner owner, CancellationToken token)
    {
        var secret = _secrets.GetSecret("IdentityEvents:ClientSecret");
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > 4096)
            throw new IdentityEventProviderException(IdentityEventProviderFailure.Credentials, isClientError: true);
        var request = new HttpRequestMessage(HttpMethod.Post, _options.Issuer + "/protocol/openid-connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = ClientId,
                ["client_secret"] = secret,
            }),
        };
        var bytes = await ReadBodyAsync(owner, request, 16 * 1024, token).ConfigureAwait(false);
        if (bytes is null) throw new IdentityEventProviderException(IdentityEventProviderFailure.Credentials, isClientError: true);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (!KeycloakEventNormalization.UniqueObject(root)
                || !KeycloakEventNormalization.Text(root, "access_token", out var access) || string.IsNullOrWhiteSpace(access) || access.Length > 16 * 1024
                || !KeycloakEventNormalization.Text(root, "token_type", out var type) || !string.Equals(type, "Bearer", StringComparison.OrdinalIgnoreCase)
                || !root.TryGetProperty("expires_in", out var expiration) || expiration.ValueKind != JsonValueKind.Number
                || !expiration.TryGetInt64(out var seconds) || seconds <= 0 || !ReadOnlyReaderToken(access))
                throw new IdentityEventProviderException(IdentityEventProviderFailure.Credentials, isClientError: true);
            return access;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private bool ReadOnlyReaderToken(string access)
    {
        // This is metadata from the authenticated fixed token endpoint, not an
        // incoming-user JWT authentication path. The admin endpoint validates
        // the bearer; these checks additionally refuse misprovisioned authority.
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
            return KeycloakEventNormalization.UniqueObject(root)
                && KeycloakEventNormalization.Text(root, "iss", out var issuer) && issuer == _options.Issuer
                && KeycloakEventNormalization.Text(root, "azp", out var client) && client == ClientId
                && root.TryGetProperty("resource_access", out var resources) && KeycloakEventNormalization.UniqueObject(resources)
                && resources.TryGetProperty("realm-management", out var management) && KeycloakEventNormalization.UniqueObject(management)
                && management.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array
                && roles.GetArrayLength() == 1 && roles[0].ValueKind == JsonValueKind.String && roles[0].GetString() == "view-events";
        }
        catch (JsonException) { return false; }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    private async Task<byte[]?> ReadBodyAsync(Owner owner, HttpRequestMessage request, int limit, CancellationToken token)
    {
        owner.Request = request;
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        byte[]? buffer = null;
        byte[]? result = null;
        Stream? stream = null;
        ExceptionDispatchInfo? failure = null;
        try
        {
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            owner.Response = response;
            if (!response.IsSuccessStatusCode)
                throw new IdentityEventProviderException(
                    response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        ? IdentityEventProviderFailure.Credentials : IdentityEventProviderFailure.Unavailable,
                    isClientError: (int)response.StatusCode is >= 400 and < 500);
            if (response.Content.Headers.ContentEncoding.Count != 0
                || !string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                throw new IdentityEventProviderException(IdentityEventProviderFailure.Policy, isClientError: true);
            if (response.Content.Headers.ContentLength is not { } declared || declared <= limit)
            {
                stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                owner.Stream = stream;
                buffer = new byte[limit + 1];
                var count = 0;
                while (count < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(count), token).ConfigureAwait(false);
                    if (read == 0) break;
                    count += read;
                }
                token.ThrowIfCancellationRequested();
                if (count <= limit) result = buffer.AsSpan(0, count).ToArray();
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { failure = ExceptionDispatchInfo.Capture(error); }
        finally { if (buffer is not null) CryptographicOperations.ZeroMemory(buffer); }
        try
        {
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
            owner.Stream = null;
            owner.Response?.Dispose(); owner.Response = null;
            request.Headers.Authorization = null;
            request.Dispose(); owner.Request = null;
        }
        catch (Exception cleanup) when (cleanup is not OutOfMemoryException and not StackOverflowException)
        {
            owner.Unconfirmed = true;
            failure ??= ExceptionDispatchInfo.Capture(new IdentityEventProviderException(IdentityEventProviderFailure.Unavailable));
        }
        if (failure is not null)
        {
            if (result is not null) CryptographicOperations.ZeroMemory(result);
            failure.Throw();
        }
        return result;
    }
}
