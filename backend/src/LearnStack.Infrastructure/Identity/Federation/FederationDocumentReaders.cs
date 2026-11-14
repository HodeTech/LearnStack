using System.Collections.Immutable;
using System.Text;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Xml;
using LearnStack.SharedKernel.Identity;

namespace LearnStack.Infrastructure.Identity.Federation;

/// <summary>
/// Closed parsers for the three documents the egress fetcher retrieves. Each returns null for any document it cannot
/// read as exactly its admitted shape; no raw value, remote message or parser text leaves them.
/// </summary>
internal static class FederationDocumentReaders
{
    internal const int MaximumUrlLength = 2048;
    private const string MetadataNamespace = "urn:oasis:names:tc:SAML:2.0:metadata";
    private const string SignatureNamespace = "http://www.w3.org/2000/09/xmldsig#";
    private const string SamlProtocol = "urn:oasis:names:tc:SAML:2.0:protocol";
    private const string PostBinding = "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST";
    private const int MaximumJwksKeys = 64;
    private static readonly JsonDocumentOptions Json = new() { MaxDepth = 16, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow };
    private static readonly string[] PrivateJwkMembers = ["d", "p", "q", "dp", "dq", "qi", "oth", "k"];

    /// <summary>
    /// OIDC discovery: the issuer must be the document URL's prefix (OpenID Connect Discovery § 4.3), and every endpoint
    /// must be an absolute HTTPS URL without userinfo or fragment.
    /// </summary>
    internal static FederationOidcDiscovery? ReadDiscovery(ReadOnlySpan<byte> body, Uri documentUrl)
    {
        try
        {
            using var document = JsonDocument.Parse(body.ToArray(), Json);
            var root = document.RootElement;
            if (!UniqueObject(root) || !Text(root, "issuer", out var issuer) || !Endpoint(root, "authorization_endpoint", out var authorization)
                || !Endpoint(root, "token_endpoint", out var token) || !Endpoint(root, "jwks_uri", out var jwks))
                return null;
            Uri? userInfo = null;
            if (root.TryGetProperty("userinfo_endpoint", out var member) && member.ValueKind != JsonValueKind.Null
                && !Endpoint(root, "userinfo_endpoint", out userInfo))
                return null;
            if (!IssuerShape(issuer) || !Uri.TryCreate(issuer.TrimEnd('/') + "/.well-known/openid-configuration", UriKind.Absolute, out var expected)
                || !string.Equals(expected.AbsoluteUri, documentUrl.AbsoluteUri, StringComparison.Ordinal))
                return null;
            return new FederationOidcDiscovery(issuer, authorization!, token!, jwks!, userInfo);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>A JWKS reduced to its public signing keys; any private key member makes the whole set unusable.</summary>
    internal static FederationJwksDocument? ReadJwks(ReadOnlySpan<byte> body)
    {
        try
        {
            using var document = JsonDocument.Parse(body.ToArray(), Json);
            var root = document.RootElement;
            if (!UniqueObject(root) || !root.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array
                || keys.GetArrayLength() > MaximumJwksKeys)
                return null;
            var count = 0;
            foreach (var key in keys.EnumerateArray())
            {
                if (!UniqueObject(key) || PrivateJwkMembers.Any(name => key.TryGetProperty(name, out _))) return null;
                if (key.TryGetProperty("use", out var use) && (use.ValueKind != JsonValueKind.String || use.GetString() != "sig")) continue;
                if (!Text(key, "kty", out var type)) return null;
                var usable = type switch
                {
                    "RSA" => Text(key, "n", out _) && Text(key, "e", out _),
                    "EC" => Text(key, "crv", out _) && Text(key, "x", out _) && Text(key, "y", out _),
                    "OKP" => Text(key, "crv", out _) && Text(key, "x", out _),
                    _ => false,
                };
                if (usable) count++;
            }
            return new FederationJwksDocument(count);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// SAML identity-provider metadata: one EntityDescriptor (no aggregate), one IDPSSODescriptor for SAML 2.0, its
    /// HTTP-POST single sign-on location and at most two signing certificates. DTDs are prohibited and nothing is
    /// resolved; an XML signature on the document is not trusted for anything.
    /// </summary>
    internal static FederationSamlMetadata? ReadSamlMetadata(byte[] body, int maximumCharacters)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = maximumCharacters,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            CloseInput = true,
        };
        var document = new XmlDocument { XmlResolver = null, PreserveWhitespace = false };
        try
        {
            using var stream = new MemoryStream(body, writable: false);
            using var reader = XmlReader.Create(stream, settings);
            document.Load(reader);
        }
        catch (XmlException) { return null; }
        catch (InvalidOperationException) { return null; }
        var root = document.DocumentElement;
        if (root is null || root.LocalName != "EntityDescriptor" || root.NamespaceURI != MetadataNamespace) return null;
        var entity = root.GetAttribute("entityID");
        if (!VisibleAscii(entity, MaximumUrlLength)) return null;
        var descriptors = Children(root, MetadataNamespace, "IDPSSODescriptor").ToList();
        if (descriptors.Count != 1) return null;
        var descriptor = descriptors[0];
        if (!descriptor.GetAttribute("protocolSupportEnumeration").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(SamlProtocol))
            return null;
        var posts = Children(descriptor, MetadataNamespace, "SingleSignOnService")
            .Where(service => service.GetAttribute("Binding") == PostBinding).ToList();
        if (posts.Count != 1 || !Https(posts[0].GetAttribute("Location"), out var singleSignOn)) return null;
        var certificates = ImmutableArray.CreateBuilder<string>();
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in Children(descriptor, MetadataNamespace, "KeyDescriptor"))
        {
            var use = key.GetAttribute("use");
            if (use.Length != 0 && use != "signing") continue;
            foreach (var info in Children(key, SignatureNamespace, "KeyInfo"))
                foreach (var data in Children(info, SignatureNamespace, "X509Data"))
                    foreach (var value in Children(data, SignatureNamespace, "X509Certificate"))
                    {
                        var pem = Pem(value.InnerText);
                        if (pem is null) return null;
                        if (fingerprints.Add(pem.Value.Fingerprint)) certificates.Add(pem.Value.Text);
                    }
        }
        if (certificates.Count is < 1 or > 2) return null;
        return new FederationSamlMetadata(entity, singleSignOn!, certificates.ToImmutable());
    }

    /// <summary>
    /// A tenant or document URL: absolute HTTPS, an ASCII DNS host, no userinfo or fragment, at most 2,048 characters.
    /// An internationalized host name refuses: the guard resolves the punycode form while the provider would be written
    /// the Unicode form, and the two must never differ (enter the IdP's ASCII name instead).
    /// </summary>
    internal static bool Https(string? value, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrEmpty(value) || value.Length > MaximumUrlLength || !Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps || parsed.UserInfo.Length != 0 || parsed.Fragment.Length != 0
            || value.Contains('#', StringComparison.Ordinal) || parsed.HostNameType != UriHostNameType.Dns
            || !Ascii.IsValid(parsed.Host) || !string.Equals(parsed.Host, parsed.IdnHost, StringComparison.OrdinalIgnoreCase))
            return false;
        uri = parsed;
        return true;
    }

