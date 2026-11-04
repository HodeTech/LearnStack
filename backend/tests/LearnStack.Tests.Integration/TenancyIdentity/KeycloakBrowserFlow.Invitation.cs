using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace LearnStack.Tests.Integration.TenancyIdentity;

internal sealed partial class KeycloakBrowserFlow
{
    internal async Task<Uri> CompleteBffRegistrationAsync(Uri authorization, string email, string password)
    {
        var callback = InvitationCallback(authorization, verification: false);
        using var login = await KeycloakHttp.SendAsync(_client, HttpMethod.Get, authorization.AbsoluteUri);
        if (login.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException("Invitation registration did not start at the provider login page.");
        var html = await KeycloakHttp.ReadBodyAsync(login);
        var links = Regex.Matches(html, @"<a\b[^>]*>", RegexOptions.IgnoreCase)
            .Select(match => Attributes(match.Value)).Where(attributes => attributes.TryGetValue("href", out var value)
                && Uri.TryCreate(_baseAddress, value, out var uri) && SameOrigin(uri, _baseAddress)
                && uri.AbsolutePath == "/realms/learnstack/login-actions/registration").ToArray();
        if (links.Length != 1)
            throw new InvalidOperationException("Invitation registration link is missing or ambiguous.");
        using var registration = await KeycloakHttp.SendAsync(_client, HttpMethod.Get, links[0]["href"]);
        if (registration.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException("Invitation registration form was refused.");
        html = await KeycloakHttp.ReadBodyAsync(registration);
        var form = FormTag().Matches(html).SingleOrDefault(match => Attributes(match.Value).GetValueOrDefault("id") == "kc-register-form")
            ?? throw new InvalidOperationException("Invitation registration form is missing.");
        var attributes = Attributes(form.Value);
        if (attributes.GetValueOrDefault("method")?.ToLowerInvariant() != "post"
            || !attributes.TryGetValue("action", out var action) || !Uri.TryCreate(_baseAddress, action, out var destination)
            || !SameOrigin(destination, _baseAddress) || destination.AbsolutePath != "/realms/learnstack/login-actions/registration")
            throw new InvalidOperationException("Invitation registration form escaped its provider.");
        var end = html.IndexOf("</form>", form.Index + form.Length, StringComparison.OrdinalIgnoreCase);
        if (end < 0) throw new InvalidOperationException("Invitation registration form is incomplete.");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match input in InputTag().Matches(html[form.Index..end]))
        {
            var values = Attributes(input.Value);
            if (!values.TryGetValue("name", out var name)) continue;
            names.Add(name);
            if (values.GetValueOrDefault("type")?.Equals("hidden", StringComparison.OrdinalIgnoreCase) == true)
                fields.Add(name, values.GetValueOrDefault("value") ?? "");
        }
        if (!RegistrationRequiredFields.All(names.Contains))
            throw new InvalidOperationException("Invitation registration lacks its ordinary fields.");
        fields["email"] = email;
        fields["firstName"] = "Invitation";
        fields["lastName"] = "Registration";
        fields["password"] = password;
        fields["password-confirm"] = password;
        if (names.Contains("username")) fields["username"] = email;
        using var response = await KeycloakHttp.SendAsync(_client, HttpMethod.Post, destination.AbsoluteUri, new FormUrlEncodedContent(fields));
        return ValidateInvitationCallback(response, authorization, callback);
    }

    // Follows the actual SMTP-delivered action token in the same provider browser.
    // No provider-admin verification mutation or email-token fabrication occurs.
    internal async Task<Uri> CompleteBffEmailVerificationAsync(Uri authorization,
        Func<CancellationToken, Task<string>> receiveMail, CancellationToken cancellationToken)
    {
        var callback = InvitationCallback(authorization, verification: true);
        using var entered = await FollowInvitationProviderAsync(authorization, callback, cancellationToken);
        if (entered.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException("Invitation verification did not require an email.");
        // Stock Keycloak 26.6 AIA requires the user's explicit Send email form.
        // The initial challenge intentionally does not issue verification mail.
        var html = await KeycloakHttp.ReadBodyAsync(entered, cancellationToken);
        var form = FormTag().Matches(html).SingleOrDefault(match => Attributes(match.Value).GetValueOrDefault("id") == "kc-verify-email-form")
            ?? throw new InvalidOperationException("Invitation verification lacks its stock send-email form.");
        var attributes = Attributes(form.Value);
        if (attributes.GetValueOrDefault("method")?.ToLowerInvariant() != "post"
            || !attributes.TryGetValue("action", out var action) || !Uri.TryCreate(_baseAddress, action, out var destination)
            || !SameOrigin(destination, _baseAddress) || destination.AbsolutePath != "/realms/learnstack/login-actions/required-action"
            || QueryHelpers.ParseQuery(destination.Query).GetValueOrDefault("execution") != "VERIFY_EMAIL")
            throw new InvalidOperationException("Invitation verification form escaped its exact required action.");
        using var sent = await KeycloakHttp.SendAsync(_client, HttpMethod.Post, destination.AbsoluteUri,
            new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>()), cancellationToken);
        if (sent.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException("Invitation verification send-email form was refused.");
        var mail = await receiveMail(cancellationToken);
        if (!Uri.TryCreate(mail, UriKind.Absolute, out var link) || !SameOrigin(link, _baseAddress)
            || link.AbsolutePath != "/realms/learnstack/login-actions/action-token" || link.Fragment.Length != 0
            || QueryHelpers.ParseQuery(link.Query).GetValueOrDefault("key").Count != 1)
            throw new InvalidOperationException("Invitation verification email lacks its owned action link.");
        using var completed = await FollowInvitationProviderAsync(link, callback, cancellationToken);
        return ValidateInvitationCallback(completed, authorization, callback);
    }

    private Uri InvitationCallback(Uri authorization, bool verification)
    {
        if (!SameOrigin(authorization, _baseAddress) || authorization.AbsolutePath != "/realms/learnstack/protocol/openid-connect/auth"
            || authorization.Fragment.Length != 0 || authorization.UserInfo.Length != 0)
            throw new InvalidOperationException("Invitation authorization escaped its provider.");
        var fields = QueryHelpers.ParseQuery(authorization.Query);
        if (fields.Any(pair => pair.Value.Count != 1) || fields.GetValueOrDefault("client_id") != "learnstack-web"
            || fields.GetValueOrDefault("code_challenge_method") != "S256"
            || fields.GetValueOrDefault("state").ToString().Length != 43
            || fields.GetValueOrDefault("nonce").ToString().Length != 43
            || (verification ? fields.GetValueOrDefault("kc_action") != "VERIFY_EMAIL" : fields.ContainsKey("kc_action"))
            || !Uri.TryCreate(fields.GetValueOrDefault("redirect_uri").ToString(), UriKind.Absolute, out var callback)
            || callback.Scheme != "https" || callback.Port != 3000 || !KnownBffHost(callback.Host)
            || callback.AbsolutePath != "/api/auth/callback/keycloak" || callback.Query.Length != 0
            || callback.Fragment.Length != 0 || callback.UserInfo.Length != 0)
            throw new InvalidOperationException("Invitation authorization lacks its exact callback and S256 state.");
        return callback;
    }

    private async Task<HttpResponseMessage> FollowInvitationProviderAsync(Uri address, Uri callback, CancellationToken cancellationToken)
    {
        for (var redirect = 0; redirect < 8; redirect++)
        {
            var response = await KeycloakHttp.SendAsync(_client, HttpMethod.Get, address.AbsoluteUri, cancellationToken: cancellationToken);
            if (response.StatusCode is not (HttpStatusCode.Found or HttpStatusCode.SeeOther)) return response;
            var location = response.Headers.Location;
            if (location is not null && !location.IsAbsoluteUri) location = new Uri(address, location);
            if (location is not null && location.GetLeftPart(UriPartial.Path) == callback.GetLeftPart(UriPartial.Path)) return response;
            response.Dispose();
            if (location is null || !SameOrigin(location, _baseAddress) || !location.AbsolutePath.StartsWith("/realms/learnstack/", StringComparison.Ordinal)
                || location.UserInfo.Length != 0 || location.Fragment.Length != 0)
                throw new InvalidOperationException("Invitation provider redirect escaped its owned boundary.");
            address = location;
        }
        throw new InvalidOperationException("Invitation provider redirect bound exceeded.");
    }

    private static Uri ValidateInvitationCallback(HttpResponseMessage response, Uri authorization, Uri callback)
    {
        var location = response.Headers.Location;
        if (response.StatusCode is not (HttpStatusCode.Found or HttpStatusCode.SeeOther) || location is null || !location.IsAbsoluteUri
            || location.GetLeftPart(UriPartial.Path) != callback.GetLeftPart(UriPartial.Path) || location.Fragment.Length != 0 || location.UserInfo.Length != 0)
            throw new InvalidOperationException("Invitation provider did not return its original callback.");
        var expected = QueryHelpers.ParseQuery(authorization.Query);
        var fields = QueryHelpers.ParseQuery(location.Query);
        if (fields.Any(pair => pair.Value.Count != 1) || fields.GetValueOrDefault("state") != expected.GetValueOrDefault("state")
            || fields.GetValueOrDefault("code").Count != 1 || fields.ContainsKey("error"))
            throw new InvalidOperationException("Invitation provider callback lacks its original state or code.");
        return location;
    }
}
