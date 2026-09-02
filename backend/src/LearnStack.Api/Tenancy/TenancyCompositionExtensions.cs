using LearnStack.Infrastructure.MultiTenancy;
using LearnStack.SharedKernel.Hosting;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LearnStack.Api.Tenancy;

/// <summary>
/// Composition-root wiring for
/// <see href="../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036</see>'s
/// anonymous, pre-authentication tier.
/// </summary>
/// <remarks>
/// Packet 4 brought the effective host, the trusted hop and the assertion recorder;
/// Packet 7 added everything host classification needs to answer a request without a
/// database — <c>Tenancy:PlatformHosts</c> and its boot-time validation, the
/// separately-capped <c>UnknownHostCache</c>, <c>HostResolutionOptions</c>, and
/// <c>IHostToTenantResolver</c> over a <c>Lazy&lt;NpgsqlDataSource&gt;</c> so a
/// platform-only deployment never builds one at all.
/// </remarks>
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
                + "the OTLP target and — from Packet 5 — the event bus and cache, so "
                + "there is no safe default: an unset key used to mean "
                + $"'{nameof(DeploymentMode.Development)}' in production. Set it in the "
                + "environment-specific appsettings file or in the environment.");
        }

        // Enum.TryParse also accepts ordinals and comma-separated lists, so
        // `Deployment__Mode=0` would parse as Development — reintroducing the
        // exact silent-default this method exists to remove, through a value
        // that looks like a typo rather than a mode. Only a declared name is
        // accepted.
        if (!Enum.TryParse<DeploymentMode>(raw, ignoreCase: true, out var mode)
            || !Enum.GetNames<DeploymentMode>().Contains(raw.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"'{DeploymentModeKey}' is '{raw}', which is not one of "
                + $"{string.Join(", ", Enum.GetNames<DeploymentMode>())}.");
        }

        return mode;
    }

    /// <summary>
    /// The host-configuration key that wires the forwarded-headers middleware
    /// without a line of code — <c>ASPNETCORE_FORWARDEDHEADERS_ENABLED</c> in
    /// the environment.
    /// </summary>
    public const string ForwardedHeadersKey = "ForwardedHeaders_Enabled";

    /// <summary>
    /// Refuses to start when forwarded headers are enabled from configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Measured, not assumed.</b> With the key set, seventy requests to
    /// <c>/healthz</c> carrying a rotating <c>X-Forwarded-For</c> produced
    /// <b>zero</b> rate-limit rejections; without it, eleven. The middleware
    /// <c>ConfigureWebDefaults</c> adds from this key runs first in the
    /// pipeline and clears <c>KnownNetworks</c>/<c>KnownProxies</c>, so
    /// <c>RemoteIpAddress</c> becomes whatever the caller wrote — which is the
    /// partition key the anonymous limiter counts on, and the storage
    /// <c>EffectiveHostAccessor</c> compares against the trusted hop's
    /// networks.
    /// </para>
    /// <para>
    /// <c>Forwarded_Headers_Are_Not_Wired</c> could not see this: it reads the
    /// assembly reference table and the text of <c>Program.cs</c>, and this
    /// path touches neither. A guard on configuration is the only thing that
    /// can, which is the same lesson <see cref="RequireDeploymentMode"/>
    /// records — the inversion was in which file carried the key.
    /// </para>
    /// <para>
    /// This is a refusal, not a correction, because there is a correct way to
    /// want forwarded headers and it is not this one: the peer must be captured
    /// <i>before</i> the middleware runs. When that ordering is built, this
    /// guard is what forces it to be designed rather than discovered.
    /// </para>
    /// </remarks>
    public static void RefuseAmbientForwardedHeaders(this IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Matched the way the framework matches it, and deliberately not with
        // GetValue<bool>: that throws a conversion error for "1", "yes", "on"
        // and "" — values a live host does **not** treat as enabling forwarded
        // headers, so the guard would have refused to start over a setting that
        // was never dangerous, with a message about type conversion rather than
        // about the hop. Measured: only a literal, case-insensitive `true`
        // makes ConfigureWebDefaults add the middleware.
        if (!string.Equals(
                configuration[ForwardedHeadersKey], "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            $"'{ForwardedHeadersKey}' is enabled (ASPNETCORE_FORWARDEDHEADERS_ENABLED). "
            + "That wires the forwarded-headers middleware ahead of everything, which "
            + "makes HttpContext.Connection.RemoteIpAddress client-supplied — the anonymous "
            + "rate limiter's partition key and the trusted hop's network check both read "
            + "it. Measured: with this key set, a client rotating X-Forwarded-For is never "
            + "rate limited. If the API needs forwarded headers, capture the peer before "
            + "them and remove this guard deliberately.");
    }

    /// <summary>
    /// Registers the effective-host accessor, the trusted-hop options and the
    /// Packet 4 assertion recorder.
    /// </summary>
    /// <remarks>
    /// Takes no <c>DeploymentMode</c>. It used to, for a hop check that skipped
    /// Development; that check is now mode-independent, and an unused parameter
    /// on a composition-root extension is an invitation to branch on it — which
    /// is the thing <c>Modules_Do_Not_Reference_DeploymentMode</c> exists to
    /// stop one layer down.
    /// </remarks>
    public static IServiceCollection AddLearnStackTenancyEdge(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Tenancy:PlatformHosts — the hosts that map to no tenant. Bound and
        // validated here so a malformed entry fails the boot rather than turning
        // the operator's own entry point into a 404 nobody sees until production.
        var platformHosts = new PlatformHostOptions
        {
            Hosts = configuration.GetSection(PlatformHostOptions.SectionName).Get<string[]>() ?? [],
        };

        platformHosts.Validate();
        services.AddSingleton(platformHosts);

        // Singleton, so the resolver's in-flight map is process-wide: a scoped one
        // gives every request its own and coalesces nothing.
        services.AddSingleton(new UnknownHostCacheOptions());
        services.AddSingleton(new HostResolutionOptions());
        services.AddSingleton<UnknownHostCache>();

        // Lazy, so the resolver's construction builds no data source: a request on
        // a platform host is answered from configuration and must cost nothing
        // below it. The composition root already registers the data source as a
        // factory rather than an instance, so this preserves that deferral instead
        // of collapsing it at the first classified request.
        services.AddSingleton(provider =>
            new Lazy<NpgsqlDataSource>(provider.GetRequiredService<NpgsqlDataSource>));
        services.AddSingleton<IHostToTenantResolver, CachedHostToTenantResolver>();

        // The membership reader that covers nothing, and the organization scope
        // validator — the two ports the reconciliation matrix consults beyond the
        // host. Both are stateless singletons; the validator shares the Lazy above,
        // so a platform-only deployment still builds no data source.
        //
        // Registered UNCONDITIONALLY, with no DeploymentMode anywhere near them. A
        // reader that were permissive in Development would reproduce exactly the
        // appsettings inversion this file argues against at the top: the mechanism
        // would be off in the environment nobody configures and on in the one that
        // does, and the demo would pass while production 404'd. Phase 03 replaces
        // DenyAllTenantMembershipReader with one that reads Membership; until then
        // "nobody is a member of anything" is the true answer, not a placeholder.
        services.AddSingleton<ITenantMembershipReader, DenyAllTenantMembershipReader>();
        services.AddSingleton<IOrganizationScopeValidator, OrganizationScopeValidator>();

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

        // A configured hop is expected but never demanded: a deployment with
        // nothing in front of the API legitimately has none, and that is both
        // lists empty. What is refused is exactly one of them — in every mode
        // and in both directions.
        //
        // Validate() above checks the shape of each entry and nothing about the
        // pair, so it did not catch this; the comment that used to sit here said
        // it did. And the check that did exist skipped Development, which is
        // where a half-configured hop is most likely to be typed and least
        // likely to be noticed, because the API answers 404 rather than
        // anything that reads as a misconfiguration.
        if (options.Networks.Count > 0 != options.Secrets.Count > 0)
        {
            throw new InvalidOperationException(
                $"'{TrustedHopOptions.SectionName}' has "
                + (options.Networks.Count > 0 ? "Networks with no Secrets" : "Secrets with no Networks")
                + ". Both are required or neither: network position alone is not the hop — on a "
                + "container bridge or a pod CIDR everything in the mesh is the gateway's "
                + "neighbour — and a secret alone is defeated by one leak into a bundle or a log.");
        }

        services.AddSingleton<EffectiveHostAccessor>();
        services.AddSingleton<ITenantAssertionRecorder, LoggingTenantAssertionRecorder>();

        // The only registered IIdempotencyStore. Correct for one instance and
        // wrong for two, and it stays registered anyway: ADR-0037 Amendment 1
        // separates the table from the store. Packet 6 ships idempotency_keys
        // because the schema is a one-way door; the durable store is additive and
        // ships on its ADR-0035 trigger — the first [Idempotent] endpoint, or the
        // first deployment running more than one instance. Standards 04's
        // "required for payment operations" list has no member yet.
        services.AddSingleton<LearnStack.SharedKernel.Idempotency.IIdempotencyStore,
            LearnStack.Infrastructure.Idempotency.InMemoryIdempotencyStore>();

        return services;
    }
}
