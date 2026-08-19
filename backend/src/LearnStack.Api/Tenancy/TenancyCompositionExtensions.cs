using LearnStack.SharedKernel.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LearnStack.Api.Tenancy;

/// <summary>
/// Composition-root wiring for the Packet 4 half of
/// <see href="../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036</see>:
/// the effective host, the trusted hop, and the assertion recorder.
/// </summary>
public static class TenancyCompositionExtensions
{
    public const string DeploymentModeKey = "Deployment:Mode";

    /// <summary>
    /// Reads <c>Deployment:Mode</c> and refuses to start without it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The key used to ship as <c>"Development"</c> in
    /// <c>appsettings.json</c> — the file that goes to <b>every</b> environment
    /// — with the same value as the code default, and
    /// <c>appsettings.Development.json</c> set no <c>Deployment</c> key at all.
    /// So every Development-guarded mechanism was on by default in a deployment
    /// that never set the key, and no amount of guarding on the value could
    /// have caught it: the inversion was in which file carried it.
    /// </para>
    /// <para>
    /// There is no default here on purpose. A default is what turned an
    /// unconfigured production host into a development one silently; a startup
    /// failure naming the key is the version of that mistake an operator can
    /// see.
    /// </para>
    /// </remarks>
    public static DeploymentMode RequireDeploymentMode(this IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var raw = configuration[DeploymentModeKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException(
                $"'{DeploymentModeKey}' is not configured. It selects the error tracker, "
                + "the OTLP target and — from Packet 5 — the event bus, cache and secret "
                + "provider, so there is no safe default: an unset key used to mean "
                + $"'{nameof(DeploymentMode.Development)}' in production. Set it in the "
                + "environment-specific appsettings file or in the environment.");
        }

        if (!Enum.TryParse<DeploymentMode>(raw, ignoreCase: true, out var mode))
        {
            throw new InvalidOperationException(
                $"'{DeploymentModeKey}' is '{raw}', which is not one of "
                + $"{string.Join(", ", Enum.GetNames<DeploymentMode>())}.");
        }

        return mode;
    }

    /// <summary>
    /// Registers the effective-host accessor, the trusted-hop options and the
    /// Packet 4 assertion recorder.
    /// </summary>
    public static IServiceCollection AddLearnStackTenancyEdge(
        this IServiceCollection services,
        IConfiguration configuration,
        DeploymentMode deploymentMode)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<TrustedHopOptions>(
            configuration.GetSection(TrustedHopOptions.SectionName));

        var options = new TrustedHopOptions();
        configuration.GetSection(TrustedHopOptions.SectionName).Bind(options);

        var problems = options.Validate();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                $"'{TrustedHopOptions.SectionName}' is misconfigured: "
                + string.Join(" ", problems)
                + " A hop that is half-configured is a hop that silently is not one, and "
                + "the failure only shows as an anonymous page render answering 404.");
        }

        // Outside Development a configured hop is expected but not demanded:
        // a deployment with nothing in front of the API legitimately has none.
        // What is refused is a half-configured one, above.
        if (deploymentMode != DeploymentMode.Development && options.Networks.Count > 0
            && options.Secrets.Count == 0)
        {
            throw new InvalidOperationException(
                $"'{TrustedHopOptions.SectionName}:Networks' is set with no Secrets. "
                + "Network position alone is not the hop — on a container bridge or a pod "
                + "CIDR everything in the mesh is the gateway's neighbour.");
        }

        services.AddSingleton<EffectiveHostAccessor>();
        services.AddSingleton<ITenantAssertionRecorder, LoggingTenantAssertionRecorder>();

        return services;
    }
}
