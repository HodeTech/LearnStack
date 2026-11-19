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
        services.AddAuthentication().AddJwtBearer(Scheme, _ => { });
        services.AddAuthorization(options => options.AddPolicy(
            PolicyName, policy => policy.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser()));
        services.AddOptions<JwtBearerOptions>(Scheme)
            .Configure<IClock, ILogger<PlatformOperatorEvents>>((options, clock, logger) =>
            {
                options.Authority = settings.Issuer;
                options.MetadataAddress = settings.MetadataAddress;
                options.RequireHttpsMetadata = new Uri(settings.Issuer).Scheme == Uri.UriSchemeHttps;
                options.MapInboundClaims = false;
                options.SaveToken = false;
                options.IncludeErrorDetails = false;
                options.AutomaticRefreshInterval = TimeSpan.FromMinutes(5);
                options.RefreshInterval = TimeSpan.FromSeconds(30);
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    RequireSignedTokens = true,
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

/// <summary>The one minter the operator authentication boundary owns (ADR-0101 § 7).</summary>
internal sealed class PlatformOperatorWitnessMinter : PlatformOperatorWitness.Minter
{
    internal static PlatformOperatorWitness Create(
        string issuer, string subjectDigest, string acr, DateTimeOffset authenticatedAt, string? correlationId) =>
        Mint(issuer, subjectDigest, acr, authenticatedAt, PlatformOperatorSettings.Executor, correlationId);
}

/// <summary>Scopes the scheme to its prefix and applies the closed token profile.</summary>
internal sealed class PlatformOperatorEvents(
    PlatformOperatorSettings settings, IClock clock, ILogger<PlatformOperatorEvents> logger) : JwtBearerEvents
{
    public override Task MessageReceived(MessageReceivedContext context)
    {
        if (!context.Request.Path.StartsWithSegments(PlatformOperatorSettings.RoutePrefix, StringComparison.Ordinal))
        {
            context.NoResult();
            return Task.CompletedTask;
        }

        var values = context.Request.Headers.Authorization;
        if (values.Count != 1
            || values[0] is not { } header
            || !header.StartsWith("Bearer ", StringComparison.Ordinal)
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
        if (context.SecurityToken is not JsonWebToken token
            || !PlatformOperatorTokenProfile.TryRead(token, settings, clock.UtcNow, Activity.Current?.Id, out var witness))
        {
            context.Fail("Operator credential refused.");
            return Task.CompletedTask;
        }

        context.HttpContext.Features.Set(new PlatformOperatorFeature(witness!));
        return Task.CompletedTask;
    }

    public override Task Challenge(JwtBearerChallengeContext context)
    {
        // One answer for a missing, malformed, expired or wrongly profiled credential: no
        // detail a probing caller could use, a counter and a log an operator can alert on.
        var cause = context.AuthenticateFailure is null ? "token_missing" : "token_invalid";
        PlatformOperatorMetrics.Unauthenticated(cause);
        LogUnauthenticated(logger, cause, null);
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

    private static readonly Action<ILogger, string, Exception?> LogUnauthenticated =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(7103, nameof(LogUnauthenticated)),
            "Platform operator credential refused: {Cause}.");
}

/// <summary>
/// The closed profile a cryptographically valid operator token must also meet (ADR-0101 § 2, § 3).
/// </summary>
/// <remarks>
/// A pure function of the validated token, the settings and the clock, so the scheme, the real-realm
/// proof and the offline rebind tool judge one definition. It admits an allow-listed <c>azp</c>, an
/// accepted <c>acr</c>, a lifetime of at most 15 minutes, and an <c>auth_time</c> that is neither in
/// the future nor older than the configured age. Nothing else in the token carries authority.
/// </remarks>
internal static class PlatformOperatorTokenProfile
{
    internal static bool TryRead(
        JsonWebToken token,
        PlatformOperatorSettings settings,
        DateTimeOffset now,
        string? correlationId,
        out PlatformOperatorWitness? witness)
    {
        witness = null;
        if (string.IsNullOrWhiteSpace(token.Subject)
            || !token.TryGetPayloadValue<string>("azp", out var azp) || !settings.AuthorizedParties.Contains(azp)
            || !token.TryGetPayloadValue<string>("acr", out var acr) || !settings.AcceptedAcr.Contains(acr)
            || !token.TryGetPayloadValue<long>("iat", out var iat)
            || !token.TryGetPayloadValue<long>("auth_time", out var authTime)
            || token.ValidTo == DateTime.MinValue)
        {
            return false;
        }

        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(iat);
        var authenticatedAt = DateTimeOffset.FromUnixTimeSeconds(authTime);
        var lifetime = new DateTimeOffset(token.ValidTo, TimeSpan.Zero) - issuedAt;
        if (lifetime <= TimeSpan.Zero || lifetime > PlatformOperatorSettings.MaxTokenLifetime
            || authenticatedAt > now + PlatformOperatorSettings.ClockSkew
            || now - authenticatedAt > settings.MaxAuthenticationAge)
        {
            return false;
        }

        witness = PlatformOperatorWitnessMinter.Create(
            settings.Issuer,
            PlatformOperatorSubject.Digest(settings.Issuer, token.Subject),
            acr,
            authenticatedAt,
            correlationId);
        return true;
    }
}
