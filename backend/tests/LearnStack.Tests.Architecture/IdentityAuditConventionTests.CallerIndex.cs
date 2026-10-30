using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using EvidenceCensus = LearnStack.Tests.Architecture.TrustedOperationSourceConventionTests.Census;

namespace LearnStack.Tests.Architecture;

public sealed partial class IdentityAuditConventionTests
{
    private readonly record struct EvidenceCallerSite(MethodDefinition Method, Instruction Instruction);

    private static IEnumerable<EvidenceCallerSite> EvidenceCallerSites(EvidenceCensus census) =>
        census.Methods.SelectMany(method => method.Body.Instructions
            .Where(instruction => instruction.Operand is MethodReference)
            .Select(instruction => new EvidenceCallerSite(method, instruction)));

    private static ILookup<string, EvidenceCallerSite> EvidenceCallerIndex(EvidenceCensus census) =>
        EvidenceCallerSites(census).ToLookup(site => ((MethodReference)site.Instruction.Operand).Name, StringComparer.Ordinal);

    // Keep these controls inside the existing provenance fact: its complete
    // production detector and all original planted mutations still run.
    private static void AssertEvidenceCallerIndex(EvidenceCensus census)
    {
        var seenNames = new HashSet<string>(StringComparer.Ordinal) { "_absentEvidenceCaller" };
        AssertIndex();
        var module = census.Type(CentralEvidence).Module;
        var probe = new TypeDefinition("EvidenceControl", "CallerIndexProbe", TypeAttributes.NotPublic, module.TypeSystem.Object);
        var method = new MethodDefinition("Run", MethodAttributes.Private | MethodAttributes.Static, module.TypeSystem.Void);
        var target = module.ImportReference(census.Method(InvocationContext, "get_CurrentInvocation"));
        var call = Instruction.Create(OpCodes.Callvirt, target);
        var duplicate = Instruction.Create(OpCodes.Ldftn, target);
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
            Change(() => EvidenceReplace(call, OpCodes.Call, call.Operand));
            Change(() => EvidenceReplace(call, OpCodes.Ldvirtftn, call.Operand));
            Change(() => EvidenceReplace(call, OpCodes.Nop, null));
            Change(() => EvidenceReplace(call, OpCodes.Call,
                new MethodReference("_changedEvidenceTarget", module.TypeSystem.Void, probe)));
            Change(() =>
            {
                var name = target.Name;
                target.Name = "_renamedEvidenceTarget";
                return () => target.Name = name;
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
            var index = EvidenceCallerIndex(census);
            // Independently traverse the current type/member/body collections;
            // no result from the indexed enumeration supplies this oracle.
            var raw = new List<EvidenceCallerSite>();
            foreach (var type in census.Types)
                foreach (var body in type.Methods)
                    if (body.HasBody)
                        foreach (var instruction in body.Body.Instructions)
                            if (instruction.Operand is MethodReference) raw.Add(new(body, instruction));
            raw.Should().NotBeEmpty();
            var expected = raw.ToLookup(site => ((MethodReference)site.Instruction.Operand).Name, StringComparer.Ordinal);
            seenNames.UnionWith(expected.Select(group => group.Key));
            index.Sum(group => group.Count()).Should().Be(raw.Count,
                "the fresh prefilter retains every actual method operand, including aliases and duplicate occurrences");
            foreach (var name in seenNames)
                index[name].SequenceEqual(expected[name]).Should().BeTrue(
                    "each current body and instruction reference retains its exact order and multiplicity after metadata mutation: {0}", name);
        }
    }
}
