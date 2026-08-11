using LearnStack.Api.Common;
using LearnStack.Application.Pipeline;
using LearnStack.Infrastructure.ErrorTracking;
using LearnStack.Infrastructure.Observability;
using LearnStack.Infrastructure.Observability.Serilog;
using LearnStack.SharedKernel.Hosting;
using LearnStack.SharedKernel.Secrets;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;
using Serilog;

namespace LearnStack.Api.Composition;

/// <summary>
/// Composition-root extension that wires the entire ADR-0032 surface in one
/// disciplined pass — Serilog, OpenTelemetry, error tracking,
/// <see cref="LearnStackExceptionHandler"/>, MediatR pipeline, the singleton
/// <see cref="ITenantContextAccessor"/>, and the request-scoped
/// <see cref="ITenantContext"/> default. The <c>wire-cross-cutting-foundation</c>
/// skill is the long-form walk; this method is the binary.
/// </summary>
public static class CrossCuttingFoundationExtensions
{
    /// <summary>
    /// Wires the cross-cutting foundation against the supplied
    /// <paramref name="builder"/>. The Serilog bootstrap runs first so
    /// startup errors are captured; OpenTelemetry tracing + metrics binds
    /// next; <see cref="IErrorTrackingProvider"/> branches by
    /// <paramref name="deploymentMode"/>; the MediatR pipeline registers the
    /// eight canonical behaviors.
    /// </summary>
    public static WebApplicationBuilder AddLearnStackCrossCuttingFoundation(
        this WebApplicationBuilder builder,
        DeploymentMode deploymentMode,
        params System.Reflection.Assembly[] mediatorHandlerAssemblies)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(mediatorHandlerAssemblies);

        // The Serilog OTLP sink + the OTel pipeline both need
        // ITenantContextAccessor (via enrichers / processor). Register the
        // observability services first so DI sees the accessor as a
        // singleton before either pipeline builds.
        builder.Services.AddLearnStackObservabilityServices();

        // ISecretProvider socket lands now so non-Dev DSN / license-key reads
        // route through one seam. SelectSecretProvider is the SINGLE site
        // that picks the implementation per DeploymentMode — both the DI
        // registration and the local AddLearnStackErrorTracking call read
        // the same instance. Phase 11 extends SelectSecretProvider with the
        // Dapr branch so adding DaprSecretProvider touches one line, not
        // two.
        var secretProvider = SelectSecretProvider(deploymentMode, builder.Configuration);
        builder.Services.TryAddSingleton<ISecretProvider>(secretProvider);

        WireSerilog(builder, deploymentMode);
        WireOpenTelemetry(builder, deploymentMode);

        builder.Services.AddLearnStackErrorTracking(
            secretProvider, builder.Configuration, deploymentMode);

        // Request-scoped ITenantContext default — Packet 7 swaps this for the
        // resolved instance produced by TenantResolverMiddleware. The
        // singleton ITenantContextAccessor is set in
        // AddLearnStackObservabilityServices above.
        builder.Services.TryAddScoped<ITenantContext>(_ => UnresolvedTenantContext.Instance);

        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<LearnStackExceptionHandler>();

        builder.Services.AddLearnStackMediatRPipeline(mediatorHandlerAssemblies);

