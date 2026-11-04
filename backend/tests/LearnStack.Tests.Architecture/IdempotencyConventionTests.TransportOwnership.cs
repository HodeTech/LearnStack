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
                .Select(instruction => (Owner: Symbol(type).Assembly + ":" + type.FullName + "." + pair.DeclaredAs, Instruction: instruction, Target: (MethodReference)instruction.Operand)))).ToArray();
        foreach (var filter in new[] { GenericFilter, ProtectedFilter })
        {
            var expectedFactory = ExpectedFilterFactory(types, filter);
            var factories = sites.Where(site => site.Target is GenericInstanceMethod generic
                && generic.GenericArguments.Any(argument => argument.FullName == filter)).ToArray();
            if (factories.Length != 1 || factories[0].Owner != "LearnStack.Api:" + KeyAttribute + ".CreateInstance"
                || factories[0].Instruction.OpCode != OpCodes.Call
                || !ExactTransportMember(factories[0].Target, expectedFactory))
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
            var expectedMember = types.Single(type => Symbol(type) == ("LearnStack.Api", Dispatcher)).Methods.Single(method => method.Name == member);
            if (calls.Length != 1 || calls[0].Owner != "LearnStack.Api:" + ProtectedFilter + ".ReplayAsync"
                || !ExactTransportMember(calls[0].Target, expectedMember)
                || (member == ".ctor" ? calls[0].Instruction.OpCode != OpCodes.Newobj : calls[0].Instruction.OpCode.Code is not (Code.Call or Code.Callvirt)))
                yield return "Protected replay callers: " + member;
        }
        var cleanupTypes = types.Where(type => type.Interfaces.Any(contract => contract.InterfaceType.FullName == CleanupPort))
            .Select(type => type.FullName).Append(CleanupPort).ToHashSet(StringComparer.Ordinal);
        var cleanup = sites.Where(site => cleanupTypes.Contains(site.Target.DeclaringType.FullName) && site.Target.Name == "EndBusinessOwnerAsync").ToArray();
        var cleanupMember = types.Single(type => Symbol(type) == ("LearnStack.SharedKernel", CleanupPort)).Methods.Single(method => method.Name == "EndBusinessOwnerAsync");
        var expected = new[] { "LearnStack.Api:" + GenericFilter + ".EndBusinessOwnerAsync", "LearnStack.Api:" + ProtectedFilter + ".EndBusinessOwnerAsync" };
        if (!cleanup.Select(site => site.Owner).Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal)
            || cleanup.Any(site => !ExactTransportMember(site.Target, cleanupMember) || site.Instruction.OpCode != OpCodes.Callvirt))
            yield return "Idempotency business cleanup callers";
    }

    private static GenericInstanceMethod ExpectedFilterFactory(TypeDefinition[] types, string filter)
    {
        var owner = types.Single(type => Symbol(type) == ("LearnStack.Api", filter));
        // Read the real framework member metadata rather than importing runtime
        // reflection parameter types, which can replace reference-assembly scopes
        // with System.Private.CoreLib and invent a false signature mismatch.
        using var framework = ModuleDefinition.ReadModule(typeof(Microsoft.Extensions.DependencyInjection.ActivatorUtilities).Assembly.Location);
        var factory = framework.GetType("Microsoft.Extensions.DependencyInjection.ActivatorUtilities").Methods
            .Single(method => method.Name == "CreateInstance" && method.GenericParameters.Count == 1
                && method.Parameters.Count == 2 && method.Parameters[0].ParameterType.FullName == "System.IServiceProvider"
                && method.Parameters[1].ParameterType.FullName == "System.Object[]");
        var expected = new GenericInstanceMethod(owner.Module.ImportReference(factory));
        expected.GenericArguments.Add(owner);
        return expected;
    }

    private static bool ExactTransportMember(MethodReference actual, MethodReference expected)
    {
        if (actual is GenericInstanceMethod || expected is GenericInstanceMethod)
        {
            if (actual is not GenericInstanceMethod left || expected is not GenericInstanceMethod right
                || !left.GenericArguments.Select(TransportTypeShape).SequenceEqual(right.GenericArguments.Select(TransportTypeShape), StringComparer.Ordinal)) return false;
            actual = left.ElementMethod; expected = right.ElementMethod;
        }
        return Symbol(actual.DeclaringType) == Symbol(expected.DeclaringType) && actual.Name == expected.Name
            && actual.HasThis == expected.HasThis && actual.ExplicitThis == expected.ExplicitThis
            && actual.CallingConvention == expected.CallingConvention && actual.GenericParameters.Count == expected.GenericParameters.Count
            && TransportTypeShape(actual.ReturnType) == TransportTypeShape(expected.ReturnType)
            && actual.Parameters.Select(parameter => TransportTypeShape(parameter.ParameterType))
                .SequenceEqual(expected.Parameters.Select(parameter => TransportTypeShape(parameter.ParameterType)), StringComparer.Ordinal);
    }

    private static string TransportTypeShape(TypeReference type) => type switch
    {
        GenericParameter parameter => (parameter.Type == GenericParameterType.Method ? "!!" : "!") + parameter.Position,
        GenericInstanceType generic => Symbol(generic.ElementType) + "<" + string.Join(",", generic.GenericArguments.Select(TransportTypeShape)) + ">",
        ArrayType array => "array:" + array.Rank + ":" + array.IsVector + ":" + TransportTypeShape(array.ElementType),
        OptionalModifierType modifier => "optional:" + TransportTypeShape(modifier.ModifierType) + ":" + TransportTypeShape(modifier.ElementType),
        RequiredModifierType modifier => "required:" + TransportTypeShape(modifier.ModifierType) + ":" + TransportTypeShape(modifier.ElementType),
        TypeSpecification specification => type.GetType().Name + ":" + TransportTypeShape(specification.ElementType),
        _ => Symbol(type).Assembly + ":" + Symbol(type).Name,
    };

    private static void TransportSignatureControls(ModuleDefinition[] modules)
    {
        var types = Types(modules).ToArray();
        var factory = types.Single(type => Symbol(type) == ("LearnStack.Api", KeyAttribute)).Methods.Single(method => method.Name == "CreateInstance");
        var targets = new List<(Instruction Instruction, string Finding)>();
        targets.AddRange(factory.Body.Instructions.Where(instruction => instruction.Operand is GenericInstanceMethod generic
            && generic.Name == "CreateInstance").Select(instruction => (instruction,
                "Idempotency filter factory callers: " + ((GenericInstanceMethod)instruction.Operand).GenericArguments.Single().FullName)));
        foreach (var type in types.Where(type => Symbol(type).Assembly == "LearnStack.Api" && type.FullName is GenericFilter or ProtectedFilter))
            foreach (var pair in Il.Methods(type).Where(pair => pair.Definition.HasBody))
                foreach (var instruction in pair.Definition.Body.Instructions)
                    if (instruction.Operand is MethodReference target)
                    {
                        if (target.DeclaringType.FullName == Dispatcher && target.Name is ".ctor" or "ReadAsync")
                            targets.Add((instruction, "Protected replay callers: " + target.Name));
                        if (target.DeclaringType.FullName == CleanupPort && target.Name == "EndBusinessOwnerAsync")
                            targets.Add((instruction, "Idempotency business cleanup callers"));
                    }
        targets.Should().HaveCount(6, "two actual framework factories, two replay members and two actual cleanup calls are independently mutated");
        foreach (var (instruction, finding) in targets)
            foreach (var mutation in new[] { "assembly", "return", "parameter", "instance" })
            {
                var original = (MethodReference)instruction.Operand;
                var definition = original is GenericInstanceMethod generic ? generic.ElementMethod : original;
                var declaring = mutation == "assembly" ? new TypeReference(definition.DeclaringType.Namespace, definition.DeclaringType.Name,
                    definition.Module, new AssemblyNameReference("TransportControl.ForeignOwner", new Version(1, 0))) : definition.DeclaringType;
                var replacement = new MethodReference(definition.Name,
                    mutation == "return" ? definition.Module.TypeSystem.Object : definition.ReturnType, declaring)
                {
                    HasThis = mutation == "instance" ? !definition.HasThis : definition.HasThis,
                    ExplicitThis = definition.ExplicitThis,
                    CallingConvention = definition.CallingConvention,
                };
                foreach (var parameter in definition.GenericParameters) replacement.GenericParameters.Add(new GenericParameter(parameter.Name, replacement));
                foreach (var parameter in definition.Parameters) replacement.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
                if (mutation == "parameter") replacement.Parameters.Add(new ParameterDefinition(definition.Module.TypeSystem.Object));
                MethodReference changed = replacement;
                if (original is GenericInstanceMethod instance)
                {
                    var copied = new GenericInstanceMethod(replacement);
                    foreach (var argument in instance.GenericArguments) copied.GenericArguments.Add(argument);
                    changed = copied;
                }
                instruction.Operand = changed;
                try { TransportOwnershipOffenders(modules).Should().Contain(finding, mutation + " changed the actual transport member operand"); }
                finally { instruction.Operand = original; }
                TransportOwnershipOffenders(modules).Should().BeEmpty("restoring the actual operand restores the live detector");
            }
    }

    private static void TransportOwnershipDirtyControls(ModuleDefinition[] modules)
    {
        var types = Types(modules).ToArray();
        var unrelated = Body(types, Dispatcher, "ReadAsync");
        TransportOwnershipOffenders(modules).Should().BeEmpty();
        TransportSignatureControls(modules);
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
