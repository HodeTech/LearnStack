using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using LearnStack.SharedKernel.Identity;

namespace LearnStack.Infrastructure.Identity.Federation;

/// <summary>
/// ADR-0099 § Fixed broker profile, as the exact provider representation. The adapter builds every write from this closed
/// per-protocol allow-list and nothing else, and a readback conforms only when its top-level members and its configuration
/// key set and values equal the profile exactly. The list is the one <c>docs/modules/identity/providers.md</c> enumerates.
/// </summary>
internal static class KeycloakFederationProfile
{
    internal const string OwnerKey = "learnstackOwner";
    internal const string FirstBrokerLoginFlow = "learnstack-first-broker-login";
    internal const string NameIdFormat = "urn:oasis:names:tc:SAML:2.0:nameid-format:persistent";
    internal const string DefaultScope = "openid email profile";

    /// <summary>Members the provider computes on every read; their values carry no configuration.</summary>
    private static readonly string[] ComputedMembers = ["internalId", "types"];

    private static readonly (string Key, bool Value)[] FixedFlags =
    [
        ("trustEmail", false), ("storeToken", false), ("addReadTokenRoleOnCreate", false), ("authenticateByDefault", false),
        ("linkOnly", false), ("hideOnLogin", true),
    ];

    /// <summary>Configuration every brokered protocol carries with a fixed value.</summary>
    private static readonly (string Key, string Value)[] CommonConfiguration =
    [
        ("showInAccountConsole", "NEVER"), ("syncMode", "IMPORT"), ("doNotStoreUsers", "false"), ("passMaxAge", "false"),
        ("loginHint", "false"), ("allowedClockSkew", "0"), ("caseSensitiveOriginalUsername", "false"),
        ("sendClientIdOnLogout", "false"), ("sendIdTokenOnLogout", "false"), ("backchannelSupported", "false"),
    ];

    private static readonly (string Key, string Value)[] OidcConfiguration =
    [
        ("useJwksUrl", "true"), ("validateSignature", "true"), ("pkceEnabled", "true"), ("pkceMethod", "S256"),
        ("disableNonce", "false"), ("disableTypeClaimCheck", "false"), ("defaultScope", DefaultScope),
        ("supportsClientAssertions", "false"), ("jwtAuthorizationGrantEnabled", "false"),
        ("acceptsPromptNoneForwardFromClient", "false"), ("filteredByClaim", "false"), ("isAccessTokenJWT", "false"),
        ("uiLocales", "false"),
    ];

    private static readonly (string Key, string Value)[] SamlConfiguration =
    [
        ("validateSignature", "true"), ("wantAssertionsSigned", "true"), ("wantAssertionsEncrypted", "false"),
        ("wantAuthnRequestsSigned", "false"), ("nameIDPolicyFormat", NameIdFormat), ("principalType", "SUBJECT"),
        ("postBindingResponse", "true"), ("postBindingAuthnRequest", "true"), ("postBindingLogout", "false"),
        ("artifactBindingResponse", "false"), ("useMetadataDescriptorUrl", "false"), ("forceAuthn", "false"),
        ("allowCreate", "true"), ("addExtensionsElementWithKeyInfo", "false"), ("signSpMetadata", "false"),
        ("xmlSigKeyInfoKeyNameTransformer", "KEY_ID"),
    ];

    internal static string OwnerMarker(FederationBrokerOwner owner) =>
        owner.TenantId.Value.ToString("D", CultureInfo.InvariantCulture) + ":" + owner.ConnectionId.ToString("D", CultureInfo.InvariantCulture);

    internal static string ProviderId(FederationBrokerProtocol protocol) => protocol switch
    {
        FederationBrokerProtocol.Oidc => "oidc",
        FederationBrokerProtocol.Saml => "saml",
        _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
    };

