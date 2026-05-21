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
        // the same instance. Packet 5 extends SelectSecretProvider with the
        // Dapr branch so adding DaprSecretProvider touches one line, not
        // two.
        var secretProvider = SelectSecretProvider(deploymentMode, builder.Configuration);
        builder.Services.TryAddSingleton<ISecretProvider>(secretProvider);

        WireSerilog(builder);
        WireOpenTelemetry(builder);

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

    private static void WireSerilog(WebApplicationBuilder builder)
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
                // (Standards 11 § Sensitive Data Exposure).
                .Enrich.With(new CorrelationContextEnricher(
                    services.GetRequiredService<ITenantContextAccessor>()))
                .Enrich.With<RedactSensitiveFieldsEnricher>()
                .WriteTo.Console(new Serilog.Formatting.Compact.RenderedCompactJsonFormatter());

            var otlpEndpoint = ctx.Configuration["Telemetry:OtlpEndpoint"];
            if (!string.IsNullOrWhiteSpace(otlpEndpoint))
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

    private static void WireOpenTelemetry(WebApplicationBuilder builder)
    {
        var serviceName = builder.Configuration["Telemetry:Service:Name"] ?? "learnstack-api";
        var serviceVersion = builder.Configuration["Telemetry:Service:Version"] ?? "0.0.0-dev";
        var otlpEndpoint = builder.Configuration["Telemetry:OtlpEndpoint"];

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
                t.AddSource("LearnStack.*");
                t.AddProcessor<TenantContextSpanProcessor>();
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    t.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
                }
            })
            .WithMetrics(m =>
            {
                m.AddAspNetCoreInstrumentation();
                m.AddHttpClientInstrumentation();
                m.AddMeter("LearnStack.*");
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    m.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
                }
            });

        _ = otel;
    }

    /// <summary>
    /// Single composition-root site that picks the
    /// <see cref="ISecretProvider"/> implementation per
    /// <see cref="DeploymentMode"/>. Packet 5 extends this method with the
    /// Dapr branch so the swap touches one line, not two. Both the DI
    /// registration and the local <c>AddLearnStackErrorTracking</c> call
    /// read the same instance returned here.
    /// </summary>
    /// <remarks>
    /// CA1859 (prefer concrete return type for perf) is suppressed
    /// deliberately: the interface return is the entire point of the
    /// helper — Packet 5 returns <c>DaprSecretProvider</c> for some
    /// modes, and the call site must not bind to a concrete type.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1859:Use concrete types when possible for improved performance",
        Justification = "Return type is intentionally ISecretProvider so Packet 5 can swap implementations per DeploymentMode.")]
    private static ISecretProvider SelectSecretProvider(
        DeploymentMode deploymentMode,
        IConfiguration configuration)
    {
        // TODO(2026-05-21, @platform, phase-02a-packet-5): light up the
        // Dapr-backed branch.
        //   DeploymentMode.SaaS / Dedicated / SelfHostedOnline →
        //     new DaprSecretProvider(...)  // Vault-backed
        //   DeploymentMode.SelfHostedAirGapped →
        //     new FileSecretProvider(...)  // disk-backed
        //   DeploymentMode.Development →
        //     keep ConfigurationSecretProvider (delegates to IConfiguration).
        // The signature stays the same so AddLearnStackErrorTracking's
        // ISecretProvider argument resolves correctly across modes.
        _ = deploymentMode;
        return new ConfigurationSecretProvider(configuration);
    }
}