    private static (string Text, string Fingerprint)? Pem(string base64)
    {
        var compact = string.Concat(base64.Where(character => !char.IsWhiteSpace(character)));
        if (compact.Length is 0 or > 8192) return null;
        byte[] der;
        try { der = Convert.FromBase64String(compact); }
        catch (FormatException) { return null; }
        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(der);
            if (certificate.PublicKey is null) return null;
        }
        catch (CryptographicException) { return null; }
        return (PemEncoding.WriteString("CERTIFICATE", der), Convert.ToHexStringLower(SHA256.HashData(der)));
    }

    private static IEnumerable<XmlElement> Children(XmlElement parent, string ns, string name) =>
        parent.ChildNodes.OfType<XmlElement>().Where(child => child.LocalName == name && child.NamespaceURI == ns);

    private static bool IssuerShape(string issuer) => VisibleAscii(issuer, MaximumUrlLength) && Https(issuer, out var uri)
        && uri!.Query.Length == 0 && !issuer.Contains('?', StringComparison.Ordinal);

    private static bool Endpoint(JsonElement root, string name, out Uri? uri)
    {
        uri = null;
        return Text(root, name, out var value) && Https(value, out uri);
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
        if (element.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        return element.EnumerateObject().All(property => names.Add(property.Name));
    }

    private static bool VisibleAscii(string? value, int maximum) => value is { Length: > 0 } text && text.Length <= maximum
        && text.All(character => character is >= '!' and <= '~');
}