    /// <summary>
    /// The full desired representation. An OIDC write without a fresh secret carries the provider's mask literal, which the
    /// pinned provider reads as "keep the stored secret"; omitting the member would erase it.
    /// </summary>
    internal static JsonObject Representation(FederationBrokerState state, FederationClientSecret? secret)
    {
        ArgumentNullException.ThrowIfNull(state);
        var representation = new JsonObject
        {
            ["alias"] = state.Owner.Alias,
            ["displayName"] = state.DisplayName,
            ["providerId"] = ProviderId(state.Protocol),
            ["enabled"] = state.Enabled,
            ["firstBrokerLoginFlowAlias"] = FirstBrokerLoginFlow,
        };
        foreach (var (key, value) in FixedFlags) representation[key] = value;
        var config = new JsonObject();
        foreach (var (key, value) in Configuration(state, secret)) config[key] = value;
        representation["config"] = config;
        return representation;
    }

    /// <summary>The exact configuration map; the secret member is the fresh value or the mask, and never read back.</summary>
    internal static SortedDictionary<string, string> Configuration(FederationBrokerState state, FederationClientSecret? secret)
    {
        var config = new SortedDictionary<string, string>(StringComparer.Ordinal) { [OwnerKey] = OwnerMarker(state.Owner) };
        foreach (var (key, value) in CommonConfiguration) config[key] = value;
        switch (state.Configuration)
        {
            case FederationOidcConfiguration oidc:
                foreach (var (key, value) in OidcConfiguration) config[key] = value;
                config["issuer"] = oidc.Issuer;
                config["clientId"] = oidc.ClientId;
                config["clientSecret"] = secret?.Reveal() ?? FederationClientSecret.MaskLiteral;
                config["clientAuthMethod"] = oidc.ClientAuthentication == FederationClientAuthentication.ClientSecretBasic
                    ? "client_secret_basic" : "client_secret_post";
                config["authorizationUrl"] = oidc.AuthorizationEndpoint.AbsoluteUri;
                config["tokenUrl"] = oidc.TokenEndpoint.AbsoluteUri;
                config["jwksUrl"] = oidc.JwksEndpoint.AbsoluteUri;
                if (oidc.UserInfoEndpoint is { } userInfo) config["userInfoUrl"] = userInfo.AbsoluteUri;
                config["disableUserInfo"] = oidc.UserInfoEndpoint is null ? "true" : "false";
                break;
            case FederationSamlConfiguration saml:
                foreach (var (key, value) in SamlConfiguration) config[key] = value;
                config["idpEntityId"] = saml.EntityId;
                config["singleSignOnServiceUrl"] = saml.SingleSignOnEndpoint.AbsoluteUri;
                config["signingCertificate"] = string.Join(',', saml.SigningCertificates.Select(Der));
                break;
            default:
                throw new ArgumentException("Federation configuration is not one of the admitted protocols.", nameof(state));
        }
        return config;
    }

