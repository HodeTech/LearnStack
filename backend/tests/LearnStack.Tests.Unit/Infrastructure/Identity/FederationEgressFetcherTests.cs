using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FluentAssertions;
using LearnStack.Infrastructure.Identity.Federation;
using LearnStack.SharedKernel.Identity;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Identity;

/// <summary>ADR-0099 § Bounded egress: every rule of the guarded fetcher, each with a dirty control.</summary>
public sealed class FederationEgressFetcherTests
{
    private const string Host = "idp.egress.test";

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.8.9.10")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("168.63.129.16")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    [InlineData("0.0.0.0")]
    [InlineData("192.0.2.10")]
    [InlineData("198.51.100.7")]
    [InlineData("203.0.113.9")]
    [InlineData("198.18.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.250")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::8.8.8.8")]
    [InlineData("64:ff9b::808:808")]
    [InlineData("2002:808:808::1")]
    [InlineData("2001::1")]
    [InlineData("2001:db8::1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("fec0::1")]
    [InlineData("ff02::1")]
    [InlineData("100::1")]
    public void Every_non_global_address_class_refuses(string address) =>
        FederationAddressPolicy.IsGlobal(IPAddress.Parse(address)).Should().BeFalse();

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("172.32.0.1")]
    [InlineData("168.63.129.15")]
    [InlineData("168.63.129.17")]
    [InlineData("100.128.0.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2a00:1450:4001::200e")]
    public void Globally_routable_addresses_are_the_dirty_control(string address) =>
        FederationAddressPolicy.IsGlobal(IPAddress.Parse(address)).Should().BeTrue();

    [Theory]
    [InlineData("http://idp.example.com/.well-known/openid-configuration")]
    [InlineData("https://user:pass@idp.example.com/x")]
    [InlineData("https://idp.example.com/x#fragment")]
    [InlineData("https://93.184.216.34/x")]
    [InlineData("https://[2606:4700:4700::1111]/x")]
    [InlineData("https://idp.example.com:8443/x")]
    [InlineData("ftp://idp.example.com/x")]
    [InlineData("https://faß.de/t")]
    [InlineData("https://xn--fa-hia.de.例え.jp/t")]
    public async Task Shape_and_port_rules_refuse_before_any_resolution(string url)
    {
        var resolutions = 0;
        var fetcher = new FederationEgressFetcher(FederationEgressOptions.Empty, (_, _) => { resolutions++; return Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }); }, null);
        (await fetcher.CheckEndpointAsync(new Uri(url))).Status.Should().Be(FederationEgressStatus.Refused);
        resolutions.Should().Be(0);
    }

