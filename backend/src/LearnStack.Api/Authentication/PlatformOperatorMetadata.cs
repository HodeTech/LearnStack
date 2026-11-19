using LearnStack.Infrastructure.PlatformOperator;
using LearnStack.SharedKernel.Time;
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
/// What the scheme knows about the health of its issuer metadata: when a fetch last succeeded and
/// last failed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two decisions hang on it.</b> While a fetch failed less than <see cref="RetryBackoff"/> ago,
/// the next fetch fails at once instead of waiting on the network, so an issuer that is down cannot
/// hold a queue of unauthenticated requests each for the transport timeout. And when the newest
/// attempt failed and the last success is older than <see cref="MaximumStaleness"/>, tokens are
/// refused: the stock manager keeps serving the keys it last fetched when a refresh fails, which is
/// right for a blip and wrong for a key the issuer has since withdrawn.
/// </para>
/// <para>
/// A manager that has not tried to refresh has recorded no failure, so the first request after a long
/// idle still validates against the keys fetched before it, and triggers the refresh that records the
/// outcome.
/// </para>
/// </remarks>
internal sealed class PlatformOperatorMetadataHealth(IClock clock)
{
    /// <summary>How long after a failed fetch the next one is refused without trying.</summary>
    internal static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(30);

    /// <summary>How old the last successful fetch may be once a later one has failed.</summary>
    internal static readonly TimeSpan MaximumStaleness = TimeSpan.FromMinutes(15);

    private long _lastSuccessTicks;
    private long _lastFailureTicks;

    internal void RecordSuccess() => Volatile.Write(ref _lastSuccessTicks, clock.UtcNow.UtcTicks);

    internal void RecordFailure() => Volatile.Write(ref _lastFailureTicks, clock.UtcNow.UtcTicks);

    /// <summary>Whether a fetch failed within the backoff and none has succeeded since.</summary>
    internal bool InBackoff()
    {
        // A failure and a success in the same tick count as the failure being later: the safe reading.
        var failure = Volatile.Read(ref _lastFailureTicks);
        return failure > 0
            && failure >= Volatile.Read(ref _lastSuccessTicks)
            && clock.UtcNow.UtcTicks - failure < RetryBackoff.Ticks;
    }

    /// <summary>Whether keys are being served that a failed refresh could not renew in time.</summary>
    internal bool IsStale()
    {
        var success = Volatile.Read(ref _lastSuccessTicks);
        return success > 0
            && Volatile.Read(ref _lastFailureTicks) >= success
            && clock.UtcNow.UtcTicks - success > MaximumStaleness.Ticks;
    }
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
    PlatformOperatorMetadataHealth health,
    ILogger<PlatformOperatorDocumentRetriever> logger) : IDocumentRetriever
{
    private const string Refusal = "Platform operator metadata refused.";

    public async Task<string> GetDocumentAsync(string address, CancellationToken cancel)
    {
        if (health.InBackoff())
        {
            // Not counted or logged again: the failure that opened the window was.
            throw new IOException(Refusal);
        }

        try
        {
            var document = await ReadAsync(address, cancel);
            health.RecordSuccess();
            return document;
        }
        catch (Exception failure) when (failure is not OperationCanceledException || !cancel.IsCancellationRequested)
        {
            // The request that triggered the fetch only learns that it found no key. The failure is
            // counted and logged here, where it happens, because an issuer that cannot be reached
            // refuses new operators and that is a state an operator of the platform must see.
            health.RecordFailure();
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
            "Platform operator issuer metadata could not be read ({FailureType}); new keys are not learned, and operators are refused once the keys already held are too old.");
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
