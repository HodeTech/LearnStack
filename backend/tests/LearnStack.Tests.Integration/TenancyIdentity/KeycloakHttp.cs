using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>Provider-test transport with closed diagnostics, finite waits and no redirect following.</summary>
internal static class KeycloakHttp
{
    internal static readonly TimeSpan RequestBudget = TimeSpan.FromSeconds(5);
    private const int MaximumBodyBytes = 256 * 1024;

    internal static HttpClient Create(Uri baseAddress, X509Certificate2 rootCertificate, CookieContainer? cookies = null) =>
        new(CreateHandler(rootCertificate, cookies))
        { BaseAddress = baseAddress, Timeout = RequestBudget, MaxResponseContentBufferSize = MaximumBodyBytes };

    internal static SocketsHttpHandler CreateHandler(X509Certificate2 rootCertificate, CookieContainer? cookies = null)
    {
        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck,
        };
        policy.CustomTrustStore.Add(rootCertificate);
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = RequestBudget,
            UseCookies = cookies is not null,
            CookieContainer = cookies ?? new CookieContainer(),
            // The normal TLS hostname and chain checks remain active. Only this owned CA is trusted.
            SslOptions = new SslClientAuthenticationOptions { CertificateChainPolicy = policy },
        };
    }

    internal static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method,
        string path, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        try { return await client.SendAsync(request, HttpCompletionOption.ResponseContentRead); }
        catch (Exception)
        {
            // HttpRequestException can contain an authorization URL, including a code or a login-action token.
            throw new InvalidOperationException("Keycloak fixture HTTP request failed.");
        }
    }

    internal static async Task<JsonDocument> JsonAsync(HttpClient client, HttpMethod method,
        string path, HttpContent? content = null)
    {
        using var response = await SendAsync(client, method, path, content);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Keycloak fixture JSON request was refused.");
        return await ReadJsonAsync(response);
    }

    internal static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        try { return JsonDocument.Parse(await ReadBodyAsync(response)); }
        catch (JsonException) { throw new InvalidOperationException("Keycloak fixture JSON response is malformed."); }
    }

    internal static async Task<string> ReadBodyAsync(HttpResponseMessage response)
    {
        try
        {
            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length > MaximumBodyBytes)
                throw new InvalidOperationException("Keycloak fixture response exceeded its body bound.");
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (Exception)
        {
            throw new InvalidOperationException("Keycloak fixture response could not be read within its body bound.");
        }
    }

    internal static string? OAuthError(JsonElement json)
    {
        if (!json.TryGetProperty("error", out var error)) return null;
        return ClosedOAuthError(error.GetString());
    }

    internal static string? ClosedOAuthError(string? error) => error switch
    {
        "invalid_request" or "invalid_client" or "invalid_grant" or "unauthorized_client" or "access_denied" => error,
        null => null,
        _ => "other_provider_error",
    };
}
