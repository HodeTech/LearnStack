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
/// audience <c>learnstack-platform</c>, lifetime and algorithm come from the stock handler, configured by
/// <see cref="PlatformOperatorValidation"/>, the definition the offline rebind tool shares;
/// <see cref="PlatformOperatorEvents.TokenValidated"/> then requires an allow-listed
/// <c>azp</c>, a token lifetime of at most 15 minutes, an accepted <c>acr</c> and a recent
/// <c>auth_time</c>. Roles and every other claim carry no authority.
/// </para>
/// <para>
/// <b>Bounded metadata.</b> The discovery document and key set come through a client of the
/// scheme's own that follows no redirect, waits five seconds, reads at most 256 KB and fetches
/// nothing outside the issuer's origin. A failed fetch makes the next one fail at once for 30
/// seconds, and keys that a failed refresh could not renew for 15 minutes are no longer trusted
/// (<see cref="PlatformOperatorMetadataHealth"/>): an issuer that stays unreachable refuses every
/// operator, which is the safe side of an outage. The stock manager's own last-known-good fallback
/// is off.
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
        services.AddSingleton<PlatformOperatorMetadataHealth>();
        services.AddAuthentication().AddJwtBearer(Scheme, _ => { });
        services.AddAuthorization(options => options.AddPolicy(
            PolicyName, policy => policy.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser()));
        services.AddOptions<JwtBearerOptions>(Scheme)
            .Configure<PlatformOperatorBackchannel, IClock, ILogger<PlatformOperatorEvents>, ILoggerFactory,
                PlatformOperatorMetadataHealth>(
                (options, backchannel, clock, logger, loggerFactory, health) =>
            {
                options.Authority = settings.Issuer;
                options.MetadataAddress = settings.MetadataAddress;
                options.RequireHttpsMetadata = new Uri(settings.Issuer).Scheme == Uri.UriSchemeHttps;
                options.MapInboundClaims = false;
                options.SaveToken = false;
                options.IncludeErrorDetails = false;
                options.Backchannel = backchannel.Client;
                options.BackchannelTimeout = PlatformOperatorBackchannel.Timeout;
                options.AutomaticRefreshInterval = PlatformOperatorValidation.AutomaticRefreshInterval;
                options.RefreshInterval = PlatformOperatorValidation.RefreshInterval;
                options.ConfigurationManager = PlatformOperatorValidation.CreateConfigurationManager(
                    settings, backchannel, health, loggerFactory);
                options.TokenValidationParameters = PlatformOperatorValidation.CreateParameters(settings);
                options.Events = new PlatformOperatorEvents(settings, clock, health, logger);
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
    PlatformOperatorSettings settings,
    IClock clock,
    PlatformOperatorMetadataHealth health,
    ILogger<PlatformOperatorEvents> logger) : JwtBearerEvents
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
        if (health.IsStale())
        {
            context.Fail(new PlatformOperatorProfileException("metadata_stale"));
            return Task.CompletedTask;
        }

        if (context.SecurityToken is not JsonWebToken token)
        {
            context.Fail(new PlatformOperatorProfileException("token_invalid"));
            return Task.CompletedTask;
        }

        if (!PlatformOperatorTokenProfile.TryRead(
                token, settings, clock.UtcNow, PlatformOperatorSettings.Executor, correlationId, out var witness, out var cause))
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
        // A caller that hung up mid-validation is not an issuer fault, whatever exception the abort raised.
        var cause = context.HttpContext.RequestAborted.IsCancellationRequested && context.AuthenticateFailure is not null
            ? "request_aborted"
            : PlatformOperatorFailure.Classify(context.AuthenticateFailure);
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
