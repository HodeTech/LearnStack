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

    internal KeycloakAdminClient(Uri baseAddress, X509Certificate2 rootCertificate, string realm, string accessToken)
    {
        _client = KeycloakHttp.Create(new Uri(baseAddress, "admin/realms/" + Uri.EscapeDataString(realm) + "/"), rootCertificate);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    internal async Task<JsonElement> GetAsync(string path = "")
    {
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
        using var response = await KeycloakHttp.SendAsync(_client, method, path,
            value is null ? null : JsonContent.Create(value));
        return response.StatusCode;
    }

    public void Dispose() => _client.Dispose();
}
