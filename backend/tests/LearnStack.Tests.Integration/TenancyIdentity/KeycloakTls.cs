using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>
/// An owned provider CA and loopback server certificate. Clients trust only this root and retain
/// ordinary TLS hostname validation; no certificate-validation bypass or HTTP cookie workaround.
/// </summary>
internal sealed class KeycloakTls : IDisposable
{
    internal X509Certificate2 RootCertificate { get; }
    internal byte[] ServerCertificatePem { get; }
    internal byte[] ServerPrivateKeyPem { get; }

    internal KeycloakTls()
    {
        var now = DateTimeOffset.UtcNow;
        using var authorityKey = RSA.Create(2048);
        var authorityRequest = new CertificateRequest("CN=LearnStack identity fixture root", authorityKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        authorityRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        authorityRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        authorityRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(authorityRequest.PublicKey, false));
        using var authority = authorityRequest.CreateSelfSigned(now.AddMinutes(-5), now.AddHours(25));

        using var serverKey = RSA.Create(2048);
        var serverRequest = new CertificateRequest("CN=localhost", serverKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        names.AddIpAddress(IPAddress.IPv6Loopback);
        serverRequest.CertificateExtensions.Add(names.Build());
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7f;
        serial[^1] |= 1;
        using var server = serverRequest.Create(authority, now.AddMinutes(-5), now.AddHours(24), serial);

        ServerCertificatePem = Encoding.ASCII.GetBytes(server.ExportCertificatePem());
        ServerPrivateKeyPem = Encoding.ASCII.GetBytes(serverKey.ExportPkcs8PrivateKeyPem());
        RootCertificate = X509CertificateLoader.LoadCertificate(authority.Export(X509ContentType.Cert));
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(ServerPrivateKeyPem);
        RootCertificate.Dispose();
    }
}
