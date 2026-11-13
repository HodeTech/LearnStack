using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using LearnStack.SharedKernel.Identity;

namespace LearnStack.Modules.Identity.Application.Federation;

/// <summary>
/// The one place ADR-0099's nonsecret digests are computed: a length-framed SHA-256 over versioned domains.
/// A secret never enters a digest, so a digest can be stored, audited and compared without disclosing one.
/// </summary>
public static class FederationDigest
{
    private const string ConfigurationDomain = "learnstack.identity.federation.configuration.v1";
    private const string DesiredDomain = "learnstack.identity.federation.desired.v1";
    private const string DriftDomain = "learnstack.identity.federation.drift.v1";

    /// <summary>The desired digest of a deleted broker: the provider holds nothing for the alias.</summary>
    public static string Absent { get; } = Hash(["learnstack.identity.federation.absent.v1"]);

    public static string Configuration(FederationBrokerConfiguration configuration) => configuration switch
    {
        FederationOidcConfiguration oidc => Hash([ConfigurationDomain, "oidc", oidc.Issuer, oidc.ClientId,
            oidc.ClientAuthentication.ToString(), oidc.AuthorizationEndpoint.AbsoluteUri, oidc.TokenEndpoint.AbsoluteUri,
            oidc.JwksEndpoint.AbsoluteUri, oidc.UserInfoEndpoint?.AbsoluteUri ?? string.Empty]),
        FederationSamlConfiguration saml => Hash([ConfigurationDomain, "saml", saml.EntityId, saml.SingleSignOnEndpoint.AbsoluteUri,
            .. saml.SigningCertificates.Select(certificate => FederationCertificates.Fingerprint(certificate) ?? "invalid")]),
        null => throw new ArgumentNullException(nameof(configuration)),
        _ => throw new ArgumentException("Federation configuration is not one of the admitted protocols.", nameof(configuration)),
    };

    /// <summary>The digest a readback must equal: the alias, configuration, display name and enabled flag.</summary>
    public static string Desired(string alias, FederationBrokerProtocol protocol, string configurationDigest, string displayName, bool enabled) =>
        Hash([DesiredDomain, alias, protocol.ToString(), configurationDigest, displayName, enabled ? "enabled" : "disabled"]);

    public static string Desired(FederationBrokerState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Desired(state.Owner.Alias, state.Protocol, Configuration(state.Configuration), state.DisplayName, state.Enabled);
    }

    /// <summary>
    /// A present, owned and conforming broker with no mapper digests exactly as its desired state would; any mapper,
    /// nonconforming profile key or foreign owner yields a drift digest no desired state can equal. An unavailable or
    /// foreign readback has no digest.
    /// </summary>
    public static string? Readback(FederationBrokerOwner owner, FederationBrokerReadback readback)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(readback);
        return readback.Status switch
        {
            FederationReadbackStatus.Absent => Absent,
            FederationReadbackStatus.Present when readback.State is { } state && state.Owner == owner
                && readback.ProfileConforms && readback.MapperCount == 0 => Desired(state),
            FederationReadbackStatus.Present when readback.State is { } state => Hash([DriftDomain,
                state.Owner == owner ? Desired(state) : "foreign", readback.ProfileConforms ? "conforming" : "nonconforming",
                readback.MapperCount.ToString(System.Globalization.CultureInfo.InvariantCulture)]),
            _ => null,
        };
    }

    private static string Hash(IReadOnlyList<string> fields)
    {
        using var buffer = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var field in fields)
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            buffer.Write(length);
            buffer.Write(bytes);
        }
        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }
}

/// <summary>Public signing certificates: one PEM CERTIFICATE block each, fingerprinted over its DER bytes.</summary>
public static class FederationCertificates
{
    public const int MaximumCount = 2;
    public const int MaximumPemLength = 6144;

    public static string? Fingerprint(string? pem)
    {
        if (pem is not { Length: > 0 and <= MaximumPemLength } text || !PemEncoding.TryFind(text, out var fields)
            || !text.AsSpan()[fields.Label].SequenceEqual("CERTIFICATE") || text[fields.Location.End..].Trim().Length != 0
            || text[..fields.Location.Start].Trim().Length != 0)
            return null;
        var der = new byte[fields.DecodedDataLength];
        if (!Convert.TryFromBase64Chars(text.AsSpan()[fields.Base64Data], der, out var written) || written != der.Length)
            return null;
        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(der);
            return certificate.PublicKey is null ? null : Convert.ToHexStringLower(SHA256.HashData(der));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
