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
        params System.Reflection.Assembly[] handlerAssemblies)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(handlerAssemblies);

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
        // Resolved FROM the accessor rather than hard-wired to the unresolved
        // singleton. Nothing wrote the accessor before the event bus, so this is
        // behaviour-preserving for every HTTP path — and it is what makes the
        // bus's tenant restoration reach the scope a handler actually resolves
        // from. Setting only the ambient accessor left the scoped ITenantContext
        // unresolved: a handler injecting it threw, and one sending a MediatR
        // command was short-circuited by TenantContextBehavior before its
        // business logic ran, so the obligation the transport advertises was
        // half-delivered. Packet 7's TenantResolverMiddleware writes the same
        // accessor.
        builder.Services.TryAddTransient<ITenantContext>(sp =>
            sp.GetRequiredService<ITenantContextAccessor>().Current
            ?? UnresolvedTenantContext.Instance);

        // IClock and IGuidFactory have existed in the kernel since Packet 2 and
        // were never registered, because nothing consumed them. Packet 4's
        // idempotency store is the first consumer of the clock — and both doc
        // comments already said "registered as a singleton at the composition
        // root", which was true of nowhere. The factory is registered with it
        // rather than left for its first consumer to discover: an unregistered
        // port whose documentation says otherwise is a trap, and the fix is one
        // line either way.
        builder.Services.TryAddSingleton<LearnStack.SharedKernel.Time.IClock,
            LearnStack.SharedKernel.Time.SystemClock>();
        builder.Services.TryAddSingleton<LearnStack.SharedKernel.Identifiers.IGuidFactory,
            LearnStack.SharedKernel.Identifiers.SystemGuidFactory>();

        // The cache socket. SelectCacheService is the SINGLE site that picks the
        // implementation per DeploymentMode, so Phase 11's Valkey adapter is one
        // line here rather than a search for every registration.
        builder.Services.TryAddSingleton(SelectCacheService);

        // The event-bus socket, same shape and the same single site.
        // IPartitionSerializer is a singleton because the ordering guarantee is
        // process-wide: one instance per scope would give each publisher its own
        // chains, and two events on one partition key would run concurrently
        // while every test still passed.
        builder.Services
            .TryAddSingleton<LearnStack.SharedKernel.Messaging.IPartitionSerializer,
                LearnStack.Infrastructure.Messaging.PartitionSerializer>();

        var integrationEventHandlers =
            LearnStack.Infrastructure.Messaging.IntegrationEventHandlerRegistry
                .Discover(handlerAssemblies);
        foreach (var subscription in integrationEventHandlers.All)
        {
            builder.Services.TryAdd(new ServiceDescriptor(
                subscription.HandlerType,
                subscription.HandlerType,
                ServiceLifetime.Scoped));
        }

        builder.Services.TryAddSingleton(integrationEventHandlers);
        builder.Services.TryAddSingleton(SelectEventBus);

        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<LearnStackExceptionHandler>();

        builder.Services.AddLearnStackMediatRPipeline(handlerAssemblies);

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
    /// <see cref="LearnStack.SharedKernel.Caching.ICacheService"/> implementation
    /// per <see cref="DeploymentMode"/>.
    /// </summary>
    /// <remarks>
    /// Every mode resolves <c>InMemoryCacheService</c> today, and the method
    /// exists anyway: it is the seam ADR-0035 asks for, and a seam that is one
    /// method is a seam Phase 11 can widen without hunting for call sites. It
    /// takes the provider rather than the mode because the mode is not what it
    /// branches on yet — a five-arm switch returning the same instance would be
    /// a branch whose test could only assert something vacuous.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1859:Use concrete types when possible for improved performance",
        Justification = "Return type is intentionally ICacheService so Phase 11 can swap implementations per DeploymentMode.")]
    private static LearnStack.SharedKernel.Caching.ICacheService SelectCacheService(
        IServiceProvider services)
    {
        // TODO(2026-08-24, @platform): Phase 11 — light up the Valkey-backed
        // branch. Demand-gated per ADR-0035; trigger: more than one application
        // instance runs concurrently. InMemoryCacheService is correct for one
        // process and costs hit rate rather than correctness for two, which is
        // why the trigger is a replica count and not a date.
        return new LearnStack.Infrastructure.Caching.InMemoryCacheService(
            services.GetRequiredService<LearnStack.SharedKernel.Time.IClock>(),
            services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>());
    }

    /// <summary>
    /// Single composition-root site that picks the <c>IEventBus</c>
    /// implementation per <see cref="DeploymentMode"/>.
    /// </summary>
    /// <remarks>
    /// CA1859 is suppressed for the same reason as the cache socket: the
    /// interface return is the point.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1859:Use concrete types when possible for improved performance",
        Justification = "Return type is intentionally IEventBus so Phase 11 can swap implementations per DeploymentMode.")]
    private static LearnStack.SharedKernel.Messaging.IEventBus SelectEventBus(
        IServiceProvider services)
    {
        // TODO(2026-08-25, @platform): Phase 11 — light up the Dapr-backed
        // branch. Demand-gated per ADR-0035; trigger: a second process needs to
        // consume an integration event, or event volume, replay or ordering
        // across processes is required. InProcessEventBus is a first-class
        // transport rather than a stub — same IIntegrationEventHandler<T>
        // contract, same IInboxGuard seam, same tenant-context restoration, same
        // per-partition ordering — so a consumer written today does not change
        // when the durable adapter lands.
        return new LearnStack.Infrastructure.Messaging.InProcessEventBus(
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<LearnStack.SharedKernel.Tenancy.ITenantContextAccessor>(),
            services.GetRequiredService<LearnStack.SharedKernel.Messaging.IPartitionSerializer>(),
            services.GetRequiredService<
                LearnStack.Infrastructure.Messaging.IntegrationEventHandlerRegistry>(),
            services.GetRequiredService<
                Microsoft.Extensions.Logging.ILogger<
                    LearnStack.Infrastructure.Messaging.InProcessEventBus>>());
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
        // SelfHostedAirGapped keeps the same provider TYPE — the matrix reads
        // "DaprSecretProvider → Vault or file", so what varies for air-gapped
        // is the backing store, not the adapter. An earlier draft of this TODO
        // named a separate FileSecretProvider type, which is the part that
        // exists in no document and contradicts the matrix.
        // The signature stays the same so AddLearnStackErrorTracking's
        // ISecretProvider argument resolves correctly across modes.
        _ = deploymentMode;
        return new ConfigurationSecretProvider(configuration);
    }
}
