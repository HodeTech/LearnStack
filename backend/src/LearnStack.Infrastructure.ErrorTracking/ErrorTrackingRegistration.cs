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
/// <c>ConfigurationSecretProvider</c> default; the Vault-backed
/// <c>DaprSecretProvider</c> is demand-gated to Phase 11 per ADR-0035 —
/// trigger: a production secret must rotate without a redeploy, or more than
/// one operator needs access to production secrets. Either way this code path
/// does not change.
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
        // dev / CI keep working with appsettings or env vars. The Vault-backed
        // DaprSecretProvider is demand-gated to Phase 11 per ADR-0035 (trigger:
        // a secret must rotate without a redeploy, or a second operator needs
        // access), and reads the same key through the same seam.
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

        // Sentry's TracesSampleRate setter throws when the value is outside
        // [0, 1]. A mis-typed appsettings value (e.g. 1.5) would otherwise
        // crash startup with a cryptic Sentry error; clamp defensively so
        // the misconfiguration degrades to "sample everything / nothing"
        // rather than a boot failure.
        var sampleRate = Math.Clamp(options.TracesSampleRate, 0.0, 1.0);

        SentrySdk.Init(o =>
        {
            o.Dsn = dsn;
            o.Environment = options.Environment;
            o.TracesSampleRate = sampleRate;
        });
    }
}
