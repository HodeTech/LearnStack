using LearnStack.Infrastructure.Identity;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace LearnStack.Api.Authentication;

/// <summary>One owned client; finite I/O, no redirects or instrumented credential metadata.</summary>
internal sealed class TenantAuthenticationBackchannel : IDisposable
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    internal const int MaximumDocumentBytes = 256 * 1024;

    public TenantAuthenticationBackchannel() : this(new SocketsHttpHandler()) { }

    // Test-owned certificate authorities use the normal TLS chain/hostname checks.
    // The transport bounds remain mandatory for every supplied handler.
    internal TenantAuthenticationBackchannel(SocketsHttpHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (handler.SslOptions.RemoteCertificateValidationCallback is not null)
            throw new InvalidOperationException("Tenant authentication certificate validation cannot be replaced.");
        handler.AllowAutoRedirect = false;
        handler.ConnectTimeout = Timeout;
        handler.UseCookies = false;
        Client = new HttpClient(handler)
        {
            Timeout = Timeout,
            MaxResponseContentBufferSize = MaximumDocumentBytes,
        };
    }

    internal HttpClient Client { get; }

    public void Dispose() => Client.Dispose();
}

/// <summary>The stock configuration manager consumes documents through this bounded transport.</summary>
internal sealed class TenantOidcDocumentRetriever(
    TenantAuthenticationOptions settings,
    TenantAuthenticationBackchannel backchannel) : IDocumentRetriever
{
    public async Task<string> GetDocumentAsync(string address, CancellationToken cancel)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !uri.IsWellFormedOriginalString()
            || !string.Equals(uri.AbsoluteUri, address, StringComparison.Ordinal)
            || !settings.ContainsDocumentAddress(uri))
        {
            throw new IOException("Tenant authentication metadata refused.");
        }

        return await BoundedMetadataReader.ReadAsync(
            backchannel.Client,
            uri,
            TenantAuthenticationBackchannel.Timeout,
            TenantAuthenticationBackchannel.MaximumDocumentBytes,
            "Tenant authentication metadata refused.",
            cancel);
    }
}

/// <summary>Discovery cannot widen the issuer authority trusted by the stock manager.</summary>
internal sealed class TenantOidcConfigurationValidator(TenantAuthenticationOptions settings)
    : IConfigurationValidator<OpenIdConnectConfiguration>
{
    public ConfigurationValidationResult Validate(OpenIdConnectConfiguration configuration) => new()
    {
        Succeeded = string.Equals(configuration.Issuer, settings.Issuer, StringComparison.Ordinal),
        ErrorMessage = "Tenant authentication metadata refused.",
    };
}
