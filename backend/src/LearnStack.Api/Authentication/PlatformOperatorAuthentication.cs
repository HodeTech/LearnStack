using System.Diagnostics;
using LearnStack.Api.Common;
using LearnStack.Infrastructure.PlatformOperator;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Time;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace LearnStack.Api.Authentication;

/// <summary>
/// The second bearer scheme, confined to the operator prefix (ADR-0101 § 2, ADR-0064
/// Amendment 9).
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a default scheme.</b> The tenant scheme stays the default and the only scheme on
/// <c>/api/v1</c>. This one authenticates only where an endpoint names it, and its
/// <c>MessageReceived</c> ignores any path outside <c>/api/platform</c>, so a tenant route
/// can never be authenticated by it. The tenant scheme in turn ignores the operator prefix:
/// it classifies by host and the prefix is unclassified.
/// </para>
/// <para>
/// <b>Stock cryptographic validation, then a closed profile.</b> Signature, exact issuer,
/// audience <c>learnstack-platform</c>, lifetime and algorithm come from the stock handler;
/// <see cref="PlatformOperatorEvents.TokenValidated"/> then requires an allow-listed
/// <c>azp</c>, a token lifetime of at most 15 minutes, an accepted <c>acr</c> and a recent
/// <c>auth_time</c>. Roles and every other claim carry no authority.
/// </para>
/// <para>
/// <b>Bounded metadata.</b> The discovery document and key set come through a client of the
/// scheme's own that follows no redirect, waits five seconds, reads at most 256 KB and fetches
/// nothing outside the issuer's origin. The last known good configuration is off: an issuer
/// that cannot be reached refuses every operator, which is the safe side of an outage.
/// </para>
/// </remarks>
internal static class PlatformOperatorAuthentication
{
    internal const string Scheme = "PlatformOperator";

    /// <summary>The named policy every operator endpoint requires.</summary>
    /// <remarks>
    /// An explicit policy, not <c>[Authorize(AuthenticationSchemes = ...)]</c> alone: the host's
    /// default and fallback policies carry only the declared-operation requirement and no
    /// authenticated-user requirement, so a scheme-only attribute would authenticate the token
    /// and then admit an anonymous caller. This policy requires an authenticated principal from
    /// this scheme and nothing from the tenant scheme.
    /// </remarks>
    internal const string PolicyName = "PlatformOperator";

    internal static IServiceCollection AddLearnStackPlatformOperatorAuthentication(
        this IServiceCollection services, PlatformOperatorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);
        IdentityModelEventSource.ShowPII = false;
        IdentityModelEventSource.LogCompleteSecurityArtifact = false;
        services.AddSingleton(settings);
        services.TryAddSingleton<IClock, SystemClock>();
        services.AddSingleton<PlatformOperatorBackchannel>();
        services.AddAuthentication().AddJwtBearer(Scheme, _ => { });
        services.AddAuthorization(options => options.AddPolicy(
            PolicyName, policy => policy.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser()));
        services.AddOptions<JwtBearerOptions>(Scheme)
            .Configure<PlatformOperatorBackchannel, IClock, ILogger<PlatformOperatorEvents>, ILogger<PlatformOperatorDocumentRetriever>>(
                (options, backchannel, clock, logger, retrieverLogger) =>
            {
                options.Authority = settings.Issuer;
                options.MetadataAddress = settings.MetadataAddress;
                options.RequireHttpsMetadata = new Uri(settings.Issuer).Scheme == Uri.UriSchemeHttps;
                options.MapInboundClaims = false;
                options.SaveToken = false;
                options.IncludeErrorDetails = false;
                options.Backchannel = backchannel.Client;
                options.BackchannelTimeout = PlatformOperatorBackchannel.Timeout;
                options.AutomaticRefreshInterval = TimeSpan.FromMinutes(5);
                options.RefreshInterval = TimeSpan.FromSeconds(30);
                options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                    settings.MetadataAddress,
                    new OpenIdConnectConfigurationRetriever(),
                    new PlatformOperatorDocumentRetriever(settings, backchannel, retrieverLogger),
                    new PlatformOperatorConfigurationValidator(settings))
                {
                    AutomaticRefreshInterval = options.AutomaticRefreshInterval,
                    RefreshInterval = options.RefreshInterval,
                    UseLastKnownGoodConfiguration = false,
                };
                options.TokenValidationParameters = new TokenValidationParameters
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
                options.Events = new PlatformOperatorEvents(settings, clock, logger);
            });
        return services;
    }
}

/// <summary>The validated operator witness, attached to the request by the scheme.</summary>
internal sealed class PlatformOperatorFeature(PlatformOperatorWitness witness)
{
    internal PlatformOperatorWitness Witness { get; } = witness;
}

