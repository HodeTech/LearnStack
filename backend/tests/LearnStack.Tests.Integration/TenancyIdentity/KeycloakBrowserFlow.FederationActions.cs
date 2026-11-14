using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>Where an application-initiated action ended; no page text, code or credential is retained.</summary>
internal sealed record ApplicationActionResult(bool ReachedIdentityProvider, bool ReachedCallback, string? ActionStatus)
{
    public override string ToString() => "Application-initiated action result (sensitive values omitted)";
}

/// <summary>Where a brokered reset completion ended: back at the tenant client, or on a provider page.</summary>
internal enum BrokeredResetEnd
{
    /// <summary>The tenant client received its callback: the pending brokered flow resumed.</summary>
    ReturnedToClient,
    /// <summary>The password was changed and the provider showed a page of its own (no callback).</summary>
    ProviderPage,
}

/// <summary>
/// ADR-0099 Step 3 review round 1 helpers: an application-initiated action that is followed hop by hop, the stock
/// update-profile action as the surviving self-service profile write, and a reset started from the brokered
/// re-authentication form and completed in a chosen browser. Every hop stays on the owned provider origin.
/// </summary>
internal sealed partial class KeycloakBrowserFlow
{
    /// <summary>
    /// Sends <c>kc_action</c> as an authorization parameter, signs in, then follows provider redirects. It stops at the
    /// tenant callback, at a test IdP page (the action redirected to a broker) or at any other provider page.
    /// </summary>
    internal async Task<ApplicationActionResult> ApplicationActionAsync(string username, string password, string callback, string action)
    {
        var path = ProviderAuthorization(callback, action);
        var response = await KeycloakHttp.SendAsync(_client, HttpMethod.Get, path);
        bool signedIn = false, confirmed = false;
        try
        {
            for (var hop = 0; hop < MaximumHops; hop++)
            {
                if (response.StatusCode is HttpStatusCode.Found or HttpStatusCode.SeeOther)
                {
                    var location = response.Headers.Location ?? throw new InvalidOperationException("Action redirect has no target.");
                    var target = location.IsAbsoluteUri ? location : new Uri(_baseAddress, location);
                    if (target.GetLeftPart(UriPartial.Path) == callback)
                        return new(false, true, QueryHelpers.ParseQuery(target.Query).GetValueOrDefault("kc_action_status").ToString() is { Length: > 0 } status
                            ? status : null);
                    if (!SameOrigin(target, _baseAddress)) throw new InvalidOperationException("Action flow left the owned provider origin.");
                    if (target.AbsolutePath.StartsWith("/realms/federation-idp-", StringComparison.Ordinal)) return new(true, false, null);
                    response.Dispose();
                    response = await KeycloakHttp.SendAsync(_client, HttpMethod.Get, target.PathAndQuery);
                    continue;
                }
                var html = await KeycloakHttp.ReadBodyAsync(response);
                var form = Form(html, out var fields, out var formAction);
                if (form == "kc-form-login" && !signedIn
                    && formAction.AbsolutePath.StartsWith("/realms/" + _realm + "/login-actions/", StringComparison.Ordinal))
                {
                    signedIn = true;
                    fields["username"] = username;
                    fields["password"] = password;
                    response.Dispose();
                    response = await KeycloakHttp.SendAsync(_client, HttpMethod.Post, formAction.PathAndQuery, new FormUrlEncodedContent(fields));
                    continue;
                }
                if (!confirmed && html.Contains("id=\"kc-link-text\"", StringComparison.Ordinal)
                    && formAction.AbsolutePath.StartsWith("/realms/" + _realm + "/login-actions/", StringComparison.Ordinal))
                {
                    // The stock idp_link confirmation page: continuing redirects to the broker.
                    confirmed = true;
                    fields["continue"] = "";
                    response.Dispose();
                    response = await KeycloakHttp.SendAsync(_client, HttpMethod.Post, formAction.PathAndQuery, new FormUrlEncodedContent(fields));
                    continue;
                }
                return new(false, false, null);
            }
            throw new InvalidOperationException("Action flow exceeded its hop bound.");
        }
        finally { response.Dispose(); }
    }

