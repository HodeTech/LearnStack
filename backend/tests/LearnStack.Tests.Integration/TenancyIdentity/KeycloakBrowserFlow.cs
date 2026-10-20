using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace LearnStack.Tests.Integration.TenancyIdentity;

internal enum KeycloakPkce { S256, Plain, Missing }

/// <summary>Actual cookie/login-form code flow; consumes redirects without fetching the reserved BFF callback.</summary>
internal sealed partial class KeycloakBrowserFlow : IDisposable
{
    private readonly Uri _baseAddress;
    private readonly HttpClient _client;

    internal KeycloakBrowserFlow(Uri baseAddress, X509Certificate2 rootCertificate)
    {
        _baseAddress = baseAddress;
        _client = KeycloakHttp.Create(baseAddress, rootCertificate, new CookieContainer());
    }

    internal async Task<KeycloakAuthorization> AuthorizeAsync(string username, string password, string redirectUri,
        KeycloakPkce pkce = KeycloakPkce.S256, string clientId = "learnstack-web", string scope = "openid profile email")
    {
        var state = RandomValue();
        var verifier = RandomValue();
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = scope,
            ["state"] = state,
            ["nonce"] = RandomValue(),
        };
        if (pkce != KeycloakPkce.Missing)
        {
            query["code_challenge"] = pkce == KeycloakPkce.Plain ? verifier
                : Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            query["code_challenge_method"] = pkce == KeycloakPkce.Plain ? "plain" : "S256";
        }
        var path = QueryHelpers.AddQueryString("realms/learnstack/protocol/openid-connect/auth", query);
        using var initial = await KeycloakHttp.SendAsync(_client, HttpMethod.Get, path);
        var callback = Callback(initial, redirectUri, state, verifier, clientId);
        if (callback is not null) return callback;
        if (initial.StatusCode != HttpStatusCode.OK)
            return KeycloakAuthorization.Refused(initial.StatusCode);
        var action = LoginAction(await KeycloakHttp.ReadBodyAsync(initial));
        using var login = await KeycloakHttp.SendAsync(_client, HttpMethod.Post, action,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = username,
                ["password"] = password,
                ["credentialId"] = "",
            }));
        var completed = Callback(login, redirectUri, state, verifier, clientId);
        if (completed is not null) return completed;
        return KeycloakAuthorization.Refused(login.StatusCode, LoginRefusal(await KeycloakHttp.ReadBodyAsync(login)));
    }

    private static string LoginRefusal(string html)
    {
        if (html.Contains("Invalid username or password", StringComparison.OrdinalIgnoreCase)) return "invalid_credentials";
        if (html.Contains("Cookie not found", StringComparison.OrdinalIgnoreCase)) return "cookie_missing";
        if (html.Contains("kc-update-profile-form", StringComparison.Ordinal)) return "profile_action_required";
        if (html.Contains("kc-passwd-update-form", StringComparison.Ordinal)) return "password_action_required";
        if (html.Contains("kc-totp-settings-form", StringComparison.Ordinal)) return "mfa_action_required";
        return "other_login_refusal";
    }

    internal async Task<KeycloakTokenExchange> RedeemAsync(KeycloakAuthorization authorization,
        string? secret = KeycloakFixture.WebClientSecret, string? verifier = null, bool omitVerifier = false)
    {
        if (authorization.Code is null)
            throw new InvalidOperationException("Keycloak fixture cannot exchange a refused authorization.");
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = authorization.ClientId,
            ["code"] = authorization.Code,
            ["redirect_uri"] = authorization.RedirectUri,
        };
        if (secret is not null) form["client_secret"] = secret;
        if (!omitVerifier) form["code_verifier"] = verifier ?? authorization.Verifier;
        return await KeycloakTokenExchange.RequestAsync(_client, form);
    }

    internal Task<KeycloakTokenExchange> RequestTokenAsync(IReadOnlyDictionary<string, string> form) =>
        KeycloakTokenExchange.RequestAsync(_client, form);

    private string LoginAction(string html)
    {
        foreach (Match form in FormTag().Matches(html))
        {
            var attributes = Attribute().Matches(form.Value).ToDictionary(
                match => match.Groups["name"].Value, match => WebUtility.HtmlDecode(match.Groups["value"].Value),
                StringComparer.OrdinalIgnoreCase);
            if (!attributes.TryGetValue("id", out var id) || id != "kc-form-login") continue;
            if (!attributes.TryGetValue("action", out var action)
                || !Uri.TryCreate(_baseAddress, action, out var uri)
                || !SameOrigin(uri, _baseAddress)
                || uri.AbsolutePath != "/realms/learnstack/login-actions/authenticate")
                throw new InvalidOperationException("Keycloak fixture login action is outside the provider boundary.");
            return uri.AbsoluteUri;
        }
        throw new InvalidOperationException("Keycloak fixture expected the ordinary login form.");
    }

    private static KeycloakAuthorization? Callback(HttpResponseMessage response, string redirectUri,
        string state, string verifier, string clientId)
    {
        if (response.StatusCode is not (HttpStatusCode.Found or HttpStatusCode.SeeOther)) return null;
        var location = response.Headers.Location;
        var expected = new Uri(redirectUri);
        if (location is null || !location.IsAbsoluteUri || !SameOrigin(location, expected)
            || location.AbsolutePath != expected.AbsolutePath || location.Fragment.Length != 0)
            throw new InvalidOperationException("Keycloak fixture authorization returned an unexpected redirect.");
        var query = QueryHelpers.ParseQuery(location.Query);
        if (query["state"].Count != 1 || query["state"][0] != state)
            throw new InvalidOperationException("Keycloak fixture authorization state did not round-trip.");
        if (query.TryGetValue("error", out var error))
            return KeycloakAuthorization.Refused(response.StatusCode, KeycloakHttp.ClosedOAuthError(error.ToString()));
        if (query["code"].Count != 1 || string.IsNullOrEmpty(query["code"][0]))
            throw new InvalidOperationException("Keycloak fixture authorization code is missing.");
        return new KeycloakAuthorization(response.StatusCode, query["code"][0], verifier, redirectUri, clientId, null);
    }

    private static bool SameOrigin(Uri first, Uri second) => first.Scheme == second.Scheme
        && first.Host == second.Host && first.Port == second.Port;

    private static string RandomValue() => Base64Url(RandomNumberGenerator.GetBytes(32));
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [GeneratedRegex("<form\\b[^>]*>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex FormTag();

    [GeneratedRegex("(?<name>[a-zA-Z-]+)\\s*=\\s*[\"'](?<value>[^\"']*)[\"']", RegexOptions.None, 1000)]
    private static partial Regex Attribute();

    public void Dispose() => _client.Dispose();
}

/// <summary>Sensitive values deliberately have no record-generated diagnostic representation.</summary>
internal sealed class KeycloakAuthorization(HttpStatusCode statusCode, string? code, string verifier,
    string redirectUri, string clientId, string? error)
{
    internal HttpStatusCode StatusCode { get; } = statusCode;
    internal string? Code { get; } = code;
    internal string Verifier { get; } = verifier;
    internal string RedirectUri { get; } = redirectUri;
    internal string ClientId { get; } = clientId;
    internal string? Error { get; } = error;
    internal bool Authorized => Code is not null;
    internal static KeycloakAuthorization Refused(HttpStatusCode status, string? error = null) =>
        new(status, null, "", "", "", error);
    public override string ToString() => "Keycloak authorization result (sensitive values omitted)";
}
