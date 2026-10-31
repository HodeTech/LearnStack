using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Census = LearnStack.Tests.Architecture.TrustedOperationSourceConventionTests.Census;

namespace LearnStack.Tests.Architecture;

public sealed partial class OperationPolicyOwnershipTests
{
    private readonly record struct OperationCallerSite(MethodDefinition Method, Instruction Instruction);

    private static IEnumerable<OperationCallerSite> OperationCallerSites(Census census) =>
        census.Methods.SelectMany(method => method.Body.Instructions
            .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj
                && instruction.Operand is MethodReference)
            .Select(instruction => new OperationCallerSite(method, instruction)));

    private static ILookup<(string Type, string Name), OperationCallerSite> OperationCallerIndex(Census census) =>
        OperationCallerSites(census).ToLookup(site =>
            (((MethodReference)site.Instruction.Operand).DeclaringType.FullName, ((MethodReference)site.Instruction.Operand).Name));

    // Keep these controls inside the existing operation-policy fact: its complete
    // production detector and all original planted mutations still run.
    private static void AssertOperationCallerIndex(Census census)
    {
        var seenTargets = new HashSet<(string Type, string Name)> { ("Absent", "_absentOperationCaller") };
        AssertIndex();
        var module = census.Type(Coordinator).Module;
        var probe = new TypeDefinition("OperationControl", "CallerIndexProbe", TypeAttributes.NotPublic, module.TypeSystem.Object);
        var method = new MethodDefinition("Run", MethodAttributes.Private | MethodAttributes.Static, module.TypeSystem.Void);
        var target = module.ImportReference(census.Method(Witness, "TryBindTo"));
        var call = Instruction.Create(OpCodes.Callvirt, target);
        var duplicate = Instruction.Create(OpCodes.Callvirt, target);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Nop));
        method.Body.Instructions.Add(call);
        method.Body.Instructions.Add(duplicate);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        probe.Methods.Add(method);
        var empty = new MethodDefinition("NoMethodOperands", MethodAttributes.Private, module.TypeSystem.Void);
        empty.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        probe.Methods.Add(empty);
        module.Types.Add(probe);
        try
        {
            AssertIndex();
            Change(() => Replace(call, OpCodes.Call, call.Operand));
            Change(() => Replace(call, OpCodes.Ldvirtftn, call.Operand));
            Change(() => Replace(call, OpCodes.Nop, null));
            Change(() => Replace(call, OpCodes.Call,
                new MethodReference("_changedOperationTarget", module.TypeSystem.Void, probe)));
            Change(() =>
            {
                var name = target.Name;
                target.Name = "_renamedOperationTarget";
                return () => target.Name = name;
            });
            Change(() => Replace(call, OpCodes.Newobj, call.Operand));
            Change(() =>
            {
                var declaring = target.DeclaringType;
                target.DeclaringType = new TypeReference("Changed", "Target", module,
                    new AssemblyNameReference("Different.Operation.Scope", new Version(1, 0)));
                return () => target.DeclaringType = declaring;
            });
            Change(() =>
            {
                var parameter = new ParameterDefinition(module.TypeSystem.Int32);
                target.Parameters.Add(parameter);
                return () => target.Parameters.Remove(parameter);
            });
            Change(() =>
            {
                method.Body.Instructions.Remove(duplicate);
                method.Body.Instructions.Insert(0, duplicate);
                return () => { method.Body.Instructions.Remove(duplicate); method.Body.Instructions.Insert(2, duplicate); };
            });
            Change(() =>
            {
                method.Body.Instructions.Remove(duplicate);
                return () => method.Body.Instructions.Insert(2, duplicate);
            });
            Change(() =>
            {
                var name = method.Name;
                method.Name = "RenamedRun";
                return () => method.Name = name;
            });
            Change(() =>
            {
                var parameter = new ParameterDefinition(module.TypeSystem.Int32);
                method.Parameters.Add(parameter);
                return () => method.Parameters.Remove(parameter);
            });
            Change(() =>
            {
                probe.Methods.Remove(method);
                return () => probe.Methods.Insert(0, method);
            });
            Change(() =>
            {
                var position = module.Types.IndexOf(probe);
                module.Types.Remove(probe);
                return () => module.Types.Insert(position, probe);
            });
            Change(() =>
            {
                var generated = new TypeDefinition(string.Empty, "<Run>d__999", TypeAttributes.NestedPrivate, module.TypeSystem.Object);
                var body = new MethodDefinition("MoveNext", MethodAttributes.Private, module.TypeSystem.Void);
                body.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, target));
                body.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                generated.Methods.Add(body);
                probe.NestedTypes.Add(generated);
                var attribute = new CustomAttribute(module.ImportReference(
                    typeof(System.Runtime.CompilerServices.AsyncStateMachineAttribute).GetConstructor([typeof(Type)])!));
                attribute.ConstructorArguments.Add(new CustomAttributeArgument(module.ImportReference(typeof(Type)), generated));
                method.CustomAttributes.Add(attribute);
                return () => { method.CustomAttributes.Remove(attribute); probe.NestedTypes.Remove(generated); };
            });
            var declaringType = target.DeclaringType;
            try
            {
                target.DeclaringType = null!;
                Action indexed = () => _ = OperationCallerIndex(census);
                Action raw = () => _ = census.Methods.SelectMany(Calls).Select(callTarget => callTarget.DeclaringType.FullName).ToArray();
                indexed.Should().Throw<NullReferenceException>("a detached admitted call must not disappear behind the index key");
                raw.Should().Throw<NullReferenceException>("the original full caller traversal fails closed on the same detached target");
            }
            finally { target.DeclaringType = declaringType; }
            AssertIndex();
        }
        finally { module.Types.Remove(probe); }
        AssertIndex();

        void Change(Func<Action> mutate)
        {
            AssertIndex();
            var restore = mutate();
            try { AssertIndex(); }
            finally { restore(); }
            AssertIndex();
        }

        void AssertIndex()
        {
            var index = OperationCallerIndex(census);
            // Independently traverse the current type/member/body collections;
            // no result from the indexed enumeration supplies this oracle.
            var raw = new List<OperationCallerSite>();
            foreach (var type in census.Types)
                foreach (var body in type.Methods)
                    if (body.HasBody)
                        foreach (var instruction in body.Body.Instructions)
                            if (instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj
                                && instruction.Operand is MethodReference) raw.Add(new(body, instruction));
            raw.Should().NotBeEmpty();
            var expected = raw.ToLookup(site =>
                (((MethodReference)site.Instruction.Operand).DeclaringType.FullName, ((MethodReference)site.Instruction.Operand).Name));
            seenTargets.UnionWith(expected.Select(group => group.Key));
            index.Sum(group => group.Count()).Should().Be(raw.Count,
                "the fresh prefilter retains every actual call/callvirt/newobj operand and duplicate occurrence");
            foreach (var targetKey in seenTargets)
                index[targetKey].SequenceEqual(expected[targetKey]).Should().BeTrue(
                    "each current body and instruction reference retains its exact order and multiplicity after metadata mutation: {0}", targetKey);
        }
    }
}
