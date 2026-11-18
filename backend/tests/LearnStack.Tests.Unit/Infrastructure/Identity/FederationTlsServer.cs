using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace LearnStack.Tests.Unit.Infrastructure.Identity;

/// <summary>
/// A loopback HTTPS responder for egress tests: one owned CA, a leaf for a named host, one scripted response per
/// connection. It records the SNI each client presented and every request line, and never follows anything itself.
/// </summary>
internal sealed class FederationTlsServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly X509Certificate2 _leaf;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly List<string> _requests = [];
    private readonly List<string?> _serverNames = [];

    internal FederationTlsServer(string certificateHost, Func<string, byte[]> respond)
    {
        Authority = CreateAuthority();
        _leaf = CreateLeaf(Authority, certificateHost);
        Respond = respond;
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    internal X509Certificate2 Authority { get; }
    internal Func<string, byte[]> Respond { get; set; }
    internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    internal IReadOnlyList<string> Requests { get { lock (_requests) return [.. _requests]; } }
    internal IReadOnlyList<string?> ServerNames { get { lock (_requests) return [.. _serverNames]; } }

    internal static byte[] Response(int status, string? contentType, byte[] body, params string[] headers)
    {
        var head = new StringBuilder("HTTP/1.1 " + status.ToString(System.Globalization.CultureInfo.InvariantCulture) + " X\r\n");
        if (contentType is not null) head.Append("Content-Type: ").Append(contentType).Append("\r\n");
        foreach (var header in headers) head.Append(header).Append("\r\n");
        head.Append("Content-Length: ").Append(body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append("\r\nConnection: close\r\n\r\n");
        return [.. Encoding.ASCII.GetBytes(head.ToString()), .. body];
    }

    internal static byte[] Json(string json, string contentType = "application/json") =>
        Response(200, contentType, Encoding.UTF8.GetBytes(json));

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { return; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await using var tls = new SslStream(client.GetStream(), false);
                string? serverName = null;
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificateSelectionCallback = (_, name) => { serverName = name; return _leaf; },
                }, _stop.Token);
                var buffer = new byte[16 * 1024];
                var count = 0;
                while (count < buffer.Length)
                {
                    var read = await tls.ReadAsync(buffer.AsMemory(count), _stop.Token);
                    if (read == 0) break;
                    count += read;
                    if (Encoding.ASCII.GetString(buffer, 0, count).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                }
                var request = Encoding.ASCII.GetString(buffer, 0, count);
                lock (_requests) { _requests.Add(request); _serverNames.Add(serverName); }
                var line = request.Split("\r\n")[0];
                await tls.WriteAsync(Respond(line), _stop.Token);
                await tls.FlushAsync(_stop.Token);
            }
            catch (Exception) when (!_stop.IsCancellationRequested)
            {
                // A refused handshake is an expected outcome for the certificate-mismatch case.
            }
        }
    }

    private static X509Certificate2 CreateAuthority()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=federation egress test root", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var now = DateTimeOffset.UtcNow;
        using var authority = request.CreateSelfSigned(now.AddMinutes(-5), now.AddHours(2));
        return X509CertificateLoader.LoadPkcs12(authority.Export(X509ContentType.Pkcs12), null);
    }

    private static X509Certificate2 CreateLeaf(X509Certificate2 authority, string host)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + host, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(host);
        request.CertificateExtensions.Add(names.Build());
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7f;
        var now = DateTimeOffset.UtcNow;
        using var leaf = request.Create(authority, now.AddMinutes(-5), now.AddHours(1), serial);
        using var withKey = leaf.CopyWithPrivateKey(key);
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try { await _loop; } catch (Exception) { }
        _leaf.Dispose();
        Authority.Dispose();
        _stop.Dispose();
    }
}
