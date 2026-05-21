using LearnStack.SharedKernel.Hosting;
using LearnStack.SharedKernel.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sentry;

namespace LearnStack.Infrastructure.ErrorTracking;

/// <summary>
/// Composition-root extension that branches on <see cref="DeploymentMode"/>
/// to pick the right <see cref="IErrorTrackingProvider"/> implementation per
/// ADR-0032 § Sub-decision 9. Sentry SDK initialisation happens here too —
/// modules never call <c>SentrySdk.Init</c> directly.
/// </summary>
public static class ErrorTrackingRegistration
{
    public static IServiceCollection AddLearnStackErrorTracking(
        this IServiceCollection services,
        IConfiguration configuration,
        DeploymentMode deploymentMode)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<ErrorTrackingOptions>(
            configuration.GetSection(ErrorTrackingOptions.SectionName));

        var options = configuration.GetSection(ErrorTrackingOptions.SectionName)
            .Get<ErrorTrackingOptions>() ?? new ErrorTrackingOptions();

        switch (deploymentMode)
        {
            case DeploymentMode.Development:
                services.AddSingleton<IErrorTrackingProvider, NoOpErrorTracker>();
                break;

            case DeploymentMode.SaaS:
            case DeploymentMode.Dedicated:
                InitSentry(options.Sentry);
                services.AddSingleton<IErrorTrackingProvider, SentryErrorTracker>();
                break;

            case DeploymentMode.SelfHostedOnline:
                if (!string.IsNullOrWhiteSpace(options.Sentry.Dsn))
                {
                    InitSentry(options.Sentry);
                    services.AddSingleton<IErrorTrackingProvider, SentryErrorTracker>();
                }
                else
                {
                    services.AddSingleton<IErrorTrackingProvider, NoOpErrorTracker>();
                }
                break;

            case DeploymentMode.SelfHostedAirGapped:
                services.AddSingleton<IErrorTrackingProvider>(sp =>
                    new LocalFileErrorTracker(
                        options.LocalFile.Directory,
                        sp.GetRequiredService<ILogger<LocalFileErrorTracker>>()));
                break;

            default:
                throw new System.Diagnostics.UnreachableException(
                    $"Unhandled DeploymentMode '{deploymentMode}' in error-tracking composition.");
        }

        return services;
    }

    private static void InitSentry(SentrySettings options)
    {
        if (string.IsNullOrWhiteSpace(options.Dsn))
        {
            throw new InvalidOperationException(
                "DeploymentMode requires a Sentry DSN but ErrorTracking:Sentry:Dsn is empty. "
                + "Provide the DSN via ISecretProvider (per ADR-0032 § Sub-decision 9).");
        }

        SentrySdk.Init(o =>
        {
            o.Dsn = options.Dsn;
            o.Environment = options.Environment;
            o.TracesSampleRate = options.TracesSampleRate;
        });
    }
}
