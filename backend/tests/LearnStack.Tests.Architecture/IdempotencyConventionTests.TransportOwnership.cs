using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

public sealed partial class IdempotencyConventionTests
{
    private const string KeyAttribute = "LearnStack.Api.Idempotency.IdempotentAttribute";
    private const string GenericFilter = "LearnStack.Api.Idempotency.IdempotencyFilter";
    private const string ProtectedFilter = "LearnStack.Api.Idempotency.ProtectedIdempotencyFilter";
    private const string CleanupPort = "LearnStack.SharedKernel.Idempotency.IIdempotencyBusinessOwnerCleanup";

    private static IEnumerable<string> TransportOwnershipOffenders(ModuleDefinition[] modules)
    {
        var types = Types(modules).ToArray();
        var sites = types.Where(type => !type.Name.Contains('<', StringComparison.Ordinal)).SelectMany(type => Il.Methods(type)
            .Where(pair => pair.Definition.HasBody).SelectMany(pair => pair.Definition.Body.Instructions
                .Where(instruction => instruction.Operand is MethodReference)
                .Select(instruction => (Owner: type.FullName + "." + pair.DeclaredAs, Instruction: instruction, Target: (MethodReference)instruction.Operand)))).ToArray();
        foreach (var filter in new[] { GenericFilter, ProtectedFilter })
        {
            var factories = sites.Where(site => site.Target is GenericInstanceMethod generic
                && generic.GenericArguments.Any(argument => argument.FullName == filter)).ToArray();
            if (factories.Length != 1 || factories[0].Owner != KeyAttribute + ".CreateInstance"
                || factories[0].Instruction.OpCode != OpCodes.Call
                || factories[0].Target.DeclaringType.FullName != "Microsoft.Extensions.DependencyInjection.ActivatorUtilities"
                || factories[0].Target.Name != "CreateInstance")
                yield return "Idempotency filter factory callers: " + filter;
            if (sites.Any(site => site.Target.DeclaringType.FullName == filter && site.Target.Name == ".ctor"))
                yield return "Direct idempotency filter construction: " + filter;
        }
        if (sites.Any(site => site.Target.DeclaringType.FullName is KeyAttribute or "Microsoft.AspNetCore.Mvc.Filters.IFilterFactory"
                && site.Target.Name is ".ctor" or "CreateInstance"))
            yield return "Direct idempotency attribute/factory invocation";
        foreach (var member in new[] { ".ctor", "ReadAsync" })
        {
            var calls = sites.Where(site => site.Target.DeclaringType.FullName == Dispatcher && site.Target.Name == member).ToArray();
            if (calls.Length != 1 || calls[0].Owner != ProtectedFilter + ".ReplayAsync"
                || (member == ".ctor" ? calls[0].Instruction.OpCode != OpCodes.Newobj : calls[0].Instruction.OpCode.Code is not (Code.Call or Code.Callvirt)))
                yield return "Protected replay callers: " + member;
        }
        var cleanupTypes = types.Where(type => type.Interfaces.Any(contract => contract.InterfaceType.FullName == CleanupPort))
            .Select(type => type.FullName).Append(CleanupPort).ToHashSet(StringComparer.Ordinal);
        var cleanup = sites.Where(site => cleanupTypes.Contains(site.Target.DeclaringType.FullName) && site.Target.Name == "EndBusinessOwnerAsync").ToArray();
        var expected = new[] { GenericFilter + ".EndBusinessOwnerAsync", ProtectedFilter + ".EndBusinessOwnerAsync" };
        if (!cleanup.Select(site => site.Owner).Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal)
            || cleanup.Any(site => site.Target.DeclaringType.FullName != CleanupPort || site.Instruction.OpCode != OpCodes.Callvirt))
            yield return "Idempotency business cleanup callers";
    }

    private static void TransportOwnershipDirtyControls(ModuleDefinition[] modules)
    {
        var types = Types(modules).ToArray();
        var unrelated = Body(types, Dispatcher, "ReadAsync");
        TransportOwnershipOffenders(modules).Should().BeEmpty();
        void Plant(Instruction instruction, string expected)
        {
            unrelated.Body.Instructions.Add(instruction);
            try { TransportOwnershipOffenders(modules).Should().Contain(expected); }
            finally { unrelated.Body.Instructions.Remove(instruction); }
        }
        var factory = types.Single(type => type.FullName == KeyAttribute).Methods.Single(method => method.Name == "CreateInstance");
        foreach (var target in factory.Body.Instructions.Select(instruction => instruction.Operand).OfType<GenericInstanceMethod>()
                     .Where(target => target.Name == "CreateInstance"))
            Plant(Instruction.Create(OpCodes.Call, target), "Idempotency filter factory callers: " + target.GenericArguments.Single().FullName);
        foreach (var instruction in factory.Body.Instructions.Where(instruction => instruction.Operand is GenericInstanceMethod { Name: "CreateInstance" }).ToArray())
        {
            var target = (GenericInstanceMethod)instruction.Operand;
            var filter = target.GenericArguments.Single();
            var factoryPosition = factory.Body.Instructions.IndexOf(instruction);
            factory.Body.Instructions.Remove(instruction);
            try { TransportOwnershipOffenders(modules).Should().Contain("Idempotency filter factory callers: " + filter.FullName); }
            finally { factory.Body.Instructions.Insert(factoryPosition, instruction); }
            target.GenericArguments[0] = types.Single(type => type.FullName == (filter.FullName == GenericFilter ? ProtectedFilter : GenericFilter));
            try { TransportOwnershipOffenders(modules).Should().Contain("Idempotency filter factory callers: " + filter.FullName); }
            finally { target.GenericArguments[0] = filter; }
        }
        Plant(Instruction.Create(OpCodes.Callvirt, factory), "Direct idempotency attribute/factory invocation");
        Plant(Instruction.Create(OpCodes.Newobj, types.Single(type => type.FullName == KeyAttribute).Methods
            .Single(method => method.IsConstructor && method.Parameters.Count == 0)), "Direct idempotency attribute/factory invocation");
        foreach (var filter in new[] { GenericFilter, ProtectedFilter })
            Plant(Instruction.Create(OpCodes.Newobj, types.Single(type => type.FullName == filter).Methods.Single(method => method.IsConstructor && !method.IsStatic)),
                "Direct idempotency filter construction: " + filter);
        foreach (var member in new[] { ".ctor", "ReadAsync" })
        {
            var target = types.Single(type => type.FullName == Dispatcher).Methods.Single(method => method.Name == member);
            Plant(Instruction.Create(member == ".ctor" ? OpCodes.Newobj : OpCodes.Ldftn, target), "Protected replay callers: " + member);
        }
        var cleanup = types.Single(type => type.FullName == CleanupPort).Methods.Single(method => method.Name == "EndBusinessOwnerAsync");
        foreach (var opcode in new[] { OpCodes.Callvirt, OpCodes.Ldftn })
            Plant(Instruction.Create(opcode, cleanup), "Idempotency business cleanup callers");
        var concrete = types.Single(type => type.FullName == "LearnStack.Infrastructure.Persistence.NpgsqlUnitOfWork")
            .Methods.Single(method => method.Name == "EndBusinessOwnerAsync");
        Plant(Instruction.Create(OpCodes.Callvirt, concrete), "Idempotency business cleanup callers");
        var replay = Body(types, ProtectedFilter, "ReplayAsync");
        var actual = replay.Body.Instructions.Single(instruction => instruction.Operand is MethodReference target
            && target.DeclaringType.FullName == Dispatcher && target.Name == "ReadAsync");
        var position = replay.Body.Instructions.IndexOf(actual);
        replay.Body.Instructions.Remove(actual);
        try { TransportOwnershipOffenders(modules).Should().Contain("Protected replay callers: ReadAsync"); }
        finally { replay.Body.Instructions.Insert(position, actual); }
        TransportOwnershipOffenders(modules).Should().BeEmpty("all executable ownership controls restore the real compiled graph");
    }
}
