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
    /// <summary>
    /// Every module assembly a rule in this class sweeps.
    /// </summary>
    /// <remarks>
    /// <c>Application.Contracts</c> is in the list because that is where
    /// integration events are declared — <c>add-integration-event</c> puts them
    /// in <c>&lt;Producer&gt;.Application.Contracts/IntegrationEvents/</c>. Without
    /// it, <c>Integration_Event_TopicNames_FollowConvention</c> would sweep only
    /// assemblies that by convention never hold an event, so it would be vacuous
    /// permanently rather than until the first module ships one — and the same
    /// omission narrowed three older rules alongside it.
    /// </remarks>
    private static readonly string[] ModuleAssemblyShapes =
    [
        "LearnStack.Modules.Tenancy.Application",
        "LearnStack.Modules.Tenancy.Application.Contracts",
        "LearnStack.Modules.Tenancy.Domain",
        "LearnStack.Modules.Tenancy.Infrastructure",
        "LearnStack.Modules.Identity.Application",
        "LearnStack.Modules.Identity.Application.Contracts",
        "LearnStack.Modules.Identity.Domain",
        "LearnStack.Modules.Identity.Infrastructure",
        "LearnStack.Modules.Customization.Application",
        "LearnStack.Modules.Customization.Application.Contracts",
        "LearnStack.Modules.Customization.Domain",
        "LearnStack.Modules.Customization.Infrastructure",
        "LearnStack.Modules.Audit.Application",
        "LearnStack.Modules.Audit.Application.Contracts",
        "LearnStack.Modules.Audit.Domain",
        "LearnStack.Modules.Audit.Infrastructure",
        "LearnStack.Modules.Content.Application",
        "LearnStack.Modules.Content.Application.Contracts",
        "LearnStack.Modules.Content.Domain",
        "LearnStack.Modules.Content.Infrastructure",
        "LearnStack.Modules.Media.Application",
        "LearnStack.Modules.Media.Application.Contracts",
        "LearnStack.Modules.Media.Domain",
        "LearnStack.Modules.Media.Infrastructure",
        "LearnStack.Modules.Education.Application",
        "LearnStack.Modules.Education.Application.Contracts",
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
    public void Registering_The_Pipeline_Twice_Registers_It_Once()
    {
        // A doubled TransactionBehavior would be a nested frame on every request — the
        // joiner path, taken for no reason, on the hot path — and a doubled
        // AuditLogBehavior would catch and rethrow the same exception twice.
        //
        // The property holds, and it is MediatR's, not ours: `AddBehavior` deduplicates,
        // so a second call adds nothing. Measured — seven behaviours and eleven total
        // registrations either way. This pins it because it is a property we depend on
        // and did not write: every test fixture in the repository registers its probe
        // handler by hand specifically to avoid re-running AddMediatR, and if this ever
        // stopped holding, that workaround would be load-bearing rather than cautious.
        //
        // A guard of our own was written for this and then removed: it changed nothing,
        // and a guard no test can kill is a comment.
        var once = new ServiceCollection();
        once.AddLearnStackMediatRPipeline();

        var twice = new ServiceCollection();
        twice.AddLearnStackMediatRPipeline();
        twice.AddLearnStackMediatRPipeline();

        static int Behaviors(IServiceCollection services) => services.Count(descriptor =>
            descriptor.ServiceType.IsGenericType
            && descriptor.ServiceType.GetGenericTypeDefinition() == typeof(IPipelineBehavior<,>));

        Behaviors(twice).Should().Be(Behaviors(once),
            "the second call is a no-op, not a second pipeline");
        twice.Count.Should().Be(once.Count, "and it adds no other registration either");
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
        // and no TransactionBehavior, which issues the RLS session variables
        // inside the transaction. This test locks the "handlers return Result<T>" invariant
        // now, while the pipeline contract is fresh. Vacuous today (no
        // handlers yet); active the moment they land. Standards 02 § MediatR
        // Use Cases (review-4 M1).
        // Every LearnStack.* project under backend/src, derived rather than listed.
        // A handler is wherever someone puts it, and the sibling rule in
        // RequestSurfaceTests was measured passing a marked request type that sat in
        // Application.Contracts — which is exactly where add-mediatr-handler tells an
        // author to put one. Loaded without a null filter: an assembly this project
        // cannot load is a missing ProjectReference, and dropping it turns "I could not
        // read this code" into "this code is clean".
        var applicationAssemblies = Directory
            .EnumerateFiles(
                RepositoryPaths.BackendSrc(), "LearnStack.*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => Assembly.Load(name!))
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
                    if (!contract.IsGenericType)
                    {
                        continue;
                    }

                    var definition = contract.GetGenericTypeDefinition();

                    // The two shapes the arity-2 check below cannot see, both of which
                    // run with ZERO behaviors. Measured against MediatR 12.4.1:
                    // typeof(IRequestHandler<>).GetInterfaces() is empty — the void
                    // handler does not derive from IRequestHandler<T, Unit> — and Unit
                    // does not implement IResultBase, so MediatR builds a chain of
                    // IPipelineBehavior<TRequest, Unit> and every LearnStack behavior,
                    // constrained on IResultBase, is excluded from it. No authority
                    // ceiling, no validation, no audit classification, and no
                    // TransactionBehavior — so no SET LOCAL app.tenant_id either.
                    definition.Should().NotBe(typeof(IRequestHandler<>),
                        $"{type.FullName} handles a void request, which MediatR runs with "
                        + "no pipeline at all. Declare it IRequest<Result<None>> instead. "
                        + "Standards 02 § MediatR Use Cases.");

                    definition.Should().NotBe(typeof(IStreamRequestHandler<,>),
                        $"{type.FullName} handles a stream request, which MediatR routes "
                        + "through IStreamPipelineBehavior<,> — of which this solution "
                        + "registers none, deliberately. Requests_Are_Never_Streamed bans "
                        + "the shape; this is the handler half of the same ban.");

                    if (definition != typeof(IRequestHandler<,>))
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
    public void Integration_Event_TopicNames_FollowConvention()
    {
        // Standards 20 § IEventBus and ADR-0006: `learnstack.{module}.{aggregate}`,
        // with `learnstack.hub.*` reserved for Hub-side topics. Asserted over the
        // declared event TYPES, so it holds for whichever IEventBus
        // implementation is registered — which is only possible because the topic
        // is declared by the event. While it was a producer-supplied string on
        // the envelope there was nothing to read, and this catalogued rule could
        // not be written at all.
        //
        // No module declares an event yet, so this would be vacuous — hence the
        // deliberate offenders below. A guard that cannot be shown to fire is
        // not a guard.
        FollowsTopicConvention("learnstack.enrollment.enrollment").Should().BeTrue();
        FollowsTopicConvention("learnstack.hub.entitlement").Should().BeTrue();
        FollowsTopicConvention("learnstack.hub.custom-domain.activated").Should().BeTrue();
        FollowsTopicConvention("EnrollmentCreated").Should().BeFalse("no namespace");
        FollowsTopicConvention("learnstack.enrollment").Should().BeFalse("no aggregate");
        FollowsTopicConvention("Learnstack.Enrollment.Enrollment").Should().BeFalse("not lower-case");
        FollowsTopicConvention("acme.enrollment.enrollment").Should().BeFalse("wrong prefix");
        FollowsTopicConvention("learnstack.-hub.event").Should().BeFalse("leading hyphen");
        FollowsTopicConvention("learnstack.hub-.event").Should().BeFalse("trailing hyphen");
        FollowsTopicConvention("learnstack.1hub.event").Should().BeFalse("leading digit");
        FollowsTopicConvention("learnstack.education.course.activated").Should().BeFalse(
            "only Hub owns a four-segment topic");
        FollowsTopicConvention("learnstack.hub.custom-domain.activated.extra").Should().BeFalse(
            "five segments");

        foreach (var name in ModuleAssemblyShapes)
        {
            var assembly = TryLoadAssembly(name);
            if (assembly is null) continue;

            var events = assembly.GetTypes()
                .Where(t => !t.IsAbstract
                            && typeof(LearnStack.SharedKernel.Messaging.IIntegrationEvent)
                                .IsAssignableFrom(t));

            foreach (var type in events)
            {
                var topic = ((LearnStack.SharedKernel.Messaging.IIntegrationEvent)
                    System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type)).Topic;

                FollowsTopicConvention(topic).Should().BeTrue(
                    $"{type.FullName} declares topic '{topic}', which is not "
                    + "learnstack.{module}.{aggregate} (Standards 20 § IEventBus)");
            }
        }
    }

    private static bool FollowsTopicConvention(string topic)
    {
        const string segment = "[a-z][a-z0-9-]*[a-z0-9]|[a-z]";
        return System.Text.RegularExpressions.Regex.IsMatch(
                   topic,
                   $@"^learnstack\.({segment})\.({segment})$",
                   System.Text.RegularExpressions.RegexOptions.None,
                   TimeSpan.FromSeconds(1))
               || System.Text.RegularExpressions.Regex.IsMatch(
                   topic,
                   $@"^learnstack\.hub\.({segment})\.({segment})$",
                   System.Text.RegularExpressions.RegexOptions.None,
                   TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Modules_Do_Not_Inject_IEventBus_Directly()
    {
        // Standards 20 § IEventBus: the only sanctioned publisher is the
        // OutboxProcessor. A module that injects IEventBus gets a synchronous
        // cross-module call with no durability and no transactional atomicity —
        // a fifth cross-module mechanism in everything but name
        // (ADR-0010 admits four), and one that looks like it works in every
        // development test because the in-process transport delivers inline.
        //
        // A namespace ban cannot express this: modules legitimately depend on
        // LearnStack.SharedKernel.Messaging for IIntegrationEvent and
        // IIntegrationEventHandler<T>. Only the bus itself is off limits.
        //
        // The module assemblies carry no types yet, so this would be vacuous —
        // which is why the checker is pointed at a deliberate offender in this
        // assembly first. A guard that cannot be shown to fire is not a guard.
        UsesForbiddenEventBusAccess(typeof(DeliberateEventBusInjector)).Should().BeTrue(
            "the checker must catch a type that does inject the bus, or it "
            + "proves nothing about the modules it is aimed at");
        UsesForbiddenEventBusAccess(typeof(DeliberateEventBusServiceLocator)).Should().BeTrue(
            "IServiceProvider is a service-locator escape hatch");
        UsesForbiddenEventBusAccess(typeof(DeliberateMethodPublisher)).Should().BeTrue(
            "method injection is still direct event-bus access");
        UsesForbiddenEventBusAccess(typeof(DeliberateInheritingPublisher)).Should().BeTrue(
            "inheriting the surface is the same violation with one extra hop");
        UsesForbiddenEventBusAccess(typeof(CrossCuttingFoundationTests)).Should().BeFalse();

        // The other direction, pinned on the one real type that exercises it:
        // TenancyDbContext inherits IInfrastructure<IServiceProvider> from
        // DbContext. A rule that read inherited members without asking where they
        // are declared flags every module context in the repository.
        UsesForbiddenEventBusAccess(
            typeof(LearnStack.Modules.Tenancy.Infrastructure.Persistence.TenancyDbContext))
            .Should().BeFalse(
                "what EF Core declares on DbContext is not what a module author wrote");

        foreach (var name in ModuleAssemblyShapes)
        {
            var assembly = TryLoadAssembly(name);
            if (assembly is null) continue;

            // Compiler- and generator-emitted types are excluded, and the reason
            // is specific rather than hygienic: Vogen emits a nested TypeConverter
            // per value object, and TypeConverter's ConvertFrom takes an
            // ITypeDescriptorContext — which implements IServiceProvider. The
            // service-locator clause caught every strongly-typed id the moment the
            // first module declared one. A generated type cannot inject anything
            // the author chose, so it is not what this rule is aimed at.
            var offenders = assembly.GetTypes()
                .Where(t => !IsGenerated(t))
                .Where(UsesForbiddenEventBusAccess)
                .Select(t => t.FullName)
                .ToList();

            offenders.Should().BeEmpty(
                $"{name} reaches IEventBus directly — by injecting it, or through "
                + "IServiceProvider, which is the same access with an extra step. "
                + "Modules write to the outbox; the OutboxProcessor publishes "
                + "(Standards 20 § IEventBus).");
        }
    }

    /// <summary>
    /// True for a type the compiler or a source generator emitted, including one
    /// nested inside a hand-written type.
    /// </summary>
    /// <remarks>
    /// Walks the declaring chain because Vogen stamps <c>[GeneratedCode]</c> on
    /// the <b>outer</b> value object and on none of the converters nested inside
    /// it — so a nested <c>EfCoreValueConverter</c> or
    /// <c>&lt;Name&gt;TypeConverter</c> is only reachable as generated through its
    /// declaring type. The side effect is that the outer <c>[ValueObject]</c>
    /// partial, which a developer co-writes, is excluded too; that is accepted
    /// because a strongly-typed id has no constructor a bus could arrive through.
    /// </remarks>
    private static bool IsGenerated(Type type)
    {
        for (var current = type; current is not null; current = current.DeclaringType)
        {
            if (current.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false)
                || current.IsDefined(typeof(System.CodeDom.Compiler.GeneratedCodeAttribute), inherit: false))
            {
                return true;
            }
        }

        return false;
    }

    private static bool UsesForbiddenEventBusAccess(Type type)
    {
        var bus = typeof(LearnStack.SharedKernel.Messaging.IEventBus);
        var serviceProvider = typeof(IServiceProvider);
        var forbidden = new[] { bus, serviceProvider };

        // Inherited members ARE read, and then filtered by where they are
        // DECLARED. The obvious form — BindingFlags.DeclaredOnly — excludes them
        // wholesale, and that is both necessary and too much: necessary because
        // DbContext implements IInfrastructure<IServiceProvider>, so every
        // module's DbContext was flagged the moment the first one existed, for
        // something EF declares and no module author wrote; too much because it
        // also stops seeing a module type that inherits a bus-shaped surface from
        // a module base, which is the same violation with one extra hop.
        //
        // The declaring assembly separates the two. A member declared in the
        // type's own assembly, or in any other module assembly, is the module's
        // business; one declared in EF Core or the SharedKernel is not.
        const BindingFlags members = BindingFlags.Instance
                                     | BindingFlags.Static
                                     | BindingFlags.Public
                                     | BindingFlags.NonPublic;

        bool InScope(MemberInfo member) =>
            member.DeclaringType is null
            || member.DeclaringType.Assembly == type.Assembly
            || ModuleAssemblyShapes.Contains(
                member.DeclaringType.Assembly.GetName().Name, StringComparer.Ordinal);

        // Through wrappers, not only at the surface. `IEventBus.IsAssignableFrom` is false
        // for `Lazy<IEventBus>`, `Func<IEventBus>`, `IEnumerable<IEventBus>` and
        // `Task<IEventBus>` — each of which injects the port just as effectively, and the
        // first of them is a shape this codebase already uses (`Lazy<NpgsqlDataSource>`).
        // A rule that only looked at the declared type was satisfied by one type argument.
        bool Banned(Type declared) =>
            Unwrap(declared).Any(inner =>
                forbidden.Any(candidate => candidate.IsAssignableFrom(inner)));

        return type.GetConstructors(members).Where(InScope).Any(constructor =>
                   constructor.GetParameters().Any(parameter => Banned(parameter.ParameterType)))
               || type.GetMethods(members).Where(InScope).Any(method =>
                   Banned(method.ReturnType)
                   || method.GetParameters().Any(parameter => Banned(parameter.ParameterType)))
               || type.GetFields(members).Where(InScope).Any(field =>
                   Banned(field.FieldType))
               || type.GetProperties(members).Where(InScope).Any(property =>
                   Banned(property.PropertyType));
    }

    /// <summary>A type that breaks the rule, so the checker can be shown to catch it.</summary>
    private sealed class DeliberateEventBusInjector(LearnStack.SharedKernel.Messaging.IEventBus bus)
    {
        public LearnStack.SharedKernel.Messaging.IEventBus Bus { get; } = bus;
    }

    /// <summary>A service-locator-shaped deliberate offender.</summary>
    private sealed class DeliberateEventBusServiceLocator(IServiceProvider services)
    {
        public IServiceProvider Services { get; } = services;
    }

    /// <summary>A base whose bus-shaped surface a derived type inherits.</summary>
    private class DeliberateEventBusBase
    {
        protected LearnStack.SharedKernel.Messaging.IEventBus Bus =>
            throw new NotSupportedException("shape only");
    }

    /// <summary>
    /// A deliberate offender that declares nothing at all and inherits the
    /// violation, so the declaring-assembly filter cannot quietly widen back into
    /// BindingFlags.DeclaredOnly.
    /// </summary>
    private sealed class DeliberateInheritingPublisher : DeliberateEventBusBase;

    /// <summary>A method-injection-shaped deliberate offender.</summary>
    private sealed class DeliberateMethodPublisher
    {
        public static Task Publish(
            LearnStack.SharedKernel.Messaging.IEventBus bus,
            LearnStack.SharedKernel.Messaging.IntegrationEventEnvelope envelope) =>
            bus.PublishAsync(envelope);
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

    /// <summary>A declared type and every type argument reachable through it.</summary>
    /// <remarks>
    /// Transitive, because the wrappers nest: <c>Func&lt;Lazy&lt;IEventBus&gt;&gt;</c> is
    /// two layers and injects the port at the bottom of both. Open generics are skipped —
    /// a type parameter names no port.
    /// </remarks>
    private static IEnumerable<Type> Unwrap(Type declared)
    {
        yield return declared;

        if (!declared.IsGenericType || declared.IsGenericTypeDefinition)
        {
            yield break;
        }

        foreach (var argument in declared.GetGenericArguments())
        {
            foreach (var inner in Unwrap(argument))
            {
                yield return inner;
            }
        }
    }
}
