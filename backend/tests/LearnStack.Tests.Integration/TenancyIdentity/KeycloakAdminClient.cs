using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>Admin-only setup/readback for the isolated provider; responses are never used as diagnostics.</summary>
internal sealed class KeycloakAdminClient : IDisposable
{
    private readonly HttpClient _client;
    private readonly Func<CancellationToken, Task<KeycloakAdminCredential>> _authenticate;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _authentication = new(1, 1);
    private KeycloakAdminCredential? _credential;
    private long _authenticatedAt;

    internal KeycloakAdminClient(Uri baseAddress, X509Certificate2 rootCertificate, string realm,
        Func<CancellationToken, Task<KeycloakAdminCredential>> authenticate)
        : this(KeycloakHttp.Create(new Uri(baseAddress, "admin/realms/" + Uri.EscapeDataString(realm) + "/"),
            rootCertificate), authenticate, TimeProvider.System)
    { }

    internal KeycloakAdminClient(HttpClient client, Func<CancellationToken, Task<KeycloakAdminCredential>> authenticate,
        TimeProvider clock) => (_client, _authenticate, _clock) = (client, authenticate, clock);

    internal async Task AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        await _authentication.WaitAsync(cancellationToken);
        try
        {
            if (_credential is not null
                && _clock.GetElapsedTime(_authenticatedAt) < _credential.Lifetime - KeycloakHttp.RequestBudget) return;
            var started = _clock.GetTimestamp();
            var credential = await _authenticate(cancellationToken);
            if (string.IsNullOrEmpty(credential.AccessToken) || credential.Lifetime <= KeycloakHttp.RequestBudget)
                throw new InvalidOperationException("Keycloak fixture administrator credential is invalid.");
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
            _authenticatedAt = started;
            _credential = credential;
        }
        finally { _authentication.Release(); }
    }

    internal async Task<JsonElement> GetAsync(string path = "")
    {
        await AuthenticateAsync();
        using var json = await KeycloakHttp.JsonAsync(_client, HttpMethod.Get, path);
        return json.RootElement.Clone();
    }

    internal async Task<JsonElement> UserAsync(string username)
    {
        var users = await GetAsync(QueryHelpers.AddQueryString("users", new Dictionary<string, string?>
        {
            ["username"] = username,
            ["exact"] = "true",
        }));
        if (users.GetArrayLength() != 1)
            throw new InvalidOperationException("Keycloak fixture expected exactly one matching account.");
        return users[0];
    }

    internal async Task<JsonElement> ClientAsync(string clientId)
    {
        var clients = await GetAsync(QueryHelpers.AddQueryString("clients", "clientId", clientId));
        if (clients.GetArrayLength() != 1)
            throw new InvalidOperationException("Keycloak fixture expected exactly one matching client.");
        return clients[0];
    }

    internal Task<HttpStatusCode> PutAsync(string path, object value) => WriteAsync(HttpMethod.Put, path, value);
    internal Task<HttpStatusCode> PostAsync(string path, object value) => WriteAsync(HttpMethod.Post, path, value);
    internal Task<HttpStatusCode> DeleteAsync(string path) => WriteAsync(HttpMethod.Delete, path, null);

    private async Task<HttpStatusCode> WriteAsync(HttpMethod method, string path, object? value)
    {
        // Refresh before sending; a refused or uncertain mutation is never replayed.
        await AuthenticateAsync();
        using var response = await KeycloakHttp.SendAsync(_client, method, path,
            value is null ? null : JsonContent.Create(value));
        return response.StatusCode;
    }

    public void Dispose() { _client.Dispose(); _authentication.Dispose(); }
}

/// <summary>In-memory provider credential with no token-bearing generated diagnostic representation.</summary>
internal sealed class KeycloakAdminCredential(string accessToken, TimeSpan lifetime)
{
    internal string AccessToken { get; } = accessToken;
    internal TimeSpan Lifetime { get; } = lifetime;
    public override string ToString() => "Keycloak administrator credential (sensitive value omitted)";
}
