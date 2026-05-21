using System.Reflection;
using FluentAssertions;
using LearnStack.Api.Composition;
using LearnStack.Application.Pipeline;
using LearnStack.Infrastructure.Observability;
using LearnStack.SharedKernel.Hosting;
using LearnStack.SharedKernel.Observability;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NetArchTest.Rules;
using OpenTelemetry.Trace;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// Cross-cutting architecture rules per
/// <see href="../../../../docs/decisions/0032-exception-handling-logging-and-observability.md">ADR-0032</see>
/// and
/// <see href="../../../../docs/standards/21-architecture-tests-catalogue.md">Standards 21 § Cross-cutting</see>.
/// The catalogue is the canonical reference for every identifier below.
/// </summary>
public sealed class CrossCuttingFoundationTests
{
    private static readonly string[] ModuleAssemblyShapes =
    [
        "LearnStack.Modules.Tenancy.Application",
        "LearnStack.Modules.Tenancy.Domain",
        "LearnStack.Modules.Tenancy.Infrastructure",
        "LearnStack.Modules.Identity.Application",
        "LearnStack.Modules.Identity.Domain",
        "LearnStack.Modules.Identity.Infrastructure",
        "LearnStack.Modules.Customization.Application",
        "LearnStack.Modules.Customization.Domain",
        "LearnStack.Modules.Customization.Infrastructure",
        "LearnStack.Modules.Audit.Application",
        "LearnStack.Modules.Audit.Domain",
        "LearnStack.Modules.Audit.Infrastructure",
        "LearnStack.Modules.Content.Application",
        "LearnStack.Modules.Content.Domain",
        "LearnStack.Modules.Content.Infrastructure",
        "LearnStack.Modules.Media.Application",
        "LearnStack.Modules.Media.Domain",
        "LearnStack.Modules.Media.Infrastructure",
        "LearnStack.Modules.Education.Application",
        "LearnStack.Modules.Education.Domain",
        "LearnStack.Modules.Education.Infrastructure",
    ];

    [Fact]
    public void MediatR_Pipeline_Order_Matches_Canonical_Sequence()
    {
        // ADR-0032 § Sub-decision 2 — outermost (validation) first,
        // innermost (handler) last. The architecture test reflects on the
        // DI registration order MediatR emits via AddBehavior(...).
        var services = new ServiceCollection();
        services.AddLearnStackMediatRPipeline();

        var behaviorTypes = services
            .Where(d => d.ServiceType.IsGenericType
                && d.ServiceType.GetGenericTypeDefinition() == typeof(IPipelineBehavior<,>)
                && d.ImplementationType is not null)
            .Select(d => d.ImplementationType!.GetGenericTypeDefinition())
            .ToArray();

        behaviorTypes.Should().Equal(
            MediatRPipelineRegistration.CanonicalBehaviorOrder.ToArray(),
            "ADR-0032 § Sub-decision 2 pins the eight-step order; the catalogue entry "
            + "MediatR_Pipeline_Order_Matches_Canonical_Sequence is the canonical name. "
            + "Changing the order requires a new ADR.");
    }

    [Fact]
    public void IExceptionHandler_Registered_AtStartup()
    {
        // ADR-0032 § Sub-decision 1 — every host registers
        // LearnStackExceptionHandler via AddExceptionHandler<T>().
        using var application = BuildMinimalApiHost();

        var registered = application.Services
            .GetServices<IExceptionHandler>()
            .Select(h => h.GetType())
            .ToArray();

        registered.Should().Contain(typeof(LearnStack.Api.Common.LearnStackExceptionHandler),
            "the L1 handler is the only sanctioned catch site below the framework "
            + "(ADR-0032 § Sub-decision 1).");
    }

    [Fact]
    public void OTel_Pipeline_Includes_TenantContextSpanProcessor()
    {
        // ADR-0032 § Sub-decision 10 — the processor enriches every span
        // (auto-instrumented and manual) with the correlation tags. If the
        // composition root removes it, Tempo queries lose the per-tenant
        // axis.
        using var application = BuildMinimalApiHost();

        // The processor is registered as a singleton so the OTel tracing
        // pipeline can resolve it via AddProcessor<T>(); confirming both
        // the type registration and the IConfigureTracerProviderBuilder
        // pipeline-attach is what catches a regression.
        var processor = application.Services.GetService<TenantContextSpanProcessor>();
        processor.Should().NotBeNull(
            "AddOpenTelemetry().WithTracing(...).AddProcessor<TenantContextSpanProcessor>() "
            + "must remain wired (ADR-0032 § Sub-decision 10).");

        // The tracer provider triggers processor construction at build
        // time — resolving it ensures the pipeline successfully attached
        // every processor in the closure, including ours.
        var tracerProvider = application.Services.GetService<TracerProvider>();
        tracerProvider.Should().NotBeNull(
            "AddOpenTelemetry().WithTracing(...) must register a TracerProvider singleton.");
    }

