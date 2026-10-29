using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.WebUtilities;

namespace LearnStack.Tests.Integration.TenancyIdentity;

internal enum KeycloakPkce { S256, Plain, Missing }

/// <summary>Actual cookie/login-form code flow; consumes redirects without fetching the reserved BFF callback.</summary>
internal sealed partial class KeycloakBrowserFlow : IDisposable
{
    private static readonly string[] RegistrationRequiredFields = ["email", "firstName", "lastName", "password", "password-confirm"];
    private readonly Uri _baseAddress;
    private readonly HttpClient _client;
    private readonly string _realm;

    internal int BffFormCallbacks { get; private set; }
    internal int BffSsoCallbacks { get; private set; }

    internal KeycloakBrowserFlow(Uri baseAddress, X509Certificate2 rootCertificate, string realm = "learnstack")
    {
        _baseAddress = baseAddress;
        _realm = KeycloakFixture.CheckedRealm(realm);
        _client = KeycloakHttp.Create(baseAddress, rootCertificate, new CookieContainer());
    }

    // Completes the RP-owned authorization URL without replacing its state,
    // nonce, challenge or verifier, and never redeems its code in this helper.
    internal async Task<Uri> CompleteBffLoginAsync(Uri authorization, string username, string password)
    {
        if (!SameOrigin(authorization, _baseAddress)
            || authorization.AbsolutePath != "/realms/learnstack/protocol/openid-connect/auth")
            throw new InvalidOperationException("BFF fixture authorization is outside the owned provider.");
        var query = QueryHelpers.ParseQuery(authorization.Query);
        var callbackText = query.GetValueOrDefault("redirect_uri").ToString();
        if (!Uri.TryCreate(callbackText, UriKind.Absolute, out var callback)
            || callback.Scheme != "https" || callback.Port != 3000
            || !KnownBffHost(callback.Host)
            || callback.AbsolutePath != "/api/auth/callback/keycloak" || callback.Query.Length != 0
            || callback.Fragment.Length != 0 || callback.UserInfo.Length != 0
            || query.GetValueOrDefault("client_id") != "learnstack-web"
            || query.GetValueOrDefault("code_challenge_method") != "S256"
            || query.Any(pair => pair.Value.Count != 1))
            throw new InvalidOperationException("BFF fixture authorization does not use the delivered callback/S256 contract.");
        using var page = await KeycloakHttp.SendAsync(_client, HttpMethod.Get, authorization.AbsoluteUri);
        if (page.StatusCode is HttpStatusCode.Found or HttpStatusCode.SeeOther)
        {
            var ssoCallback = ValidateCallback(page);
            BffSsoCallbacks++;
            return ssoCallback;
        }
        if (page.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException("BFF fixture expected a provider login form or owned SSO callback.");
        var action = LoginAction(await KeycloakHttp.ReadBodyAsync(page));
        using var submitted = await KeycloakHttp.SendAsync(_client, HttpMethod.Post, action,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = username,
                ["password"] = password,
                ["credentialId"] = "",
            }));
        var formCallback = ValidateCallback(submitted);
        BffFormCallbacks++;
        return formCallback;

