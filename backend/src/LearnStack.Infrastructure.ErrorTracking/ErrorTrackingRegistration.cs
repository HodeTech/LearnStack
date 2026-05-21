using LearnStack.SharedKernel.Hosting;
using LearnStack.SharedKernel.Observability;
using LearnStack.SharedKernel.Secrets;
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
/// <remarks>
/// Sentry DSN reads through <see cref="ISecretProvider"/> per ADR-0032
/// § Sub-decision 9. Phase 02a Packet 3 ships the
/// <c>ConfigurationSecretProvider</c> default — Vault-equipped deployments
/// pick up the Dapr-backed implementation in Packet 5 without changing
/// this code path.
/// </remarks>
public static class ErrorTrackingRegistration
{
    public static IServiceCollection AddLearnStackErrorTracking(
        this IServiceCollection services,
        ISecretProvider secretProvider,
        IConfiguration configuration,
        DeploymentMode deploymentMode)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(secretProvider);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<ErrorTrackingOptions>(
            configuration.GetSection(ErrorTrackingOptions.SectionName));

        var options = configuration.GetSection(ErrorTrackingOptions.SectionName)
            .Get<ErrorTrackingOptions>() ?? new ErrorTrackingOptions();

        // ADR-0032 § Sub-decision 9 binds DSN lookup to the secret provider.
        // ConfigurationSecretProvider falls through to IConfiguration so
        // dev / CI keep working with appsettings or env vars; Packet 5's
        // DaprSecretProvider reads from Vault in production.
        var resolvedDsn = secretProvider.GetSecret("ErrorTracking:Sentry:Dsn");

        switch (deploymentMode)
        {
            case DeploymentMode.Development:
                services.AddSingleton<IErrorTrackingProvider, NoOpErrorTracker>();
                break;

            case DeploymentMode.SaaS:
            case DeploymentMode.Dedicated:
                InitSentry(resolvedDsn, options.Sentry);
                services.AddSingleton<IErrorTrackingProvider, SentryErrorTracker>();
                break;

            case DeploymentMode.SelfHostedOnline:
                if (!string.IsNullOrWhiteSpace(resolvedDsn))
                {
                    InitSentry(resolvedDsn, options.Sentry);
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

    private static void InitSentry(string? dsn, SentrySettings options)
    {
        if (string.IsNullOrWhiteSpace(dsn))
        {
            throw new InvalidOperationException(
                "DeploymentMode requires a Sentry DSN but ISecretProvider returned null for "
                + "'ErrorTracking:Sentry:Dsn'. Provide the DSN via the secret provider — Vault "
                + "in production, env var or appsettings in dev (the ConfigurationSecretProvider "
                + "fall-through). Per ADR-0032 § Sub-decision 9.");
        }

        SentrySdk.Init(o =>
        {
            o.Dsn = dsn;
            o.Environment = options.Environment;
            o.TracesSampleRate = options.TracesSampleRate;
        });
    }
}
