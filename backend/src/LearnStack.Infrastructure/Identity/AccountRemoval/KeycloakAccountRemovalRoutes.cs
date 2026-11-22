namespace LearnStack.Infrastructure.Identity.AccountRemoval;

/// <summary>
/// The complete set of provider routes the account-removal adapter may build (ADR-0107 § 7): the realm token endpoint for the
/// confined client's credentials and the deletion of one user by its Keycloak identifier. No other user, group, role, client
/// or realm route exists here. The identifier is validated to a canonical UUID before it reaches the path and is escaped
/// again as one segment, so no subject text can add a path, a query or a second segment.
/// </summary>
internal static class KeycloakAccountRemovalRoutes
{
    internal const string ClientId = "learnstack-account-removal";
    internal const string AdminRole = "manage-users";

    internal static Uri Token(KeycloakAccountRemovalOptions options) => options.TokenEndpoint;

    /// <summary>The user to delete. The subject must be the canonical lowercase UUID text Keycloak issues as a user identifier.</summary>
    internal static Uri User(KeycloakAccountRemovalOptions options, string subject)
    {
        if (!TryCanonicalSubject(subject, out var canonical))
            throw new ArgumentException("Account removal subject is not a provider user identifier.", nameof(subject));
        return new(options.AdminRealm, "users/" + Uri.EscapeDataString(canonical));
    }

    /// <summary>
    /// True only for the lowercase hyphenated UUID text, the form Keycloak generates and the OIDC <c>sub</c> carries. A
    /// differently cased or braced spelling is not canonicalized: a different string would address a different (or no) user,
    /// and answering "absent" for a spelling that merely differs would report success for an account that still exists.
    /// </summary>
    internal static bool TryCanonicalSubject(string? subject, out string canonical)
    {
        canonical = string.Empty;
        if (subject is not { Length: 36 } || !Guid.TryParseExact(subject, "D", out var parsed) || parsed == Guid.Empty
            || !string.Equals(parsed.ToString("D"), subject, StringComparison.Ordinal))
            return false;
        canonical = subject;
        return true;
    }
}
