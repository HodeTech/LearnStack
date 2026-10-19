using LearnStack.Api.Tenancy;
using LearnStack.SharedKernel.Time;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace LearnStack.Api.Authentication;

/// <summary>Closed tenant scheme. Production registration is deferred until P02b-5 Step 4.</summary>
internal static class TenantAuthenticationRegistration
{
    internal const string Scheme = "TenantBearer";

    internal static IServiceCollection AddLearnStackTenantAuthentication(
        this IServiceCollection services, TenantAuthenticationOptions settings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);
        IdentityModelEventSource.ShowPII = false;
        IdentityModelEventSource.LogCompleteSecurityArtifact = false;
        services.AddSingleton(settings);
        services.TryAddSingleton<IClock, SystemClock>();
        services.AddSingleton<TenantAuthenticationBackchannel>();
        services.AddLogging(logging => logging.AddFilter(
            TenantAuthenticationDiagnostics.VendorCategory, LogLevel.None));
        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = Scheme;
            options.DefaultChallengeScheme = Scheme;
        }).AddJwtBearer(Scheme, _ => { });
        services.AddOptions<JwtBearerOptions>(Scheme)
            .Configure<TenantAuthenticationBackchannel, IClock, ILogger<TenantAuthenticationEvents>>(
                (options, backchannel, clock, logger) =>
                {
                    options.Authority = settings.Issuer;
                    options.MetadataAddress = settings.MetadataAddress;
                    options.RequireHttpsMetadata = new Uri(settings.Issuer).Scheme == Uri.UriSchemeHttps;
                    options.MapInboundClaims = false;
                    options.SaveToken = false;
                    options.IncludeErrorDetails = false;
                    options.Backchannel = backchannel.Client;
                    options.BackchannelTimeout = TenantAuthenticationBackchannel.Timeout;
                    options.AutomaticRefreshInterval = TimeSpan.FromMinutes(5);
                    options.RefreshInterval = TimeSpan.FromSeconds(30);
                    options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                        settings.MetadataAddress,
                        new OpenIdConnectConfigurationRetriever(),
                        new TenantOidcDocumentRetriever(settings, backchannel))
                    {
                        AutomaticRefreshInterval = options.AutomaticRefreshInterval,
                        RefreshInterval = options.RefreshInterval,
                        UseLastKnownGoodConfiguration = false,
                    };
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        RequireSignedTokens = true,
                        ValidateIssuerSigningKey = true,
                        ValidateIssuer = true,
                        ValidIssuer = settings.Issuer,
                        ValidateAudience = true,
                        RequireAudience = true,
                        ValidAudience = TenantAuthenticationOptions.Audience,
                        IgnoreTrailingSlashWhenValidatingAudience = false,
                        RequireExpirationTime = true,
                        ValidateLifetime = true,
                        ClockSkew = TenantAuthenticationOptions.ClockSkew,
                        ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                        ValidTypes = ["JWT"],
                        LogValidationExceptions = false,
                        LogTokenId = false,
                        IncludeTokenOnFailedValidation = false,
                    };
                    options.Events = new TenantAuthenticationEvents(settings, clock, logger);
                });
        return services;
    }
}

/// <summary>Only a cryptographically validated token can publish its typed identity.</summary>
internal sealed class TenantAuthenticationEvents(
    TenantAuthenticationOptions settings,
    IClock clock,
    ILogger<TenantAuthenticationEvents> logger) : JwtBearerEvents
{
    public override Task MessageReceived(MessageReceivedContext context)
    {
        if (!HostClassificationMiddleware.ClassifiesPath(context.Request.Path)
            || context.HttpContext.Features.Get<HostClassification>() is null)
        {
            context.NoResult();
            return Task.CompletedTask;
        }

        var values = context.Request.Headers.Authorization;
        var present = context.Request.Headers.ContainsKey("Authorization");
        context.HttpContext.Features.Set(new TenantCredentialFeature { IsPresent = present });
        if (!present)
        {
            context.NoResult();
        }
        else if (values.Count != 1 || !TryToken(values[0], out var token)
            || !TenantTokenProfile.HasClosedJoseHeader(token))
        {
            TenantAuthenticationDiagnostics.Outcome(logger, "credential", "rejected");
            context.Fail("Tenant credential refused.");
        }
        else
        {
            context.Token = token;
        }

        return Task.CompletedTask;
    }

    public override Task TokenValidated(TokenValidatedContext context)
    {
        var feature = context.HttpContext.Features.Get<TenantCredentialFeature>();
        if (feature is null || !TryToken(context.Request.Headers.Authorization[0], out var token)
            || !TenantTokenProfile.TryReadValidated(
                token, settings, clock.UtcNow, out var identity, out _))
        {
            TenantAuthenticationDiagnostics.Outcome(logger, "profile", "rejected");
            context.Fail("Tenant credential refused.");
        }
        else
        {
            feature.Identity = identity;
            TenantAuthenticationDiagnostics.Outcome(logger, "validation", "accepted");
        }

        return Task.CompletedTask;
    }

    public override Task AuthenticationFailed(AuthenticationFailedContext context)
    {
        if (context.HttpContext.Features.Get<TenantCredentialFeature>() is { } feature)
        {
            feature.Identity = null;
        }

        TenantAuthenticationDiagnostics.Outcome(logger, "validation", "rejected");
        // Replace the returned failure too: base-handler event 7 sees its message.
        context.Fail("Tenant credential refused.");
        return Task.CompletedTask;
    }

    private static bool TryToken(string? header, out string token)
    {
        token = string.Empty;
        const string prefix = "Bearer ";
        if (header is null || !header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || header.Length > TenantAuthenticationOptions.MaxTokenBytes + prefix.Length)
        {
            return false;
        }

        token = header[prefix.Length..];
        return token.Length > 0 && !token.Any(char.IsWhiteSpace) && !token.Contains(',');
    }
}