    /// <summary>
    /// Reads an owned representation. Null when the owner marker or alias names someone else, or when the representation
    /// cannot be read as the protocol's admitted shape (it is then not LearnStack's to adopt, rewrite or delete).
    /// </summary>
    internal static (FederationBrokerState State, bool Conforms)? Read(JsonElement root, FederationBrokerOwner owner)
    {
        if (root.ValueKind != JsonValueKind.Object || !UniqueObject(root)
            || !Text(root, "alias", out var alias) || !string.Equals(alias, owner.Alias, StringComparison.Ordinal)
            || !root.TryGetProperty("config", out var configElement) || configElement.ValueKind != JsonValueKind.Object
            || !UniqueObject(configElement))
            return null;
        var config = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in configElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String) return null;
            config[property.Name] = property.Value.GetString()!;
        }
        if (!config.TryGetValue(OwnerKey, out var marker) || !string.Equals(marker, OwnerMarker(owner), StringComparison.Ordinal)) return null;
        if (!Text(root, "providerId", out var providerId) || !Text(root, "displayName", out var displayName)
            || !root.TryGetProperty("enabled", out var enabledElement) || enabledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return null;
        FederationBrokerConfiguration configuration;
        try
        {
            switch (providerId)
            {
                case "oidc":
                    if (!config.TryGetValue("issuer", out var issuer) || !config.TryGetValue("clientId", out var clientId)
                        || !config.TryGetValue("clientAuthMethod", out var method) || !Endpoint(config, "authorizationUrl", out var authorization)
                        || !Endpoint(config, "tokenUrl", out var token) || !Endpoint(config, "jwksUrl", out var jwks))
                        return null;
                    Uri? userInfo = null;
                    if (config.ContainsKey("userInfoUrl") && !Endpoint(config, "userInfoUrl", out userInfo)) return null;
                    FederationClientAuthentication authentication;
                    if (method == "client_secret_post") authentication = FederationClientAuthentication.ClientSecretPost;
                    else if (method == "client_secret_basic") authentication = FederationClientAuthentication.ClientSecretBasic;
                    else return null;
                    configuration = new FederationOidcConfiguration(issuer, clientId, authentication, authorization!, token!, jwks!, userInfo);
                    break;
                case "saml":
                    if (!config.TryGetValue("idpEntityId", out var entity) || !Endpoint(config, "singleSignOnServiceUrl", out var singleSignOn)
                        || !config.TryGetValue("signingCertificate", out var certificates))
                        return null;
                    var pems = ImmutableArray.CreateBuilder<string>();
                    foreach (var value in certificates.Split(',', StringSplitOptions.TrimEntries))
                    {
                        if (Pem(value) is not { } pem) return null;
                        pems.Add(pem);
                    }
                    configuration = new FederationSamlConfiguration(entity, singleSignOn!, pems.ToImmutable());
                    break;
                default:
                    return null;
            }
        }
        catch (ArgumentException) { return null; }
        var state = new FederationBrokerState(owner, displayName, enabledElement.GetBoolean(), configuration);
        return (state, Conforms(root, config, state));
    }

    /// <summary>Exact equality with the representation the profile would write for this state, secret member masked.</summary>
    private static bool Conforms(JsonElement root, Dictionary<string, string> config, FederationBrokerState state)
    {
        var expected = Representation(state, null);
        var members = root.EnumerateObject().Where(member => !ComputedMembers.Contains(member.Name, StringComparer.Ordinal)).ToList();
        if (members.Count != expected.Count) return false;
        foreach (var member in members)
        {
            if (!expected.TryGetPropertyValue(member.Name, out var wanted)) return false;
            if (member.Name == "config") continue;
            if (wanted is null || !JsonNode.DeepEquals(JsonNode.Parse(member.Value.GetRawText()), wanted)) return false;
        }
        var wantedConfig = Configuration(state, null);
        if (state.Protocol == FederationBrokerProtocol.Oidc)
        {
            // The secret is write-only: a held secret reads back as the mask; an absent one is drift.
            if (!config.TryGetValue("clientSecret", out var held) || held != FederationClientSecret.MaskLiteral) return false;
        }
        return config.Count == wantedConfig.Count && wantedConfig.All(pair => config.TryGetValue(pair.Key, out var value)
            && string.Equals(value, pair.Value, StringComparison.Ordinal));
    }

    private static string Der(string pem)
    {
        if (!PemEncoding.TryFind(pem, out var fields)) throw new ArgumentException("Federation certificate is not PEM.");
        return string.Concat(pem[fields.Base64Data].Where(character => !char.IsWhiteSpace(character)));
    }

    private static string? Pem(string base64)
    {
        if (base64.Length is 0 or > 8192) return null;
        try
        {
            var der = Convert.FromBase64String(base64);
            using var certificate = X509CertificateLoader.LoadCertificate(der);
            return PemEncoding.WriteString("CERTIFICATE", der);
        }
        catch (FormatException) { return null; }
        catch (CryptographicException) { return null; }
    }

    private static bool Endpoint(Dictionary<string, string> config, string key, out Uri? uri)
    {
        uri = null;
        return config.TryGetValue(key, out var value) && Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps;
    }

    private static bool Text(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var member) || member.ValueKind != JsonValueKind.String) return false;
        value = member.GetString() ?? string.Empty;
        return value.Length != 0;
    }

    private static bool UniqueObject(JsonElement element)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return element.EnumerateObject().All(property => names.Add(property.Name));
    }
}
