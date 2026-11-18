using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FluentAssertions;
using LearnStack.Infrastructure.Identity.Federation;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Identity;

/// <summary>
/// The SAML metadata reader parses a document from a tenant-chosen host. The size cap bounds characters, not nesting, so
/// the depth and the certificate text are bounded by the reader itself (ADR-0099 § Egress; review finding A1).
/// </summary>
public sealed class FederationDocumentReadersTests
{
    private const int Limit = 256 * 1024;

    [Fact]
    public void Plain_metadata_is_admitted()
    {
        var parsed = FederationDocumentReaders.ReadSamlMetadata(Bytes(Metadata(Certificate())), Limit);
        parsed.Should().NotBeNull();
        parsed!.SigningCertificates.Should().ContainSingle();
    }

    [Theory]
    [InlineData(40)]
    [InlineData(20_000)]
    public void Element_nesting_beyond_the_bound_refuses_before_any_tree_is_walked(int depth)
    {
        // The nesting sits inside the certificate: the recursive InnerText walk this once reached overflowed the stack.
        var nested = string.Concat(Enumerable.Repeat("<x>", depth)) + Certificate() + string.Concat(Enumerable.Repeat("</x>", depth));
        FederationDocumentReaders.ReadSamlMetadata(Bytes(Metadata(nested)), Limit).Should().BeNull();
    }

    [Fact]
    public void A_nesting_that_fills_the_cap_refuses_without_crashing_the_process()
    {
        // About 37,000 nested elements fit in the 256 KiB cap.
        var depth = (Limit - 2_000) / 7;
        var nested = string.Concat(Enumerable.Repeat("<x>", depth)) + string.Concat(Enumerable.Repeat("</x>", depth));
        FederationDocumentReaders.ReadSamlMetadata(Bytes(Metadata(nested)), Limit).Should().BeNull();
    }

    [Fact]
    public void A_certificate_that_contains_an_element_is_not_text_and_refuses()
    {
        FederationDocumentReaders.ReadSamlMetadata(Bytes(Metadata("<x>" + Certificate() + "</x>")), Limit).Should().BeNull();
    }

    [Theory]
    [InlineData(40)]
    [InlineData(20_000)]
    public void Element_nesting_beyond_the_bound_refuses_wherever_it_sits(int depth)
    {
        // Outside the certificate, so only the depth bound can refuse it.
        var padding = string.Concat(Enumerable.Repeat("<ds:Extension>", depth)) + string.Concat(Enumerable.Repeat("</ds:Extension>", depth));
        var document = Metadata(Certificate()).Replace("<md:SingleSignOnService", padding + "<md:SingleSignOnService", StringComparison.Ordinal);
        FederationDocumentReaders.ReadSamlMetadata(Bytes(document), Limit).Should().BeNull();
    }

    [Fact]
    public void Nesting_at_the_bound_is_still_admitted_so_the_control_is_not_vacuous()
    {
        // Metadata proper is eight levels deep; a document just under the bound must still parse.
        var padding = string.Concat(Enumerable.Repeat("<ds:Extension>", 10)) + string.Concat(Enumerable.Repeat("</ds:Extension>", 10));
        var document = Metadata(Certificate()).Replace("<md:SingleSignOnService", padding + "<md:SingleSignOnService", StringComparison.Ordinal);
        FederationDocumentReaders.ReadSamlMetadata(Bytes(document), Limit).Should().NotBeNull();
    }

    [Fact]
    public void Invalid_utf8_in_an_idp_json_document_refuses_instead_of_throwing()
    {
        // JsonDocument.Parse accepts the overlong byte pair C0 AF; reading the string later throws.
        byte[] Json(string before, string after) => [.. Encoding.UTF8.GetBytes(before), 0xC0, 0xAF, .. Encoding.UTF8.GetBytes(after)];
        var jwks = Json("{\"keys\":[{\"kty\":\"RSA\",\"use\":\"sig", "\",\"n\":\"AQAB\",\"e\":\"AQAB\"}]}");
        var discovery = Json("{\"issuer\":\"https://idp.example.com/realms/a", "\"}");
        var url = new Uri("https://idp.example.com/realms/a/.well-known/openid-configuration");
        FederationDocumentReaders.ReadJwks(jwks).Should().BeNull();
        FederationDocumentReaders.ReadDiscovery(discovery, url).Should().BeNull();
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static string Certificate()
    {
        using var key = ECDsa.Create();
        using var certificate = new CertificateRequest("CN=saml", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return Convert.ToBase64String(certificate.RawData);
    }

    private static string Metadata(string certificate) =>
        "<md:EntityDescriptor xmlns:md=\"urn:oasis:names:tc:SAML:2.0:metadata\" xmlns:ds=\"http://www.w3.org/2000/09/xmldsig#\" "
        + "entityID=\"https://saml.egress.test/entity\"><md:IDPSSODescriptor protocolSupportEnumeration=\"urn:oasis:names:tc:SAML:2.0:protocol\">"
        + "<md:KeyDescriptor use=\"signing\"><ds:KeyInfo><ds:X509Data><ds:X509Certificate>" + certificate
        + "</ds:X509Certificate></ds:X509Data></ds:KeyInfo></md:KeyDescriptor>"
        + "<md:SingleSignOnService Binding=\"urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST\" Location=\"https://saml.egress.test/sso\"/>"
        + "</md:IDPSSODescriptor></md:EntityDescriptor>";
}