        return builder;
    }

    private static void WireSerilog(WebApplicationBuilder builder, DeploymentMode deploymentMode)
    {
        builder.Host.UseSerilog((ctx, services, cfg) =>
        {
            cfg.ReadFrom.Configuration(ctx.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("service.name", "learnstack-api")
                // ADR-0032 § Sub-decision 8: the correlation-context enricher
                // copies tenant.id / organization.id / user.id / module /
                // correlation.id from the singleton ITenantContextAccessor
                // onto every LogEvent. The redaction enricher then strips
                // sensitive properties before the formatter touches them
                // (Standards 11 § Sensitive Data Exposure). Both are resolved
                // from DI (registered as singletons in
                // AddLearnStackObservabilityServices) so the instances in the
                // pipeline are the registered ones — no parallel new()'d copy.
                .Enrich.With(services.GetRequiredService<CorrelationContextEnricher>())
                .Enrich.With(services.GetRequiredService<RedactSensitiveFieldsEnricher>())
                .WriteTo.Console(new Serilog.Formatting.Compact.RenderedCompactJsonFormatter());

            // Air-gapped never egresses logs over the network (Standards 20
            // § Composition Root and Deployment Mode). The console sink above
            // is the operator's local capture; the OTLP sink is wired only
            // for the network-capable modes.
            // TODO(2026-05-22, @platform): Phase 11 ops — add a Serilog file
            // sink under /var/learnstack/otel/ for SelfHostedAirGapped (the
            // contract target in Standards 20). Deferred pending the
            // file-target package decision.
            var otlpEndpoint = ctx.Configuration["Telemetry:OtlpEndpoint"];
            if (deploymentMode != DeploymentMode.SelfHostedAirGapped
                && !string.IsNullOrWhiteSpace(otlpEndpoint))
            {
                cfg.WriteTo.OpenTelemetry(o =>
                {
                    o.Endpoint = otlpEndpoint;
                    o.Protocol = Serilog.Sinks.OpenTelemetry.OtlpProtocol.Grpc;
                    o.ResourceAttributes = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["service.name"] = "learnstack-api",
                    };
                });
            }
            // The OTel LoggerProvider (AddOpenTelemetry().WithLogging()) is
            // intentionally NOT registered alongside; double-export would
            // duplicate every log line. ADR-0032 § Sub-decision 8.
        });
    }

    private static void WireOpenTelemetry(WebApplicationBuilder builder, DeploymentMode deploymentMode)
    {
        var serviceName = builder.Configuration["Telemetry:Service:Name"] ?? "learnstack-api";
        var serviceVersion = builder.Configuration["Telemetry:Service:Version"] ?? "0.0.0-dev";
        var otlpEndpoint = builder.Configuration["Telemetry:OtlpEndpoint"];

        // Air-gapped must not phone home to a network collector (Standards 20
        // § Composition Root and Deployment Mode). The exporter is wired only
        // for the network-capable modes; air-gapped relies on the local
        // capture path. The source / meter filters use the documented
        // lowercase convention (learnstack.<module>) so they match the manual
        // ActivitySource / Meter names without depending on case-insensitive
        // wildcard matching.
        // TODO(2026-05-22, @platform): Phase 11 ops — add an OTLP file
        // exporter under /var/learnstack/otel/ for SelfHostedAirGapped (the
        // Standards 20 contract target). Deferred pending the file-exporter
        // package decision; until then air-gapped traces/metrics stay in-process.
        var exportOverNetwork = deploymentMode != DeploymentMode.SelfHostedAirGapped
            && !string.IsNullOrWhiteSpace(otlpEndpoint);

        var otel = builder.Services
            .AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(
                serviceName: serviceName,
                serviceVersion: serviceVersion))
            .WithTracing(t =>
            {
                t.AddAspNetCoreInstrumentation();
                t.AddHttpClientInstrumentation();
                t.AddEntityFrameworkCoreInstrumentation();
                t.AddSource("learnstack.*");
                t.AddProcessor<TenantContextSpanProcessor>();
                if (exportOverNetwork)
                {
                    t.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint!));
                }
            })
            .WithMetrics(m =>
            {
                m.AddAspNetCoreInstrumentation();
                m.AddHttpClientInstrumentation();
                m.AddMeter("learnstack.*");
                if (exportOverNetwork)
                {
                    m.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint!));
                }
            });

        _ = otel;
    }

    /// <summary>
    /// Single composition-root site that picks the
    /// <see cref="ISecretProvider"/> implementation per
    /// <see cref="DeploymentMode"/>. Phase 11 extends this method with the
    /// Dapr branch so the swap touches one line, not two. Both the DI
    /// registration and the local <c>AddLearnStackErrorTracking</c> call
    /// read the same instance returned here.
    /// </summary>
    /// <remarks>
    /// CA1859 (prefer concrete return type for perf) is suppressed
    /// deliberately: the interface return is the entire point of the
    /// helper — Phase 11 returns <c>DaprSecretProvider</c> for some
    /// modes, and the call site must not bind to a concrete type.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1859:Use concrete types when possible for improved performance",
        Justification = "Return type is intentionally ISecretProvider so Phase 11 can swap implementations per DeploymentMode.")]
    private static ISecretProvider SelectSecretProvider(
        DeploymentMode deploymentMode,
        IConfiguration configuration)
    {
        // TODO(2026-08-10, @platform): Phase 11 — light up the Dapr-backed
        // branch. Demand-gated per ADR-0035; trigger: a production secret must
        // rotate without a redeploy, or more than one operator needs access to
        // production secrets. The target wiring is Standards 20 § Deployment
        // matrix:
        //   every non-Development mode → new DaprSecretProvider(...)  // Vault
        //   DeploymentMode.Development →
        //     keep ConfigurationSecretProvider (delegates to IConfiguration).
        // SelfHostedAirGapped is Vault-backed too — an earlier draft of this
        // TODO named a FileSecretProvider, which exists in no document and
        // contradicts that matrix.
        // The signature stays the same so AddLearnStackErrorTracking's
        // ISecretProvider argument resolves correctly across modes.
        _ = deploymentMode;
        return new ConfigurationSecretProvider(configuration);
    }
}
