using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace LearnStack.Tests.Integration.TenancyIdentity;

internal sealed partial class KeycloakBrowserFlow
{
    internal async Task ChangeProviderPasswordAsync(string username, string password, string replacement, string callback)
    {
        var form = await RequiredActionAsync("UPDATE_PASSWORD", username, password, callback, "kc-passwd-update-form");
        form.Fields["password-new"] = replacement; form.Fields["password-confirm"] = replacement;
        using var submitted = await KeycloakHttp.SendAsync(_client, HttpMethod.Post, form.Action, new FormUrlEncodedContent(form.Fields));
        RequireActionCompletion(submitted, callback);
    }

    internal async Task ConfigureProviderTotpAsync(string username, string password, string callback)
    {
        var form = await RequiredActionAsync("CONFIGURE_TOTP", username, password, callback, "kc-totp-settings-form");
        if (!form.Fields.TryGetValue("totpSecret", out var secret)) throw new InvalidOperationException("Owned provider OTP form has no secret.");
        form.Fields["totp"] = Totp(secret); form.Fields["userLabel"] = "Owned provider event proof";
        using var submitted = await KeycloakHttp.SendAsync(_client, HttpMethod.Post, form.Action, new FormUrlEncodedContent(form.Fields));
        RequireActionCompletion(submitted, callback);
    }

