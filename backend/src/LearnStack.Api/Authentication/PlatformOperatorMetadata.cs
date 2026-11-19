using LearnStack.Infrastructure.PlatformOperator;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace LearnStack.Api.Authentication;

/// <summary>
/// The operator scheme's own bounded client: finite I/O and no redirects, cookies or
/// instrumented credential metadata (ADR-0101 § 2).
/// </summary>
/// <remarks>
/// Owned apart from the tenant client because the two trust different issuers: sharing a
/// client would share nothing a reader should have to reason about.
/// </remarks>
internal sealed class PlatformOperatorBackchannel : IDisposable
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    internal const int MaximumDocumentBytes = 256 * 1024;

    public PlatformOperatorBackchannel() : this(new SocketsHttpHandler()) { }

    // Test-owned certificate authorities use the normal TLS chain/hostname checks; the
    // transport bounds remain mandatory for every supplied handler.
    internal PlatformOperatorBackchannel(SocketsHttpHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (handler.SslOptions.RemoteCertificateValidationCallback is not null)
        {
            throw new InvalidOperationException("Platform operator certificate validation cannot be replaced.");
        }

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

/// <summary>
/// Fetches the discovery document and the key set, and nothing at another origin.
/// </summary>
/// <remarks>
/// A discovery document that names a <c>jwks_uri</c> elsewhere would let whoever controls that
/// host choose the keys the operator scheme trusts, so every address must share the issuer's
/// origin and be canonical.
/// </remarks>
internal sealed class PlatformOperatorDocumentRetriever(
    PlatformOperatorSettings settings,
    PlatformOperatorBackchannel backchannel,
    ILogger<PlatformOperatorDocumentRetriever> logger) : IDocumentRetriever
{
    private const string Refusal = "Platform operator metadata refused.";

    public async Task<string> GetDocumentAsync(string address, CancellationToken cancel)
    {
        try
        {
            return await ReadAsync(address, cancel);
        }
        catch (Exception failure) when (failure is not OperationCanceledException || !cancel.IsCancellationRequested)
        {
            // The request that triggered the fetch only learns that it found no key. The failure is
            // counted and logged here, where it happens, because an issuer that cannot be reached
            // refuses every operator and that is a state an operator of the platform must see.
            PlatformOperatorMetrics.MetadataFailed();
            LogMetadataFailed(logger, failure.GetType().Name, null);
            throw;
        }
    }

    private async Task<string> ReadAsync(string address, CancellationToken cancel)
    {
        var issuer = new Uri(settings.Issuer);
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !uri.IsWellFormedOriginalString()
            || !string.Equals(uri.AbsoluteUri, address, StringComparison.Ordinal)
            || !string.Equals(uri.GetLeftPart(UriPartial.Authority), issuer.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal))
        {
            throw new IOException(Refusal);
        }

        return await BoundedMetadataReader.ReadAsync(
            backchannel.Client,
            uri,
            PlatformOperatorBackchannel.Timeout,
            PlatformOperatorBackchannel.MaximumDocumentBytes,
            Refusal,
            cancel);
    }

    private static readonly Action<ILogger, string, Exception?> LogMetadataFailed =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(7106, nameof(LogMetadataFailed)),
            "Platform operator issuer metadata could not be read ({FailureType}); operators are refused until it can.");
}

/// <summary>Discovery cannot widen the issuer authority the stock manager trusts.</summary>
internal sealed class PlatformOperatorConfigurationValidator(PlatformOperatorSettings settings)
    : IConfigurationValidator<OpenIdConnectConfiguration>
{
    public ConfigurationValidationResult Validate(OpenIdConnectConfiguration configuration) => new()
    {
        Succeeded = string.Equals(configuration.Issuer, settings.Issuer, StringComparison.Ordinal),
        ErrorMessage = "Platform operator metadata refused.",
    };
}
