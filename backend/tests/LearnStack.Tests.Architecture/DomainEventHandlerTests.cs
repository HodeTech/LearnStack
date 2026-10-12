using FluentAssertions;
using LearnStack.SharedKernel.Caching;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Observability;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Resilience;
using LearnStack.SharedKernel.Secrets;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed class DomainEventHandlerTests
{
    private const string HandlerContract = "MediatR.INotificationHandler`1";
    private const string ProductionHandler = "LearnStack.Modules.Tenancy.Application.Tenant.TenantCreatedDomainEventHandler";
    private static readonly string[] ModuleLayers = ["Domain", "Application.Contracts", "Application", "Infrastructure"];

    // These are the provider ports that exist today. A new provider contract must
    // extend this set; this guard does not pretend to recognize future SDKs by name.
    private static readonly Type[] ProviderPorts =
    [
        typeof(ICacheService), typeof(ISecretProvider), typeof(IEntitlementProvider),
        typeof(IFeatureFlags), typeof(IKillswitchOverlay), typeof(IErrorTrackingProvider),
        typeof(IProviderResilience<>), typeof(IHostToTenantResolver), typeof(IHostResolutionInvalidator),
        typeof(ITenantMembershipReader), typeof(IOrganizationScopeValidator),
        typeof(LearnStack.SharedKernel.Audit.IAuditConfigService),
    ];

    /// <summary>
    /// <see href="../../../docs/decisions/0057-transactional-event-production-and-completion.md#collection-completion-and-nested-refusals">ADR-0057</see>:
    /// domain reactions translate facts to outbox messages; they cannot start another effect.
    /// </summary>
    [Fact]
    public void Domain_Event_Handlers_Are_Outbox_Only()
    {
        var modules = ReadProductionModules();
        try
        {
            AssertProductionCoverage(modules);
            var graph = new TypeGraph(modules);
            var roots = graph.DomainHandlers();
            roots.Should().NotBeEmpty("Fix: classify production INotificationHandler<T> implementations whose T is an IDomainEvent");
            Offenders(roots, graph).Should().BeEmpty(
                "Fix: translate the saved domain fact through IOutbox only; move writes, requests, publication and provider work to commands or integration consumers (ADR-0057)");
        }
        finally
        {
            foreach (var module in modules) module.Dispose();
        }
    }

    [Fact]
    public void Domain_handler_census_recognizes_inherited_generic_contracts_and_excludes_other_notifications()
    {
        using var module = ReadProbes();
        var kernel = ReadKernelForProbes();
        try
        {
            var graph = new TypeGraph([module, .. kernel]);
            var names = graph.DomainHandlers().Select(type => type.FullName).ToArray();
            names.Should().Contain([ProbeName(typeof(CleanHandler)), ProbeName(typeof(InheritedHandler)), ProbeName(typeof(GenericHandler<>))])
                .And.NotContain([ProbeName(typeof(OtherNotificationHandler)), ProbeName(typeof(NotificationHandler<>))],
                    "ordinary notifications are outside this domain-event contract even when their handler has forbidden dependencies");
        }
        finally
        {
            foreach (var assembly in kernel) assembly.Dispose();
        }
    }

    public static TheoryData<Type, string> ForbiddenDependencies
    {
        get
        {
            var cases = new TheoryData<Type, string>
            {
                { typeof(ProbeEntity), "entity/root" },
                { typeof(IAggregateRoot<TenantId>), "entity/root" },
                { typeof(IHasDomainEvents), "entity/root" },
                { typeof(IAggregateWriteStore<ProbeEntity, TenantId>), "persistence" },
                { typeof(IUnitOfWork), "persistence" },
                { typeof(Microsoft.EntityFrameworkCore.DbContext), "persistence" },
                { typeof(Npgsql.NpgsqlConnection), "persistence" },
                { typeof(System.Data.Common.DbConnection), "persistence" },
                { typeof(LearnStack.SharedKernel.Audit.IAuditStore), "persistence" },
                { typeof(LearnStack.SharedKernel.Idempotency.IIdempotencyStore), "persistence" },
                { typeof(ISender), "nested request" },
                { typeof(IMediator), "nested request" },
                { typeof(IRequestHandler<>), "nested request" },
                { typeof(IRequestHandler<,>), "nested request" },
                { typeof(IPublisher), "notification publication" },
                { typeof(IEventBus), "event bus" },
                { typeof(HttpClient), "external provider" },
                { typeof(IServiceProvider), "service locator" },
                { typeof(Microsoft.Extensions.DependencyInjection.IServiceScopeFactory), "service locator" },
            };
            foreach (var port in ProviderPorts) cases.Add(port, "external provider");
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(ForbiddenDependencies))]
    public void Domain_handler_guard_rejects_each_forbidden_dependency(Type dependency, string category)
    {
        using var module = ReadProbes();
        var kernel = ReadKernelForProbes();
        try
        {
            var root = AddHandler(module, "DirtyHandler");
            root.Fields.Add(new FieldDefinition("Dependency", FieldAttributes.Public, module.ImportReference(dependency)));
            var graph = new TypeGraph([module, .. kernel]);
            Offenders(graph.DomainHandlers().Where(type => type == root), graph)
                .Should().Contain(offender => offender.Contains($"[{category}]", StringComparison.Ordinal)
                    && offender.Contains(dependency.IsGenericType ? dependency.GetGenericTypeDefinition().FullName! : ProbeName(dependency), StringComparison.Ordinal),
                    $"Fix: the same production predicate must detect {dependency.FullName}");
        }
        finally
        {
            foreach (var assembly in kernel) assembly.Dispose();
        }
    }

    public static TheoryData<Type, bool> HelperPaths => new()
    {
        { typeof(DirectHelper), true }, { typeof(IndirectHelper), true }, { typeof(IHelper), true },
        { typeof(InheritedHelper), true }, { typeof(InheritedDependencyHelper), true }, { typeof(IGenericHelper<ProbeDomainEvent>), true },
        { typeof(AsyncHelper), true }, { typeof(LambdaHelper), true },
        { typeof(AbstractDispatchHelper), true }, { typeof(VirtualDispatchHelper), true },
        { typeof(PureHelper), false }, { typeof(PureIndirectHelper), false }, { typeof(IPureHelper), false },
        { typeof(PureInheritedHelper), false }, { typeof(IGenericPureHelper<ProbeDomainEvent>), false },
        { typeof(PureAsyncHelper), false }, { typeof(PureLambdaHelper), false },
        { typeof(PureAbstractDispatchHelper), false }, { typeof(PureVirtualDispatchHelper), false },
    };

    [Theory]
    [MemberData(nameof(HelperPaths))]
    public void Domain_handler_guard_follows_helpers_interfaces_inheritance_async_and_lambdas(Type helper, bool forbidden)
    {
        using var module = ReadProbes();
        var kernel = ReadKernelForProbes();
        try
        {
            var root = AddHandler(module, "HelperHandler");
            root.Fields.Add(new FieldDefinition("Helper", FieldAttributes.Public, module.ImportReference(helper)));
            var graph = new TypeGraph([module, .. kernel]);
            var classified = graph.DomainHandlers().Single(type => type == root);
            var offenders = Offenders([classified], graph);
            if (forbidden)
                offenders.Should().Contain(offender => offender.Contains(typeof(IUnitOfWork).FullName!, StringComparison.Ordinal)
                        && offender.Contains("HelperHandler", StringComparison.Ordinal),
                    $"Fix: the production walk must follow the forbidden dependency through {helper.Name}");
            else
                offenders.Should().BeEmpty($"Fix: pure {helper.Name} must remain legal through the same helper walk");
        }
        finally
        {
            foreach (var assembly in kernel) assembly.Dispose();
        }
    }

    [Fact]
    public void Domain_handler_guard_accepts_translation_and_pure_helpers_without_traversing_outbox_implementations()
    {
        using var module = ReadProbes();
        var kernel = ReadKernelForProbes();
        try
        {
            var graph = new TypeGraph([module, .. kernel]);
            var root = graph.DomainHandlers().Single(type => type.FullName == ProbeName(typeof(CleanHandler)));
            Offenders([root], graph).Should().BeEmpty(
                "event/identifier values, IClock, IGuidFactory, IOutbox and pure helper implementations are sanctioned leaves or paths; an outbox implementation's persistence is not a handler effect");
        }
        finally
        {
            foreach (var assembly in kernel) assembly.Dispose();
        }
    }

    [Theory]
    [InlineData("RaiseDomainEvent", "further domain event")]
    [InlineData("Handle", "notification publication")]
    public void Domain_handler_guard_rejects_further_raise_and_direct_notification_dispatch(string methodName, string category)
    {
        using var module = ReadProbes();
        var kernel = ReadKernelForProbes();
        try
        {
            var root = AddHandler(module, "DispatchHandler");
            var owner = methodName == "Handle"
                ? module.ImportReference(typeof(INotificationHandler<ProbeDomainEvent>))
                : module.ImportReference(typeof(ProbeEntity));
            var method = new MethodDefinition("Dispatch", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, new MethodReference(methodName, module.TypeSystem.Void, owner)));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            root.Methods.Add(method);
            var graph = new TypeGraph([module, .. kernel]);
            Offenders(graph.DomainHandlers().Where(type => type == root), graph).Should()
                .Contain(offender => offender.Contains($"[{category}]", StringComparison.Ordinal)
                    && offender.Contains(methodName, StringComparison.Ordinal),
                    "Fix: do not dispatch another notification or raise another domain event from a domain reaction");
        }
        finally
        {
            foreach (var assembly in kernel) assembly.Dispose();
        }
    }

    [Fact]
    public void Domain_handler_guard_reaches_planted_roots_and_helpers_in_every_production_assembly()
    {
        var modules = ReadProductionModules();
        try
        {
            AssertProductionCoverage(modules);
            var roots = new TypeGraph(modules).DomainHandlers();
            var productionRoot = roots.Single(type => type.FullName == ProductionHandler);
            foreach (var owner in modules)
            {
                // Equal helper names in different assemblies deliberately exercise scope
                // resolution. Only the referenced copy has a forbidden dependency.
                var helper = new TypeDefinition("ReviewProbe", "ScopedHelper", TypeAttributes.Class | TypeAttributes.Public);
                owner.Types.Add(helper);
                helper.Fields.Add(new FieldDefinition("Unit", FieldAttributes.Public, owner.ImportReference(typeof(IUnitOfWork))));
                var dependency = new FieldDefinition("ReviewDependency", FieldAttributes.Private, productionRoot.Module.ImportReference(helper));
                productionRoot.Fields.Add(dependency);
                var decoyOwner = modules.First(module => module != owner);
                var decoy = new TypeDefinition("ReviewProbe", "ScopedHelper", TypeAttributes.Class | TypeAttributes.Public);
                decoyOwner.Types.Add(decoy);
                var plantedRoot = AddHandler(owner, "PlantedDomainHandler", productionEvent: true);
                plantedRoot.Fields.Add(new FieldDefinition("Unit", FieldAttributes.Public, owner.ImportReference(typeof(IUnitOfWork))));
                try
                {
                    var graph = new TypeGraph(modules);
                    var offenders = Offenders(graph.DomainHandlers(), graph);
                    offenders.Should().Contain(offender => offender.StartsWith(Label(productionRoot), StringComparison.Ordinal)
                        && offender.Contains(Label(helper), StringComparison.Ordinal),
                        $"Fix: preserve assembly scope and follow helpers in {owner.Name}, including Domain, Contracts and every core project");
                    offenders.Should().Contain(offender => offender.StartsWith(Label(plantedRoot), StringComparison.Ordinal),
                        $"Fix: discover domain-event handlers in {owner.Name}; no production layer may be excluded from the root census");
                }
                finally
                {
                    productionRoot.Fields.Remove(dependency);
                    owner.Types.Remove(helper);
                    owner.Types.Remove(plantedRoot);
                    decoyOwner.Types.Remove(decoy);
                }
            }
        }
        finally
        {
            foreach (var module in modules) module.Dispose();
        }
    }

    private static List<ModuleDefinition> ReadProductionModules()
    {
        var modules = new List<ModuleDefinition>();
        try
        {
            foreach (var assembly in ProductionAssemblies.All()) modules.Add(ModuleDefinition.ReadModule(assembly.Location));
            return modules;
        }
        catch
        {
            foreach (var module in modules) module.Dispose();
            throw;
        }
    }

    private static void AssertProductionCoverage(IEnumerable<ModuleDefinition> modules)
    {
        var names = modules.Select(module => module.Assembly.Name.Name).ToArray();
        names.Should().BeEquivalentTo(ProductionAssemblies.Names(),
            "Fix: scan all production projects, including every core helper assembly");
        foreach (var module in names.Where(name => name.StartsWith("LearnStack.Modules.", StringComparison.Ordinal))
                     .Select(name => name.Split('.')[2]).Distinct(StringComparer.Ordinal))
        {
            names.Where(name => name.StartsWith($"LearnStack.Modules.{module}.", StringComparison.Ordinal)).Should()
                .BeEquivalentTo(ModuleLayers
                    .Select(layer => $"LearnStack.Modules.{module}.{layer}"),
                    $"Fix: include all four {module} layers in the handler and helper census");
        }
    }

    private static string[] Offenders(IEnumerable<TypeDefinition> roots, TypeGraph graph)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var root in roots.OrderBy(Label, StringComparer.Ordinal))
        {
            var queue = new Queue<(TypeDefinition Type, string Path)>();
            queue.Enqueue((root, Label(root)));
            var visited = new HashSet<(string Assembly, string Type)>();
            while (queue.TryDequeue(out var current))
            {
                if (!visited.Add(Key(current.Type))) continue;
                foreach (var method in Il.Methods(current.Type).Select(pair => pair.Definition).Where(method => method.HasBody))
                {
                    foreach (var call in method.Body.Instructions.Select(instruction => instruction.Operand).OfType<MethodReference>())
                    {
                        if (call.Name == "RaiseDomainEvent")
                            offenders.Add($"{current.Path} -> {call.FullName} [further domain event]");
                        if (call.Name == "Handle" && graph.Hierarchy(call.DeclaringType).Any(IsNotificationHandler))
                            offenders.Add($"{current.Path} -> {call.FullName} [notification publication]");
                    }
                }

                foreach (var reference in Il.ReferencedTypeReferences(current.Type)
                             .OrderBy(reference => Key(reference).Assembly, StringComparer.Ordinal)
                             .ThenBy(reference => reference.FullName, StringComparer.Ordinal))
                {
                    var forbidden = Forbidden(reference, graph);
                    if (forbidden is not null)
                    {
                        offenders.Add($"{current.Path} -> {Label(reference)} [{forbidden}]");
                        continue;
                    }

                    if (IsTerminal(reference, graph)) continue;
                    if (graph.Resolve(reference) is not { } helper) continue;
                    queue.Enqueue((helper, $"{current.Path} -> {Label(helper)}"));
                    if (!helper.IsSealed)
                    {
                        foreach (var implementation in graph.Implementations(helper))
                            queue.Enqueue((implementation, $"{current.Path} -> {Label(helper)} -> {Label(implementation)}"));
                    }
                }
            }
        }

        return [.. offenders];
    }

    private static string? Forbidden(TypeReference reference, TypeGraph graph)
    {
        var hierarchy = graph.Hierarchy(reference).ToArray();
        if (hierarchy.Any(type => Is(type, typeof(IAggregateWriteStore<,>)) || Is(type, typeof(IUnitOfWork))
            || Is(type, typeof(LearnStack.SharedKernel.Audit.IAuditStore))
            || Is(type, typeof(LearnStack.SharedKernel.Idempotency.IIdempotencyStore))
            || InNamespace(type, "Microsoft.EntityFrameworkCore") || InNamespace(type, "Npgsql") || InNamespace(type, "System.Data")))
            return "persistence";
        if (hierarchy.Any(type => Is(type, typeof(Entity<>)) || Is(type, typeof(IAggregateRoot<>)) || Is(type, typeof(IHasDomainEvents))))
            return "entity/root";
        if (hierarchy.Any(type => Is(type, typeof(ISender)) || Is(type, typeof(IMediator))
            || Is(type, typeof(IRequestHandler<>)) || Is(type, typeof(IRequestHandler<,>)))) return "nested request";
        if (hierarchy.Any(type => Is(type, typeof(IPublisher)))) return "notification publication";
        if (hierarchy.Any(type => Is(type, typeof(IEventBus)))) return "event bus";
        if (hierarchy.Any(type => type.FullName == typeof(IServiceProvider).FullName
            || InNamespace(type, "Microsoft.Extensions.DependencyInjection"))) return "service locator";
        if (hierarchy.Any(type => ProviderPorts.Any(port => Is(type, port)) || InNamespace(type, "System.Net.Http")))
            return "external provider";
        return null;
    }

    private static bool IsTerminal(TypeReference reference, TypeGraph graph) =>
        Is(reference, typeof(IOutbox)) || Is(reference, typeof(IGuidFactory)) || Is(reference, typeof(IClock))
        || IsNotificationHandler(reference) || Is(reference, typeof(INotification))
        || graph.Hierarchy(reference).Any(type => Is(type, typeof(IDomainEvent))
            || Is(type, typeof(IIntegrationEvent)) || Is(type, typeof(IStronglyTypedId<>)));

    private static bool IsNotificationHandler(TypeReference type) =>
        type.GetElementType().FullName == HandlerContract && Key(type).Assembly == typeof(INotificationHandler<>).Assembly.GetName().Name;

    private static bool Is(TypeReference reference, Type type) =>
        Key(reference) == (type.Assembly.GetName().Name!, type.FullName!);

    private static bool InNamespace(TypeReference type, string name) =>
        type.Namespace == name || type.Namespace.StartsWith(name + ".", StringComparison.Ordinal);

    private static (string Assembly, string Type) Key(TypeReference reference)
    {
        var element = reference.GetElementType();
        var assembly = element.Scope is AssemblyNameReference scope ? scope.Name
            : element.Scope is ModuleDefinition module ? module.Assembly.Name.Name : element.Scope?.Name ?? "";
        return (assembly, element.FullName);
    }

    private static string Label(TypeReference type) => $"{Key(type).Assembly}:{type.FullName}";

    private sealed class TypeGraph
    {
        private readonly Dictionary<(string Assembly, string Type), TypeDefinition> _types;
        private readonly Dictionary<(string Assembly, string Type), TypeDefinition[]> _implementations;
        public TypeGraph(IEnumerable<ModuleDefinition> modules)
        {
            _types = modules.SelectMany(module => module.GetTypes()).ToDictionary(Key);
            // Match interfaces and non-sealed base classes conservatively: every
            // production implementation/subclass is a possible virtual dispatch target.
            // Generic definitions and inherited implementations count as well.
            _implementations = _types.Values.Where(type => !type.IsInterface)
                .SelectMany(type => Hierarchy(type).Where(contract => Resolve(contract) is { IsSealed: false })
                    .Select(contract => (Contract: Key(contract), Implementation: type)))
                .GroupBy(pair => pair.Contract)
                .ToDictionary(group => group.Key, group => group.Select(pair => pair.Implementation)
                    .Distinct().OrderBy(Label, StringComparer.Ordinal).ToArray());
        }

        public TypeDefinition? Resolve(TypeReference reference) => _types.GetValueOrDefault(Key(reference));

        public TypeDefinition[] DomainHandlers() => _types.Values.Where(type => !type.IsInterface
                && Hierarchy(type).OfType<GenericInstanceType>().Any(contract => IsNotificationHandler(contract)
                    && Hierarchy(contract.GenericArguments[0]).Any(argument => Is(argument, typeof(IDomainEvent)))))
            .OrderBy(Label, StringComparer.Ordinal).ToArray();

        public TypeDefinition[] Implementations(TypeReference contract) =>
            _implementations.GetValueOrDefault(Key(contract)) ?? [];

        public IEnumerable<TypeReference> Hierarchy(TypeReference reference)
        {
            var queue = new Queue<TypeReference>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            queue.Enqueue(reference);
            while (queue.TryDequeue(out var current))
            {
                if (!visited.Add(Label(current))) continue;
                yield return current;
                if (current is GenericParameter parameter)
                {
                    foreach (var constraint in parameter.Constraints) queue.Enqueue(constraint.ConstraintType);
                    continue;
                }

                if (Resolve(current) is not { } definition) continue;
                var arguments = current is GenericInstanceType generic ? generic.GenericArguments.ToArray() : [];
                if (definition.BaseType is { } parent) queue.Enqueue(Substitute(parent, arguments));
                foreach (var contract in definition.Interfaces) queue.Enqueue(Substitute(contract.InterfaceType, arguments));
            }
        }

        private static TypeReference Substitute(TypeReference reference, TypeReference[] arguments)
        {
            if (reference is GenericParameter { Type: GenericParameterType.Type } parameter && parameter.Position < arguments.Length)
                return arguments[parameter.Position];
            if (reference is not GenericInstanceType generic) return reference;
            var result = new GenericInstanceType(generic.ElementType);
            foreach (var argument in generic.GenericArguments) result.GenericArguments.Add(Substitute(argument, arguments));
            return result;
        }
    }

    private static ModuleDefinition ReadProbes() => ModuleDefinition.ReadModule(typeof(DomainEventHandlerTests).Assembly.Location);

    private static ModuleDefinition[] ReadKernelForProbes() => [ModuleDefinition.ReadModule(typeof(IDomainEvent).Assembly.Location)];

    private static string ProbeName(Type type) => type.FullName!.Replace('+', '/');

    private static TypeDefinition AddHandler(ModuleDefinition module, string name, bool productionEvent = false)
    {
        var root = new TypeDefinition("ReviewProbe", name, TypeAttributes.Class | TypeAttributes.Public);
        // Use the shipped event in production plants: its full ancestry must be
        // resolved from the production universe, not from this test assembly.
        var contract = new GenericInstanceType(module.ImportReference(typeof(INotificationHandler<>)));
        contract.GenericArguments.Add(module.ImportReference(productionEvent
            ? typeof(LearnStack.Modules.Tenancy.Domain.Events.TenantCreatedDomainEvent)
            : typeof(ProbeDomainEvent)));
        root.Interfaces.Add(new InterfaceImplementation(contract));
        module.Types.Add(root);
        return root;
    }

    private sealed record ProbeDomainEvent : DomainEvent;
    private sealed record OtherNotification : INotification;

    private abstract class GenericHandler<T> : INotificationHandler<T> where T : IDomainEvent
    {
        public Task Handle(T notification, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private abstract class NotificationHandler<T> : INotificationHandler<T> where T : INotification
    {
        public Task Handle(T notification, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class InheritedHandler : NotificationHandler<ProbeDomainEvent>;

    private sealed class OtherNotificationHandler : INotificationHandler<OtherNotification>
    {
        public IUnitOfWork? Unit { get; }
        public Task Handle(OtherNotification notification, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CleanHandler(IOutbox outbox, IGuidFactory guids, IClock clock, IPureHelper helper)
        : INotificationHandler<ProbeDomainEvent>
    {
        public Task Handle(ProbeDomainEvent notification, CancellationToken cancellationToken) =>
            outbox.EnqueueAsync(new LearnStack.Modules.Tenancy.Application.Contracts.IntegrationEvents.TenantCreatedIntegrationEventV1
            {
                EventId = guids.NewUuidV7(),
                TenantId = helper.Read(TenantId.From(notification.EventId)).Value,
                OccurredAt = clock.UtcNow,
            }, cancellationToken);
    }

    private interface IPureHelper
    {
        TenantId Read(TenantId id);
    }

    private class PureHelper : IPureHelper
    {
        public TenantId Read(TenantId id) => id;
    }

    private sealed class PureIndirectHelper
    {
        public PureHelper? Value { get; }
    }

    private sealed class PureInheritedHelper : PureHelper;
    private interface IGenericPureHelper<T>;
    private abstract class GenericPureHelperBase<T> : IGenericPureHelper<T>;
    private sealed class GenericPureHelper : GenericPureHelperBase<ProbeDomainEvent>;

    private static class PureAsyncHelper
    {
        public static async Task<object> Read()
        {
            await Task.Yield();
            return "fact";
        }
    }

    private static class PureLambdaHelper
    {
        public static Func<object> Read() => () => "fact";
    }

    // Deliberately forbidden persistence behind the sanctioned terminal: following
    // IOutbox implementations would make the clean handler fail this same predicate.
    private sealed class OutboxProbe : IOutbox
    {
        public IUnitOfWork? Unit { get; }
        public Task EnqueueAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ProbeEntity : Entity<TenantId>, IAggregateRoot<TenantId>;

    private class DirectHelper
    {
        public IUnitOfWork? Unit { get; }
    }

    private sealed class IndirectHelper
    {
        public DirectHelper? Value { get; }
    }

    private interface IHelper;
    private class HelperBase : IHelper;

    private sealed class InheritedHelper : HelperBase
    {
        public IUnitOfWork? Unit { get; }
    }

    private sealed class InheritedDependencyHelper : DirectHelper;
    private interface IGenericHelper<T>;
    private abstract class GenericHelperBase<T> : IGenericHelper<T>;

    private sealed class GenericHelper : GenericHelperBase<ProbeDomainEvent>
    {
        public IUnitOfWork? Unit { get; }
    }

    private abstract class AbstractDispatchHelper { public abstract Type Read(); }
    private sealed class DirtyAbstractDispatchHelper : AbstractDispatchHelper
    {
        public override Type Read() => typeof(IUnitOfWork);
    }
    private class VirtualDispatchHelper { public virtual Type Read() => typeof(Guid); }
    private sealed class DirtyVirtualDispatchHelper : VirtualDispatchHelper
    {
        public override Type Read() => typeof(IUnitOfWork);
    }
    private abstract class PureAbstractDispatchHelper { public abstract Type Read(); }
    private sealed class PureAbstractDispatchImplementation : PureAbstractDispatchHelper
    {
        public override Type Read() => typeof(Guid);
    }
    private class PureVirtualDispatchHelper { public virtual Type Read() => typeof(Guid); }
    private sealed class PureVirtualDispatchImplementation : PureVirtualDispatchHelper
    {
        public override Type Read() => typeof(TenantId);
    }

    private static class AsyncHelper
    {
        public static async Task<object> Read()
        {
            await Task.Yield();
            return typeof(IUnitOfWork);
        }
    }

    private static class LambdaHelper
    {
        public static Func<object> Read() => () => typeof(IUnitOfWork);
    }
}