/// <summary>Scopes the scheme to its prefix and applies the closed token profile.</summary>
internal sealed class PlatformOperatorEvents(
    PlatformOperatorSettings settings, IClock clock, ILogger<PlatformOperatorEvents> logger) : JwtBearerEvents
{
    public override Task MessageReceived(MessageReceivedContext context)
    {
        if (!context.Request.Path.StartsWithSegments(PlatformOperatorSettings.RoutePrefix, StringComparison.OrdinalIgnoreCase))
        {
            context.NoResult();
            return Task.CompletedTask;
        }

        var values = context.Request.Headers.Authorization;
        if (values.Count != 1
            || values[0] is not { } header
            || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            || header.Length <= "Bearer ".Length)
        {
            context.NoResult();
            return Task.CompletedTask;
        }

        context.Token = header["Bearer ".Length..];
        return Task.CompletedTask;
    }

    public override Task TokenValidated(TokenValidatedContext context)
    {
        // The correlation id is the request's trace when one exists and the server's request
        // id otherwise, so a result row is never recorded without a handle an operator can use.
        var correlationId = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
        if (context.SecurityToken is not JsonWebToken token)
        {
            context.Fail(new PlatformOperatorProfileException("token_invalid"));
            return Task.CompletedTask;
        }

        if (!PlatformOperatorTokenProfile.TryRead(token, settings, clock.UtcNow, correlationId, out var witness, out var cause))
        {
            context.Fail(new PlatformOperatorProfileException(cause.Label()));
            return Task.CompletedTask;
        }

        context.HttpContext.Features.Set(new PlatformOperatorFeature(witness!));
        return Task.CompletedTask;
    }

    public override Task AuthenticationFailed(AuthenticationFailedContext context)
    {
        // The stock handler rethrows what it did not classify, an unreachable issuer included,
        // which would answer 500 and hide the cause. Failing here keeps one answer and lets
        // the challenge below name the cause on the counter and the log.
        context.Fail(context.Exception);
        return Task.CompletedTask;
    }

    public override Task Challenge(JwtBearerChallengeContext context)
    {
        // One answer for a missing, malformed, expired or wrongly profiled credential: no
        // detail a probing caller could use, a counter and a log an operator can alert on.
        var cause = PlatformOperatorFailure.Classify(context.AuthenticateFailure);
        PlatformOperatorMetrics.Unauthenticated(cause);
        LogUnauthenticated(logger, cause, context.AuthenticateFailure?.GetType().Name ?? "none", null);
        context.HandleResponse();
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return Respond(context.HttpContext, StatusCodes.Status401Unauthorized, "lockey_unauthorized");
    }

    public override Task Forbidden(ForbiddenContext context) =>
        Respond(context.HttpContext, StatusCodes.Status403Forbidden, "lockey_forbidden");

    internal static Task Respond(HttpContext http, int status, string key)
    {
        var error = new Error(new LocalizedMessage(key));
        http.Response.StatusCode = status;
        return http.Response.WriteAsJsonAsync(
            ProblemDetailsFactory.For(error, http),
            options: null,
            contentType: ProblemDetailsMediaType.Value,
            cancellationToken: http.RequestAborted);
    }

    private static readonly Action<ILogger, string, string, Exception?> LogUnauthenticated =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(7103, nameof(LogUnauthenticated)),
            "Platform operator credential refused: {Cause} ({FailureType}).");
}

/// <summary>Why a cryptographically valid operator token failed the closed profile.</summary>
internal enum PlatformOperatorTokenCause
{
    Subject = 0,
    Issuer,
    AuthorizedParty,
    AuthenticationContext,
    IssuedAt,
    AuthenticationTime,
    Lifetime,
    AuthenticationAge,
}

/// <summary>The bounded label set a counter carries; nothing a caller wrote reaches it.</summary>
internal static class PlatformOperatorTokenCauseLabels
{
    internal static string Label(this PlatformOperatorTokenCause cause) => cause switch
    {
        PlatformOperatorTokenCause.Subject => "profile_subject",
        PlatformOperatorTokenCause.Issuer => "profile_issuer",
        PlatformOperatorTokenCause.AuthorizedParty => "profile_azp",
        PlatformOperatorTokenCause.AuthenticationContext => "profile_acr",
        PlatformOperatorTokenCause.IssuedAt => "profile_iat",
        PlatformOperatorTokenCause.AuthenticationTime => "profile_auth_time",
        PlatformOperatorTokenCause.Lifetime => "profile_lifetime",
        PlatformOperatorTokenCause.AuthenticationAge => "profile_auth_age",
        _ => "profile_invalid",
    };
}

/// <summary>Carries a closed profile label from <c>TokenValidated</c> to the challenge.</summary>
internal sealed class PlatformOperatorProfileException(string label) : Exception("Operator credential refused.")
{
    internal string Label { get; } = label;
}

/// <summary>Maps an authentication failure to a closed label; the response never varies.</summary>
internal static class PlatformOperatorFailure
{
    internal static string Classify(Exception? failure) => failure switch
    {
        null => "token_missing",
        PlatformOperatorProfileException profile => profile.Label,
        AggregateException { InnerExceptions.Count: > 0 } aggregate => Classify(aggregate.InnerExceptions[0]),
        SecurityTokenExpiredException => "token_expired",
        SecurityTokenNotYetValidException or SecurityTokenNoExpirationException or SecurityTokenInvalidLifetimeException
            => "token_lifetime",
        SecurityTokenInvalidSignatureException or SecurityTokenSignatureKeyNotFoundException => "token_signature",
        SecurityTokenInvalidIssuerException => "token_issuer",
        SecurityTokenInvalidAudienceException => "token_audience",
        SecurityTokenInvalidAlgorithmException => "token_algorithm",
        SecurityTokenInvalidTypeException => "token_type",
        _ when IsMetadataFailure(failure) => "metadata_unavailable",
        _ => "token_invalid",
    };

