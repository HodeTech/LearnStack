using FluentAssertions;
using LearnStack.Api.Composition;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Modules.Customization.Application.IntegrationEvents;
using LearnStack.Modules.Tenancy.Application.Contracts.IntegrationEvents;
using LearnStack.SharedKernel.Hosting;
using LearnStack.SharedKernel.Messaging;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private const string IntegrationHandlerContract = "LearnStack.SharedKernel.Messaging.IIntegrationEventHandler`1";
    private const string IntegrationSubscription = "LearnStack.Infrastructure.Messaging.IntegrationEventSubscription";
    private const string IntegrationRegistry = "LearnStack.Infrastructure.Messaging.IntegrationEventHandlerRegistry";
    private static readonly Type[] RequiredDeliveryHandlers =
        [typeof(InitializeContentTypeDefaultHandler), typeof(InitializeTaxonomyDefaultHandler)];
    private static readonly string[] IntegrationModuleLayers = ["Domain", "Application.Contracts", "Application", "Infrastructure"];

    /// <summary>
    /// ADR-0046 and Accepted P02b-3: every production handler has one declared Active
    /// subscription and scoped registration; typed invocation and the known reflection
    /// capability stay in the delivery owner. PostgreSQL tests prove claim-before-
    /// construction, context ordering and atomicity. This is not arbitrary reflection
    /// dataflow or control-flow dominance analysis.
    /// </summary>
    [Fact]
    public void Integration_Event_Handlers_Use_InboxGuard()
    {
        using var census = ProductionCensus.Read();
        IntegrationDeliveryOffenders(census, ReadIntegrationDeliveryRegistration()).Should().BeEmpty(
            "Fix: declare every concrete integration handler as an Active scoped subscription and invoke it only through the inbox-owned delivery callback");
    }

    [Fact]
    public void Integration_handler_census_rejects_missing_assemblies_layers_and_production_subjects()
    {
        using var census = ProductionCensus.Read();
        var registration = ReadIntegrationDeliveryRegistration();
        foreach (var module in census.Modules.ToArray())
            AssertMutation(census, "a missing production assembly " + module.Name, () =>
            {
                census.Modules.Remove(module);
                return () => census.Modules.Add(module);
            }, current => IntegrationCoverageOffenders(current, registration));

        foreach (var handler in RequiredDeliveryHandlers)
        {
            var subject = census.Types[(handler.Assembly.GetName().Name!, handler.FullName!)];
            var subjectModule = subject.Module;
            AssertMutation(census, "a missing actual handler " + handler.Name, () =>
            {
                subjectModule.Types.Remove(subject);
                return () => subjectModule.Types.Add(subject);
            }, current => IntegrationCoverageOffenders(current, registration));
        }

        // Each production layer is also a possible newly added handler's home.
        // Comparing with the manifest, rather than requiring just two known names,
        // makes this extra unregistered subject fail everywhere.
        foreach (var module in census.Modules)
            AssertMutation(census, "an unregistered integration handler in " + module.Name, () =>
            {
                var probe = AddIntegrationHandlerProbe(module, "UnregisteredIntegrationHandler");
                return () => module.Types.Remove(probe);
            }, current => IntegrationCoverageOffenders(current, registration));
    }

    [Fact]
    public void Integration_handler_discovery_sees_inherited_generic_and_explicit_contracts()
    {
        using var census = ProductionCensus.Read();
        using var probes = ModuleDefinition.ReadModule(typeof(OutboxDispatchConventionTests).Assembly.Location);
        var types = census.Types;
        foreach (var type in probes.GetTypes()) types.Add(Key(type), type);
        var bindings = IntegrationHandlerBindings(types).ToArray();
        foreach (var handler in new[] { typeof(IntegrationDeliveryProbeHandler), typeof(IntegrationDeliveryProbeInheritedHandler),
                     typeof(IntegrationDeliveryProbeExplicitHandler) })
            bindings.Where(binding => TypeId(binding.Handler) == IntegrationRuntimeTypeId(handler))
                .Select(binding => TypeId(binding.Event)).Should().Equal([IntegrationRuntimeTypeId(typeof(TenantCreatedIntegrationEventV1))],
                    "Fix: preserve closed event arguments through inherited and explicit integration contracts");
        bindings.Select(binding => TypeId(binding.Handler)).Should().NotContain(
            [IntegrationRuntimeTypeId(typeof(IntegrationDeliveryProbeBase<>)), IntegrationRuntimeTypeId(typeof(IntegrationDeliveryProbeClean))],
            "abstract bases and unrelated HandleAsync methods are not concrete subscribers");
    }

    [Fact]
    public void Integration_invocation_census_rejects_all_layer_direct_interface_helper_async_and_delegate_bypasses()
    {
        using var census = ProductionCensus.Read();
        var types = census.Types;
        var handler = types[(typeof(InitializeContentTypeDefaultHandler).Assembly.GetName().Name!, typeof(InitializeContentTypeDefaultHandler).FullName!)];
        var handle = handler.Methods.Single(method => method.Name == "HandleAsync");
        var constructor = handler.Methods.Single(method => method.IsConstructor && !method.IsStatic);
        var contract = typeof(IIntegrationEventHandler<TenantCreatedIntegrationEventV1>).GetMethod("HandleAsync")!;
        var subscription = types[(InfrastructureAssembly, IntegrationSubscription)];
        var capability = subscription.Properties.Single(property => property.Name == "Handle").GetMethod;
        var invoke = types[(InfrastructureAssembly, DeliveryExecutorType)].Methods.Single(method => method.Name == "InvokeAsync");

        foreach (var module in census.Modules)
        {
            AssertMutation(census, "direct handler invocation in " + module.Name, () =>
                AddCallProbe(module, "DirectIntegrationInvocation", handle, OpCodes.Call), IntegrationInvocationOffenders);
            AssertMutation(census, "interface handler invocation in " + module.Name, () =>
                AddCallProbe(module, "InterfaceIntegrationInvocation", module.ImportReference(contract), OpCodes.Callvirt), IntegrationInvocationOffenders);
            AssertMutation(census, "handler construction in " + module.Name, () =>
                AddCallProbe(module, "DirectIntegrationConstruction", constructor, OpCodes.Newobj), IntegrationInvocationOffenders);
        }

        var application = census.Modules.Single(module => module.Assembly.Name.Name == DeliveryApplication);
        foreach (var target in new[] { handle, capability, invoke })
        {
            foreach (var opcode in new[] { OpCodes.Call, OpCodes.Callvirt, OpCodes.Ldftn, OpCodes.Ldvirtftn })
                AssertMutation(census, "an extra invocation capability through " + opcode.Name, () =>
                    AddCallProbe(application, "IntegrationInvocationHelper", target, opcode), IntegrationInvocationOffenders);
            AssertMutation(census, "an asynchronous invocation helper", () =>
                AddAsyncCallProbe(application, "AsyncIntegrationInvocationHelper", target), IntegrationInvocationOffenders);
        }

        var executor = types[(InfrastructureAssembly, DeliveryExecutorType)];
        AssertMutation(census, "another helper inside the real executor", () =>
        {
            var method = AddCall(executor, "UnexpectedInvocationHelper", invoke, OpCodes.Call);
            return () => executor.Methods.Remove(method);
        }, IntegrationInvocationOffenders);
        var invokeBody = OwnedBodies(census).Single(item => item.Owner == invoke && item.Body.Name == "MoveNext").Body;
        var readHandle = invokeBody.Body.Instructions.Single(instruction => instruction.Operand is MethodReference method
            && method.Name == "get_Handle" && SameType(method.DeclaringType, subscription));
        AssertMutation(census, "a missing actual reflective invocation capability", () =>
            ReplaceInstruction(readHandle, OpCodes.Ldstr, "removed invocation capability"), IntegrationInvocationOffenders);

        var clean = AddProbe(application, "CleanIntegrationMetadataObserver");
        AddCall(clean, "Observe", subscription.Properties.Single(property => property.Name == "EventType").GetMethod, OpCodes.Callvirt);
        try { IntegrationInvocationOffenders(census).Should().BeEmpty("reading event identity does not invoke a subscriber"); }
        finally { application.Types.Remove(clean); }
    }

    [Fact]
    public void Integration_invocation_compiled_controls_distinguish_helpers_inheritance_async_and_clean_methods()
    {
        using var census = ProductionCensus.Read();
        using var probes = ModuleDefinition.ReadModule(typeof(OutboxDispatchConventionTests).Assembly.Location);
        census.Modules.Add(probes);
        try
        {
            var types = census.Types;
            var calls = CallSites(census).Where(site => site.Owner.Contains("/IntegrationDeliveryProbe", StringComparison.Ordinal)).ToArray();
            var offenders = IntegrationTypedInvocationOffenders(types, calls,
                probes.GetTypes().Where(type => type.FullName.Contains("/IntegrationDeliveryProbe", StringComparison.Ordinal)));
            foreach (var name in new[] { "Direct", "ViaInterface", "ViaInherited", "Capture", "Async", "Run",
                         "ResolveOptional", "ResolveRequired", "ResolveAll", "ResolveWrapped", "ResolveKeyed", "ResolveRequiredKeyed",
                         "ResolveAllKeyed", "Activate", "ResolveOrActivate", "ResolveAsync", "CaptureResolution" })
                offenders.Should().Contain(offender => offender.Contains("::" + name + "(", StringComparison.Ordinal),
                    "Fix: compiled " + name + " must reach the same typed invocation detector");
            offenders.Should().Contain(offender => offender.Contains("::ResolveConstrained", StringComparison.Ordinal),
                "a generic helper constrained to a handler cannot hide typed DI resolution");
            offenders.Should().Contain(offender => offender.Contains("::Construct(", StringComparison.Ordinal));
            offenders.Should().NotContain(offender => offender.Contains("/IntegrationDeliveryProbeClean", StringComparison.Ordinal),
                "ordinary HandleAsync names, async helpers and metadata reads are clean");
        }
        finally { census.Modules.Remove(probes); }
    }

    [Fact]
    public void Integration_manifest_census_rejects_missing_rebound_duplicate_and_unscoped_bindings()
    {
        using var census = ProductionCensus.Read();
        var registration = ReadIntegrationDeliveryRegistration();
        IntegrationCoverageOffenders(census, registration).Should().BeEmpty();
        var first = registration.Handlers[0];
        var manifest = registration.Manifests[0];
        var descriptor = registration.Services.Single(service => service.ServiceType == first.HandlerType);
        var mutations = new[]
        {
            registration with { Handlers = registration.Handlers.Skip(1).ToArray() },
            registration with { Handlers = [.. registration.Handlers, first] },
            registration with { Handlers = [first with { EventType = typeof(IIntegrationEvent) }, .. registration.Handlers.Skip(1)] },
            registration with { Handlers = [first with { Handle = typeof(object).GetMethod(nameof(ToString))! }, .. registration.Handlers.Skip(1)] },
            registration with { Manifests = registration.Manifests.Skip(1).ToArray() },
            registration with { Manifests = [.. registration.Manifests, manifest] },
            registration with { Manifests = [manifest with { Subscription = OutboxSubscriptionManifest.Pending(manifest.Subscription.Id) }, .. registration.Manifests.Skip(1)] },
            registration with { Manifests = [manifest with { Subscription = OutboxSubscriptionManifest.Active(manifest.Subscription.Id, typeof(object)) }, .. registration.Manifests.Skip(1)] },
            registration with { Manifests = [manifest with { Subscription = OutboxSubscriptionManifest.Active(manifest.Subscription.Id, manifest.Subscription.HandlerType!, OutboxSubscriptionExecutionScope.PrivilegedPlatform) }, .. registration.Manifests.Skip(1)] },
            registration with { Manifests = [manifest with { Subscription = OutboxSubscriptionManifest.Active(manifest.Subscription.Id + "-renamed", manifest.Subscription.HandlerType!) }, .. registration.Manifests.Skip(1)] },
            registration with { Services = registration.Services.Where(service => service != descriptor).ToArray() },
            registration with { Services = [.. registration.Services, descriptor] },
            registration with { Services = [.. registration.Services.Where(service => service != descriptor), ServiceDescriptor.Singleton(first.HandlerType, first.HandlerType)] },
            registration with { Services = [.. registration.Services.Where(service => service != descriptor), ServiceDescriptor.Scoped(first.HandlerType, _ => throw new InvalidOperationException("A metadata census must never construct a handler."))] },
        };
        foreach (var mutation in mutations)
            IntegrationCoverageOffenders(census, mutation).Should().NotBeEmpty(
                "Fix: handler, manifest, reflective method and concrete scoped registration must agree independently");
        IntegrationCoverageOffenders(census, registration).Should().BeEmpty("the actual composition is the clean control");
    }

    private static SortedSet<string> IntegrationDeliveryOffenders(ProductionCensus census, IntegrationDeliveryRegistration registration)
    {
        var offenders = IntegrationCoverageOffenders(census, registration);
        offenders.UnionWith(IntegrationInvocationOffenders(census));
        offenders.UnionWith(IntegrationLifecycleOffenders(census));
        return offenders;
    }

    private static SortedSet<string> IntegrationCoverageOffenders(ProductionCensus census, IntegrationDeliveryRegistration registration)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var assemblies = census.Modules.Select(module => module.Assembly.Name.Name).ToArray();
        CheckSet(offenders, "integration-handler production assemblies", assemblies, ProductionAssemblies.Names());
        foreach (var module in assemblies.Where(name => name.StartsWith("LearnStack.Modules.", StringComparison.Ordinal))
                     .Select(name => name.Split('.')[2]).Distinct(StringComparer.Ordinal))
            CheckSet(offenders, module + " integration-handler layers", assemblies.Where(name => name.StartsWith("LearnStack.Modules." + module + ".", StringComparison.Ordinal)),
                IntegrationModuleLayers.Select(layer => "LearnStack.Modules." + module + "." + layer));

        var bindings = IntegrationHandlerBindings(census.Types).ToArray();
        if (bindings.Length == 0) offenders.Add("Missing nonempty production integration-handler population");
        foreach (var required in RequiredDeliveryHandlers)
            if (!bindings.Any(binding => TypeId(binding.Handler) == IntegrationRuntimeTypeId(required)))
                offenders.Add("Missing real default handler: " + required.FullName);
        foreach (var binding in bindings.Where(binding => binding.Handler.HasGenericParameters || binding.Event.ContainsGenericParameter))
            offenders.Add("Integration handlers must be closed concrete types: " + TypeId(binding.Handler));
        CheckSet(offenders, "independent handlers and registered metadata",
            bindings.Select(binding => TypeId(binding.Event) + " -> " + TypeId(binding.Handler)),
            registration.Handlers.Select(handler => IntegrationRuntimeTypeId(handler.EventType) + " -> " + IntegrationRuntimeTypeId(handler.HandlerType)));
        CheckSet(offenders, "Active manifests and handler metadata",
            registration.Manifests.Where(item => item.Subscription.Readiness == OutboxSubscriptionReadiness.Active)
                .Select(item => IntegrationRuntimeTypeId(item.EventType) + " -> " + IntegrationRuntimeTypeId(item.Subscription.HandlerType!)),
            registration.Handlers.Select(handler => IntegrationRuntimeTypeId(handler.EventType) + " -> " + IntegrationRuntimeTypeId(handler.HandlerType)));
        if (registration.Manifests.Select(item => item.Subscription.Id).Distinct(StringComparer.Ordinal).Count() != registration.Manifests.Length)
            offenders.Add("Subscription IDs must be unique");
        foreach (var (id, handler) in new[]
                 {
                     ("customization.initialize-content-type-default.v1", typeof(InitializeContentTypeDefaultHandler)),
                     ("customization.initialize-taxonomy-default.v1", typeof(InitializeTaxonomyDefaultHandler)),
                 })
            if (registration.Manifests.Count(item => item.EventType == typeof(TenantCreatedIntegrationEventV1)
                    && item.Subscription.Id == id && item.Subscription.HandlerType == handler
                    && item.Subscription.Readiness == OutboxSubscriptionReadiness.Active
                    && item.Subscription.ExecutionScope == OutboxSubscriptionExecutionScope.OrdinaryTenant) != 1)
                offenders.Add("Missing exact ordinary Active default subscription: " + id);

        foreach (var handler in registration.Handlers)
        {
            var contract = typeof(IIntegrationEventHandler<>).MakeGenericType(handler.EventType);
            if (handler.ContractType != contract || !handler.HandlerType.GetInterfaces().Contains(contract)
                || handler.Handle != contract.GetMethod("HandleAsync"))
                offenders.Add("Invalid handler contract/method metadata: " + handler.HandlerType.FullName);
        }
        foreach (var handler in registration.Handlers.Select(handler => handler.HandlerType).Distinct())
        {
            var descriptors = registration.Services.Where(service => service.ServiceType == handler).ToArray();
            if (descriptors.Length != 1 || descriptors[0].IsKeyedService || descriptors[0].Lifetime != ServiceLifetime.Scoped
                || descriptors[0].ImplementationType != handler)
                offenders.Add("Handler requires exactly one concrete scoped registration: " + handler.FullName);
        }
        foreach (var root in new[] { typeof(IntegrationEventHandlerRegistry), typeof(OutboxEventRegistry) })
        {
            var descriptors = registration.Services.Where(service => service.ServiceType == root).ToArray();
            if (descriptors.Length != 1 || descriptors[0].IsKeyedService || descriptors[0].Lifetime != ServiceLifetime.Singleton)
                offenders.Add("One singleton metadata root is required: " + root.FullName);
        }
        return offenders;
    }

    private static SortedSet<string> IntegrationInvocationOffenders(ProductionCensus census)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var types = census.Types;
        foreach (var subject in new[] { (InfrastructureAssembly, DeliveryExecutorType), (InfrastructureAssembly, IntegrationSubscription),
                     (InfrastructureAssembly, IntegrationRegistry), (KernelAssembly, IntegrationHandlerContract) })
            if (!types.ContainsKey(subject)) offenders.Add("Missing integration invocation subject: " + subject.Item2);
        if (offenders.Count != 0) return offenders;
        var sites = CallSites(census).ToArray();
        offenders.UnionWith(IntegrationTypedInvocationOffenders(types, sites, types.Values));
        var executor = types[(InfrastructureAssembly, DeliveryExecutorType)];
        var subscription = types[(InfrastructureAssembly, IntegrationSubscription)];
        var invokers = executor.Methods.Where(method => method.Name == "InvokeAsync").ToArray();
        var owners = executor.Methods.Where(method => method.Name == "ExecuteOwnedAsync").ToArray();
        if (invokers.Length != 1 || owners.Length != 1 || !invokers[0].IsPrivate || !invokers[0].IsStatic)
        {
            offenders.Add("DeliveryExecutor requires its private invocation and one owned entry");
            return offenders;
        }
        var invoke = MethodId(invokers[0]);
        var owner = MethodId(owners[0]);
        var metadataMembers = subscription.Methods.Where(method => method.CustomAttributes.Any(attribute =>
                attribute.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute")
            && method.Name is "PrintMembers" or "Equals" or "GetHashCode" or "Deconstruct" or "<Clone>$" or ".ctor")
            .Select(MethodId).ToHashSet(StringComparer.Ordinal);
        var handleReaders = sites.Where(site => SameType(site.Target.DeclaringType, subscription) && site.Target.Name == "get_Handle").ToArray();
        CheckSet(offenders, "subscription Handle consumers", handleReaders.Where(site => !metadataMembers.Contains(site.Owner)).Select(site => site.Owner), [invoke]);
        foreach (var site in sites.Where(site => SameType(site.Target.DeclaringType, subscription)
                     && (site.Target.Name == "Deconstruct" || site.Target.Name.StartsWith("set_", StringComparison.Ordinal))))
            if (!metadataMembers.Contains(site.Owner)) offenders.Add("Extra subscription metadata extraction/mutation: " + site.Owner);
        var metadataFactory = types[(InfrastructureAssembly, IntegrationRegistry)].Methods.Where(method => method.Name == "CreateSubscription").Select(MethodId).ToArray();
        CheckSet(offenders, "subscription construction", sites.Where(site => SameType(site.Target.DeclaringType, subscription)
                && site.Target.Name == ".ctor" && !metadataMembers.Contains(site.Owner)).Select(site => site.Owner), metadataFactory);
        if (metadataFactory.Length != 1) offenders.Add("Missing sole subscription metadata factory");
        var reflectiveCalls = sites.Where(site => site.Owner == invoke && site.Target.Name == "Invoke"
            && site.Target.DeclaringType.FullName is "System.Reflection.MethodBase" or "System.Reflection.MethodInfo").ToArray();
        if (reflectiveCalls.Length != 1) offenders.Add("Missing sole reflective handler invocation in the delivery boundary");
        if (sites.Count(site => site.Owner == invoke && site.Target.Name == "GetRequiredService"
                && site.Target.DeclaringType.FullName == "Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions"
                && ParametersMatch(site.Target, ["System.IServiceProvider", "System.Type"])) != 1)
            offenders.Add("Missing runtime-type handler resolution in the delivery boundary");

        // Follow actual delegate operands from the attributed owner's bodies. A
        // generated name alone confers no authority. These edges identify the known
        // callback chain; they do not prove argument flow or execution dominance.
        var bodies = OwnedBodies(census).GroupBy(item => MethodId(item.Owner))
            .ToDictionary(group => group.Key, group => group.Select(item => item.Body).ToArray(), StringComparer.Ordinal);
        var callbacks = bodies.GetValueOrDefault(owner, []).SelectMany(DelegateTargets).Select(MethodId).ToArray();
        var preparedCallbacks = callbacks.Where(callback => bodies.GetValueOrDefault(callback, []).SelectMany(MethodOperands)
            .Any(target => target.Name == "ExecutePreparedDeliveryAsync" && target.DeclaringType.FullName == DeliveryCoordinator)).ToArray();
        if (preparedCallbacks.Length != 1) offenders.Add("Missing sole prepared-delivery callback rooted in ExecuteOwnedAsync");
        var invocationCallbacks = preparedCallbacks.SelectMany(callback => bodies.GetValueOrDefault(callback, []).SelectMany(DelegateTargets))
            .Select(MethodId).ToArray();
        if (invocationCallbacks.Length != 1) offenders.Add("Missing sole handler callback below the prepared delivery callback");
        CheckSet(offenders, "private handler invocation callers", sites.Where(site => site.Target.Name == "InvokeAsync"
                && SameType(site.Target.DeclaringType, executor)).Select(site => site.Owner), invocationCallbacks);
        CheckSet(offenders, "prepared delivery entries", sites.Where(site => site.Target.Name == "ExecutePreparedDeliveryAsync"
                && site.Target.DeclaringType.FullName == DeliveryCoordinator).Select(site => site.Owner), [owner, .. preparedCallbacks]);
        if (sites.Count(site => site.Owner == owner && site.Target.Name == "ExecuteAsync"
                && site.Target.DeclaringType.FullName == "LearnStack.Application.Audit.AuditExecutionCoordinator") != 1)
            offenders.Add("Missing audit classification entry in the delivery owner");
        return offenders;
    }

    private static SortedSet<string> IntegrationTypedInvocationOffenders(
        Dictionary<(string Assembly, string Type), TypeDefinition> types,
        IEnumerable<(string Owner, MethodReference Target)> sites, IEnumerable<TypeDefinition> bodies)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var site in sites.Where(site => (site.Target.Name == "HandleAsync" || site.Target.Name.EndsWith(".HandleAsync", StringComparison.Ordinal))
                     && IntegrationHandlerContracts(site.Target.DeclaringType, types).Any()))
            offenders.Add("Typed handler invocation: " + site.Owner + " -> " + site.Target.FullName);
        foreach (var site in sites.Where(site => site.Target is GenericInstanceMethod generic
                     && IsIntegrationResolutionCapability(generic)
                     && generic.GenericArguments.Any(argument => ContainsIntegrationHandler(argument, types))))
            offenders.Add("Typed handler resolution/construction: " + site.Owner + " -> " + site.Target.FullName);
        foreach (var method in bodies.SelectMany(type => type.Methods).Where(method => method.HasBody))
            foreach (var instruction in method.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Newobj))
                if (instruction.Operand is MethodReference target && IntegrationHandlerContracts(target.DeclaringType, types).Any())
                    offenders.Add("Direct handler construction: " + MethodId(method) + " -> " + target.FullName);
        return offenders;
    }

    // Only statically typed Microsoft DI capabilities are classified here. The
    // production runtime-Type resolver is pinned above; arbitrary Type-valued
    // argument provenance and reflective construction are not inferred.
    private static bool IsIntegrationResolutionCapability(MethodReference method) =>
        Key(method.DeclaringType).Assembly == "Microsoft.Extensions.DependencyInjection.Abstractions"
        && (method.DeclaringType.FullName switch
        {
            "Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions" =>
                method.Name is "GetService" or "GetRequiredService" or "GetServices",
            "Microsoft.Extensions.DependencyInjection.ServiceProviderKeyedServiceExtensions" =>
                method.Name is "GetKeyedService" or "GetRequiredKeyedService" or "GetKeyedServices",
            "Microsoft.Extensions.DependencyInjection.ActivatorUtilities" =>
                method.Name is "CreateInstance" or "GetServiceOrCreateInstance",
            _ => false,
        });

    private static bool ContainsIntegrationHandler(TypeReference type,
        Dictionary<(string Assembly, string Type), TypeDefinition> types)
    {
        var pending = new Queue<TypeReference>();
        var visited = new HashSet<TypeReference>();
        pending.Enqueue(type);
        while (pending.TryDequeue(out var current))
        {
            if (!visited.Add(current)) continue;
            // A parameter has constraints rather than a resolvable declaring type.
            if (current is GenericParameter parameter)
            {
                foreach (var constraint in parameter.Constraints) pending.Enqueue(constraint.ConstraintType);
                continue;
            }
            if (IntegrationHandlerContracts(current, types).Any()) return true;
            if (current is GenericInstanceType generic)
                foreach (var argument in generic.GenericArguments) pending.Enqueue(argument);
            else if (current is TypeSpecification specification) pending.Enqueue(specification.ElementType);
        }
        return false;
    }

    private static IEnumerable<(TypeDefinition Handler, TypeReference Event)> IntegrationHandlerBindings(
        Dictionary<(string Assembly, string Type), TypeDefinition> types) =>
        types.Values.Where(type => !type.IsInterface && !type.IsAbstract)
            .SelectMany(type => IntegrationHandlerContracts(type, types).Select(contract => (type, contract.GenericArguments[0])));

    private static IEnumerable<GenericInstanceType> IntegrationHandlerContracts(TypeReference type,
        Dictionary<(string Assembly, string Type), TypeDefinition> types)
    {
        var pending = new Queue<TypeReference>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        pending.Enqueue(type);
        while (pending.TryDequeue(out var current))
        {
            if (!visited.Add(TypeId(current))) continue;
            if (current is GenericInstanceType contract && Key(current) == (KernelAssembly, IntegrationHandlerContract))
                yield return contract;
            if (!types.TryGetValue(Key(current), out var definition)) continue;
            var arguments = current is GenericInstanceType generic ? generic.GenericArguments.ToArray() : [];
            if (definition.BaseType is { } parent) pending.Enqueue(SubstituteIntegrationArguments(parent, arguments));
            foreach (var implemented in definition.Interfaces)
                pending.Enqueue(SubstituteIntegrationArguments(implemented.InterfaceType, arguments));
        }
    }

    private static TypeReference SubstituteIntegrationArguments(TypeReference reference, TypeReference[] arguments)
    {
        if (reference is GenericParameter { Type: GenericParameterType.Type } parameter && parameter.Position < arguments.Length)
            return arguments[parameter.Position];
        if (reference is not GenericInstanceType generic) return reference;
        var result = new GenericInstanceType(generic.ElementType);
        foreach (var argument in generic.GenericArguments) result.GenericArguments.Add(SubstituteIntegrationArguments(argument, arguments));
        return result;
    }

    private static IntegrationDeliveryRegistration ReadIntegrationDeliveryRegistration()
    {
        var production = ProductionAssemblies.All().SelectMany(assembly => assembly.GetTypes()).ToArray();
        var handlerAssemblies = production.Where(type => !type.IsAbstract && !type.IsInterface
                && type.GetInterfaces().Any(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IIntegrationEventHandler<>)))
            .Select(type => type.Assembly).Distinct().ToArray();
        var builder = WebApplication.CreateBuilder([]);
        builder.Configuration.Sources.Clear();
        // Use the real registration method with independently discovered assemblies.
        // The actual Program root and hosted effects are covered by the real default-
        // delivery integration fixture; no business handler is constructed here.
        builder.AddLearnStackCrossCuttingFoundation(DeploymentMode.Development, handlerAssemblies);
        using var provider = builder.Services.BuildServiceProvider();
        var handlers = provider.GetRequiredService<IntegrationEventHandlerRegistry>();
        var registry = provider.GetRequiredService<OutboxEventRegistry>();
        var events = production.Where(type => !type.IsAbstract && !type.IsInterface && !type.ContainsGenericParameters
            && typeof(IIntegrationEvent).IsAssignableFrom(type));
        return new(handlers.All.ToArray(), events.SelectMany(type => registry.Resolve(type.AssemblyQualifiedName).Subscriptions
            .Select(subscription => new IntegrationManifestBinding(type, subscription))).ToArray(), builder.Services.ToArray());
    }

    private static string IntegrationRuntimeTypeId(Type type) => type.Assembly.GetName().Name + ":" + type.FullName!.Replace('+', '/');

    private static TypeDefinition AddIntegrationHandlerProbe(ModuleDefinition module, string name)
    {
        var type = AddProbe(module, name);
        var contract = new GenericInstanceType(module.ImportReference(typeof(IIntegrationEventHandler<>)));
        contract.GenericArguments.Add(module.ImportReference(typeof(TenantCreatedIntegrationEventV1)));
        type.Interfaces.Add(new InterfaceImplementation(contract));
        return type;
    }

    private sealed record IntegrationManifestBinding(Type EventType, OutboxSubscriptionManifest Subscription);
    private sealed record IntegrationDeliveryRegistration(IntegrationEventSubscription[] Handlers,
        IntegrationManifestBinding[] Manifests, ServiceDescriptor[] Services);

    private sealed class IntegrationDeliveryProbeHandler : IIntegrationEventHandler<TenantCreatedIntegrationEventV1>
    {
        public Task HandleAsync(TenantCreatedIntegrationEventV1 @event, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private abstract class IntegrationDeliveryProbeBase<T> : IIntegrationEventHandler<T> where T : IIntegrationEvent
    {
        public Task HandleAsync(T @event, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class IntegrationDeliveryProbeInheritedHandler : IntegrationDeliveryProbeBase<TenantCreatedIntegrationEventV1>;
    private sealed class IntegrationDeliveryProbeExplicitHandler : IIntegrationEventHandler<TenantCreatedIntegrationEventV1>
    {
        Task IIntegrationEventHandler<TenantCreatedIntegrationEventV1>.HandleAsync(TenantCreatedIntegrationEventV1 @event, CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private interface IIntegrationDeliveryProbeHelper
    {
        Task Run(IIntegrationEventHandler<TenantCreatedIntegrationEventV1> handler, TenantCreatedIntegrationEventV1 fact);
    }
    private sealed class IntegrationDeliveryProbeHelper : IIntegrationDeliveryProbeHelper
    {
        public Task Run(IIntegrationEventHandler<TenantCreatedIntegrationEventV1> handler, TenantCreatedIntegrationEventV1 fact) => handler.HandleAsync(fact);
    }
    private interface IIntegrationDeliveryProbeCleanHelper
    {
        Task Run(TenantCreatedIntegrationEventV1 fact);
    }
    private sealed class IntegrationDeliveryProbeCleanHelper : IIntegrationDeliveryProbeCleanHelper
    {
        public Task Run(TenantCreatedIntegrationEventV1 fact) => IntegrationDeliveryProbeClean.HandleAsync(fact);
    }
    private static class IntegrationDeliveryProbeCalls
    {
        public static Task Direct(IntegrationDeliveryProbeHandler handler, TenantCreatedIntegrationEventV1 fact) => handler.HandleAsync(fact);
        public static Task ViaInterface(IIntegrationEventHandler<TenantCreatedIntegrationEventV1> handler, TenantCreatedIntegrationEventV1 fact) => handler.HandleAsync(fact);
        public static Task ViaInherited(IntegrationDeliveryProbeInheritedHandler handler, TenantCreatedIntegrationEventV1 fact) => handler.HandleAsync(fact);
        public static Func<TenantCreatedIntegrationEventV1, CancellationToken, Task> Capture(IIntegrationEventHandler<TenantCreatedIntegrationEventV1> handler) => handler.HandleAsync;
        public static async Task Async(IIntegrationEventHandler<TenantCreatedIntegrationEventV1> handler, TenantCreatedIntegrationEventV1 fact)
        {
            await Task.Yield();
            await handler.HandleAsync(fact);
        }
        public static Task ViaHelper(IIntegrationDeliveryProbeHelper helper, IIntegrationEventHandler<TenantCreatedIntegrationEventV1> handler, TenantCreatedIntegrationEventV1 fact) => helper.Run(handler, fact);
        public static InitializeContentTypeDefaultHandler Construct(ISender sender) => new(sender);
        public static IntegrationDeliveryProbeHandler? ResolveOptional(IServiceProvider services) => services.GetService<IntegrationDeliveryProbeHandler>();
        public static IntegrationDeliveryProbeHandler ResolveRequired(IServiceProvider services) => services.GetRequiredService<IntegrationDeliveryProbeHandler>();
        public static IEnumerable<IIntegrationEventHandler<TenantCreatedIntegrationEventV1>> ResolveAll(IServiceProvider services) => services.GetServices<IIntegrationEventHandler<TenantCreatedIntegrationEventV1>>();
        public static IEnumerable<IIntegrationEventHandler<TenantCreatedIntegrationEventV1>> ResolveWrapped(IServiceProvider services) => services.GetRequiredService<IEnumerable<IIntegrationEventHandler<TenantCreatedIntegrationEventV1>>>();
        public static IntegrationDeliveryProbeHandler? ResolveKeyed(IServiceProvider services) => services.GetKeyedService<IntegrationDeliveryProbeHandler>("probe");
        public static IntegrationDeliveryProbeHandler ResolveRequiredKeyed(IServiceProvider services) => services.GetRequiredKeyedService<IntegrationDeliveryProbeHandler>("probe");
        public static IEnumerable<IIntegrationEventHandler<TenantCreatedIntegrationEventV1>> ResolveAllKeyed(IServiceProvider services) => services.GetKeyedServices<IIntegrationEventHandler<TenantCreatedIntegrationEventV1>>("probe");
        public static IntegrationDeliveryProbeHandler Activate(IServiceProvider services) => ActivatorUtilities.CreateInstance<IntegrationDeliveryProbeHandler>(services);
        public static IntegrationDeliveryProbeHandler ResolveOrActivate(IServiceProvider services) => ActivatorUtilities.GetServiceOrCreateInstance<IntegrationDeliveryProbeHandler>(services);
        public static T ResolveConstrained<T>(IServiceProvider services) where T : IIntegrationEventHandler<TenantCreatedIntegrationEventV1> => services.GetRequiredService<T>();
        public static async Task<IntegrationDeliveryProbeHandler> ResolveAsync(IServiceProvider services)
        {
            await Task.Yield();
            return services.GetRequiredService<IntegrationDeliveryProbeHandler>();
        }
        public static Func<IServiceProvider, IntegrationDeliveryProbeHandler> CaptureResolution() => ServiceProviderServiceExtensions.GetRequiredService<IntegrationDeliveryProbeHandler>;
    }
    private static class IntegrationDeliveryProbeClean
    {
        public static Task HandleAsync(TenantCreatedIntegrationEventV1 fact) => Task.CompletedTask;
        public static async Task Async(TenantCreatedIntegrationEventV1 fact) => await HandleAsync(fact);
        public static Task ViaHelper(IIntegrationDeliveryProbeCleanHelper helper, TenantCreatedIntegrationEventV1 fact) => helper.Run(fact);
        public static Type Read(IntegrationEventSubscription subscription) => subscription.EventType;
        public static object? ResolveOptional(IServiceProvider services) => services.GetService<object>();
        public static object ResolveRequired(IServiceProvider services) => services.GetRequiredService<object>();
        public static IEnumerable<object> ResolveAll(IServiceProvider services) => services.GetServices<object>();
        public static IEnumerable<object> ResolveWrapped(IServiceProvider services) => services.GetRequiredService<IEnumerable<object>>();
        public static object? ResolveKeyed(IServiceProvider services) => services.GetKeyedService<object>("probe");
        public static object ResolveRequiredKeyed(IServiceProvider services) => services.GetRequiredKeyedService<object>("probe");
        public static IEnumerable<object> ResolveAllKeyed(IServiceProvider services) => services.GetKeyedServices<object>("probe");
        public static object Activate(IServiceProvider services) => ActivatorUtilities.CreateInstance<object>(services);
        public static object ResolveOrActivate(IServiceProvider services) => ActivatorUtilities.GetServiceOrCreateInstance<object>(services);
        public static T ResolveConstrained<T>(IServiceProvider services) where T : class, IComparable<T> => services.GetRequiredService<T>();
        public static async Task<object> ResolveAsync(IServiceProvider services)
        {
            await Task.Yield();
            return services.GetRequiredService<object>();
        }
        public static Func<IServiceProvider, object> CaptureResolution() => ServiceProviderServiceExtensions.GetRequiredService<object>;
    }
}
