using LearnStack.SharedKernel.Identity;

namespace LearnStack.Infrastructure.Identity.Federation;

/// <summary>
/// The complete set of provider routes the federation adapter may build (ADR-0099 § Ownership and the confined adapter):
/// the realm token endpoint for the admin client's credentials, the identity-provider collection (create, and for the
/// operator one bounded page of the listing), one owned instance by its derived alias, and that instance's mapper list.
/// No other alias, user, flow, realm, client or required-action route exists here, and no import route with a remote
/// URL. Every route is relative to the configured
/// realm; an alias comes only from <see cref="FederationBrokerOwner"/>, which admits the derived form alone.
/// </summary>
internal static class KeycloakFederationRoutes
{
    internal const string ClientId = "learnstack-federation-admin";
    internal const string AdminRole = "manage-identity-providers";

    internal static Uri Token(KeycloakFederationOptions options) => options.TokenEndpoint;

    internal static Uri Instances(KeycloakFederationOptions options) => new(options.AdminRealm, "identity-provider/instances");

    /// <summary>One bounded page of the realm's identity-provider instances, in full representation (ADR-0099 Amendment 10).</summary>
    internal static Uri InstancePage(KeycloakFederationOptions options, int first, int max)
    {
        if (first < 0 || max < 1)
            throw new ArgumentOutOfRangeException(nameof(first), "A page is a non-negative offset and a positive size.");
        // The collection route of create, with the one query the listing admits and no other member.
        return new(Instances(options), "?first=" + first.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "&max=" + max.ToString(System.Globalization.CultureInfo.InvariantCulture) + "&briefRepresentation=false");
    }

    internal static Uri Instance(KeycloakFederationOptions options, FederationBrokerOwner owner) =>
        new(options.AdminRealm, "identity-provider/instances/" + Alias(owner));

    /// <summary>One instance by an alias of the derived form: the operator reads it before it knows whose it is.</summary>
    internal static Uri InstanceOf(KeycloakFederationOptions options, string alias) =>
        new(options.AdminRealm, "identity-provider/instances/" + DerivedAlias(alias));

    internal static Uri Mappers(KeycloakFederationOptions options, FederationBrokerOwner owner) =>
        new(options.AdminRealm, "identity-provider/instances/" + Alias(owner) + "/mappers");

    private static string Alias(FederationBrokerOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        // The owner type already refuses anything but lsf- and 32 lowercase hex characters; repeat it at the route.
        return DerivedAlias(owner.Alias);
    }

    private static string DerivedAlias(string alias)
    {
        if (alias is null || alias.Length != 36 || !alias.StartsWith(FederationBrokerOwner.AliasPrefix, StringComparison.Ordinal)
            || !alias.AsSpan(4).ContainsOnlyHexLower())
            throw new ArgumentException("Federation alias is not a derived alias.", nameof(alias));
        return alias;
    }

    private static bool ContainsOnlyHexLower(this ReadOnlySpan<char> value)
    {
        foreach (var character in value)
            if (!char.IsAsciiHexDigitLower(character) && !char.IsAsciiDigit(character)) return false;
        return true;
    }
}
