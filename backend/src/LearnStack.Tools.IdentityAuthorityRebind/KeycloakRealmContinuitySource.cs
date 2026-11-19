using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LearnStack.Infrastructure.Identity;
using LearnStack.Modules.Identity.Infrastructure.Bindings;

namespace LearnStack.Tools.IdentityAuthorityRebind;

/// <summary>
/// Reads the new realm for the continuity proof: its discovery document, its key set and every user it holds, with a
/// read-only <c>view-users</c> credential the operator supplies at run time (ADR-0104 § 4, § 6).
/// </summary>
/// <remarks>
/// <para>
/// This is not a LearnStack runtime or federation credential, and it lives only in the tool's environment. Every
/// address the discovery document names must be at the issuer's own origin, every body is bounded, no redirect is
/// followed, and a refusal is one fixed exception that names no address, credential or body.
/// </para>
/// <para>
/// The enumeration is complete or it is refused: the pages must add up to the reported count, and the count must be
/// the same before and after the pages were read, so a realm that changed while it was read is not a proof.
/// </para>
/// </remarks>
internal sealed class KeycloakRealmContinuitySource(HttpClient client, string clientId, string clientSecret)
    : INewRealmContinuitySource
{
    internal const int PageSize = 100;
    internal const int MaximumUsers = 2_000_000;
    private const int MaximumDocumentBytes = 256 * 1024;
    private const int MaximumPageBytes = 4 * 1024 * 1024;
    private const int MaximumKeys = 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>The bounded client of the tool: no redirects, cookies or proxy, a replaced certificate check refused.</summary>
    internal static HttpClient CreateClient(SocketsHttpHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler();
        if (handler.SslOptions.RemoteCertificateValidationCallback is not null)
        {
            throw new InvalidOperationException("Realm certificate validation cannot be replaced.");
        }

        handler.AllowAutoRedirect = false;
        handler.UseCookies = false;
        handler.UseProxy = false;
        handler.ConnectTimeout = TimeSpan.FromSeconds(5);
        handler.MaxConnectionsPerServer = 2;
        return new HttpClient(handler) { Timeout = Timeout, MaxResponseContentBufferSize = MaximumPageBytes };
    }

    public async Task<NewRealmSnapshot> ReadAsync(string issuer, CancellationToken cancellationToken = default)
    {
        try
        {
            var origin = new Uri(issuer);
            var realm = RealmOf(origin);
            var discovery = await ReadDiscoveryAsync(origin, cancellationToken);
            var keys = await CountKeysAsync(discovery.KeySet, cancellationToken);
            var token = await ObtainTokenAsync(discovery.TokenEndpoint, cancellationToken);

            var admin = new Uri(origin, realm.AdminPath);
            var before = await ReadCountAsync(admin, token, cancellationToken);
            var subjects = await EnumerateAsync(admin, token, cancellationToken);
            var after = await ReadCountAsync(admin, token, cancellationToken);
            var complete = before == after && subjects.Count == before;
            return new NewRealmSnapshot(discovery.Issuer, keys, complete, subjects);
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException or JsonException
            or InvalidOperationException or UriFormatException or TaskCanceledException or FormatException or ArgumentException
            or KeyNotFoundException)
        {
            // One fixed refusal: no address, credential or body is carried out of the transport.
            throw new NewRealmUnavailableException();
        }
    }

    private async Task<(string Issuer, Uri KeySet, Uri TokenEndpoint)> ReadDiscoveryAsync(Uri issuer, CancellationToken cancellationToken)
    {
        var document = new Uri(issuer.AbsoluteUri + "/.well-known/openid-configuration");
        var body = await BoundedMetadataReader.ReadAsync(
            client, document, Timeout, MaximumDocumentBytes, "Realm discovery refused.", cancellationToken);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var discovered = root.GetProperty("issuer").GetString() ?? throw new InvalidOperationException();
        return (discovered, SameOrigin(issuer, root.GetProperty("jwks_uri").GetString()),
            SameOrigin(issuer, root.GetProperty("token_endpoint").GetString()));
    }

    private async Task<int> CountKeysAsync(Uri keySet, CancellationToken cancellationToken)
    {
        var body = await BoundedMetadataReader.ReadAsync(
            client, keySet, Timeout, MaximumDocumentBytes, "Realm key set refused.", cancellationToken);
        using var json = JsonDocument.Parse(body);
        var keys = json.RootElement.GetProperty("keys");
        if (keys.GetArrayLength() > MaximumKeys)
        {
            throw new InvalidOperationException();
        }

        return keys.EnumerateArray().Count(key => key.TryGetProperty("kty", out var kty) && kty.ValueKind == JsonValueKind.String
            && !(key.TryGetProperty("use", out var use) && use.GetString() == "enc"));
    }

    private async Task<string> ObtainTokenAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var json = await SendAsync(request, MaximumDocumentBytes, cancellationToken);
        var token = json.RootElement.GetProperty("access_token").GetString();
        return string.IsNullOrEmpty(token) ? throw new InvalidOperationException() : token;
    }

    private async Task<int> ReadCountAsync(Uri admin, string token, CancellationToken cancellationToken)
    {
        using var request = Authorized(new Uri(admin.AbsoluteUri + "/users/count"), token);
        using var json = await SendAsync(request, 64, cancellationToken);
        return json.RootElement.ValueKind == JsonValueKind.Number && json.RootElement.TryGetInt32(out var count)
            && count is >= 0 and <= MaximumUsers
            ? count
            : throw new InvalidOperationException();
    }

    private async Task<List<string>> EnumerateAsync(Uri admin, string token, CancellationToken cancellationToken)
    {
        var subjects = new List<string>();
        for (var first = 0; first <= MaximumUsers; first += PageSize)
        {
            using var request = Authorized(
                new Uri(admin.AbsoluteUri + $"/users?first={first}&max={PageSize}&briefRepresentation=true"), token);
            using var page = await SendAsync(request, MaximumPageBytes, cancellationToken);
            if (page.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException();
            }

            var count = 0;
            foreach (var user in page.RootElement.EnumerateArray())
            {
                // Keycloak's user id is the OpenID Connect subject its tokens carry.
                subjects.Add(user.GetProperty("id").GetString() ?? throw new InvalidOperationException());
                count++;
            }

            if (count < PageSize)
            {
                return subjects;
            }
        }

        throw new InvalidOperationException();
    }

    private static HttpRequestMessage Authorized(Uri uri, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, int maximumBytes, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > maximumBytes)
        {
            throw new IOException("Realm answer refused.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, deadline.Token)) > 0)
        {
            if (body.Length + read > maximumBytes)
            {
                throw new IOException("Realm answer refused.");
            }

            body.Write(buffer, 0, read);
        }

        return JsonDocument.Parse(Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length),
            new JsonDocumentOptions { MaxDepth = 16 });
    }

    private static Uri SameOrigin(Uri issuer, string? address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)
            || !string.Equals(uri.GetLeftPart(UriPartial.Authority), issuer.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal))
        {
            throw new InvalidOperationException();
        }

        return uri;
    }

    /// <summary>The realm the issuer names and where its administration API lives: the issuer path without <c>/realms/&lt;name&gt;</c>.</summary>
    private static (string Name, string AdminPath) RealmOf(Uri issuer)
    {
        const string Marker = "/realms/";
        var path = issuer.AbsolutePath;
        var at = path.LastIndexOf(Marker, StringComparison.Ordinal);
        var name = at < 0 ? string.Empty : path[(at + Marker.Length)..];
        if (name.Length == 0 || name.Contains('/'))
        {
            throw new InvalidOperationException();
        }

        return (name, path[..at] + "/admin/realms/" + Uri.EscapeDataString(name));
    }
}
