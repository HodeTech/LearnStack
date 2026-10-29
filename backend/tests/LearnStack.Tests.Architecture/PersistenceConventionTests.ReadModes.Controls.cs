using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

public sealed partial class PersistenceConventionTests
{
    private static void AssertReadOnlyProducerControls(MethodDefinition owner, ModuleDefinition[] modules)
    {
        var declarations = owner.Body.Instructions.Where(instruction => instruction.Operand is MethodReference target
            && target.DeclaringType.FullName == ReadOnlyDeclaration && target.Name == ".ctor").ToArray();
        declarations.Should().HaveCount(ReadOnlyProducers.Count(producer => producer.Owner == ReadOnlyOwner(owner)));
        foreach (var declared in declarations)
        {
            var literal = owner.Body.Instructions[owner.Body.Instructions.IndexOf(declared) - 2];
            var originalType = (TypeReference)literal.Operand;
            var replacements = ReadOnlyProducers.Select(producer => modules.Single(module => module.Assembly.Name.Name == producer.Assembly)
                    .GetType(producer.Request)).Cast<TypeReference>()
                .Concat(new[] { owner.Module.ImportReference(typeof(string)),
                    owner.Module.ImportReference(typeof(LearnStack.Modules.Tenancy.Application.Contracts.Tenant.ListOrganizationsQuery)),
                    owner.Module.ImportReference(typeof(LearnStack.Modules.Tenancy.Application.Contracts.Tenant.GetOrganizationDetailQuery)) });
            foreach (var replacement in replacements.Where(type => type.FullName != originalType.FullName))
            {
                literal.Operand = owner.Module.ImportReference(replacement);
                try { AssertDirty(); }
                finally { literal.Operand = originalType; }
            }
            literal.Operand = new TypeReference(originalType.Namespace, originalType.Name, owner.Module,
                owner.Module.TypeSystem.String.Scope);
            try { AssertDirty(); }
            finally { literal.Operand = originalType; }
            foreach (var instruction in new[] { literal, literal.Next, declared })
            {
                var opcode = instruction.OpCode;
                instruction.OpCode = OpCodes.Nop;
                try { AssertDirty(); }
                finally { instruction.OpCode = opcode; }
            }
            var index = owner.Body.Instructions.IndexOf(declared);
            owner.Body.Instructions.Remove(declared);
            try { AssertDirty(); }
            finally { owner.Body.Instructions.Insert(index, declared); }
            foreach (var opcode in new[] { OpCodes.Newobj, OpCodes.Call, OpCodes.Ldftn })
            {
                var duplicate = Instruction.Create(opcode, (MethodReference)declared.Operand);
                owner.Body.Instructions.Add(duplicate);
                try { AssertDirty(); }
                finally { owner.Body.Instructions.Remove(duplicate); }
            }
        }
        ExplicitReadOnlyModeOffenders(modules).Should().BeEmpty("restoring every exact declaration restores the clean census");

        void AssertDirty() => ExplicitReadOnlyModeOffenders(modules).Should().NotBeEmpty(
            "Fix: preserve each exact module, CLR request and assembly tuple; an approved duplicate cannot hide a missing declaration");
    }

