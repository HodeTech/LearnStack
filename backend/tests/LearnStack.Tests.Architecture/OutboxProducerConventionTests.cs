using FluentAssertions;
using LearnStack.Infrastructure.Messaging;
using LearnStack.SharedKernel.Messaging;
using Mono.Cecil;
using Mono.Cecil.Cil;
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
                     typeof(AbstractDispatchProbe), typeof(VirtualDispatchProbe), typeof(DelegateProbe) })
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
        var borrowedWriteGuard = BorrowedUnitWriteGuard(types);
        while (queue.TryDequeue(out var type))
        {
            if (!visited.Add(Key(type))) continue;
            foreach (var reference in Il.ReferencedTypeReferences(type))
            {
                if (ForbiddenCapability(reference.FullName)) offenders.Add(type.FullName + " -> " + reference.FullName);
                if (reference.FullName == "LearnStack.SharedKernel.Persistence.IUnitOfWork") continue;
                if (borrowedWriteGuard && Key(reference) == ("LearnStack.SharedKernel", WriteGuardType)) continue;
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

    private const string WriteGuardType = "LearnStack.SharedKernel.Persistence.IUnitOfWorkWriteGuard";
    private const string UnitType = "LearnStack.Infrastructure.Persistence.NpgsqlUnitOfWork";
    private const string JobOwnerType = "LearnStack.SharedKernel.Jobs.JobApplicationSourceOwner";

    // Borrowing the unit's denial-only port must not traverse its unrelated commit,
    // resolver and connection ownership. This boundary exists only while the complete
    // production census has this one implementation and its exact guard closure is pure.
    private static bool BorrowedUnitWriteGuard(Dictionary<(string Assembly, string Type), TypeDefinition> types)
    {
        if (!types.TryGetValue(("LearnStack.SharedKernel", WriteGuardType), out var contract)
            || !types.TryGetValue(("LearnStack.Infrastructure", UnitType), out var unit)
            || !types.TryGetValue(("LearnStack.SharedKernel", JobOwnerType), out var job)
            || !contract.IsInterface || contract.Interfaces.Count != 0 || contract.Fields.Count != 0
            || contract.Methods.Count != 1 || contract.Methods[0] is not
            { Name: "RequireWriteAllowed", IsAbstract: true, Parameters.Count: 0, ReturnType.FullName: "System.Void" }
            || !unit.IsSealed) return false;
        var implementations = types.Values.Where(type => !type.IsInterface && Implements(type, contract, types)).ToArray();
        if (implementations.Length != 1 || implementations[0] != unit) return false;
        MethodDefinition? Method(TypeDefinition owner, string name) => owner.Methods.SingleOrDefault(method => method.Name == name);
        var guard = Method(unit, "RequireWriteAllowed");
        var refuse = Method(unit, "RefuseFixedSeedRead");
        var poison = Method(unit, "PoisonFixedSeedRead");
        var external = Method(job, "RefuseExternalEntry");
        var isExternal = Method(job, "get_IsExternal");
        var refused = Method(job, "Refused");
        var initialize = Method(job, ".cctor");
        if (guard is not { IsPublic: true, IsStatic: false, Parameters.Count: 0, ReturnType.FullName: "System.Void" }
            || refuse is not { IsPrivate: true, IsStatic: false, Parameters.Count: 0, ReturnType.FullName: "System.Void" }
            || poison is not { IsPrivate: true, IsStatic: false, Parameters.Count: 0, ReturnType.FullName: "System.Void" }) return false;
        return GuardBody(guard,
                [(Code.Call, JobOwnerType, "RefuseExternalEntry", 0), (Code.Call, "System.ObjectDisposedException", "ThrowIf", 2),
                    (Code.Call, UnitType, "RefuseFixedSeedRead", 0)],
                [(Code.Ldfld, UnitType, "_disposed"), (Code.Ldfld, UnitType, "_seedReadState")])
            && GuardBody(refuse,
                [(Code.Call, UnitType, "PoisonFixedSeedRead", 0), (Code.Newobj, "System.InvalidOperationException", ".ctor", 1)], [])
            && GuardBody(poison, [], [(Code.Stfld, UnitType, "_seedReadState"), (Code.Stfld, UnitType, "_rollbackOnly"),
                (Code.Stfld, UnitType, "_tenantContextIssued"), (Code.Stfld, UnitType, "_tenantContextAnnouncement")])
            && GuardBody(external, [(Code.Call, JobOwnerType, "get_IsExternal", 0), (Code.Call, JobOwnerType, "Refused", 0)], [])
            && GuardBody(isExternal, [(Code.Callvirt, "System.Threading.AsyncLocal`1<System.Object>", "get_Value", 0)],
                [(Code.Ldsfld, JobOwnerType, "External")])
            && GuardBody(refused, [(Code.Newobj, "System.InvalidOperationException", ".ctor", 1)], [])
            && GuardBody(initialize,
                [(Code.Newobj, "System.Threading.AsyncLocal`1<" + JobOwnerType + "/Binding>", ".ctor", 0),
                    (Code.Newobj, "System.Threading.AsyncLocal`1<System.Object>", ".ctor", 0)],
                [(Code.Stsfld, JobOwnerType, "Ambient"), (Code.Stsfld, JobOwnerType, "External")]);
    }

    private static bool GuardBody(MethodDefinition? method,
        (Code Opcode, string Owner, string Name, int Parameters)[] expectedCalls,
        (Code Opcode, string Owner, string Name)[] expectedFields)
    {
        if (method is not { HasBody: true } || method.Body.ExceptionHandlers.Count != 0
            || method.Body.Variables.Any(local => local.VariableType.FullName is not
                ("System.Boolean" or "System.Int32" or "System.InvalidOperationException"))) return false;
        var instructions = method.Body.Instructions;
        var calls = instructions.Where(instruction => instruction.Operand is MethodReference).Select(instruction =>
        {
            var call = (MethodReference)instruction.Operand;
            return (instruction.OpCode.Code, call.DeclaringType.FullName, call.Name, call.Parameters.Count);
        }).ToArray();
        var fields = instructions.Where(instruction => instruction.Operand is FieldReference).Select(instruction =>
        {
            var field = (FieldReference)instruction.Operand;
            return (instruction.OpCode.Code, field.DeclaringType.FullName, field.Name);
        }).ToArray();
        if (!calls.OrderBy(value => value.ToString(), StringComparer.Ordinal).SequenceEqual(expectedCalls.OrderBy(value => value.ToString(), StringComparer.Ordinal))
            || !fields.OrderBy(value => value.ToString(), StringComparer.Ordinal).SequenceEqual(expectedFields.OrderBy(value => value.ToString(), StringComparer.Ordinal))) return false;
        // This includes unreachable instructions: an unused unknown helper, indirect
        // call or delegate acquisition cannot hide behind a legitimate guard name.
        return instructions.All(instruction => instruction.OpCode.Code is Code.Nop or Code.Ret or Code.Call or Code.Callvirt or Code.Newobj
            or Code.Ldarg_0 or Code.Ldstr or Code.Ldnull or Code.Ldc_I4 or Code.Ldc_I4_S or Code.Ldc_I4_0 or Code.Ldc_I4_1
            or Code.Ldc_I4_2 or Code.Ldc_I4_3 or Code.Ldc_I4_4 or Code.Ldc_I4_5 or Code.Ldc_I4_6 or Code.Ldc_I4_7 or Code.Ldc_I4_8
            or Code.Ldfld or Code.Stfld or Code.Ldsfld or Code.Stsfld or Code.Throw or Code.Dup or Code.Pop
            or Code.Ceq or Code.Cgt_Un or Code.Br or Code.Br_S or Code.Brtrue or Code.Brtrue_S or Code.Brfalse or Code.Brfalse_S
            or Code.Beq or Code.Beq_S or Code.Bne_Un or Code.Bne_Un_S
            or Code.Ldloc or Code.Ldloc_S or Code.Ldloc_0 or Code.Ldloc_1 or Code.Ldloc_2 or Code.Ldloc_3
            or Code.Stloc or Code.Stloc_S or Code.Stloc_0 or Code.Stloc_1 or Code.Stloc_2 or Code.Stloc_3);
    }

    [Fact]
    public void Borrowed_write_guard_boundary_checks_its_implementation_and_every_denial_helper()
    {
        var modules = ReadProductionModules();
        try
        {
            var types = TypeMap(modules);
            var unit = types[("LearnStack.Infrastructure", UnitType)];
            var job = types[("LearnStack.SharedKernel", JobOwnerType)];
            var root = types[("LearnStack.Infrastructure", typeof(PostgresOutbox).FullName!)];
            void Dirty()
            {
                BorrowedUnitWriteGuard(types).Should().BeFalse();
                Offenders([root], types).Should().NotBeEmpty();
            }
            BorrowedUnitWriteGuard(types).Should().BeTrue();
            var helpers = unit.Methods.Where(method => method.Name is "RequireWriteAllowed" or "RefuseFixedSeedRead" or "PoisonFixedSeedRead")
                .Concat(job.Methods.Where(method => method.Name is "RefuseExternalEntry" or "get_IsExternal" or "Refused" or ".cctor"));
            foreach (var helper in helpers)
            {
                var acquisition = Instruction.Create(OpCodes.Newobj, helper.Module.ImportReference(typeof(Npgsql.NpgsqlConnection).GetConstructor(Type.EmptyTypes)!));
                helper.Body.Instructions.Add(acquisition);
                try { Dirty(); } finally { helper.Body.Instructions.Remove(acquisition); }
            }
            var guard = unit.Methods.Single(method => method.Name == "RequireWriteAllowed");
            var unknownHelper = new MethodDefinition("ReviewUnknownGuardHelper", MethodAttributes.Private, unit.Module.TypeSystem.Void);
            unknownHelper.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            unit.Methods.Add(unknownHelper);
            var unusedCall = Instruction.Create(OpCodes.Call, unknownHelper);
            guard.Body.Instructions.Add(unusedCall);
            try { Dirty(); }
            finally { guard.Body.Instructions.Remove(unusedCall); unit.Methods.Remove(unknownHelper); }
            var other = new TypeDefinition("ReviewProbe", "OtherWriteGuard", TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed,
                unit.Module.TypeSystem.Object);
            other.Interfaces.Add(new InterfaceImplementation(unit.Module.ImportReference(types[("LearnStack.SharedKernel", WriteGuardType)])));
            unit.Module.Types.Add(other);
            types.Add(Key(other), other);
            try { Dirty(); }
            finally { types.Remove(Key(other)); unit.Module.Types.Remove(other); }
            BorrowedUnitWriteGuard(types).Should().BeTrue();
            Offenders([root], types).Should().BeEmpty("removing malicious guard changes restores the borrowed denial boundary");
        }
        finally
        {
            foreach (var module in modules) module.Dispose();
        }
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
    private sealed class DelegateProbe
    {
        public static void Run() { Action acquire = ConnectionProbe.Run; acquire(); }
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
