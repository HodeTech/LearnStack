using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private sealed class LifecycleCapabilityMemo(Dictionary<(string Assembly, string Type), TypeDefinition> types)
    {
        private readonly Dictionary<MethodReference, string?> _capabilities = new(ReferenceEqualityComparer.Instance);
        internal int Count => _capabilities.Count;

        internal string? Classify(MethodReference target)
        {
            if (_capabilities.TryGetValue(target, out var capability)) return capability;
            // No candidate exclusion or alternate classifier: the first hit
            // executes the complete existing rule, including invalid signatures.
            capability = IntegrationLifecycleCapability(target, types);
            _capabilities.Add(target, capability);
            return capability;
        }
    }

    private static void AssertLifecycleCapabilityMemo(ProductionCensus census)
    {
        // Compare every actual production occurrence, preserving repeated
        // references and null results, before introducing bounded controls.
        var productionTargets = census.Modules.SelectMany(module => module.GetTypes())
            .SelectMany(type => type.Methods).Where(method => method.HasBody)
            .SelectMany(method => method.Body.Instructions).Select(instruction => instruction.Operand)
            .OfType<MethodReference>().ToArray();
        var production = AssertEquivalent(productionTargets);
        production.Should().Contain((string?)null);
        production.Should().Contain(value => value == "unit.commit");

        var application = census.Modules.Single(module => module.Assembly.Name.Name == DeliveryApplication);
        var types = census.Types;
        var unit = application.ImportReference(types[(KernelAssembly, LifecycleUnit)]);
        var scope = application.ImportReference(types[(KernelAssembly, LifecycleScope)]);
        var parent = AddProbe(application, "LifecycleMemoParent");
        var child = AddProbe(application, "LifecycleMemoChild");
        var caller = AddProbe(application, "LifecycleMemoCaller");
        var inherited = new InterfaceImplementation(unit);
        parent.Interfaces.Add(inherited);
        child.BaseType = parent;
        var target = new MethodReference("CommitAsync", application.ImportReference(typeof(Task)), child) { HasThis = true };
        var cancellation = new ParameterDefinition(application.ImportReference(typeof(CancellationToken)));
        target.Parameters.Add(cancellation);
        var complete = new MethodReference("CompleteAsync", target.ReturnType, child) { HasThis = true };
        complete.Parameters.Add(new ParameterDefinition(cancellation.ParameterType));
        var wrongScope = new TypeReference(child.Namespace, child.Name, application,
            new AssemblyNameReference("_OtherLifecycleMemoAssembly", new Version(1, 0, 0, 0)));
        var sameName = new MethodReference(target.Name, target.ReturnType, wrongScope) { HasThis = true };
        sameName.Parameters.Add(new ParameterDefinition(cancellation.ParameterType));
        var body = AddCall(caller, "Run", target, OpCodes.Callvirt);
        var call = body.Body.Instructions[0];
        call.Operand = target; // Mutate the actual operand identity, without relying on import reuse.
        body.Body.Instructions.Insert(1, Instruction.Create(OpCodes.Ldftn, target));
        body.Body.Instructions.Insert(2, Instruction.Create(OpCodes.Callvirt, sameName));
        body.Body.Instructions.Insert(3, Instruction.Create(OpCodes.Callvirt, complete));
        try
        {
            target.FullName.Should().Be(sameName.FullName, "assembly scope must distinguish otherwise identical method names");
            AssertCurrent("unit.commit");
            Change(() => ReplaceInstruction(call, OpCodes.Ldvirtftn, call.Operand), "unit.commit");
            Change(() => ReplaceInstruction(call, OpCodes.Callvirt, sameName), null);
            Change(() =>
            {
                var name = target.Name;
                target.Name = "_UnrelatedLifecycleMethod";
                return () => target.Name = name;
            }, null);
            Change(() =>
            {
                var name = target.Name;
                target.Name = LifecycleUnit + ".CommitAsync";
                return () => target.Name = name;
            }, "unit.commit");
            Change(() =>
            {
                var result = target.ReturnType;
                target.ReturnType = application.TypeSystem.Void;
                return () => target.ReturnType = result;
            }, "invalid-unit");
            Change(() =>
            {
                target.Parameters.Remove(cancellation);
                return () => target.Parameters.Add(cancellation);
            }, "invalid-unit");
            Change(() =>
            {
                var declaring = target.DeclaringType;
                target.DeclaringType = application.TypeSystem.Object;
                return () => target.DeclaringType = declaring;
            }, null);
            Change(() =>
            {
                var original = wrongScope.Scope;
                wrongScope.Scope = application;
                return () => wrongScope.Scope = original;
            }, "unit.commit");
            Change(() =>
            {
                parent.Interfaces.Remove(inherited);
                return () => parent.Interfaces.Add(inherited);
            }, null);
            Change(() =>
            {
                var original = child.BaseType;
                child.BaseType = application.TypeSystem.Object;
                return () => child.BaseType = original;
            }, null);
            Change(() =>
            {
                inherited.InterfaceType = scope;
                return () => inherited.InterfaceType = unit;
            }, null, "scope.complete");
        }
        finally
        {
            application.Types.Remove(caller);
            application.Types.Remove(child);
            application.Types.Remove(parent);
        }

        void Change(Func<Action> mutate, string? expected, string? expectedComplete = null)
        {
            AssertCurrent("unit.commit");
            var restore = mutate();
            try { AssertCurrent(expected, expectedComplete); }
            finally { restore(); }
            AssertCurrent("unit.commit");
        }

        void AssertCurrent(string? expected, string? expectedComplete = null)
        {
            // The operands are read anew from actual body instructions after
            // each edit; a saved target list cannot conceal operand replacement.
            var current = body.Body.Instructions.Select(instruction => instruction.Operand).OfType<MethodReference>().ToArray();
            var actual = AssertEquivalent(current);
            actual[0].Should().Be(expected);
            actual[3].Should().Be(expectedComplete);
        }

        string?[] AssertEquivalent(MethodReference[] targets)
        {
            var currentTypes = census.Types;
            var expected = targets.Select(target => IntegrationLifecycleCapability(target, currentTypes)).ToArray();
            var memo = new LifecycleCapabilityMemo(currentTypes);
            var actual = targets.Select(memo.Classify).ToArray();
            actual.Should().Equal(expected, "memoization retains the complete raw classification for every current operand occurrence");
            targets.Reverse().Select(memo.Classify).Should().Equal(expected.Reverse(),
                "a repeated null or non-null result remains exact in either encounter order");
            memo.Count.Should().Be(new HashSet<MethodReference>(targets, ReferenceEqualityComparer.Instance).Count,
                "each actual reference, including null-classified targets, occupies exactly one evaluation-local entry");
            return actual;
        }
    }
}