    internal async Task RequestProviderResetAsync(string username, string callback)
    {
        using var page = await KeycloakHttp.SendAsync(_client, HttpMethod.Get, ProviderAuthorization(callback));
        if (page.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException("Owned reset flow requires a fresh login form.");
        var html = await KeycloakHttp.ReadBodyAsync(page);
        var links = Regex.Matches(html, "<a\\b[^>]*>", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))
            .Select(match => Attributes(match.Value).GetValueOrDefault("href")).Where(value => value is not null)
            .Select(value => new Uri(_baseAddress, value)).Where(uri => SameOrigin(uri, _baseAddress)
                && uri.AbsolutePath == "/realms/learnstack/login-actions/reset-credentials").ToArray();
        if (links.Length != 1) throw new InvalidOperationException("Owned reset link is missing or ambiguous.");
        using var reset = await KeycloakHttp.SendAsync(_client, HttpMethod.Get, links[0].AbsoluteUri);
        var form = ReadProviderForm(await KeycloakHttp.ReadBodyAsync(reset), "kc-reset-password-form", "reset-credentials");
        form.Fields["username"] = username;
        using var requested = await KeycloakHttp.SendAsync(_client, HttpMethod.Post, form.Action, new FormUrlEncodedContent(form.Fields));
        if (requested.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Found or HttpStatusCode.SeeOther))
            throw new InvalidOperationException("Owned password reset request was refused.");
    }

    internal async Task CompleteProviderResetAsync(string link, string replacement)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || !SameOrigin(uri, _baseAddress)
            || uri.AbsolutePath != "/realms/learnstack/login-actions/action-token")
            throw new InvalidOperationException("Owned reset action token is outside the provider.");
        var html = await FollowProviderPageAsync(uri.AbsoluteUri);
        var form = ReadProviderForm(html, "kc-passwd-update-form", "required-action", "reset-credentials");
        form.Fields["password-new"] = replacement; form.Fields["password-confirm"] = replacement;
        using var submitted = await KeycloakHttp.SendAsync(_client, HttpMethod.Post, form.Action, new FormUrlEncodedContent(form.Fields));
        if (submitted.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Found or HttpStatusCode.SeeOther))
            throw new InvalidOperationException("Owned password reset completion was refused.");
    }

    private async Task<ProviderActionForm> RequiredActionAsync(string action, string username, string password, string callback, string formId)
    {
        if (action is not ("UPDATE_PASSWORD" or "CONFIGURE_TOTP")) throw new InvalidOperationException("Owned provider action is not declared.");
        var html = await FollowProviderPageAsync(ProviderAuthorization(callback, action));
        if (FormTag().Matches(html).Any(form => Attributes(form.Value).GetValueOrDefault("id") == "kc-form-login"))
        {
            using var login = await KeycloakHttp.SendAsync(_client, HttpMethod.Post, LoginAction(html),
                new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = username, ["password"] = password, ["credentialId"] = "" }));
            html = login.StatusCode is HttpStatusCode.Found or HttpStatusCode.SeeOther
                ? await FollowProviderPageAsync(RequireProviderRedirect(login)) : await KeycloakHttp.ReadBodyAsync(login);
        }
        return ReadProviderForm(html, formId, "required-action");
    }

    private static string ProviderAuthorization(string callback, string? action = null)
    {
        if (!Uri.TryCreate(callback, UriKind.Absolute, out var target) || target.Scheme != "https" || target.Port != 3000
            || !KnownBffHost(target.Host) || target.AbsolutePath != "/api/auth/callback/keycloak")
            throw new InvalidOperationException("Owned action callback is outside the fixture.");
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = "learnstack-web",
            ["redirect_uri"] = callback,
            ["response_type"] = "code",
            ["scope"] = "openid email",
            ["state"] = RandomValue(),
            ["nonce"] = RandomValue(),
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(RandomValue()))),
            ["code_challenge_method"] = "S256"
        };
        if (action is not null) query["kc_action"] = action;
        return QueryHelpers.AddQueryString("realms/learnstack/protocol/openid-connect/auth", query);
    }

    private async Task<string> FollowProviderPageAsync(string path)
    {
        for (var count = 0; count < 8; count++)
        {
            using var page = await KeycloakHttp.SendAsync(_client, HttpMethod.Get, path);
            if (page.StatusCode == HttpStatusCode.OK) return await KeycloakHttp.ReadBodyAsync(page);
            path = RequireProviderRedirect(page);
        }
        throw new InvalidOperationException("Owned provider action exceeded its redirect bound.");
    }

    private string RequireProviderRedirect(HttpResponseMessage page)
    {
        if (page.StatusCode is not (HttpStatusCode.Found or HttpStatusCode.SeeOther) || page.Headers.Location is not { } location
            || !Uri.TryCreate(_baseAddress, location, out var target) || !SameOrigin(target, _baseAddress)
            || !target.AbsolutePath.StartsWith("/realms/learnstack/", StringComparison.Ordinal))
            throw new InvalidOperationException("Owned provider action redirect escaped its source.");
        return target.AbsoluteUri;
    }

    private ProviderActionForm ReadProviderForm(string html, string id, params string[] paths)
    {
        var matches = FormTag().Matches(html).Where(form => Attributes(form.Value).GetValueOrDefault("id") == id).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("Owned provider action form is missing or ambiguous.");
        var form = matches[0]; var attributes = Attributes(form.Value);
        if (!attributes.TryGetValue("action", out var action) || !Uri.TryCreate(_baseAddress, action, out var uri)
            || !SameOrigin(uri, _baseAddress) || !paths.Any(path => uri.AbsolutePath == "/realms/learnstack/login-actions/" + path))
            throw new InvalidOperationException("Owned provider action form escaped its source.");
        var end = html.IndexOf("</form>", form.Index + form.Length, StringComparison.OrdinalIgnoreCase);
        if (end < 0) throw new InvalidOperationException("Owned provider action form is incomplete.");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match input in InputTag().Matches(html[form.Index..end]))
        {
            var item = Attributes(input.Value);
            if (item.TryGetValue("name", out var name) && item.GetValueOrDefault("type")?.Equals("hidden", StringComparison.OrdinalIgnoreCase) == true)
                fields.Add(name, item.GetValueOrDefault("value") ?? "");
        }
        return new(uri.AbsoluteUri, fields);
    }

    private static void RequireActionCompletion(HttpResponseMessage response, string callback)
    {
        if (response.StatusCode is not (HttpStatusCode.Found or HttpStatusCode.SeeOther) || response.Headers.Location is not { IsAbsoluteUri: true } location
            || location.GetLeftPart(UriPartial.Path) != callback || QueryHelpers.ParseQuery(location.Query).GetValueOrDefault("kc_action_status") != "success")
            throw new InvalidOperationException("Owned provider action did not complete its actual callback.");
    }

    private static string Totp(string secret)
    {
        if (secret.Length is < 16 or > 128 || secret.Any(char.IsControl)) throw new InvalidOperationException("Owned OTP secret has an invalid shape.");
        // Pinned TotpBean puts the raw secret in this hidden field; its displayed
        // totpSecretEncoded/QR value is a separate base32 representation.
        Span<byte> counter = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(counter, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        var key = Encoding.UTF8.GetBytes(secret);
#pragma warning disable CA5350 // Pinned Keycloak's stock TOTP protocol; invocation-owned test credential only.
        var hash = HMACSHA1.HashData(key, counter);
#pragma warning restore CA5350
        CryptographicOperations.ZeroMemory(key);
        var offset = hash[^1] & 15; var value = BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset, 4)) & int.MaxValue;
        CryptographicOperations.ZeroMemory(hash);
        return (value % 1000000).ToString("D6", CultureInfo.InvariantCulture);
    }

    private sealed class ProviderActionForm(string action, Dictionary<string, string> fields)
    {
        internal string Action { get; } = action;
        internal Dictionary<string, string> Fields { get; } = fields;
        public override string ToString() => "Owned provider action (sensitive fields omitted)";
    }
}
