using LearnStack.SharedKernel.Identity;

namespace LearnStack.Infrastructure.Identity.Federation;

/// <summary>
/// The complete set of provider routes the federation adapter may build (ADR-0099 § Ownership and the confined adapter):
/// the realm token endpoint for the admin client's credentials, the identity-provider collection (create only), one owned
/// instance by its derived alias, and that instance's mapper list. No other alias, user, flow, realm, client or
/// required-action route exists here, and no import route with a remote URL. Every route is relative to the configured
/// realm; an alias comes only from <see cref="FederationBrokerOwner"/>, which admits the derived form alone.
/// </summary>
internal static class KeycloakFederationRoutes
{
    internal const string ClientId = "learnstack-federation-admin";
    internal const string AdminRole = "manage-identity-providers";

    internal static Uri Token(KeycloakFederationOptions options) => options.TokenEndpoint;

    internal static Uri Instances(KeycloakFederationOptions options) => new(options.AdminRealm, "identity-provider/instances");

    internal static Uri Instance(KeycloakFederationOptions options, FederationBrokerOwner owner) =>
        new(options.AdminRealm, "identity-provider/instances/" + Alias(owner));

    internal static Uri Mappers(KeycloakFederationOptions options, FederationBrokerOwner owner) =>
        new(options.AdminRealm, "identity-provider/instances/" + Alias(owner) + "/mappers");

    private static string Alias(FederationBrokerOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        // The owner type already refuses anything but lsf- and 32 lowercase hex characters; repeat it at the route.
        if (owner.Alias.Length != 36 || !owner.Alias.StartsWith(FederationBrokerOwner.AliasPrefix, StringComparison.Ordinal)
            || !owner.Alias.AsSpan(4).ContainsOnlyHexLower())
            throw new ArgumentException("Federation alias is not a derived alias.", nameof(owner));
        return owner.Alias;
    }

    private static bool ContainsOnlyHexLower(this ReadOnlySpan<char> value)
    {
        foreach (var character in value)
            if (!char.IsAsciiHexDigitLower(character) && !char.IsAsciiDigit(character)) return false;
        return true;
    }
}