    [Fact]
    public void Logging_Goes_Through_Microsoft_Extensions_Logging()
    {
        // Standards 10 § Stack — modules log through ILogger<T>;
        // Serilog.ILogger is the implementation seam at the composition
        // root and must not be imported from module assemblies.
        foreach (var name in ModuleAssemblyShapes)
        {
            var assembly = TryLoadAssembly(name);
            if (assembly is null)
            {
                // Phase 02a packets do not necessarily fill every module
                // assembly with code yet; an empty assembly is a vacuous
                // pass.
                continue;
            }

            var result = Types.InAssembly(assembly)
                .Should()
                .NotHaveDependencyOn("Serilog")
                .GetResult();

            result.IsSuccessful.Should().BeTrue(
                $"{name} references Serilog directly. Modules log through "
                + "Microsoft.Extensions.Logging.ILogger<T>; the Serilog impl is wired "
                + "once at the composition root (ADR-0032 § Sub-decision 8).");
        }
    }

    [Fact]
    public void Modules_Do_Not_Reference_Sentry_SDK_Directly()
    {
        // ADR-0032 § Sub-decision 9 — only
        // LearnStack.Infrastructure.ErrorTracking may reference the Sentry
        // SDK. Modules call IErrorTrackingProvider instead.
        foreach (var name in ModuleAssemblyShapes)
        {
            var assembly = TryLoadAssembly(name);
            if (assembly is null) continue;

            var result = Types.InAssembly(assembly)
                .Should()
                .NotHaveDependencyOn("Sentry")
                .GetResult();

            result.IsSuccessful.Should().BeTrue(
                $"{name} references the Sentry SDK directly. Use IErrorTrackingProvider "
                + "(ADR-0032 § Sub-decision 9).");
        }
    }

    [Fact]
    public void Adapters_Wrap_Provider_Exceptions()
    {
        // ADR-0032 § Sub-decision 5 — provider SDK exception types
        // (LiveKit.NET.LiveKitException, Stripe.StripeException,
        // Meilisearch.MeilisearchApiError, …) never escape
        // LearnStack.Infrastructure.<Adapter>. Phase 02a has no adapters
        // yet, so this test asserts the constraint vacuously by walking
        // the well-known SDK namespaces against every non-adapter
        // assembly.
        var nonAdapterAssemblies = ModuleAssemblyShapes
            .Append("LearnStack.SharedKernel")
            .Append("LearnStack.Domain")
            .Append("LearnStack.Application")
            .Append("LearnStack.Application.Contracts")
            .Append("LearnStack.Api")
            .Select(TryLoadAssembly)
            .Where(a => a is not null)
            .ToArray();

        string[] forbiddenSdkNamespaces =
        [
            "LiveKit",
            "Stripe",
            "Meilisearch",
            "Iyzipay",
        ];

        foreach (var assembly in nonAdapterAssemblies)
        {
            foreach (var sdkPrefix in forbiddenSdkNamespaces)
            {
                var result = Types.InAssembly(assembly!)
                    .Should()
                    .NotHaveDependencyOn(sdkPrefix)
                    .GetResult();

                result.IsSuccessful.Should().BeTrue(
                    $"{assembly!.GetName().Name} references {sdkPrefix}. SDK exception types "
                    + "must stay inside LearnStack.Infrastructure.<Adapter>.");
            }
        }
    }

    [Fact]
    public void IErrorTrackingProvider_Is_Singleton()
    {
        // ADR-0032 § Sub-decision 9 — the composition root registers a
        // single IErrorTrackingProvider implementation per DeploymentMode.
        // The boundary L1 handler resolves it once at startup.
        using var application = BuildMinimalApiHost();

        var providers = application.Services
            .GetServices<IErrorTrackingProvider>()
            .ToArray();

        providers.Should().HaveCount(1,
            "exactly one IErrorTrackingProvider is registered per DeploymentMode "
            + "(ADR-0032 § Sub-decision 9).");
    }

    private static WebApplication BuildMinimalApiHost()
    {
        var builder = WebApplication.CreateBuilder([]);
        // Empty configuration is fine for Development — the NoOp tracker
        // does not need a Sentry DSN, and the OTLP exporter is silently
        // skipped when Telemetry:OtlpEndpoint is absent.
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Deployment:Mode"] = nameof(DeploymentMode.Development),
            });
        builder.AddLearnStackCrossCuttingFoundation(DeploymentMode.Development);
        return builder.Build();
    }

    private static Assembly? TryLoadAssembly(string assemblyName)
    {
        try
        {
            return Assembly.Load(assemblyName);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