    private static void AssertReadOnlyShapeControls(TypeDefinition type, string propertyType, string property, string constructorArgument)
    {
        bool Matches() => ReadOnlyShape(type, propertyType, property, constructorArgument);
        Matches().Should().BeTrue();
        var field = type.Fields[0];
        var declaredProperty = type.Properties[0];
        var fieldAttributes = field.Attributes;
        var fieldType = field.FieldType;
        var propertyName = declaredProperty.Name;
        var declaredType = declaredProperty.PropertyType;
        var getter = declaredProperty.GetMethod;
        var extraField = new FieldDefinition("ExtraState", FieldAttributes.Private | FieldAttributes.InitOnly, fieldType);
        var extraProperty = new PropertyDefinition("ExtraProperty", PropertyAttributes.None, fieldType);
        var extraEvent = new EventDefinition("Changed", EventAttributes.None, type.Module.ImportReference(typeof(Action)));
        var generic = new GenericParameter("TAdditional", type);
        foreach (var mutation in new (Action Apply, Action Undo)[]
        {
            (() => type.IsSealed = false, () => type.IsSealed = true),
            (() => type.Fields.Add(extraField), () => type.Fields.Remove(extraField)),
            (() => type.Fields.Remove(field), () => type.Fields.Add(field)),
            (() => field.IsPublic = true, () => field.Attributes = fieldAttributes),
            (() => field.IsInitOnly = false, () => field.Attributes = fieldAttributes),
            (() => field.IsStatic = true, () => field.Attributes = fieldAttributes),
            (() => field.FieldType = type.Module.TypeSystem.String, () => field.FieldType = fieldType),
            (() => type.Properties.Add(extraProperty), () => type.Properties.Remove(extraProperty)),
            (() => type.Properties.Remove(declaredProperty), () => type.Properties.Add(declaredProperty)),
            (() => declaredProperty.Name = "Selector", () => declaredProperty.Name = propertyName),
            (() => declaredProperty.PropertyType = type.Module.TypeSystem.String, () => declaredProperty.PropertyType = declaredType),
            (() => declaredProperty.SetMethod = getter, () => declaredProperty.SetMethod = null),
            (() => declaredProperty.GetMethod = null, () => declaredProperty.GetMethod = getter),
            (() => type.Events.Add(extraEvent), () => type.Events.Remove(extraEvent)),
            (() => type.GenericParameters.Add(generic), () => type.GenericParameters.Remove(generic)),
        })
        {
            mutation.Apply();
            try { Matches().Should().BeFalse("the same immutable-shape predicate must reject each independent production metadata mutation"); }
            finally { mutation.Undo(); }
            Matches().Should().BeTrue("restoring this production shape restores the same predicate");
        }
    }

    private static void AssertReadOnlySelectionControl(MethodDefinition handle, ModuleDefinition[] modules)
    {
        ReadOnlyModeSelectionMatches(handle).Should().BeTrue();
        var contains = handle.Body.Instructions.Single(instruction => instruction.Operand is MethodReference target
            && target.Name == "Contains" && target.DeclaringType.GetElementType().FullName == "System.Collections.Frozen.FrozenSet`1");
        var readOnly = (Instruction)contains.Next.Operand;
        var resume = readOnly.Next;
        var fallbackBranch = handle.Body.Instructions.Single(instruction => instruction.OpCode.FlowControl == FlowControl.Branch
            && instruction.Operand == resume);
        var genericType = (TypeReference)contains.Previous.Previous.Operand;
        var conversion = (MethodReference)contains.Previous.Operand;
        var planted = new[]
        {
            Instruction.Create(OpCodes.Ldtoken, genericType),
            Instruction.Create(OpCodes.Call, conversion),
            Instruction.Create(OpCodes.Callvirt, handle.Module.ImportReference(typeof(Type).GetProperty(nameof(Type.Name))!.GetMethod!)),
            Instruction.Create(OpCodes.Ldstr, "GetAccountSiteQuery"),
            Instruction.Create(OpCodes.Call, handle.Module.ImportReference(typeof(string).GetMethod("op_Equality", [typeof(string), typeof(string)])!)),
            Instruction.Create(OpCodes.Brfalse, resume),
            Instruction.Create(OpCodes.Pop),
            Instruction.Create(OpCodes.Ldc_I4_1),
        };
        var il = handle.Body.GetILProcessor();
        foreach (var instruction in planted) il.InsertBefore(resume, instruction);
        fallbackBranch.Operand = planted[0];
        try
        {
            ReadOnlyModeSelectionMatches(handle).Should().BeFalse("a naming override cannot replace the already-correct exact-type mode selection");
            ExplicitReadOnlyModeOffenders(modules).Should().Contain(error => error.StartsWith(
                "Transaction mode must reach the coordinator only", StringComparison.Ordinal));
        }
        finally
        {
            fallbackBranch.Operand = resume;
            foreach (var instruction in planted) il.Remove(instruction);
        }
        ReadOnlyModeSelectionMatches(handle).Should().BeTrue();
    }

    private static bool ReadOnlyModeSelectionMatches(MethodDefinition handle)
    {
        if (!handle.HasBody) return false;
        var lookups = handle.Body.Instructions.Where(instruction => instruction.Operand is MethodReference method
            && method.Name == "Contains" && method.DeclaringType.GetElementType().FullName == "System.Collections.Frozen.FrozenSet`1").ToArray();
        var calls = handle.Body.Instructions.Where(instruction => instruction.Operand is MethodReference method
            && method.Name == "ExecuteRequestAsync" && method.DeclaringType.FullName ==
                "LearnStack.Application.Persistence.TransactionExecutionCoordinator").ToArray();
        if (lookups.Length != 1 || calls.Length != 1 || lookups[0].Next is not { } branch
            || branch.OpCode.Code is not (Code.Brtrue or Code.Brtrue_S)
            || branch.Operand is not Instruction readOnly || readOnly.OpCode != OpCodes.Ldc_I4_1
            || !IsReadOnlyFallback(branch, readOnly)) return false;
        var fallback = branch.Next.Next.Next.Next.Next;
        if (!Predecessors(readOnly).SequenceEqual([branch])) return false;
        for (var current = branch.Next; current != fallback.Next; current = current.Next)
            if (!Predecessors(current).SequenceEqual([current.Previous])) return false;
        var producers = ModeArgumentProducers(handle, calls[0]);
        return producers is not null && producers.SetEquals([readOnly, fallback]);

        IEnumerable<Instruction> Predecessors(Instruction target) =>
            handle.Body.Instructions.Where(instruction => ModeSuccessors(instruction).Contains(target));
    }