    // The stock manager wraps every retrieval failure in an InvalidOperationException (IDX20803)
    // whose cause is the transport or the retriever's own refusal.
    private static bool IsMetadataFailure(Exception failure)
    {
        for (var current = failure; current is not null; current = current.InnerException)
        {
            if (current is IOException or HttpRequestException or TaskCanceledException)
            {
                return true;
            }
        }

        return failure is InvalidOperationException && failure.Message.Contains("IDX20803", StringComparison.Ordinal);
    }
}

/// <summary>
/// The closed profile a cryptographically valid operator token must also meet (ADR-0101 § 2, § 3).
/// </summary>
/// <remarks>
/// <para>
/// A pure function of the validated token, the settings and the clock, so the scheme, the real-realm
/// proof and the offline rebind tool judge one definition. It admits the pinned <c>iss</c>, an
/// allow-listed <c>azp</c>, an accepted <c>acr</c>, a lifetime of at most 15 minutes, and an
/// <c>auth_time</c> that is neither in the future nor older than the configured age. Nothing else
/// in the token carries authority.
/// </para>
/// <para>
/// <b>It owns the witness minter.</b> The minter is nested and private, so no other type in the
/// assembly can mint a witness, and a source census pins who may call <see cref="TryRead"/>.
/// </para>
/// </remarks>
internal static class PlatformOperatorTokenProfile
{
    internal static bool TryRead(
        JsonWebToken token,
        PlatformOperatorSettings settings,
        DateTimeOffset now,
        string? correlationId,
        out PlatformOperatorWitness? witness,
        out PlatformOperatorTokenCause cause)
    {
        witness = null;
        cause = default;

        // Stock validation already pinned the issuer; this restates it so the profile is
        // correct for any caller, including one that validated with other parameters.
        if (!string.Equals(token.Issuer, settings.Issuer, StringComparison.Ordinal))
        {
            cause = PlatformOperatorTokenCause.Issuer;
            return false;
        }

        if (string.IsNullOrWhiteSpace(token.Subject))
        {
            cause = PlatformOperatorTokenCause.Subject;
            return false;
        }

        if (!token.TryGetPayloadValue<string>("azp", out var azp) || !settings.AuthorizedParties.Contains(azp))
        {
            cause = PlatformOperatorTokenCause.AuthorizedParty;
            return false;
        }

        if (!token.TryGetPayloadValue<string>("acr", out var acr) || !settings.AcceptedAcr.Contains(acr))
        {
            cause = PlatformOperatorTokenCause.AuthenticationContext;
            return false;
        }

        if (!token.TryGetPayloadValue<long>("iat", out var iat) || !IsUnixSeconds(iat) || token.ValidTo == DateTime.MinValue)
        {
            cause = PlatformOperatorTokenCause.IssuedAt;
            return false;
        }

        if (!token.TryGetPayloadValue<long>("auth_time", out var authTime) || !IsUnixSeconds(authTime))
        {
            cause = PlatformOperatorTokenCause.AuthenticationTime;
            return false;
        }

        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(iat);
        var authenticatedAt = DateTimeOffset.FromUnixTimeSeconds(authTime);
        var lifetime = new DateTimeOffset(token.ValidTo, TimeSpan.Zero) - issuedAt;
        if (lifetime <= TimeSpan.Zero || lifetime > PlatformOperatorSettings.MaxTokenLifetime)
        {
            cause = PlatformOperatorTokenCause.Lifetime;
            return false;
        }

        if (authenticatedAt > now + PlatformOperatorSettings.ClockSkew || now - authenticatedAt > settings.MaxAuthenticationAge)
        {
            cause = PlatformOperatorTokenCause.AuthenticationAge;
            return false;
        }

        witness = WitnessMinter.Create(
            settings.Issuer,
            PlatformOperatorSubject.Digest(settings.Issuer, token.Subject),
            acr,
            authenticatedAt,
            correlationId);
        return true;
    }

    // DateTimeOffset.FromUnixTimeSeconds throws outside year 1..9999; a claim an issuer signed
    // is still not trusted to be in range.
    private static bool IsUnixSeconds(long value) => value is >= -62135596800L and <= 253402300799L;

    /// <summary>The one minter the operator authentication boundary owns (ADR-0101 § 7).</summary>
    private sealed class WitnessMinter : PlatformOperatorWitness.Minter
    {
        internal static PlatformOperatorWitness Create(
            string issuer, string subjectDigest, string acr, DateTimeOffset authenticatedAt, string? correlationId) =>
            Mint(issuer, subjectDigest, acr, authenticatedAt, PlatformOperatorSettings.Executor, correlationId);
    }
}