        Uri ValidateCallback(HttpResponseMessage response)
        {
            var location = response.Headers.Location;
            if (response.StatusCode is not (HttpStatusCode.Found or HttpStatusCode.SeeOther)
                || location is null || !location.IsAbsoluteUri
                || location.GetLeftPart(UriPartial.Path) != callback.GetLeftPart(UriPartial.Path))
                throw new InvalidOperationException("BFF fixture provider login did not reach its owned callback.");
            var returned = QueryHelpers.ParseQuery(location.Query);
            if (returned.GetValueOrDefault("state") != query.GetValueOrDefault("state")
                || returned.GetValueOrDefault("code").Count != 1 || returned.ContainsKey("error")
                || location.Fragment.Length != 0 || location.UserInfo.Length != 0
                || returned.Any(pair => pair.Value.Count != 1))
                throw new InvalidOperationException("BFF fixture provider callback lacks its original state or code.");
            return location;
        }
    }

    private static bool KnownBffHost(string host) =>
        LearnStack.Tools.Seeder.SeedData.All.Any(tenant => tenant.Host == host)
        || LearnStack.Modules.Tenancy.Application.Contracts.Seeding.SeedAdministrationOriginManifest.All
            .Any(origin => origin.Host == host);

    internal async Task<Uri> CompleteBffLogoutAsync(Uri logout)
    {
        if (!SameOrigin(logout, _baseAddress)
            || logout.AbsolutePath != "/realms/learnstack/protocol/openid-connect/logout")
            throw new InvalidOperationException("BFF fixture logout is outside the owned provider.");
        var query = QueryHelpers.ParseQuery(logout.Query);
        if (!Uri.TryCreate(query.GetValueOrDefault("post_logout_redirect_uri"), UriKind.Absolute, out var target)
            || target.Scheme != "https" || target.Port != 3000 || target.AbsolutePath != "/"
            || target.Query.Length != 0 || target.Fragment.Length != 0 || target.UserInfo.Length != 0
            || query.Any(pair => pair.Value.Count != 1)
            || !KnownBffHost(target.Host))
            throw new InvalidOperationException("BFF fixture logout return is outside the delivered hosts.");
        using var response = await KeycloakHttp.SendAsync(_client, HttpMethod.Get, logout.AbsoluteUri);
        var location = response.Headers.Location;
        if (response.StatusCode is not (HttpStatusCode.Found or HttpStatusCode.SeeOther)
            || location is null || !location.IsAbsoluteUri
            || location.GetLeftPart(UriPartial.Path) != target.GetLeftPart(UriPartial.Path)
            || location.Fragment.Length != 0 || location.UserInfo.Length != 0
            || QueryHelpers.ParseQuery(location.Query).GetValueOrDefault("state") != query.GetValueOrDefault("state"))
            throw new InvalidOperationException("BFF fixture provider logout did not return its owned state.");
        return location;
    }

    internal async Task<KeycloakAuthorization> AuthorizeAsync(string username, string password, string redirectUri,
        KeycloakPkce pkce = KeycloakPkce.S256, string clientId = "learnstack-web", string scope = "openid profile email",
        Func<Task<string>>? otpCode = null)
    {
        var state = RandomValue();
        var verifier = RandomValue();
        var nonce = RandomValue();
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = scope,
            ["state"] = state,
            ["nonce"] = nonce,
        };
        if (pkce != KeycloakPkce.Missing)
        {
            query["code_challenge"] = pkce == KeycloakPkce.Plain ? verifier
                : Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            query["code_challenge_method"] = pkce == KeycloakPkce.Plain ? "plain" : "S256";
        }
        var path = QueryHelpers.AddQueryString("realms/" + _realm + "/protocol/openid-connect/auth", query);
        using var initial = await KeycloakHttp.SendAsync(_client, HttpMethod.Get, path);
        var callback = Callback(initial, redirectUri, state, verifier, clientId, nonce);
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
        var completed = Callback(login, redirectUri, state, verifier, clientId, nonce);
        if (completed is not null) return completed;
        var html = await KeycloakHttp.ReadBodyAsync(login);
        if (!html.Contains("kc-otp-login-form", StringComparison.Ordinal))
            return KeycloakAuthorization.Refused(login.StatusCode, LoginRefusal(html));
        var otpAction = OtpAction(html, out var otpFields);
        if (otpCode is null) return KeycloakAuthorization.Refused(login.StatusCode, "mfa_challenge_required", mfaChallenged: true);
        otpFields["otp"] = await otpCode();
        using var otp = await KeycloakHttp.SendAsync(_client, HttpMethod.Post, otpAction,
            new FormUrlEncodedContent(otpFields));
        var authorized = Callback(otp, redirectUri, state, verifier, clientId, nonce);
        if (authorized is not null) return authorized.WithMfaChallenge();
        var refused = await KeycloakHttp.ReadBodyAsync(otp);
        return KeycloakAuthorization.Refused(otp.StatusCode,
            refused.Contains("id=\"input-error-otp\"", StringComparison.Ordinal) ? "invalid_otp" : LoginRefusal(refused),
            mfaChallenged: true);
    }

    // The pinned registration form is exercised as delivered, including any hidden inputs.
    // Only the fixed provider registration endpoint and its same-origin action are accepted.
    internal async Task<KeycloakRegistration> RegisterAsync(string username, string password, string redirectUri,
        IReadOnlyCollection<KeyValuePair<string, string>>? injectedAttributes = null)
    {
        var state = RandomValue();
        var verifier = RandomValue();
        var nonce = RandomValue();
        var path = QueryHelpers.AddQueryString("realms/" + _realm + "/protocol/openid-connect/registrations",
            new Dictionary<string, string?>
            {
                ["client_id"] = "learnstack-web",
                ["redirect_uri"] = redirectUri,
                ["response_type"] = "code",
                ["scope"] = "openid profile email",
                ["state"] = state,
                ["nonce"] = nonce,
                ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
                ["code_challenge_method"] = "S256",
            });
        using var page = await KeycloakHttp.SendAsync(_client, HttpMethod.Get, path);
        if (page.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException("Keycloak fixture ordinary registration form was refused.");
        var html = await KeycloakHttp.ReadBodyAsync(page);
        var form = FormTag().Matches(html).Cast<Match>().SingleOrDefault(match =>
            Attributes(match.Value).GetValueOrDefault("id") == "kc-register-form")
            ?? throw new InvalidOperationException("Keycloak fixture ordinary registration form is missing.");
        var formAttributes = Attributes(form.Value);
        if (formAttributes.GetValueOrDefault("method")?.ToLowerInvariant() != "post"
            || !formAttributes.TryGetValue("action", out var action)
            || !Uri.TryCreate(_baseAddress, action, out var uri) || !SameOrigin(uri, _baseAddress)
            || uri.AbsolutePath != "/realms/" + _realm + "/login-actions/registration")
            throw new InvalidOperationException("Keycloak fixture registration action is outside the provider boundary.");
        var end = html.IndexOf("</form>", form.Index + form.Length, StringComparison.OrdinalIgnoreCase);
        if (end < 0) throw new InvalidOperationException("Keycloak fixture registration form is incomplete.");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match input in InputTag().Matches(html[form.Index..end]))
        {
            var attributes = Attributes(input.Value);
            if (!attributes.TryGetValue("name", out var name)) continue;
            names.Add(name);
            if (attributes.GetValueOrDefault("type")?.Equals("hidden", StringComparison.OrdinalIgnoreCase) == true)
                fields.Add(name, attributes.GetValueOrDefault("value") ?? "");
        }
        if (!RegistrationRequiredFields.All(names.Contains))
            throw new InvalidOperationException("Keycloak fixture registration form lacks ordinary required fields.");
        fields["email"] = username;
        fields["firstName"] = "Provider";
        fields["lastName"] = "Registration";
        fields["password"] = password;
        fields["password-confirm"] = password;
        if (names.Contains("username")) fields["username"] = username;
        var submittedFields = fields.ToList();
        if (injectedAttributes is not null) submittedFields.AddRange(injectedAttributes);
        using var submitted = await KeycloakHttp.SendAsync(_client, HttpMethod.Post, uri.AbsoluteUri,
            new FormUrlEncodedContent(submittedFields));
        var callback = Callback(submitted, redirectUri, state, verifier, "learnstack-web", nonce);
        // A refusal may redisplay the form; no raw form or error body is retained or diagnosed.
        return new KeycloakRegistration(submitted.StatusCode, callback?.Authorized == true, names);
    }

    private static Dictionary<string, string> Attributes(string tag) => Attribute().Matches(tag).ToDictionary(
        match => match.Groups["name"].Value, match => WebUtility.HtmlDecode(match.Groups["value"].Value),
        StringComparer.OrdinalIgnoreCase);

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
        var exchange = await KeycloakTokenExchange.RequestAsync(_client, form, _realm);
        if (exchange.StatusCode == HttpStatusCode.OK)
        {
            try
            {
                using var id = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(exchange.IdToken.Split('.')[1]));
                if (!id.RootElement.TryGetProperty("nonce", out var nonce) || nonce.ValueKind != JsonValueKind.String
                    || nonce.GetString() != authorization.Nonce) throw new InvalidOperationException();
            }
            catch (Exception) { throw new InvalidOperationException("Keycloak fixture ID token nonce did not round-trip."); }
        }
        return exchange;
    }

    internal Task<KeycloakTokenExchange> RequestTokenAsync(IReadOnlyDictionary<string, string> form) =>
        KeycloakTokenExchange.RequestAsync(_client, form, _realm);

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
                || uri.AbsolutePath != "/realms/" + _realm + "/login-actions/authenticate")
                throw new InvalidOperationException("Keycloak fixture login action is outside the provider boundary.");
            return uri.AbsoluteUri;
        }
        throw new InvalidOperationException("Keycloak fixture expected the ordinary login form.");
    }

    private string OtpAction(string html, out Dictionary<string, string> fields)
    {
        var form = FormTag().Matches(html).Cast<Match>().SingleOrDefault(match =>
            Attributes(match.Value).GetValueOrDefault("id") == "kc-otp-login-form")
            ?? throw new InvalidOperationException("Keycloak fixture expected the pinned OTP challenge.");
        var attributes = Attributes(form.Value);
        var end = html.IndexOf("</form>", form.Index + form.Length, StringComparison.OrdinalIgnoreCase);
        if (attributes.GetValueOrDefault("method")?.ToLowerInvariant() != "post"
            || !attributes.TryGetValue("action", out var action)
            || !Uri.TryCreate(_baseAddress, action, out var uri) || !SameOrigin(uri, _baseAddress)
            || uri.AbsolutePath != "/realms/" + _realm + "/login-actions/authenticate" || end < 0)
            throw new InvalidOperationException("Keycloak fixture OTP action is outside the provider boundary.");
        var inputs = InputTag().Matches(html[form.Index..end]).Cast<Match>().Select(match => Attributes(match.Value)).ToArray();
        var hidden = inputs.Where(input => input.GetValueOrDefault("type")?.Equals("hidden", StringComparison.OrdinalIgnoreCase) == true).ToArray();
        if (inputs.Count(input => input.GetValueOrDefault("name") == "otp") != 1
            || hidden.Length != 1 || hidden[0].GetValueOrDefault("name") != "selectedCredentialId"
            || !Guid.TryParse(hidden[0].GetValueOrDefault("value"), out _)
            || html[form.Index..end].Contains("id=\"kc-otp-credential-", StringComparison.Ordinal))
            throw new InvalidOperationException("Keycloak fixture OTP form differs from the pinned single-credential form.");
        fields = new Dictionary<string, string> { ["selectedCredentialId"] = hidden[0]["value"] };
        return uri.AbsoluteUri;
    }

    private KeycloakAuthorization? Callback(HttpResponseMessage response, string redirectUri,
        string state, string verifier, string clientId, string nonce)
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
        if (query["iss"].Count != 1 || query["iss"][0] != new Uri(_baseAddress, "realms/" + _realm).AbsoluteUri)
            throw new InvalidOperationException("Keycloak fixture authorization issuer did not round-trip.");
        if (query.TryGetValue("error", out var error))
            return KeycloakAuthorization.Refused(response.StatusCode, KeycloakHttp.ClosedOAuthError(error.ToString()));
        if (query["code"].Count != 1 || string.IsNullOrEmpty(query["code"][0]))
            throw new InvalidOperationException("Keycloak fixture authorization code is missing.");
        return new KeycloakAuthorization(response.StatusCode, query["code"][0], verifier, redirectUri, clientId, null, nonce);
    }

    private static bool SameOrigin(Uri first, Uri second) => first.Scheme == second.Scheme
        && first.Host == second.Host && first.Port == second.Port;

    private static string RandomValue() => Base64Url(RandomNumberGenerator.GetBytes(32));
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [GeneratedRegex("<form\\b[^>]*>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex FormTag();

    [GeneratedRegex("<input\\b[^>]*>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex InputTag();

    [GeneratedRegex("(?<name>[a-zA-Z-]+)\\s*=\\s*[\"'](?<value>[^\"']*)[\"']", RegexOptions.None, 1000)]
    private static partial Regex Attribute();

    public void Dispose() => _client.Dispose();
}

/// <summary>Sensitive values deliberately have no record-generated diagnostic representation.</summary>
internal sealed class KeycloakAuthorization(HttpStatusCode statusCode, string? code, string verifier,
    string redirectUri, string clientId, string? error, string nonce = "", bool mfaChallenged = false)
{
    internal HttpStatusCode StatusCode { get; } = statusCode;
    internal string? Code { get; } = code;
    internal string Verifier { get; } = verifier;
    internal string RedirectUri { get; } = redirectUri;
    internal string ClientId { get; } = clientId;
    internal string? Error { get; } = error;
    internal string Nonce { get; } = nonce;
    internal bool Authorized => Code is not null;
    internal bool MfaChallenged { get; } = mfaChallenged;
    internal KeycloakAuthorization WithMfaChallenge() => new(StatusCode, Code, Verifier, RedirectUri, ClientId, Error, Nonce, true);
    internal static KeycloakAuthorization Refused(HttpStatusCode status, string? error = null, bool mfaChallenged = false) =>
        new(status, null, "", "", "", error, mfaChallenged: mfaChallenged);
    public override string ToString() => "Keycloak authorization result (sensitive values omitted)";
}

/// <summary>Only closed registration facts and non-sensitive form field names leave the browser helper.</summary>
internal sealed class KeycloakRegistration(HttpStatusCode statusCode, bool authorized, IReadOnlySet<string> fields)
{
    internal HttpStatusCode StatusCode { get; } = statusCode;
    internal bool Authorized { get; } = authorized;
    internal IReadOnlySet<string> Fields { get; } = fields;
    public override string ToString() => "Keycloak registration result (sensitive values omitted)";
}