    [Fact]
    public async Task An_overlong_url_refuses_and_the_limit_itself_is_admitted()
    {
        var resolutions = 0;
        var fetcher = new FederationEgressFetcher(FederationEgressOptions.Empty,
            (_, _) => { resolutions++; return Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }); }, null);
        var prefix = "https://idp.example.com/";
        (await fetcher.CheckEndpointAsync(new Uri(prefix + new string('a', 2048 - prefix.Length)))).Status.Should().Be(FederationEgressStatus.Admitted);
        (await fetcher.CheckEndpointAsync(new Uri(prefix + new string('a', 2049 - prefix.Length)))).Status.Should().Be(FederationEgressStatus.Refused);
        resolutions.Should().Be(1);
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("169.254.169.254")]
    [InlineData("127.0.0.1")]
    public async Task One_private_answer_among_public_ones_refuses_the_host(string hostile)
    {
        var fetcher = new FederationEgressFetcher(FederationEgressOptions.Empty,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34"), IPAddress.Parse(hostile) }), null);
        (await fetcher.CheckEndpointAsync(new Uri("https://idp.example.com/token"))).Status.Should().Be(FederationEgressStatus.Refused);
        // Dirty control: the same host with only its public answer is admitted.
        var clean = new FederationEgressFetcher(FederationEgressOptions.Empty,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }), null);
        (await clean.CheckEndpointAsync(new Uri("https://idp.example.com/token"))).Status.Should().Be(FederationEgressStatus.Admitted);
    }

    [Fact]
    public async Task The_operator_allow_list_admits_exactly_the_named_host_and_port()
    {
        var resolve = (string _, CancellationToken _) => Task.FromResult(new[] { IPAddress.Parse("10.20.30.40") });
        var listed = new FederationEgressFetcher(new FederationEgressOptions(["idp.corp.internal", "sso.corp.internal:8443"]), resolve, null);
        (await listed.CheckEndpointAsync(new Uri("https://idp.corp.internal/x"))).Status.Should().Be(FederationEgressStatus.Admitted);
        (await listed.CheckEndpointAsync(new Uri("https://sso.corp.internal:8443/x"))).Status.Should().Be(FederationEgressStatus.Admitted);
        // Controls: another port, another host, a subdomain and the unlisted default all refuse.
        (await listed.CheckEndpointAsync(new Uri("https://idp.corp.internal:8443/x"))).Status.Should().Be(FederationEgressStatus.Refused);
        (await listed.CheckEndpointAsync(new Uri("https://sso.corp.internal/x"))).Status.Should().Be(FederationEgressStatus.Refused);
        (await listed.CheckEndpointAsync(new Uri("https://a.idp.corp.internal/x"))).Status.Should().Be(FederationEgressStatus.Refused);
        var unlisted = new FederationEgressFetcher(FederationEgressOptions.Empty, resolve, null);
        (await unlisted.CheckEndpointAsync(new Uri("https://idp.corp.internal/x"))).Status.Should().Be(FederationEgressStatus.Refused);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.8.9.10")]
    [InlineData("169.254.169.254")]
    [InlineData("169.254.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("192.0.2.10")]
    [InlineData("224.0.0.1")]
    [InlineData("240.0.0.1")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::10.0.0.1")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("fec0::1")]
    [InlineData("ff02::1")]
    [InlineData("fd00:ec2::254")]
    [InlineData("fd00:ec2::23")]
    [InlineData("fd00:ec2:ffff::1")]
    [InlineData("fd20:ce::254")]
    [InlineData("fd20:ce:1::1")]
    [InlineData("168.63.129.16")]
    public async Task An_allow_listed_host_still_refuses_loopback_link_local_embedded_and_reserved_addresses(string address)
    {
        FederationAddressPolicy.IsAllowListable(IPAddress.Parse(address)).Should().BeFalse();
        var listed = new FederationEgressFetcher(new FederationEgressOptions(["idp.corp.internal", "sso.corp.internal:8443"]),
            (_, _) => Task.FromResult(new[] { IPAddress.Parse(address) }), null);
        (await listed.CheckEndpointAsync(new Uri("https://idp.corp.internal/x"))).Status.Should().Be(FederationEgressStatus.Refused);
        (await listed.CheckEndpointAsync(new Uri("https://sso.corp.internal:8443/x"))).Status.Should().Be(FederationEgressStatus.Refused);
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.1")]
    [InlineData("fd12:3456::1")]
    [InlineData("fd00:ec3::254")]
    [InlineData("93.184.216.34")]
    public async Task An_allow_listed_host_admits_private_and_public_addresses_as_the_dirty_control(string address)
    {
        FederationAddressPolicy.IsAllowListable(IPAddress.Parse(address)).Should().BeTrue();
        var listed = new FederationEgressFetcher(new FederationEgressOptions(["idp.corp.internal"]),
            (_, _) => Task.FromResult(new[] { IPAddress.Parse(address) }), null);
        (await listed.CheckEndpointAsync(new Uri("https://idp.corp.internal/x"))).Status.Should().Be(FederationEgressStatus.Admitted);
    }

    [Fact]
    public async Task A_resolver_that_never_answers_is_unavailable_within_the_ceiling()
    {
        var never = new TaskCompletionSource<IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetcher = new FederationEgressFetcher(FederationEgressOptions.Empty, (_, _) => never.Task, null);
        var started = System.Diagnostics.Stopwatch.StartNew();
        (await fetcher.FetchJwksAsync(new Uri("https://idp.example.com/certs"))).Status.Should().Be(FederationEgressStatus.Unavailable);
        started.Elapsed.Should().BeLessThan(FederationEgressFetcher.Ceiling + TimeSpan.FromSeconds(1),
            "the call returns at its three-second ceiling although the resolver never completes");
        never.Task.IsCompleted.Should().BeFalse();
    }

    [Theory]
    [InlineData("*.corp.internal")]
    [InlineData("https://idp.corp.internal")]
    [InlineData("idp.corp.internal/path")]
    [InlineData("10.0.0.1")]
    [InlineData("idp.corp.internal:0")]
    [InlineData("idp.corp.internal:65536")]
    [InlineData("idp.corp.internal:08443")]
    [InlineData(" idp.corp.internal")]
    [InlineData("user@idp.corp.internal")]
    [InlineData("")]
    public void Allow_list_entries_admit_no_wildcard_scheme_path_or_address(string entry) =>
        FluentActions.Invoking(() => new FederationEgressOptions([entry])).Should().Throw<ArgumentException>();

    [Fact]
    public async Task Discovery_connects_to_the_validated_address_with_sni_for_the_original_host()
    {
        var issuer = $"https://{Host}:{{port}}/realms/acme";
        await using var server = new FederationTlsServer(Host, _ => []);
        var actual = issuer.Replace("{port}", server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        server.Respond = _ => FederationTlsServer.Json(Discovery(actual));
        var resolutions = 0;
        var fetcher = Fetcher(server, () => resolutions++);
        var result = await fetcher.FetchOidcDiscoveryAsync(new Uri(actual + "/.well-known/openid-configuration"));
        result.Status.Should().Be(FederationEgressStatus.Admitted);
        result.Value!.Issuer.Should().Be(actual);
        result.Value.TokenEndpoint.AbsoluteUri.Should().Be(actual + "/protocol/openid-connect/token");
        // One resolution; the connection was dialled from the validated address and presented the original host as SNI.
        resolutions.Should().Be(1);
        server.ServerNames.Should().Equal(Host);
        server.Requests.Should().ContainSingle().Which.Should().NotContain("Cookie").And.NotContain("Authorization")
            .And.Contain("Accept-Encoding: identity");
    }

    [Fact]
    public async Task A_certificate_for_another_host_refuses_even_at_the_validated_address()
    {
        await using var server = new FederationTlsServer("other.egress.test", _ => FederationTlsServer.Json("{}"));
        var fetcher = Fetcher(server);
        var url = new Uri($"https://{Host}:{server.Port}/realms/acme/.well-known/openid-configuration");
        (await fetcher.FetchOidcDiscoveryAsync(url)).Status.Should().Be(FederationEgressStatus.Refused);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task A_redirect_is_never_followed(int status)
    {
        await using var server = new FederationTlsServer(Host, _ => []);
        server.Respond = _ => FederationTlsServer.Response(status, null, [], $"Location: https://{Host}:{server.Port}/elsewhere");
        var fetcher = Fetcher(server);
        (await fetcher.FetchJwksAsync(new Uri($"https://{Host}:{server.Port}/certs"))).Status.Should().Be(FederationEgressStatus.Refused);
        server.Requests.Should().ContainSingle("the redirect target was never requested");
    }

    [Fact]
    public async Task Body_caps_refuse_one_byte_over_and_admit_the_cap()
    {
        await using var server = new FederationTlsServer(Host, _ => []);
        var fetcher = Fetcher(server);
        var url = new Uri($"https://{Host}:{server.Port}/certs");
        var padding = FederationEgressFetcher.DocumentLimit - Encoding.UTF8.GetByteCount(Jwks(""));
        server.Respond = _ => FederationTlsServer.Json(Jwks(new string(' ', padding)));
        (await fetcher.FetchJwksAsync(url)).Status.Should().Be(FederationEgressStatus.Admitted);
        server.Respond = _ => FederationTlsServer.Json(Jwks(new string(' ', padding + 1)));
        (await fetcher.FetchJwksAsync(url)).Status.Should().Be(FederationEgressStatus.Refused);
        // Discovery has the smaller 64 KiB cap.
        server.Respond = _ => FederationTlsServer.Json("{\"pad\":\"" + new string('a', FederationEgressFetcher.DiscoveryLimit) + "\"}");
        (await fetcher.FetchOidcDiscoveryAsync(new Uri($"https://{Host}:{server.Port}/realms/acme/.well-known/openid-configuration")))
            .Status.Should().Be(FederationEgressStatus.Refused);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/xml")]
    [InlineData("application/javascript")]
    public async Task A_json_document_with_another_content_type_refuses(string contentType)
    {
        await using var server = new FederationTlsServer(Host, _ => FederationTlsServer.Json(Jwks(""), contentType));
        var fetcher = Fetcher(server);
        (await fetcher.FetchJwksAsync(new Uri($"https://{Host}:{server.Port}/certs"))).Status.Should().Be(FederationEgressStatus.Refused);
    }

    [Fact]
    public async Task Compressed_content_refuses()
    {
        await using var server = new FederationTlsServer(Host, _ => FederationTlsServer.Response(200, "application/json",
            Encoding.UTF8.GetBytes(Jwks("")), "Content-Encoding: gzip"));
        (await Fetcher(server).FetchJwksAsync(new Uri($"https://{Host}:{server.Port}/certs"))).Status.Should().Be(FederationEgressStatus.Refused);
    }

    [Fact]
    public async Task A_discovery_document_naming_another_issuer_refuses()
    {
        await using var server = new FederationTlsServer(Host, _ => FederationTlsServer.Json(Discovery("https://attacker.example.com/realms/acme")));
        var url = new Uri($"https://{Host}:{server.Port}/realms/acme/.well-known/openid-configuration");
        (await Fetcher(server).FetchOidcDiscoveryAsync(url)).Status.Should().Be(FederationEgressStatus.Refused);
    }

    [Fact]
    public async Task A_jwks_publishing_private_key_material_refuses()
    {
        await using var server = new FederationTlsServer(Host, _ => FederationTlsServer.Json(
            "{\"keys\":[{\"kty\":\"RSA\",\"use\":\"sig\",\"n\":\"AQAB\",\"e\":\"AQAB\",\"d\":\"secret\"}]}"));
        (await Fetcher(server).FetchJwksAsync(new Uri($"https://{Host}:{server.Port}/certs"))).Status.Should().Be(FederationEgressStatus.Refused);
    }

    [Fact]
    public async Task Saml_metadata_with_a_dtd_refuses_and_plain_metadata_is_admitted()
    {
        await using var server = new FederationTlsServer(Host, _ => []);
        var url = new Uri($"https://{Host}:{server.Port}/metadata");
        var certificate = Certificate();
        server.Respond = _ => FederationTlsServer.Json(Metadata(certificate), "application/samlmetadata+xml");
        var admitted = await Fetcher(server).FetchSamlMetadataAsync(url);
        admitted.Status.Should().Be(FederationEgressStatus.Admitted);
        admitted.Value!.EntityId.Should().Be("https://saml.egress.test/entity");
        admitted.Value.SingleSignOnEndpoint.AbsoluteUri.Should().Be("https://saml.egress.test/sso");
        admitted.Value.SigningCertificates.Should().ContainSingle();
        var external = "<?xml version=\"1.0\"?><!DOCTYPE md:EntityDescriptor [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]>"
            + Metadata(certificate).Replace("https://saml.egress.test/entity", "&xxe;", StringComparison.Ordinal);
        server.Respond = _ => FederationTlsServer.Json(external, "application/xml");
        (await Fetcher(server).FetchSamlMetadataAsync(url)).Status.Should().Be(FederationEgressStatus.Refused);
        var bomb = "<?xml version=\"1.0\"?><!DOCTYPE lolz [<!ENTITY lol \"lol\"><!ENTITY lol2 \"&lol;&lol;&lol;\">]>" + Metadata(certificate);
        server.Respond = _ => FederationTlsServer.Json(bomb, "application/xml");
        (await Fetcher(server).FetchSamlMetadataAsync(url)).Status.Should().Be(FederationEgressStatus.Refused);
    }

    [Fact]
    public async Task The_fetch_sends_no_trace_context_to_the_tenant_chosen_host()
    {
        // A W3C traceparent, tracestate or baggage header would hand a tenant-chosen host this platform's trace identifiers.
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllData,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        using var source = new System.Diagnostics.ActivitySource("federation-egress-test");
        using var caller = source.StartActivity("caller");
        caller.Should().NotBeNull("the control needs a current activity to propagate");
        await using var server = new FederationTlsServer(Host, _ => FederationTlsServer.Json("{\"keys\":[]}"));
        await Fetcher(server).FetchJwksAsync(new Uri($"https://{Host}:{server.Port}/certs"));
        var request = server.Requests.Should().ContainSingle().Which.ToLowerInvariant();
        request.Should().Contain("get /certs").And.NotContain("traceparent").And.NotContain("tracestate").And.NotContain("baggage");
    }

    [Fact]
    public async Task A_hanging_host_is_unavailable_within_the_ceiling()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var fetcher = new FederationEgressFetcher(new FederationEgressOptions([$"{Host}:{port}"]),
                (_, _) => Task.FromResult(new[] { ValidatedPrivate }), null, LocalListener);
            var started = System.Diagnostics.Stopwatch.StartNew();
            (await fetcher.FetchJwksAsync(new Uri($"https://{Host}:{port}/certs"))).Status.Should().Be(FederationEgressStatus.Unavailable);
            started.Elapsed.Should().BeLessThan(FederationEgressFetcher.Ceiling + TimeSpan.FromSeconds(2));
        }
        finally { listener.Stop(); }
    }

    /// <summary>
    /// The address the test resolver answers: private, so an allow-listed host admits it. The test route dials the local
    /// TLS listener in its place; the address policy itself still runs on this answer.
    /// </summary>
    private static readonly IPAddress ValidatedPrivate = IPAddress.Parse("10.255.0.7");

    private static IPAddress LocalListener(IPAddress validated)
    {
        validated.Should().Be(ValidatedPrivate, "only a validated address is ever dialled");
        return IPAddress.Loopback;
    }

    private static FederationEgressFetcher Fetcher(FederationTlsServer server, Action? resolved = null) =>
        new(new FederationEgressOptions([$"{Host}:{server.Port}"]), (host, _) =>
        {
            host.Should().Be(Host);
            resolved?.Invoke();
            return Task.FromResult(new[] { ValidatedPrivate });
        }, [server.Authority], LocalListener);

    private static string Discovery(string issuer) =>
        "{\"issuer\":\"" + issuer + "\",\"authorization_endpoint\":\"" + issuer + "/protocol/openid-connect/auth\",\"token_endpoint\":\"" + issuer
        + "/protocol/openid-connect/token\",\"jwks_uri\":\"" + issuer + "/protocol/openid-connect/certs\",\"userinfo_endpoint\":\"" + issuer
        + "/protocol/openid-connect/userinfo\"}";

    private static string Jwks(string padding) => "{\"keys\":[{\"kty\":\"RSA\",\"use\":\"sig\",\"n\":\"AQAB\",\"e\":\"AQAB\"}]" + padding + "}";

    private static string Certificate()
    {
        using var key = System.Security.Cryptography.ECDsa.Create();
        using var certificate = new CertificateRequest("CN=saml", key, System.Security.Cryptography.HashAlgorithmName.SHA256)
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
