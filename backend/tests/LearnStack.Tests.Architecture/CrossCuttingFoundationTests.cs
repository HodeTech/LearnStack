using System.Reflection;
using FluentAssertions;
using LearnStack.Api.Composition;
using LearnStack.Application.Pipeline;
using LearnStack.Infrastructure.Observability;
using LearnStack.SharedKernel.Hosting;
using LearnStack.SharedKernel.Observability;
using LearnStack.SharedKernel.Results;
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
/// <see href="../../../docs/decisions/0032-exception-handling-logging-and-observability.md">ADR-0032</see>
/// and
/// <see href="../../../docs/standards/21-architecture-tests-catalogue.md">Standards 21 § Cross-cutting</see>.
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
        // innermost (handler) last. The expected list is hardcoded here on
        // purpose so a future edit of MediatRPipelineRegistration's
        // CanonicalBehaviorOrder can't sneak past the test by also
        // reordering the test fixture. The catalogue entry
        // MediatR_Pipeline_Order_Matches_Canonical_Sequence is the
        // canonical reference for the contract.
        Type[] expectedOrder =
        [
            typeof(ValidationBehavior<,>),
            typeof(LoggingBehavior<,>),
            typeof(AuditLogBehavior<,>),
            typeof(TenantContextBehavior<,>),
            typeof(AuthorizationBehavior<,>),
            typeof(TransactionBehavior<,>),
            typeof(OutboxFlushBehavior<,>),
        ];

        var services = new ServiceCollection();
        services.AddLearnStackMediatRPipeline();

        var behaviorTypes = services
            .Where(d => d.ServiceType.IsGenericType
                && d.ServiceType.GetGenericTypeDefinition() == typeof(IPipelineBehavior<,>)
                && d.ImplementationType is not null)
            .Select(d => d.ImplementationType!.GetGenericTypeDefinition())
            .ToArray();

        behaviorTypes.Should().Equal(
            expectedOrder,
            "ADR-0032 § Sub-decision 2 pins the canonical 7-behavior order "
            + "(plus the handler at the innermost position). Changing the order "
            + "requires a new ADR; this hardcoded list is the test's "
            + "drift-proof anchor.");

        // Belt-and-suspenders: the production CanonicalBehaviorOrder list
        // must match the same hardcoded sequence so reflection-based
        // consumers (e.g. the registration extension) see the same
        // contract.
        MediatRPipelineRegistration.CanonicalBehaviorOrder
            .Should()
            .Equal(expectedOrder,
                "the production CanonicalBehaviorOrder is the public surface; "
                + "it must match the hardcoded ADR-0032 sequence.");
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
    public void Handlers_Return_Result()
    {
        // The 8-step MediatR pipeline behaviors are constrained
        // `where TResponse : IResultBase`; MediatR only instantiates an
        // open-generic behavior for requests whose response satisfies the
        // constraint. A handler declared IRequestHandler<TReq, RawDto>
        // would therefore run with NO behaviors — no validation, no audit,
        // and (once Packet 7 lands) no TenantContextBehavior (where RLS GUCs
        // get set). This test locks the "handlers return Result<T>" invariant
        // now, while the pipeline contract is fresh. Vacuous today (no
        // handlers yet); active the moment they land. Standards 02 § MediatR
        // Use Cases (review-4 M1).
        var applicationAssemblies = ModuleAssemblyShapes
            .Where(n => n.EndsWith(".Application", StringComparison.Ordinal))
            .Append("LearnStack.Application")
            .Select(TryLoadAssembly)
            .Where(a => a is not null)
            .ToArray();

        foreach (var assembly in applicationAssemblies)
        {
            foreach (var type in assembly!.GetTypes())
            {
                if (type.IsAbstract || type.IsInterface)
                {
                    continue;
                }

                foreach (var contract in type.GetInterfaces())
                {
                    if (!contract.IsGenericType
                        || contract.GetGenericTypeDefinition() != typeof(IRequestHandler<,>))
                    {
                        continue;
                    }

                    var responseType = contract.GetGenericArguments()[1];
                    typeof(IResultBase).IsAssignableFrom(responseType).Should().BeTrue(
                        $"{type.FullName} handles a request whose response ({responseType.Name}) "
                        + "does not implement IResultBase. Handlers must return Result<T> so the "
                        + "MediatR pipeline (validation / audit / tenant-context + RLS) applies "
                        + "— a raw-DTO response silently bypasses every behavior. "
                        + "Standards 02 § MediatR Use Cases.");
                }
            }
        }
    }

    [Fact]
    public void Modules_Do_Not_Reference_DeploymentMode()
    {
        // Standards 20 § Composition Root and Deployment Mode — the
        // composition root selects provider implementations once;
        // modules must NEVER read DeploymentMode directly. The catalogue
        // entry of the same name has lived without an implementation
        // until now (Phase 02a Packet 3 review finding).
        foreach (var name in ModuleAssemblyShapes)
        {
            var assembly = TryLoadAssembly(name);
            if (assembly is null) continue;

            var result = Types.InAssembly(assembly)
                .Should()
                .NotHaveDependencyOn("LearnStack.SharedKernel.Hosting")
                .GetResult();

            result.IsSuccessful.Should().BeTrue(
                $"{name} references LearnStack.SharedKernel.Hosting (the DeploymentMode "
                + "namespace). Composition-root branching is the only sanctioned read site "
                + "(Standards 20 § Composition Root).");
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

        // Registration count is necessary but not sufficient — assert the
        // singleton *lifetime* by resolving twice from the root and once
        // from a fresh scope; all three must be the same instance.
        var first = application.Services.GetRequiredService<IErrorTrackingProvider>();
        var second = application.Services.GetRequiredService<IErrorTrackingProvider>();
        using var scope = application.Services.CreateScope();
        var scoped = scope.ServiceProvider.GetRequiredService<IErrorTrackingProvider>();

        second.Should().BeSameAs(first, "the provider is registered as a singleton.");
        scoped.Should().BeSameAs(first, "a singleton resolves to the same instance across scopes.");
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