    /// <summary>
    /// The stock update-profile action (application-initiated): the form's own hidden and prefilled fields are submitted
    /// with the new first name and any injected pairs. True only when the provider completed the action at the callback.
    /// </summary>
    internal async Task<bool> UpdateProviderProfileAsync(string username, string password, string callback, string firstName,
        IReadOnlyCollection<KeyValuePair<string, string>>? injected = null)
    {
        var html = await FollowProviderPageAsync(ProviderAuthorization(callback, "UPDATE_PROFILE"));
        if (FormTag().Matches(html).Any(form => Attributes(form.Value).GetValueOrDefault("id") == "kc-form-login"))
        {
            using var login = await KeycloakHttp.SendAsync(_client, HttpMethod.Post, LoginAction(html),
                new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = username, ["password"] = password, ["credentialId"] = "" }));
            html = login.StatusCode is HttpStatusCode.Found or HttpStatusCode.SeeOther
                ? await FollowProviderPageAsync(RequireProviderRedirect(login)) : await KeycloakHttp.ReadBodyAsync(login);
        }
        if (Form(html, out var fields, out var action) != "kc-update-profile-form"
            || action.AbsolutePath != "/realms/" + _realm + "/login-actions/required-action")
            throw new InvalidOperationException("Owned update-profile form is missing or outside its source.");
        fields["firstName"] = firstName;
        // An injected name replaces the form's own field of that name, if the form shows one.
        foreach (var name in injected?.Select(pair => pair.Key).Distinct(StringComparer.Ordinal) ?? []) fields.Remove(name);
        var submitted = fields.ToList();
        if (injected is not null) submitted.AddRange(injected);
        using var response = await KeycloakHttp.SendAsync(_client, HttpMethod.Post, action.PathAndQuery, new FormUrlEncodedContent(submitted));
        return response.StatusCode is HttpStatusCode.Found or HttpStatusCode.SeeOther && response.Headers.Location is { IsAbsoluteUri: true } location
            && location.GetLeftPart(UriPartial.Path) == callback
            && QueryHelpers.ParseQuery(location.Query).GetValueOrDefault("kc_action_status") == "success";
    }

    /// <summary>
    /// The "forgot password" link on the brokered re-authentication form: the reset request is submitted for
    /// <paramref name="resetUsername"/> from inside the pending first-broker session.
    /// </summary>
    internal async Task<bool> RequestResetFromReauthenticationAsync(string alias, string idpUsername, string idpPassword, string resetUsername,
        string redirectUri)
    {
        var page = await ReauthenticationPageAsync(alias, idpUsername, idpPassword, redirectUri);
        if (page is null) return false;
        var links = Regex.Matches(page, "<a\\b[^>]*>", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))
            .Select(match => Attributes(match.Value).GetValueOrDefault("href")).Where(value => value is not null)
            .Select(value => new Uri(_baseAddress, value)).Where(uri => SameOrigin(uri, _baseAddress)
                && uri.AbsolutePath == "/realms/" + _realm + "/login-actions/reset-credentials").ToArray();
        if (links.Length != 1) return false;
        using var reset = await KeycloakHttp.SendAsync(_client, HttpMethod.Get, links[0].PathAndQuery);
        var form = ReadProviderForm(await KeycloakHttp.ReadBodyAsync(reset), "kc-reset-password-form", "reset-credentials");
        form.Fields["username"] = resetUsername;
        using var requested = await KeycloakHttp.SendAsync(_client, HttpMethod.Post, form.Action, new FormUrlEncodedContent(form.Fields));
        return requested.StatusCode is HttpStatusCode.OK or HttpStatusCode.Found or HttpStatusCode.SeeOther;
    }

    /// <summary>
    /// Opens the emailed reset action token in this browser, sets <paramref name="replacement"/>, and follows the provider
    /// until it either returns to the tenant client (the pending flow resumed) or shows a page of its own.
    /// </summary>
    internal async Task<BrokeredResetEnd> CompleteResetAsync(string link, string replacement, string redirectUri)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || !SameOrigin(uri, _baseAddress)
            || uri.AbsolutePath != "/realms/" + _realm + "/login-actions/action-token")
            throw new InvalidOperationException("Owned reset action token is outside the provider.");
        var response = await KeycloakHttp.SendAsync(_client, HttpMethod.Get, uri.PathAndQuery);
        var submitted = false;
        try
        {
            for (var hop = 0; hop < MaximumHops; hop++)
            {
                if (response.StatusCode is HttpStatusCode.Found or HttpStatusCode.SeeOther)
                {
                    var location = response.Headers.Location ?? throw new InvalidOperationException("Reset redirect has no target.");
                    var target = location.IsAbsoluteUri ? location : new Uri(_baseAddress, location);
                    if (target.GetLeftPart(UriPartial.Path) == new Uri(redirectUri).GetLeftPart(UriPartial.Path)) return BrokeredResetEnd.ReturnedToClient;
                    if (!SameOrigin(target, _baseAddress)) throw new InvalidOperationException("Reset flow left the owned provider origin.");
                    response.Dispose();
                    response = await KeycloakHttp.SendAsync(_client, HttpMethod.Get, target.PathAndQuery);
                    continue;
                }
                var html = await KeycloakHttp.ReadBodyAsync(response);
                if (!submitted && Form(html, out var fields, out var action) == "kc-passwd-update-form")
                {
                    submitted = true;
                    fields["password-new"] = replacement;
                    fields["password-confirm"] = replacement;
                    response.Dispose();
                    response = await KeycloakHttp.SendAsync(_client, HttpMethod.Post, action.PathAndQuery, new FormUrlEncodedContent(fields));
                    continue;
                }
                if (!submitted) throw new InvalidOperationException("Owned reset token did not reach its password form.");
                return BrokeredResetEnd.ProviderPage;
            }
            throw new InvalidOperationException("Reset flow exceeded its hop bound.");
        }
        finally { response.Dispose(); }
    }

    /// <summary>Runs the brokered flow to the tenant realm's re-authentication form and returns that page.</summary>
    private async Task<string?> ReauthenticationPageAsync(string alias, string idpUsername, string idpPassword, string redirectUri)
    {
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = "learnstack-web",
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = "openid profile email",
            ["state"] = RandomValue(),
            ["nonce"] = RandomValue(),
            ["code_challenge"] = Base64Url(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(RandomValue()))),
            ["code_challenge_method"] = "S256",
            ["kc_idp_hint"] = alias,
        };
        var response = await KeycloakHttp.SendAsync(_client, HttpMethod.Get,
            QueryHelpers.AddQueryString("realms/" + _realm + "/protocol/openid-connect/auth", query));
        var reachedIdp = false;
        try
        {
            for (var hop = 0; hop < MaximumHops; hop++)
            {
                if (response.StatusCode is HttpStatusCode.Found or HttpStatusCode.SeeOther)
                {
                    var location = response.Headers.Location ?? throw new InvalidOperationException("Brokered redirect has no target.");
                    var target = location.IsAbsoluteUri ? location : new Uri(_baseAddress, location);
                    if (!SameOrigin(target, _baseAddress)) return null;
                    response.Dispose();
                    response = await KeycloakHttp.SendAsync(_client, HttpMethod.Get, target.PathAndQuery);
                    continue;
                }
                var html = await KeycloakHttp.ReadBodyAsync(response);
                var form = Form(html, out var fields, out var action);
                if (form == "kc-form-login" && action.AbsolutePath.StartsWith("/realms/federation-idp-", StringComparison.Ordinal))
                {
                    reachedIdp = true;
                    fields["username"] = idpUsername;
                    fields["password"] = idpPassword;
                }
                else if (html.Contains("value=\"linkAccount\"", StringComparison.Ordinal)) fields["submitAction"] = "linkAccount";
                else if (reachedIdp && form == "kc-form-login"
                    && action.AbsolutePath.StartsWith("/realms/" + _realm + "/login-actions/", StringComparison.Ordinal))
                    return html;
                else return null;
                response.Dispose();
                response = await KeycloakHttp.SendAsync(_client, HttpMethod.Post, action.PathAndQuery, new FormUrlEncodedContent(fields));
            }
            return null;
        }
        finally { response.Dispose(); }
    }
}
