using FluentAssertions;
using LearnStack.Infrastructure.Messaging;
using LearnStack.SharedKernel.Messaging;
using Mono.Cecil;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>ADR-0057: enqueue borrows the admitted unit; only its owner commits.</summary>
public sealed class OutboxProducerConventionTests
{
    [Fact]
    public void Outbox_Enqueue_Uses_The_Announced_Transaction()
    {
        var modules = ReadProductionModules();
        try
        {
            modules.Select(module => module.Assembly.Name.Name).Should().BeEquivalentTo(ProductionAssemblies.Names(),
                "Fix: every production helper assembly must participate in the producer walk");
            var types = TypeMap(modules);
            var roots = Roots(types);
            roots.Select(type => type.FullName).Should().Contain(typeof(PostgresOutbox).FullName,
                "Fix: classify the real registered enqueue implementation before scanning its helpers");
            Offenders(roots, types).Should().BeEmpty(
                "Fix: enqueue must reuse IUnitOfWork.Connection/Transaction; no helper may open, commit, roll back or publish independently");
        }
        finally
        {
            foreach (var module in modules) module.Dispose();
        }
    }

    [Fact]
    public void Producer_walk_detects_connection_commit_publication_and_interface_helpers()
    {
        using var module = ModuleDefinition.ReadModule(typeof(OutboxProducerConventionTests).Assembly.Location);
        using var kernel = ModuleDefinition.ReadModule(typeof(IOutbox).Assembly.Location);
        var types = TypeMap([module, kernel]);
        Roots(types).Select(type => type.FullName).Should().Contain(typeof(InheritedOutboxProbe).FullName!.Replace('+', '/'),
            "Fix: an inherited IOutbox implementation must remain in the production census");
        foreach (var probe in new[] { typeof(ConnectionProbe), typeof(CommitProbe), typeof(PublishProbe),
                     typeof(InterfaceProbe), typeof(AsyncProbe), typeof(LocatorProbe),
                     typeof(AbstractDispatchProbe), typeof(VirtualDispatchProbe) })
        {
            Offenders([module.GetType(probe.FullName!.Replace('+', '/'))], types).Should().NotBeEmpty(
                $"Fix: the same producer walk must reject {probe.Name}");
        }
        foreach (var probe in new[] { typeof(CleanProbe), typeof(CleanAbstractDispatchProbe), typeof(CleanVirtualDispatchProbe) })
            Offenders([module.GetType(probe.FullName!.Replace('+', '/'))], types).Should().BeEmpty(
                $"Fix: the same producer walk must preserve clean {probe.Name}");
    }

