using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>Where an ADR-0099 brokered browser flow stopped; no page text, code or credential is retained.</summary>
internal enum BrokeredStop
{
    /// <summary>The tenant client received an authorization code.</summary>
    Authorized,
    /// <summary>The tenant realm showed an error page (for example, no existing account for the asserted address).</summary>
    ErrorPage,
    /// <summary>The tenant realm showed its own local login form: the hint did not start any broker.</summary>
    LocalLogin,
    /// <summary>The link confirmation page was reached and deliberately not submitted.</summary>
    ConfirmationAbandoned,
    /// <summary>The re-authentication form refused the submitted credentials.</summary>
    ReauthenticationRefused,
    /// <summary>The tenant client received an OAuth error.</summary>
    OAuthError,
}

/// <remarks><see cref="DeliveredSamlResponses"/> counts SAML POST-binding responses the browser delivered to the tenant
/// broker endpoint; it is zero for an OIDC broker. <see cref="SignatureRefused"/> is the closed fact that the error page
/// was the pinned provider's invalid-signature refusal of the delivered SAML response.</remarks>
internal sealed record BrokeredResult(BrokeredStop Stop, KeycloakAuthorization? Authorization, bool ReachedIdentityProvider,
    bool ReachedConfirmation, bool PrefilledUsername, int DeliveredSamlResponses = 0, bool SignatureRefused = false)
{
    public override string ToString() => "Brokered flow result (sensitive values omitted)";
}

/// <summary>
/// A cookie-carrying browser over the owned loopback origin for ADR-0099's link-only first-broker flow: tenant authorize
/// with <c>kc_idp_hint</c>, the test IdP's own login form, the link confirmation, then the tenant realm's re-authentication
/// form. Every hop stays on the fixture origin; the tenant client's redirect is captured, never fetched.
/// </summary>
internal sealed partial class KeycloakBrowserFlow
{
    private const int MaximumHops = 24;