    // Track the actual mode stack slot at the coordinator call. A union retains
    // both sanctioned branch producers; a local, override or extra producer fails
    // closed rather than being mistaken for the earlier membership expression.
    private static HashSet<Instruction>? ModeArgumentProducers(MethodDefinition owner, Instruction call)
    {
        var states = new Dictionary<Instruction, HashSet<Instruction>[]>();
        var pending = new Queue<Instruction>();
        states.Add(owner.Body.Instructions[0], []);
        pending.Enqueue(owner.Body.Instructions[0]);
        while (pending.TryDequeue(out var instruction))
        {
            if (instruction == call) continue;
            var stack = states[instruction].Select(values => new HashSet<Instruction>(values)).ToList();
            if (instruction.OpCode == OpCodes.Dup)
            {
                if (stack.Count == 0) return null;
                stack.Add(new HashSet<Instruction>(stack[^1]));
            }
            else
            {
                var pop = ModePopCount(instruction, owner);
                var push = ModePushCount(instruction);
                if (pop < 0 || push < 0 || stack.Count < pop) return null;
                stack.RemoveRange(stack.Count - pop, pop);
                for (var index = 0; index < push; index++) stack.Add([instruction]);
            }
            if (instruction.OpCode.Code is Code.Leave or Code.Leave_S) stack.Clear();
            foreach (var next in ModeSuccessors(instruction))
            {
                var incoming = stack.Select(values => new HashSet<Instruction>(values)).ToArray();
                if (!states.TryGetValue(next, out var previous))
                {
                    states.Add(next, incoming);
                    pending.Enqueue(next);
                    continue;
                }
                if (previous.Length != incoming.Length) return null;
                var changed = false;
                for (var index = 0; index < previous.Length; index++)
                {
                    var count = previous[index].Count;
                    previous[index].UnionWith(incoming[index]);
                    changed |= previous[index].Count != count;
                }
                if (changed) pending.Enqueue(next);
            }
        }
        var target = (MethodReference)call.Operand;
        return states.TryGetValue(call, out var arguments) && target.Parameters.Count == 4 && arguments.Length >= 4
            ? arguments[arguments.Length - target.Parameters.Count + 1] : null;
    }

    private static IEnumerable<Instruction> ModeSuccessors(Instruction instruction)
    {
        if (instruction.OpCode.FlowControl is FlowControl.Return or FlowControl.Throw) yield break;
        if (instruction.Operand is Instruction branch) yield return branch;
        if (instruction.Operand is Instruction[] branches)
            foreach (var target in branches) yield return target;
        if (instruction.OpCode.FlowControl != FlowControl.Branch && instruction.Next is { } next) yield return next;
    }

    private static int ModePopCount(Instruction instruction, MethodDefinition owner) => instruction.OpCode.StackBehaviourPop switch
    {
        StackBehaviour.Pop0 => 0,
        StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
        StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8
            or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
        StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8
            or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref => 3,
        StackBehaviour.Varpop when instruction.OpCode == OpCodes.Ret => owner.ReturnType.FullName == "System.Void" ? 0 : 1,
        StackBehaviour.Varpop when instruction.Operand is MethodReference method =>
            method.Parameters.Count + (method.HasThis && instruction.OpCode != OpCodes.Newobj ? 1 : 0),
        _ => -1,
    };

    private static int ModePushCount(Instruction instruction) => instruction.OpCode.StackBehaviourPush switch
    {
        StackBehaviour.Push0 => 0,
        StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4
            or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
        StackBehaviour.Varpush when instruction.Operand is MethodReference method =>
            instruction.OpCode == OpCodes.Newobj || method.ReturnType.FullName != "System.Void" ? 1 : 0,
        _ => -1,
    };
}
