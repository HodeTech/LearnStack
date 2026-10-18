using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private sealed record DetectorInstructionIndex(
        ILookup<string, DetectorMethodInstruction> Methods, ILookup<string, DetectorFieldInstruction> Fields);

    private readonly record struct DetectorMethodInstruction(MethodDefinition Body, MethodDefinition Owner,
        Instruction Instruction, MethodReference Target)
    {
        public string OwnerId => MethodId(Owner);
    }

    private readonly record struct DetectorFieldInstruction(MethodDefinition Body, MethodDefinition Owner,
        Instruction Instruction, FieldReference Target)
    {
        public string OwnerId => MethodId(Owner);
    }

    // Runs inside the existing planted-control companion, without another
    // catalogue rule or fewer clean/dirty/restored production evaluations.
    private static void AssertDetectorInstructionIndexes(ProductionCensus census, ModuleDefinition module, MethodReference target)
    {
        var seenMethodNames = new HashSet<string>(StringComparer.Ordinal) { "_absentMethod" };
        var seenFieldNames = new HashSet<string>(StringComparer.Ordinal) { "_absentField" };
        AssertIndexes();
        var probe = AddProbe(module, "InstructionIndexProbe");
        try
        {
            var method = AddCall(probe, "Run", target, OpCodes.Call);
            method.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Nop));
            var call = method.Body.Instructions[1];
            var duplicate = Instruction.Create(OpCodes.Ldftn, (MethodReference)call.Operand);
            var field = new FieldDefinition("_indexProbe", FieldAttributes.Private, module.TypeSystem.Object);
            probe.Fields.Add(field);
            var read = Instruction.Create(OpCodes.Ldfld, field);
            var write = Instruction.Create(OpCodes.Stfld, field);
            method.Body.Instructions.Insert(2, duplicate);
            method.Body.Instructions.Insert(3, read);
            method.Body.Instructions.Insert(4, write);
            var empty = new MethodDefinition("NoMethodOperands", MethodAttributes.Private, module.TypeSystem.Void);
            empty.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            probe.Methods.Add(empty);
            AssertIndexes();

            Change(() => ReplaceInstruction(call, OpCodes.Callvirt, call.Operand));
            Change(() => ReplaceInstruction(call, OpCodes.Call, module.ImportReference(typeof(object).GetMethod("ToString")!)));
            var unique = new MethodReference("_uniqueScanTarget", module.TypeSystem.Void, probe);
            Change(() => ReplaceInstruction(call, OpCodes.Call, unique));
            Change(() =>
            {
                method.Body.Instructions.Remove(duplicate);
                method.Body.Instructions.Insert(0, duplicate);
                return () => { method.Body.Instructions.Remove(duplicate); method.Body.Instructions.Insert(2, duplicate); };
            });
            Change(() =>
            {
                method.Body.Instructions.Remove(write);
                return () => method.Body.Instructions.Insert(4, write);
            });
            Change(() =>
            {
                var name = field.Name;
                field.Name = "_renamedIndexProbe";
                return () => field.Name = name;
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
            Change(() => AddAsyncCallProbe(module, "InstructionIndexAsyncProbe", target));
            Change(() =>
            {
                var detached = new MethodDefinition("DetachedIndexTarget", MethodAttributes.Private, module.TypeSystem.Void);
                probe.Methods.Add(detached);
                var undo = ReplaceInstruction(call, OpCodes.Call, detached);
                probe.Methods.Remove(detached);
                return undo;
            });
            Change(() =>
            {
                module.Types.Remove(probe);
                return () => module.Types.Add(probe);
            });

            using (census.BeginDetectorScan())
            {
                var overlap = () => census.BeginDetectorScan();
                overlap.Should().Throw<InvalidOperationException>();
                ReferenceEquals(census.MethodInstructions(target.Name), census.MethodInstructions(target.Name)).Should().BeTrue();
                ReferenceEquals(census.FieldInstructions(field.Name), census.FieldInstructions(field.Name)).Should().BeTrue();
            }
            ReferenceEquals(census.MethodInstructions(target.Name), census.MethodInstructions(target.Name)).Should().BeFalse();
            ReferenceEquals(census.FieldInstructions(field.Name), census.FieldInstructions(field.Name)).Should().BeFalse();
        }
        finally { module.Types.Remove(probe); }
        AssertIndexes();

        void Change(Func<Action> mutate)
        {
            AssertIndexes();
            var undo = mutate();
            try { AssertIndexes(); }
            finally { undo(); }
            AssertIndexes();
        }

        void AssertIndexes()
        {
            using var scan = census.BeginDetectorScan();
            // Independently enumerate the actual current body collections. Never
            // compare two reads of the same index or normalize away duplicates.
            var methods = new List<DetectorMethodInstruction>();
            var fields = new List<DetectorFieldInstruction>();
            var expectedBodies = new Dictionary<MethodDefinition, List<MethodDefinition>>(ReferenceEqualityComparer.Instance);
            var ownerOrder = new List<MethodDefinition>();
            foreach (var (body, owner) in EnumerateOwnedBodies(census))
            {
                if (!expectedBodies.TryGetValue(owner, out var group))
                {
                    group = [];
                    expectedBodies.Add(owner, group);
                    ownerOrder.Add(owner);
                }
                group.Add(body);
                foreach (var instruction in body.Body.Instructions)
                {
                    if (instruction.Operand is MethodReference method) methods.Add(new(body, owner, instruction, method));
                    if (instruction.Operand is FieldReference field) fields.Add(new(body, owner, instruction, field));
                }
            }
            methods.Should().NotBeEmpty();
            fields.Should().NotBeEmpty();
            var indexedBodies = ObservationBodiesByOwner(OwnedBodies(census));
            indexedBodies.Select(group => group.Key).Should().Equal(ownerOrder,
                "observation roots must retain the independently enumerated actual owner order");
            foreach (var owner in ownerOrder)
                indexedBodies[owner].SequenceEqual(expectedBodies[owner], ReferenceEqualityComparer.Instance).Should().BeTrue(
                    "ordinary and attribute-linked bodies must preserve exact references, order and multiplicity after every metadata mutation");
            expectedBodies.Values.Should().Contain(group => group.Count > 1,
                "the real production population must exercise a method and its linked generated body");
            var absentOwner = new MethodDefinition("_absentObservationOwner", MethodAttributes.Private, module.TypeSystem.Void);
            indexedBodies[absentOwner].Should().BeEmpty("an owner absent from the actual current body population cannot acquire another method's body");
            var methodsByName = methods.ToLookup(item => item.Target.Name, StringComparer.Ordinal);
            seenMethodNames.UnionWith(methodsByName.Select(group => group.Key));
            foreach (var name in seenMethodNames)
                census.MethodInstructions(name).SequenceEqual(methodsByName[name]).Should().BeTrue(
                    "the index must preserve actual body/owner/target/instruction order and multiplicity for {0}", name);
            foreach (var (type, name) in new[]
                     {
                         (JobRuntimeGate, ".ctor"), (JobGeneration, ".ctor"), (JobRuntimeGate, "ForPool"),
                         (JobFatalBoundary, "Terminate"), (JobRuntimeGate, "Terminate"),
                         (JobRuntimeGate, "FailFast"), (JobRuntimeGate, "EnsureFinalExitRegistered"),
                         (JobRuntimeGate, "OnFinalExit"), (JobRuntimeGate, "BeginFinalExit"),
                         (JobFatalBoundary, ".ctor"), (JobRoutingStore, "TryClaimOwnedAsync"),
                         (JobClosedActivator, ".ctor"), (JobBoundInvocation, ".ctor"),
                         (JobClosedActivator, "Bind"), (JobInvocationContract, "BindGeneration"),
                     })
            {
                var raw = methodsByName[name]
                    .Where(item => item.Target.DeclaringType.FullName == type && item.Target.Name == name)
                    .Select(item => MethodId(item.Owner) + ":" + item.Instruction.OpCode.Name + ":" + MethodId(item.Target)).ToArray();
                var indexed = census.MethodInstructions(name)
                    .Where(item => item.Target.DeclaringType.FullName == type && item.Target.Name == name)
                    .Select(item => MethodId(item.Owner) + ":" + item.Instruction.OpCode.Name + ":" + MethodId(item.Target)).ToArray();
                indexed.Should().Equal(raw,
                    "selected Hangfire capability diagnostics must retain independent raw order and multiplicity");
            }
            var fieldsByName = fields.ToLookup(item => item.Target.Name, StringComparer.Ordinal);
            seenFieldNames.UnionWith(fieldsByName.Select(group => group.Key));
            foreach (var name in seenFieldNames)
                census.FieldInstructions(name).SequenceEqual(fieldsByName[name]).Should().BeTrue(
                    "the index must preserve actual field accesses, owners and duplicate opcodes for {0}", name);

            // Record equality covers references/order, not computed properties.
            // Independently check formatted owners across same-object renames,
            // signature edits and the real attribute-linked async control.
            foreach (var expected in methods.Where(item => item.Owner.DeclaringType.Namespace == "ReviewProbe"))
                census.MethodInstructions(expected.Target.Name).Single(item => ReferenceEquals(item.Instruction, expected.Instruction))
                    .OwnerId.Should().Be(MethodId(expected.Owner));
            foreach (var expected in fields.Where(item => item.Owner.DeclaringType.Namespace == "ReviewProbe"))
                census.FieldInstructions(expected.Target.Name).Single(item => ReferenceEquals(item.Instruction, expected.Instruction))
                    .OwnerId.Should().Be(MethodId(expected.Owner));

            var sites = CallSites(census).ToArray();
            var ownedSites = OwnedCallSites(census).ToArray();
            var ownerIds = new Dictionary<MethodDefinition, string>(ReferenceEqualityComparer.Instance);
            ownedSites.Length.Should().Be(sites.Length, "typed call sites must retain every duplicate operand");
            ownedSites.Zip(sites).All(pair => ReferenceEquals(pair.First.Target, pair.Second.Target)
                    && OwnerId(pair.First.Owner) == pair.Second.Owner).Should().BeTrue(
                "the independent legacy traversal must retain the same actual target references, owner identities and order after each mutation");
            var legacyDetached = new SortedSet<string>(StringComparer.Ordinal);
            var typedDetached = new SortedSet<string>(StringComparer.Ordinal);
            RecordDetachedTargets(legacyDetached, sites);
            RecordDetachedTargets(typedDetached, ownedSites);
            typedDetached.Should().Equal(legacyDetached, "detached targets must preserve exact offender diagnostics");
            foreach (var detached in ownedSites.Where(site => site.Target.Name == "DetachedIndexTarget"))
                typedDetached.Should().Contain(OwnerId(detached.Owner) + ": detached production method target DetachedIndexTarget");
            foreach (var owner in census.Types.Values.Where(type => type.Namespace == "ReviewProbe").SelectMany(type => type.Methods))
            {
                // These planted owners have either their ordinary body or one
                // explicitly linked state machine; generated names alone confer
                // no ownership. The empty/leading-NOP bodies must stay harmless.
                var generated = owner.CustomAttributes.SingleOrDefault(attribute =>
                    attribute.AttributeType.FullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute")
                    ?.ConstructorArguments[0].Value as TypeDefinition;
                var body = generated?.Methods.Single(method => method.Name == "MoveNext") ?? owner;
                var expected = body.HasBody ? body.Body.Instructions.Select(instruction => instruction.Operand)
                    .OfType<MethodReference>().ToArray() : [];
                var ownerId = MethodId(owner);
                sites.Where(site => site.Owner == ownerId).Select(site => site.Target)
                    .Should().Equal(expected, "ordinary and linked async owners retain every actual call operand in order");
            }

            string OwnerId(MethodDefinition owner)
            {
                if (ownerIds.TryGetValue(owner, out var identity)) return identity;
                identity = MethodId(owner);
                ownerIds.Add(owner, identity);
                return identity;
            }
        }
    }
}