    internal async Task<BrokeredResult> BrokeredAuthorizeAsync(string alias, string idpUsername, string idpPassword,
        string? localUsername, string? localPassword, string redirectUri, bool confirmLink = true, string? extraQuery = null)
    {
        var state = RandomValue();
        var verifier = RandomValue();
        var nonce = RandomValue();
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = "learnstack-web",
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = "openid profile email",
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256",
            ["kc_idp_hint"] = alias,
        };
        var path = QueryHelpers.AddQueryString("realms/" + _realm + "/protocol/openid-connect/auth", query) + (extraQuery ?? "");
        var response = await KeycloakHttp.SendAsync(_client, HttpMethod.Get, path);
        var lastPath = "authorize";
        bool reachedIdp = false, reachedConfirmation = false, prefilled = false;
        var samlResponses = 0;
        try
        {
            for (var hop = 0; hop < MaximumHops; hop++)
            {
                if (response.StatusCode is HttpStatusCode.Found or HttpStatusCode.SeeOther)
                {
                    var location = response.Headers.Location ?? throw new InvalidOperationException("Brokered redirect has no target.");
                    var target = location.IsAbsoluteUri ? location : new Uri(_baseAddress, location);
                    var expected = new Uri(redirectUri);
                    if (SameOrigin(target, expected) && target.AbsolutePath == expected.AbsolutePath)
                    {
                        var callback = Callback(response, redirectUri, state, verifier, "learnstack-web", nonce)!;
                        return new(callback.Authorized ? BrokeredStop.Authorized : BrokeredStop.OAuthError, callback, reachedIdp,
                            reachedConfirmation, prefilled, samlResponses);
                    }
                    if (!SameOrigin(target, _baseAddress))
                        throw new InvalidOperationException("Brokered flow left the owned provider origin.");
                    if (target.AbsolutePath.StartsWith("/realms/federation-idp-", StringComparison.Ordinal)) reachedIdp = true;
                    response.Dispose();
                    lastPath = target.AbsolutePath;
                    response = await KeycloakHttp.SendAsync(_client, HttpMethod.Get, target.PathAndQuery);
                    continue;
                }
                if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.Unauthorized
                    && response.StatusCode != HttpStatusCode.BadRequest)
                    throw new InvalidOperationException("Brokered flow returned an unexpected status "
                        + ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture) + " after " + lastPath + ".");
                var html = await KeycloakHttp.ReadBodyAsync(response);
                var form = Form(html, out var fields, out var action);
                if (SamlPostBinding(fields, action, alias) is { } binding)
                {
                    // The SAML POST binding page auto-submits its hidden form, as a browser's script would: the
                    // AuthnRequest to the test IdP, or the IdP's response to the tenant broker endpoint. Nothing is edited.
                    if (binding == SamlMessage.Request) reachedIdp = true;
                    else samlResponses++;
                }
                else if (form == "kc-form-login" && action.AbsolutePath.StartsWith("/realms/federation-idp-", StringComparison.Ordinal))
                {
                    reachedIdp = true;
                    fields["username"] = idpUsername;
                    fields["password"] = idpPassword;
                }
                else if (html.Contains("value=\"linkAccount\"", StringComparison.Ordinal))
                {
                    reachedConfirmation = true;
                    if (!confirmLink) return new(BrokeredStop.ConfirmationAbandoned, null, reachedIdp, true, prefilled, samlResponses);
                    fields["submitAction"] = "linkAccount";
                }
                else if (form == "kc-form-login" && action.AbsolutePath.StartsWith("/realms/" + _realm + "/login-actions/", StringComparison.Ordinal))
                {
                    if (!reachedIdp) return new(BrokeredStop.LocalLogin, null, false, false, false, samlResponses);
                    // A redisplayed form after one submission is a refusal; nothing is retried.
                    if (localUsername is null || localPassword is null)
                        return new(BrokeredStop.ReauthenticationRefused, null, reachedIdp, reachedConfirmation, prefilled, samlResponses);
                    prefilled = fields.TryGetValue("username", out var shown) && shown.Length != 0;
                    fields["username"] = localUsername;
                    fields["password"] = localPassword;
                    localUsername = null;
                    localPassword = null;
                }
                else
                {
                    var signatureRefused = response.StatusCode == HttpStatusCode.BadRequest
                        && lastPath == "POST /realms/" + _realm + "/broker/" + alias + "/endpoint"
                        && html.Contains("Invalid signature in response from identity provider", StringComparison.Ordinal);
                    return new(BrokeredStop.ErrorPage, null, reachedIdp, reachedConfirmation, prefilled, samlResponses, signatureRefused);
                }
                response.Dispose();
                lastPath = "POST " + action.AbsolutePath;
                response = await KeycloakHttp.SendAsync(_client, HttpMethod.Post, action.PathAndQuery, new FormUrlEncodedContent(fields));
            }
            throw new InvalidOperationException("Brokered flow exceeded its hop bound.");
        }
        finally { response.Dispose(); }
    }

    private enum SamlMessage { Request, Response }

    /// <summary>
    /// A SAML POST-binding form: exactly one SAML message field, posted to the test IdP's SAML endpoint (a request) or to
    /// the tenant broker endpoint for <paramref name="alias"/> (a response). Any other target is not followed.
    /// </summary>
    private SamlMessage? SamlPostBinding(Dictionary<string, string> fields, Uri action, string alias)
    {
        var request = fields.ContainsKey("SAMLRequest");
        var response = fields.ContainsKey("SAMLResponse");
        if (request == response) return null;
        if (request && action.AbsolutePath == "/realms/" + KeycloakFixture.SamlIdentityProviderRealm + "/protocol/saml")
            return SamlMessage.Request;
        if (response && action.AbsolutePath == "/realms/" + _realm + "/broker/" + alias + "/endpoint") return SamlMessage.Response;
        throw new InvalidOperationException("SAML POST binding targets an unexpected endpoint.");
    }

    /// <summary>The first form on the page with its hidden and prefilled text inputs, or null.</summary>
    private string? Form(string html, out Dictionary<string, string> fields, out Uri action)
    {
        fields = [];
        action = _baseAddress;
        var match = FormTag().Matches(html).Cast<Match>().FirstOrDefault(value => Attributes(value.Value).ContainsKey("action"));
        if (match is null) return null;
        var attributes = Attributes(match.Value);
        if (!Uri.TryCreate(_baseAddress, attributes["action"], out var target) || !SameOrigin(target, _baseAddress))
            throw new InvalidOperationException("Brokered form action is outside the provider origin.");
        action = target;
        var end = html.IndexOf("</form>", match.Index + match.Length, StringComparison.OrdinalIgnoreCase);
        foreach (var input in InputTag().Matches(html[match.Index..(end < 0 ? html.Length : end)]).Cast<Match>().Select(value => Attributes(value.Value)))
        {
            var type = input.GetValueOrDefault("type")?.ToLowerInvariant();
            if (input.TryGetValue("name", out var name) && type is "hidden" or "text" or "email" && input.TryGetValue("value", out var value))
                fields[name] = value;
        }
        return attributes.GetValueOrDefault("id");
    }
}