    [Fact]
    public void Producer_walk_reaches_helpers_in_other_production_assemblies()
    {
        var modules = ReadProductionModules();
        try
        {
            var owner = modules.Single(module => module.Name == "LearnStack.Infrastructure.dll");
            var root = owner.GetType(typeof(PostgresOutbox).FullName!);
            foreach (var name in new[] { "LearnStack.Application.dll", "LearnStack.Modules.Tenancy.Application.Contracts.dll" })
            {
                var external = modules.Single(module => module.Name == name);
                var helper = new TypeDefinition("ReviewProbe", "ProducerHelper", TypeAttributes.Public | TypeAttributes.Class);
                helper.Fields.Add(new FieldDefinition("Bus", FieldAttributes.Public, external.ImportReference(typeof(IEventBus))));
                external.Types.Add(helper);
                var field = new FieldDefinition("ReviewHelper", FieldAttributes.Private, owner.ImportReference(helper));
                root.Fields.Add(field);
                Offenders([root], TypeMap(modules)).Should().Contain(item => item.StartsWith(helper.FullName, StringComparison.Ordinal),
                    "Fix: a producer helper in another layer must not escape the production walk");
                root.Fields.Remove(field);
                external.Types.Remove(helper);
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

    private static Dictionary<(string Assembly, string Type), TypeDefinition> TypeMap(IEnumerable<ModuleDefinition> modules) =>
        modules.SelectMany(module => module.GetTypes()).ToDictionary(type => (type.Module.Assembly.Name.Name, type.FullName));

    private static TypeDefinition[] Roots(Dictionary<(string Assembly, string Type), TypeDefinition> types)
    {
        var contract = types[(typeof(IOutbox).Assembly.GetName().Name!, typeof(IOutbox).FullName!)];
        return types.Values.Where(type => !type.IsInterface && !type.IsAbstract && Implements(type, contract, types)).ToArray();
    }

    private static (string Assembly, string Type) Key(TypeReference reference) =>
        (reference.Scope is AssemblyNameReference assembly ? assembly.Name
            : reference.Scope is ModuleDefinition module ? module.Assembly.Name.Name : reference.Scope.Name,
         reference.FullName);

    private static SortedSet<string> Offenders(IEnumerable<TypeDefinition> roots,
        Dictionary<(string Assembly, string Type), TypeDefinition> types)
    {
        var visited = new HashSet<(string Assembly, string Type)>();
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var queue = new Queue<TypeDefinition>(roots);
        while (queue.TryDequeue(out var type))
        {
            if (!visited.Add(Key(type))) continue;
            foreach (var reference in Il.ReferencedTypeReferences(type))
            {
                if (ForbiddenCapability(reference.FullName)) offenders.Add(type.FullName + " -> " + reference.FullName);
                if (reference.FullName == "LearnStack.SharedKernel.Persistence.IUnitOfWork") continue;
                if (!types.TryGetValue(Key(reference), out var helper)) continue;
                queue.Enqueue(helper);
                if (!helper.IsSealed)
                {
                    foreach (var target in types.Values.Where(candidate => !candidate.IsInterface && Implements(candidate, helper, types)))
                        queue.Enqueue(target);
                }
            }
            foreach (var method in Il.Methods(type).Select(item => item.Definition).Where(method => method.HasBody))
            {
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.Operand is not MethodReference call) continue;
                    var owner = call.DeclaringType.FullName;
                    var connection = owner is "Npgsql.NpgsqlConnection" or "System.Data.Common.DbConnection" or "System.Data.IDbConnection";
                    var transaction = owner is "Npgsql.NpgsqlTransaction" or "System.Data.Common.DbTransaction"
                        or "System.Data.IDbTransaction" or "LearnStack.SharedKernel.Persistence.IUnitOfWork";
                    if ((connection && call.Name is ".ctor" or "Open" or "OpenAsync" or "BeginTransaction" or "BeginTransactionAsync")
                        || (transaction && call.Name is "BeginTransactionAsync" or "Commit" or "CommitAsync" or "Rollback" or "RollbackAsync")
                        || owner.StartsWith("Npgsql.NpgsqlDataSource", StringComparison.Ordinal)
                        || owner is "System.Data.Common.DbDataSource" or "System.Data.Common.DbProviderFactory")
                        offenders.Add(type.FullName + " -> " + call.FullName);
                }
            }
        }
        return offenders;
    }

    private static bool Implements(TypeDefinition candidate, TypeDefinition contract,
        Dictionary<(string Assembly, string Type), TypeDefinition> types)
    {
        var queue = new Queue<TypeReference>(candidate.Interfaces.Select(item => item.InterfaceType));
        if (candidate.BaseType is { } parent) queue.Enqueue(parent);
        var visited = new HashSet<(string Assembly, string Type)>();
        while (queue.TryDequeue(out var reference))
        {
            if (reference is GenericInstanceType generic) reference = generic.ElementType;
            if (!visited.Add(Key(reference))) continue;
            if (Key(reference) == Key(contract)) return true;
            if (!types.TryGetValue(Key(reference), out var definition)) continue;
            foreach (var inherited in definition.Interfaces) queue.Enqueue(inherited.InterfaceType);
            if (definition.BaseType is { } baseType) queue.Enqueue(baseType);
        }
        return false;
    }

    private static bool ForbiddenCapability(string name) => name is
        "LearnStack.SharedKernel.Messaging.IEventBus" or "MediatR.IPublisher" or "MediatR.ISender" or "MediatR.IMediator"
        or "System.IServiceProvider" or "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory"
        || name.StartsWith("Npgsql.NpgsqlDataSource", StringComparison.Ordinal);

    private sealed class ConnectionProbe
    {
        public static void Run() { using var connection = new Npgsql.NpgsqlConnection(); connection.Open(); }
    }
    private sealed class CommitProbe(LearnStack.SharedKernel.Persistence.IUnitOfWork unit)
    {
        public Task Run() => unit.CommitAsync();
    }
    private sealed class PublishProbe(IEventBus bus)
    {
        public Task Run(IntegrationEventEnvelope envelope) => bus.PublishAsync(envelope);
    }
    private abstract class AbstractOutboxProbe : IOutbox
    {
        public Task EnqueueAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class InheritedOutboxProbe : AbstractOutboxProbe;
    private interface IHelper { void Run(); }
    private abstract class HelperBase : IHelper { public abstract void Run(); }
    private sealed class DirtyHelper : HelperBase { public override void Run() => ConnectionProbe.Run(); }
    private sealed class InterfaceProbe(IHelper helper) { public void Run() => helper.Run(); }
    private abstract class AbstractDispatchHelper { public abstract void Run(); }
    private sealed class DirtyAbstractDispatchHelper : AbstractDispatchHelper { public override void Run() => ConnectionProbe.Run(); }
    private sealed class AbstractDispatchProbe(AbstractDispatchHelper helper) { public void Run() => helper.Run(); }
    private class VirtualDispatchHelper { public virtual void Run() { } }
    private sealed class DirtyVirtualDispatchHelper : VirtualDispatchHelper { public override void Run() => ConnectionProbe.Run(); }
    private sealed class VirtualDispatchProbe(VirtualDispatchHelper helper) { public void Run() => helper.Run(); }
    private abstract class CleanAbstractDispatchHelper { public abstract void Run(); }
    private sealed class CleanAbstractDispatchImplementation : CleanAbstractDispatchHelper { public override void Run() { } }
    private sealed class CleanAbstractDispatchProbe(CleanAbstractDispatchHelper helper) { public void Run() => helper.Run(); }
    private class CleanVirtualDispatchHelper { public virtual void Run() { } }
    private sealed class CleanVirtualDispatchImplementation : CleanVirtualDispatchHelper { public override void Run() { } }
    private sealed class CleanVirtualDispatchProbe(CleanVirtualDispatchHelper helper) { public void Run() => helper.Run(); }
    private sealed class AsyncProbe
    {
        public static async Task Run() { await Task.Yield(); ConnectionProbe.Run(); }
    }
    private sealed class LocatorProbe(IServiceProvider provider)
    {
        public object? Run() => provider.GetService(typeof(IEventBus));
    }
    private sealed class CleanProbe(LearnStack.SharedKernel.Persistence.IUnitOfWork unit)
    {
        public System.Data.Common.DbTransaction? Read() => unit.Transaction;
    }
}
