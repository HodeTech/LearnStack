using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private static ILookup<string, (string Owner, MethodReference Target)> RuntimeCallSitesByName(
        IEnumerable<(string Owner, MethodReference Target)> sites) =>
        sites.ToLookup(site => site.Target.Name, StringComparer.Ordinal);

    private static void AssertRuntimeCallSiteIndex(ProductionCensus census)
    {
        using (census.BeginDetectorScan())
        {
            var indexed = RuntimeCallSitesByName(CallSites(census));
            var raw = EnumerateCallSites(census).ToArray();
            raw.Should().NotBeEmpty("the control enumerates the complete actual current production call population");
            AssertSites(indexed, raw, ["_absentRuntimeCall"]);
        }

        using var module = ModuleDefinition.CreateModule("RuntimeCallSiteControl", ModuleKind.Dll);
        var type = new TypeDefinition("RuntimeControl", "Owner", TypeAttributes.NotPublic, module.TypeSystem.Object);
        module.Types.Add(type);
        var method = new MethodDefinition("Run", MethodAttributes.Private, module.TypeSystem.Void);
        type.Methods.Add(method);
        var target = new MethodReference("Target", module.TypeSystem.Void, type);
        var call = Instruction.Create(OpCodes.Call, target);
        var alias = Instruction.Create(OpCodes.Ldftn, target);
        var foreign = new MethodReference("Target", module.TypeSystem.Void,
            new TypeReference(type.Namespace, type.Name, module, new AssemblyNameReference("Foreign.Scope", new Version(1, 0))));
        method.Body.Instructions.Add(call);
        method.Body.Instructions.Add(alias);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, foreign));
        AssertFixture();
        Change(() => ReplaceInstruction(call, OpCodes.Nop, target));
        Change(() => ReplaceInstruction(call, OpCodes.Nop, null!));
        Change(() => ReplaceInstruction(call, OpCodes.Call, new MethodReference("Replacement", module.TypeSystem.Void, type)));
        Change(() => { target.Name = "Changed"; return () => target.Name = "Target"; });
        Change(() =>
        {
            target.DeclaringType = null!;
            return () => target.DeclaringType = type;
        });
        Change(() =>
        {
            var parameter = new ParameterDefinition(module.TypeSystem.Int32);
            target.Parameters.Add(parameter);
            return () => target.Parameters.Remove(parameter);
        });
        Change(() => { method.Name = "RenamedOwner"; return () => method.Name = "Run"; });
        Change(() =>
        {
            method.Body.Instructions.Remove(alias);
            method.Body.Instructions.Insert(0, alias);
            return () => { method.Body.Instructions.Remove(alias); method.Body.Instructions.Insert(1, alias); };
        });
        Change(() => { type.Methods.Remove(method); return () => type.Methods.Add(method); });

        void Change(Func<Action> mutate)
        {
            var undo = mutate();
            try { AssertFixture(); }
            finally { undo(); }
            AssertFixture();
        }

        void AssertFixture()
        {
            var raw = module.GetTypes().SelectMany(current => current.Methods).Where(current => current.HasBody)
                .SelectMany(current => current.Body.Instructions.Where(instruction => instruction.Operand is MethodReference)
                    .Select(instruction => (Owner: MethodId(current), Target: (MethodReference)instruction.Operand))).ToArray();
            var indexed = RuntimeCallSitesByName(raw);
            AssertSites(indexed, raw, ["Target", "Changed", "Replacement", "_absentRuntimeCall"]);
            var original = raw.Where(site => TargetsDeclaredType(site.Target, type) && site.Target.Name == "Target").ToArray();
            var filtered = indexed["Target"].Where(site => TargetsDeclaredType(site.Target, type) && site.Target.Name == "Target").ToArray();
            SameSites(filtered, original);
            var detached = new SortedSet<string>(StringComparer.Ordinal);
            RecordDetachedTargets(detached, raw);
            detached.Count.Should().Be(raw.Any(site => !HasAttachedDeclaringType(site.Target)) ? 1 : 0,
                "the complete detached-target census still sees aliases and repeated targets");
        }

        static void AssertSites(ILookup<string, (string Owner, MethodReference Target)> indexed,
            (string Owner, MethodReference Target)[] raw, string[] extraNames)
        {
            var expected = new Dictionary<string, List<(string Owner, MethodReference Target)>>(StringComparer.Ordinal);
            foreach (var site in raw)
            {
                if (!expected.TryGetValue(site.Target.Name, out var group)) expected.Add(site.Target.Name, group = []);
                group.Add(site);
            }
            indexed.Sum(group => group.Count()).Should().Be(raw.Length);
            foreach (var name in expected.Keys.Concat(extraNames).Distinct(StringComparer.Ordinal))
                SameSites(indexed[name].ToArray(), expected.TryGetValue(name, out var group) ? group.ToArray() : []);
        }

        static void SameSites((string Owner, MethodReference Target)[] actual, (string Owner, MethodReference Target)[] expected)
        {
            actual.Length.Should().Be(expected.Length);
            for (var index = 0; index < actual.Length; index++)
            {
                actual[index].Owner.Should().Be(expected[index].Owner);
                ReferenceEquals(actual[index].Target, expected[index].Target).Should().BeTrue(
                    "the index retains exact target references, order and multiplicity without changing owner/scope/opcode checks");
            }
        }
    }
}
