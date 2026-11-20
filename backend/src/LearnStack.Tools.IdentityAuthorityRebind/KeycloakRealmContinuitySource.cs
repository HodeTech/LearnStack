using System.Net;
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
/// The enumeration is complete or it is refused: the pages must add up to the reported count with no repeated
/// subject, the count must be the same before and after, and a second full pass must report the same set, so a realm
/// that changed while it was read is not a proof. A token that expires mid-enumeration is acquired again once per
/// request, and a realm above <see cref="MaximumUsers"/> is refused because its proof cannot finish in the tool's
/// deadline.
/// </para>
/// </remarks>
internal sealed class KeycloakRealmContinuitySource(HttpClient client, string clientId, string clientSecret)
    : INewRealmContinuitySource
{
    internal const int PageSize = 100;
    internal const int MaximumUsers = 200_000;
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
        // The step the read has reached, so a refusal names where it stopped and never what was being read.
        var step = NewRealmStep.Discovery;
        try
        {
            var origin = new Uri(issuer);
            var realm = RealmOf(origin);
            var discovery = await ReadDiscoveryAsync(origin, cancellationToken);
            if (!string.Equals(discovery.Issuer, issuer, StringComparison.Ordinal))
            {
                // The realm names another issuer: the proof refuses on it, so neither the credential nor a user list
                // is sent for or read from a realm that is not the target.
                return new NewRealmSnapshot(discovery.Issuer, 0, false, []);
            }

            step = NewRealmStep.KeySet;
            var keys = await CountKeysAsync(discovery.KeySet, cancellationToken);
            step = NewRealmStep.Token;
            var session = new AdminSession(discovery.TokenEndpoint);
            session.Token = await ObtainTokenAsync(session.TokenEndpoint, cancellationToken);

            var admin = new Uri(origin, realm.AdminPath);
            step = NewRealmStep.UserCount;
            var before = await ReadCountAsync(admin, session, cancellationToken);
            step = NewRealmStep.UserList;
            var first = await EnumerateAsync(admin, session, cancellationToken);
            var second = await EnumerateAsync(admin, session, cancellationToken);
            step = NewRealmStep.UserCount;
            var after = await ReadCountAsync(admin, session, cancellationToken);
            var complete = before == after && first.Count == before && second.Count == before
                && first.Distinct(StringComparer.Ordinal).Count() == first.Count
                && first.Order(StringComparer.Ordinal).SequenceEqual(second.Order(StringComparer.Ordinal), StringComparer.Ordinal);
            return new NewRealmSnapshot(discovery.Issuer, keys, complete, first);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A request that timed out: refused like any transport failure. The run's own deadline propagates.
            throw new NewRealmUnavailableException(step);
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException or JsonException
            or InvalidOperationException or UriFormatException or FormatException or ArgumentException
            or KeyNotFoundException)
        {
            // One fixed refusal: no address, credential or body is carried out of the transport.
            throw new NewRealmUnavailableException(step);
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

    private async Task<int> ReadCountAsync(Uri admin, AdminSession session, CancellationToken cancellationToken)
    {
        using var json = await SendAuthorizedAsync(new Uri(admin.AbsoluteUri + "/users/count"), 64, session, cancellationToken);
        return json.RootElement.ValueKind == JsonValueKind.Number && json.RootElement.TryGetInt32(out var count)
            && count is >= 0 and <= MaximumUsers
            ? count
            : throw new InvalidOperationException();
    }

    private async Task<List<string>> EnumerateAsync(Uri admin, AdminSession session, CancellationToken cancellationToken)
    {
        var subjects = new List<string>();
        for (var first = 0; first <= MaximumUsers; first += PageSize)
        {
            using var page = await SendAuthorizedAsync(
                new Uri(admin.AbsoluteUri + $"/users?first={first}&max={PageSize}&briefRepresentation=true"),
                MaximumPageBytes, session, cancellationToken);
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

    /// <summary>
    /// One administration read. A <c>401</c> means the token expired during a long enumeration, so a new one is
    /// obtained and the same read is repeated once; a second <c>401</c> is a refusal.
    /// </summary>
    private async Task<JsonDocument> SendAuthorizedAsync(
        Uri uri, int maximumBytes, AdminSession session, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            try
            {
                return await SendAsync(request, maximumBytes, cancellationToken);
            }
            catch (RealmUnauthorizedException) when (attempt == 0)
            {
                session.Token = await ObtainTokenAsync(session.TokenEndpoint, cancellationToken);
            }
        }
    }

    private sealed class AdminSession(Uri tokenEndpoint)
    {
        internal Uri TokenEndpoint { get; } = tokenEndpoint;

        internal string Token { get; set; } = string.Empty;
    }

    /// <summary>The administration API answered <c>401</c>: the credential's token is no longer accepted.</summary>
    private sealed class RealmUnauthorizedException() : IOException("Realm answer refused.");

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, int maximumBytes, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new RealmUnauthorizedException();
        }

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

        // The path of a Uri is already escaped, so the segment is used as it is and never escaped twice.
        return (name, path[..at] + "/admin/realms/" + name);
    }
}
