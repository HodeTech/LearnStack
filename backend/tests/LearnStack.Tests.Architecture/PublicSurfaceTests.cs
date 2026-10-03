using System.Reflection;
using FluentAssertions;
using LearnStack.Api.PublicReads;
using LearnStack.Infrastructure.Audit;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed class PublicSurfaceTests
{
    [Fact]
    public void PublicSurface_Requests_Are_Registered_Off()
    {
        var marked = ProductionAssemblies.All().SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.IsDefined(typeof(PublicSurfaceAttribute), false)).ToArray();
        marked.Should().NotBeEmpty("the public endpoint census must have a real request");
        NonOff(marked, AuditCatalogDiscovery.Catalogue()).Should().BeEmpty(
            "every public request must be explicitly Off, rather than audited or unclassified");
    }

    [Fact]
    public void Off_guard_detects_every_audit_tier_and_unregistered_requests()
    {
        var catalogue = new AuditCatalog([new ProbeSource()]);
        NonOff([typeof(MustProbe), typeof(ShouldProbe), typeof(MayProbe), typeof(UnregisteredProbe), typeof(OffProbe)], catalogue)
            .Should().BeEquivalentTo([nameof(MustProbe), nameof(ShouldProbe), nameof(MayProbe), nameof(UnregisteredProbe)]);
    }

    [Fact]
    public void PublicSurface_Endpoints_Dispatch_Through_The_Pipeline()
    {
        var controllers = ProductionAssemblies.All().SelectMany(assembly => assembly.GetTypes()).Where(IsPublicController).ToArray();
        controllers.Should().BeEquivalentTo([typeof(PublicSiteController)], "add each approved public controller to the census");
        using var module = ModuleDefinition.ReadModule(typeof(PublicSiteController).Assembly.Location);
        foreach (var controller in controllers)
        {
            var actions = controller.GetMethods().Where(method => method.IsDefined(typeof(HttpMethodAttribute), true)).ToArray();
            actions.Should().HaveCount(2);
            actions.SelectMany(method => method.GetCustomAttributes<HttpMethodAttribute>().SelectMany(attribute => attribute.HttpMethods))
                .Should().BeEquivalentTo(["GET", "HEAD"]);
            var type = module.GetType(controller.FullName!);
            foreach (var action in actions)
                Dispatches(type, action.Name).Should().BeTrue($"{controller.Name}.{action.Name} must construct a marked query and call ISender.Send");
        }
        MinimalBypasses(module).Should().BeEmpty("the only direct data-free MapGet admitted here is the existing healthz bootstrap");
    }

    [Fact]
    public void Dispatch_guard_detects_handler_helper_and_minimal_api_bypasses()
    {
        using var module = ModuleDefinition.ReadModule(typeof(PublicSurfaceTests).Assembly.Location);
        IsPublicController(typeof(PublicController)).Should().BeTrue("controller tokens must not hide the public prefix");
        IsPublicController(typeof(UppercaseController)).Should().BeTrue("routing is case-insensitive and may put the suffix on the action");
        IsPublicController(typeof(CleanController)).Should().BeFalse();
        Dispatches(Definition<CleanController>(module), "Get").Should().BeTrue();
        Dispatches(Definition<DirectHandlerController>(module), "Get").Should().BeFalse();
        Dispatches(Definition<HelperController>(module), "Get").Should().BeFalse();
        Dispatches(Definition<UnmarkedController>(module), "Get").Should().BeFalse();
        Dispatches(Definition<DiscardedMarkedController>(module), "Get").Should().BeFalse();
        Dispatches(Definition<MixedController>(module), "Get").Should().BeFalse();
        Dispatches(Definition<LambdaSendController>(module), "Get").Should().BeFalse();
        Dispatches(Definition<PrivateSendController>(module), "Get").Should().BeFalse();
        Dispatches(Definition<VariableController>(module), "Get").Should().BeFalse("unverifiable request provenance must fail closed");
        MinimalBypasses(module).Should().Contain(typeof(MinimalProbe).FullName!);
        var program = new TypeDefinition("", "Program", Mono.Cecil.TypeAttributes.Class);
        module.Types.Add(program);
        var map = new MethodDefinition("Map", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, module.TypeSystem.Void);
        program.Methods.Add(map);
        // These in-memory instruction lists are scanned, never executed.
        var call = Il.Methods(module.GetType(typeof(MinimalProbe).FullName!.Replace('+', '/'))).SelectMany(method => method.Definition.Body.Instructions)
            .Select(instruction => instruction.Operand).OfType<MethodReference>().Single(reference => reference.Name == "MapGet");
        var writer = map.Body.GetILProcessor();
        writer.Emit(Mono.Cecil.Cil.OpCodes.Ldstr, "/healthz");
        writer.Emit(Mono.Cecil.Cil.OpCodes.Call, call);
        MinimalBypasses(module).Should().NotContain("Program", "one existing health bootstrap is the clean control");
        writer.Emit(Mono.Cecil.Cil.OpCodes.Call, call);
        MinimalBypasses(module).Should().Contain("Program", "a second mapping cannot borrow the healthz exception");
    }

    [Fact]
    public void PublicSurface_Controllers_Do_Not_Access_Persistence()
    {
        var modules = ProductionAssemblies.All().Select(assembly => ModuleDefinition.ReadModule(assembly.Location)).ToArray();
        try
        {
            var map = Types(modules).ToDictionary(Key, StringComparer.Ordinal);
            var roots = ProductionAssemblies.All().SelectMany(assembly => assembly.GetTypes()).Where(IsPublicController)
                .Select(type => map[$"{type.Assembly.GetName().Name}:{type.FullName!.Replace('+', '/')}"]).ToArray();
            roots.Should().NotBeEmpty();
            PersistenceOffenders(roots, map).Should().BeEmpty(
                "public controllers and their production helpers must dispatch through the pipeline, never acquire persistence or a handler");
        }
        finally { foreach (var module in modules) module.Dispose(); }
    }

    [Fact]
    public void Persistence_guard_follows_helpers_concrete_types_and_service_location()
    {
        using var module = ModuleDefinition.ReadModule(typeof(PublicSurfaceTests).Assembly.Location);
        var map = Types([module]).ToDictionary(Key, StringComparer.Ordinal);
        PersistenceOffenders([Definition<CleanController>(module)], map).Should().BeEmpty();
        PersistenceOffenders([Definition<CleanGenericHelperController>(module)], map).Should().BeEmpty();
        Dispatches(Definition<PostDispatchHelperController>(module), "Get").Should().BeTrue(
            "the transitive persistence guard must independently refuse helper dispatch after a clean direct Send");
        foreach (var type in new[] { typeof(DirectHandlerController), typeof(HelperController), typeof(ConnectionProbe), typeof(ServiceLocatorProbe), typeof(InterfaceHelperController), typeof(AbstractHelperController), typeof(GenericInterfaceHelperController), typeof(GenericAbstractHelperController), typeof(PostDispatchHelperController), typeof(ConcreteMediatorProbe), typeof(PublisherProbe) })
            PersistenceOffenders([module.GetType(type.FullName!.Replace('+', '/'))], map).Should().NotBeEmpty(type.Name);
    }

    private static IEnumerable<string> NonOff(IEnumerable<Type> marked, AuditCatalog catalogue) => marked
        .Where(type => !catalogue.TryGet(type, out var registration) || !registration.WritesNoRow || registration.Entries.Count != 0)
        .Select(type => type.Name);

    private static bool IsPublicController(Type type)
    {
        if (type.IsAbstract || !typeof(ControllerBase).IsAssignableFrom(type)) return false;
        var name = type.Name.EndsWith("Controller", StringComparison.Ordinal) ? type.Name[..^10] : type.Name;
        return type.GetCustomAttributes<RouteAttribute>().Any(route => type.GetMethods()
            .SelectMany(method => method.GetCustomAttributes<HttpMethodAttribute>()).Any(action =>
            {
                var path = $"{route.Template?.Trim('/')}/{action.Template?.Trim('/')}".Trim('/')
                    .Replace("[controller]", name, StringComparison.OrdinalIgnoreCase);
                return path.Equals("public", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("public/", StringComparison.OrdinalIgnoreCase);
            }));
    }

    // Each action is checked independently, including its generated async body.
    // Merely finding Send somewhere else in the same controller is insufficient.
    private static bool Dispatches(TypeDefinition type, string action)
    {
        var actionNames = type.Methods.Where(method => method.CustomAttributes.Any(attribute =>
                attribute.AttributeType.FullName == typeof(HttpGetAttribute).FullName
                || attribute.AttributeType.FullName == typeof(HttpHeadAttribute).FullName))
            .Select(method => method.Name).Append(action).ToHashSet(StringComparer.Ordinal);
        var allSends = Il.Methods(type).Where(method => method.Definition.HasBody)
            .SelectMany(method => method.Definition.Body.Instructions)
            .Count(instruction => instruction.Operand is MethodReference call
                && call.DeclaringType.FullName == typeof(ISender).FullName && call.Name == "Send");
        if (allSends != actionNames.Count) return false;
        var sends = Il.Methods(type).Where(method => (method.DeclaredAs == action || method.Definition.Name.StartsWith("<" + action + ">", StringComparison.Ordinal)) && method.Definition.HasBody)
            .SelectMany(method => method.Definition.Body.Instructions.Select((instruction, index) => (method.Definition, instruction, index)))
            .Where(item => item.instruction.Operand is MethodReference call
                && call.DeclaringType.FullName == typeof(ISender).FullName && call.Name == "Send").ToArray();
        // Approved actions dispatch once, constructing the request at that call.
        // Locals, branches and helper-produced requests are deliberately unverifiable.
        if (sends.Length != 1) return false;
        var (definition, instruction, index) = sends[0];
        var send = (MethodReference)instruction.Operand;
        if (!send.HasThis || send.Parameters.Count != 2
            || send.Parameters[1].ParameterType.FullName != typeof(CancellationToken).FullName) return false;
        var position = index - 1;
        if (!SkipValue(definition.Body.Instructions, ref position)) return false;
        while (position >= 0 && definition.Body.Instructions[position].OpCode == OpCodes.Nop) position--;
        return position >= 0 && definition.Body.Instructions[position].OpCode == OpCodes.Newobj
            && definition.Body.Instructions[position].Operand is MethodReference constructor
            && MarkedNames.Contains(constructor.DeclaringType.FullName.Replace('/', '+'));
    }

    // Walk the cancellation-token expression backwards. Stop at control flow and
    // unsupported stack shapes rather than guessing the request argument's origin.
    private static bool SkipValue(Mono.Collections.Generic.Collection<Instruction> instructions, ref int position)
    {
        var needed = 1;
        while (position >= 0 && needed > 0)
        {
            var instruction = instructions[position--];
            if (instruction.OpCode == OpCodes.Nop) continue;
            if (instruction.OpCode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch or FlowControl.Return or FlowControl.Throw) return false;
            var push = instruction.OpCode.StackBehaviourPush switch
            {
                StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4 or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
                StackBehaviour.Varpush when instruction.Operand is MethodReference call =>
                    instruction.OpCode == OpCodes.Newobj || call.ReturnType.FullName != "System.Void" ? 1 : 0,
                _ => 0
            };
            var pop = instruction.OpCode.StackBehaviourPop switch
            {
                StackBehaviour.Pop0 => 0,
                StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
                StackBehaviour.Varpop when instruction.Operand is MethodReference call =>
                    call.Parameters.Count + (call.HasThis && instruction.OpCode != OpCodes.Newobj ? 1 : 0),
                _ => -1
            };
            if (push != 1 || pop < 0) return false;
            needed = needed - push + pop;
        }
        return needed == 0;
    }

    private static readonly HashSet<string> MarkedNames = ProductionAssemblies.All()
        .Append(typeof(PublicSurfaceTests).Assembly).SelectMany(assembly => assembly.GetTypes())
        .Where(type => type.IsDefined(typeof(PublicSurfaceAttribute), false))
        .Select(type => type.FullName!).ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<string> MinimalBypasses(ModuleDefinition module) => Types([module]).Where(type =>
    {
        var methods = Il.Methods(type).Where(method => method.Definition.HasBody).ToArray();
        var calls = methods.SelectMany(method => method.Definition.Body.Instructions)
            .Select(instruction => instruction.Operand).OfType<MethodReference>()
            .Where(call => call.Name is "MapGet" or "MapPost" or "MapPut" or "MapDelete" or "MapPatch" or "MapMethods").ToArray();
        return calls.Length != 0 && !(type.Name == "Program" && calls.Length == 1
            && methods.Any(method => method.Definition.Body.Instructions.Any(item => item.Operand is string path && path == "/healthz")));
    }).Select(type => type.FullName.Replace('/', '+'));

    private static IEnumerable<string> PersistenceOffenders(IEnumerable<TypeDefinition> roots, Dictionary<string, TypeDefinition> map)
    {
        var controllers = roots.ToArray();
        var controllerKeys = controllers.Select(Key).ToHashSet(StringComparer.Ordinal);
        var queue = new Queue<TypeDefinition>(controllers);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (queue.TryDequeue(out var type))
        {
            if (!visited.Add(Key(type))) continue;
            if (type.IsInterface || type.IsAbstract)
                foreach (var implementation in map.Values.Where(candidate => !candidate.IsInterface && Implements(candidate, Key(type), map, [])))
                    queue.Enqueue(implementation);
            var references = Il.ReferencedTypeReferences(type).ToArray();
            if (references.Any(reference => Forbidden(reference.FullName))
                || (!controllerKeys.Contains(Key(type)) && references.Any(reference =>
                    reference.FullName == typeof(ISender).FullName || reference.FullName == typeof(IMediator).FullName))
                || Il.Methods(type).Any(method => method.Definition.HasBody && method.Definition.Body.Instructions.Any(instruction =>
                    instruction.Operand is MethodReference call && call.Name == "get_RequestServices")))
                yield return type.FullName;
            foreach (var reference in references)
                if (map.TryGetValue(Key(reference), out var helper)
                    // Il already scans generated members as part of their owner.
                    // Re-enqueuing an action's state machine would misclassify its
                    // sanctioned sender as a separate helper dependency.
                    && !(helper.IsNested && visited.Contains(Key(helper.DeclaringType))
                        && helper.CustomAttributes.Any(attribute => attribute.AttributeType.Name == "CompilerGeneratedAttribute")))
                    queue.Enqueue(helper);
        }
    }

    private static bool Implements(TypeDefinition type, string contract, Dictionary<string, TypeDefinition> map, HashSet<string> visited)
    {
        if (!visited.Add(Key(type))) return false;
        return type.Interfaces.Select(item => item.InterfaceType).Concat(type.BaseType is { } parent ? [parent] : [])
            .Any(reference => Key(reference) == contract
                || (map.TryGetValue(Key(reference), out var definition) && Implements(definition, contract, map, visited)));
    }

    private static bool Forbidden(string name) => name.StartsWith("Npgsql", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
        || name.StartsWith("System.Data.", StringComparison.Ordinal)
        || name.StartsWith("MediatR.IRequestHandler", StringComparison.Ordinal)
        || name.StartsWith("MediatR.INotificationHandler", StringComparison.Ordinal)
        || name == typeof(IMediator).FullName || name == typeof(Mediator).FullName || name == typeof(IPublisher).FullName
        || name.StartsWith("Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions", StringComparison.Ordinal)
        || name == typeof(IServiceProvider).FullName
        || name.StartsWith("LearnStack.SharedKernel.Persistence.IUnitOfWork", StringComparison.Ordinal)
        || (name.StartsWith("LearnStack.Modules.", StringComparison.Ordinal)
            && (name.Contains(".Infrastructure.", StringComparison.Ordinal) || name.Contains(".Domain.", StringComparison.Ordinal)
                || (name.Contains(".Application.", StringComparison.Ordinal) && !name.Contains(".Application.Contracts.", StringComparison.Ordinal))))
        || name.EndsWith("IPublicTenantConfigurationReader", StringComparison.Ordinal)
        || name.EndsWith("ITenantSettingsAccessor", StringComparison.Ordinal);

    private static string Key(TypeReference type)
    {
        var definition = type.GetElementType();
        return $"{definition.Scope?.Name?.Replace(".dll", "", StringComparison.Ordinal)}:{definition.FullName}";
    }
    private static IEnumerable<TypeDefinition> Types(IEnumerable<ModuleDefinition> modules) => modules.SelectMany(module => module.Types.SelectMany(Flatten));
    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(Flatten));
    private static TypeDefinition Definition<T>(ModuleDefinition module) => module.GetType(typeof(T).FullName!.Replace('+', '/'));

    [PublicSurface] private sealed record OffProbe : IRequest<Result<string>>;
    [PublicSurface] private sealed record MustProbe;
    [PublicSurface] private sealed record ShouldProbe;
    [PublicSurface] private sealed record MayProbe;
    [PublicSurface] private sealed record UnregisteredProbe;
    private sealed record UnmarkedProbe : IRequest<Result<string>>;
    private sealed class ProbeSource : IAuditCatalogSource
    {
        public string ModuleName => "probe";
        public void Describe(IAuditCatalogBuilder builder) => builder.Off<OffProbe>()
            .MustAudit<MustProbe>("probe.page.read", OperationType.ReadSensitive, typeof(object))
            .ShouldAudit<ShouldProbe>("probe.page.read", OperationType.ReadSensitive, typeof(object))
            .MayAudit<MayProbe>("probe.page.read", OperationType.ReadSensitive, typeof(object));
    }
    private sealed class CleanController(ISender sender)
    {
        public Task<Result<string>> Get(CancellationToken cancellationToken) => sender.Send(new OffProbe(), cancellationToken);
    }
    private sealed class DirectHandlerController(IRequestHandler<OffProbe, Result<string>> handler)
    {
        public Task<Result<string>> Get(CancellationToken cancellationToken) => handler.Handle(new OffProbe(), cancellationToken);
    }
    private sealed class HelperController(DirectHandlerController helper)
    {
        public Task<Result<string>> Get(CancellationToken cancellationToken) => helper.Get(cancellationToken);
    }
    private sealed class UnmarkedController(ISender sender)
    {
        public Task<Result<string>> Get(CancellationToken cancellationToken) => sender.Send(new UnmarkedProbe(), cancellationToken);
    }
    private sealed class DiscardedMarkedController(ISender sender)
    {
        public Task<Result<string>> Get(CancellationToken cancellationToken)
        {
            _ = new OffProbe();
            return sender.Send(new UnmarkedProbe(), cancellationToken);
        }
    }
    private sealed class MixedController(ISender sender)
    {
        public async Task<Result<string>> Get(CancellationToken cancellationToken)
        {
            var result = await sender.Send(new OffProbe(), cancellationToken);
            await sender.Send(new UnmarkedProbe(), cancellationToken);
            return result;
        }
    }
    private sealed class LambdaSendController(ISender sender)
    {
        public async Task<Result<string>> Get(CancellationToken cancellationToken)
        {
            var result = await sender.Send(new OffProbe(), cancellationToken);
            Func<Task<Result<string>>> extra = () => sender.Send(new UnmarkedProbe(), cancellationToken);
            await extra();
            return result;
        }
    }
    private sealed class PrivateSendController(ISender sender)
    {
        public async Task<Result<string>> Get(CancellationToken cancellationToken)
        {
            var result = await sender.Send(new OffProbe(), cancellationToken);
            await HiddenSend(cancellationToken);
            return result;
        }
        private Task<Result<string>> HiddenSend(CancellationToken cancellationToken) => sender.Send(new UnmarkedProbe(), cancellationToken);
    }
    private sealed class VariableController(ISender sender, IRequest<Result<string>> request)
    {
        public Task<Result<string>> Get(CancellationToken cancellationToken)
        {
            _ = new OffProbe();
            return sender.Send(request, cancellationToken);
        }
    }
    private interface IConnectionProbe
    {
        Task<string> Get(CancellationToken cancellationToken);
    }
    private sealed class InterfaceHelperController(IConnectionProbe helper)
    {
        public Task<string> Get(CancellationToken cancellationToken) => helper.Get(cancellationToken);
    }
    private abstract class AbstractConnectionProbe
    {
        public abstract Task<string> Get(CancellationToken cancellationToken);
    }
    private sealed class AbstractHelperController(AbstractConnectionProbe helper)
    {
        public Task<string> Get(CancellationToken cancellationToken) => helper.Get(cancellationToken);
    }
    private sealed class ConcreteConnectionProbe(Npgsql.NpgsqlDataSource source) : AbstractConnectionProbe
    {
        public override async Task<string> Get(CancellationToken cancellationToken)
        {
            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            return connection.State.ToString();
        }
    }
    private sealed class ConnectionProbe(Npgsql.NpgsqlDataSource source) : IConnectionProbe
    {
        public async Task<string> Get(CancellationToken cancellationToken)
        {
            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            return connection.State.ToString();
        }
    }
    private interface IGenericConnectionProbe<T>
    {
        Task<string> Get(CancellationToken cancellationToken);
    }
    private abstract class GenericAbstractConnectionProbe<T>
    {
        public abstract Task<string> Get(CancellationToken cancellationToken);
    }
    private sealed class GenericConnectionProbe<T>(Npgsql.NpgsqlDataSource source)
        : GenericAbstractConnectionProbe<T>, IGenericConnectionProbe<T>
    {
        public override async Task<string> Get(CancellationToken cancellationToken)
        {
            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            return connection.State.ToString();
        }
    }
    private sealed class GenericInterfaceHelperController(IGenericConnectionProbe<string> helper)
    {
        public Task<string> Get(CancellationToken cancellationToken) => helper.Get(cancellationToken);
    }
    private sealed class GenericAbstractHelperController(GenericAbstractConnectionProbe<string> helper)
    {
        public Task<string> Get(CancellationToken cancellationToken) => helper.Get(cancellationToken);
    }
    private interface ICleanGenericHelper<T>
    {
        T Get();
    }
    private sealed class CleanGenericHelper<T> : ICleanGenericHelper<T>
    {
        public T Get() => default!; // Never executed; clean generic implementation traversal control.
    }
    private sealed class CleanGenericHelperController(ICleanGenericHelper<string> helper)
    {
        public string Get() => helper.Get();
    }
    private sealed class HiddenSendHelper(ISender sender)
    {
        public Task<Result<string>> Get(CancellationToken cancellationToken) => sender.Send(new UnmarkedProbe(), cancellationToken);
    }
    private sealed class PostDispatchHelperController(ISender sender, HiddenSendHelper helper)
    {
        public async Task<Result<string>> Get(CancellationToken cancellationToken)
        {
            var result = await sender.Send(new OffProbe(), cancellationToken);
            await helper.Get(cancellationToken);
            return result;
        }
    }
    private sealed class ConcreteMediatorProbe(Mediator mediator)
    {
        public Task<Result<string>> Get(CancellationToken cancellationToken) => mediator.Send(new UnmarkedProbe(), cancellationToken);
    }
    private sealed class PublisherProbe(IPublisher publisher)
    {
        public Task Get(CancellationToken cancellationToken) => publisher.Publish(new object(), cancellationToken);
    }
    private sealed class ServiceLocatorProbe(IServiceProvider services)
    {
        public object? Get() => services.GetService(typeof(Npgsql.NpgsqlDataSource));
    }
    [Route("[controller]")]
    private sealed class PublicController : ControllerBase
    {
        [HttpGet("site")] public OkObjectResult Get() => Ok("probe");
    }
    [Route("PUBLIC")]
    private sealed class UppercaseController : ControllerBase
    {
        [HttpHead("site")] public OkObjectResult Head() => Ok("probe");
    }
    private static class MinimalProbe
    {
        public static void Map(WebApplication app) => app.MapGet("/api/v1/public/probe", () => "bypass");
    }
}
