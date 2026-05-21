using LearnStack.Api.Common;
using LearnStack.Application.Pipeline;
using LearnStack.Infrastructure.ErrorTracking;
using LearnStack.Infrastructure.Observability;
using LearnStack.SharedKernel.Hosting;
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

        WireSerilog(builder);
        builder.Services.AddLearnStackObservabilityServices();
        WireOpenTelemetry(builder);

        builder.Services.AddLearnStackErrorTracking(builder.Configuration, deploymentMode);

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
}
