using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace LearnStack.Infrastructure.PlatformOperator;

/// <summary>
/// The one definition of how an operator token is validated: the cryptographic parameters, the bounded
/// metadata manager and, offline, the validator that applies both (ADR-0101 § 2, § 7, ADR-0104 § 3).
/// </summary>
/// <remarks>
/// The API's bearer scheme takes <see cref="CreateParameters"/> and <see cref="CreateConfigurationManager"/>
/// and lets the stock handler validate; the offline issuer-rebind tool takes the same two and calls
/// <see cref="PlatformOperatorTokenValidator"/>. Both then apply <see cref="PlatformOperatorTokenProfile"/>, so
/// the two callers cannot diverge on issuer, audience, algorithm, lifetime, assurance or authentication age.
/// </remarks>
public static class PlatformOperatorValidation
{
    /// <summary>How often the stock manager refreshes the issuer metadata on its own.</summary>
    public static readonly TimeSpan AutomaticRefreshInterval = TimeSpan.FromMinutes(5);

    /// <summary>The shortest interval between two refreshes the manager will start.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>The cryptographic validation every operator token must pass before the closed profile.</summary>
    public static TokenValidationParameters CreateParameters(PlatformOperatorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new TokenValidationParameters
        {
            RequireSignedTokens = true,
            TryAllIssuerSigningKeys = false,
            ValidateIssuerSigningKey = true,
            ValidateIssuer = true,
            ValidIssuer = settings.Issuer,
            ValidateAudience = true,
            RequireAudience = true,
            ValidAudience = PlatformOperatorSettings.Audience,
            RequireExpirationTime = true,
            ValidateLifetime = true,
            ClockSkew = PlatformOperatorSettings.ClockSkew,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ValidTypes = ["JWT"],
            LogValidationExceptions = false,
            LogTokenId = false,
            IncludeTokenOnFailedValidation = false,
        };
    }

    /// <summary>
    /// The bounded metadata manager: same-origin addresses only, a validated discovery issuer, and no stale-key
    /// fallback of the stock manager's own.
    /// </summary>
    public static ConfigurationManager<OpenIdConnectConfiguration> CreateConfigurationManager(
        PlatformOperatorSettings settings,
        PlatformOperatorBackchannel backchannel,
        PlatformOperatorMetadataHealth health,
        ILogger retrieverLogger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(backchannel);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(retrieverLogger);
        return new ConfigurationManager<OpenIdConnectConfiguration>(
            settings.MetadataAddress,
            new OpenIdConnectConfigurationRetriever(),
            new PlatformOperatorDocumentRetriever(settings, backchannel, health, retrieverLogger),
            new PlatformOperatorConfigurationValidator(settings))
        {
            AutomaticRefreshInterval = AutomaticRefreshInterval,
            RefreshInterval = RefreshInterval,
            UseLastKnownGoodConfiguration = false,
        };
    }
}

/// <summary>What <see cref="PlatformOperatorTokenValidator"/> decided: a witness, or one closed cause label.</summary>
public sealed class PlatformOperatorValidationResult
{
    private PlatformOperatorValidationResult(PlatformOperatorWitness? witness, string? cause)
    {
        Witness = witness;
        Cause = cause;
    }

    /// <summary>The validated operator, or null when the token was refused.</summary>
    public PlatformOperatorWitness? Witness { get; }

    /// <summary>The closed cause label of a refusal, never a value the token carried.</summary>
    public string? Cause { get; }

    /// <summary>Whether the token was accepted.</summary>
    public bool IsValid => Witness is not null;

    internal static PlatformOperatorValidationResult Valid(PlatformOperatorWitness witness) => new(witness, null);

    internal static PlatformOperatorValidationResult Refused(string cause) => new(null, cause);
}

/// <summary>
/// The offline caller of the operator validator: the token comes from a tool's standard input or environment, not
/// from a request, and everything else is the API scheme's definition.
/// </summary>
/// <remarks>
/// A refusal carries a closed label and nothing the token held. The metadata fetch is the scheme's bounded one,
/// so an unreachable issuer refuses the operator rather than trusting a key it did not just read.
/// </remarks>
public sealed class PlatformOperatorTokenValidator(
    PlatformOperatorSettings settings,
    ConfigurationManager<OpenIdConnectConfiguration> configuration,
    PlatformOperatorMetadataHealth health,
    IClock clock)
{
    /// <summary>The longest compact token the offline validator reads.</summary>
    public const int MaximumTokenCharacters = 16 * 1024;

    private static readonly JsonWebTokenHandler Handler = new() { MapInboundClaims = false };

    /// <summary>Validates one compact access token and applies the closed profile for <paramref name="executor"/>.</summary>
    public async Task<PlatformOperatorValidationResult> ValidateAsync(
        string? token, string executor, string correlationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executor);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        if (executor is not (PlatformOperatorSettings.Executor or PlatformOperatorSettings.RebindToolExecutor))
        {
            throw new ArgumentException("The executor is not a registered operator executor.", nameof(executor));
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return PlatformOperatorValidationResult.Refused("token_missing");
        }

        if (token.Length > MaximumTokenCharacters)
        {
            return PlatformOperatorValidationResult.Refused("token_invalid");
        }

        try
        {
            var metadata = await configuration.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
            var parameters = PlatformOperatorValidation.CreateParameters(settings);
            parameters.IssuerSigningKeys = metadata.SigningKeys;
            var result = await Handler.ValidateTokenAsync(token, parameters).ConfigureAwait(false);
            if (!result.IsValid)
            {
                return PlatformOperatorValidationResult.Refused(PlatformOperatorFailure.Classify(result.Exception));
            }

            if (health.IsStale())
            {
                return PlatformOperatorValidationResult.Refused("metadata_stale");
            }

            if (result.SecurityToken is not JsonWebToken validated)
            {
                return PlatformOperatorValidationResult.Refused("token_invalid");
            }

            return PlatformOperatorTokenProfile.TryRead(
                validated, settings, clock.UtcNow, executor, correlationId, out var witness, out var cause)
                ? PlatformOperatorValidationResult.Valid(witness!)
                : PlatformOperatorValidationResult.Refused(cause.Label());
        }
        catch (Exception failure) when (failure is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return PlatformOperatorValidationResult.Refused(PlatformOperatorFailure.Classify(failure));
        }
    }
}
