using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

/// <summary>Shared closed-surface capabilities and fail-closed call-argument provenance.</summary>
internal static partial class EndpointComposition
{
    internal static bool IsMapping(MethodReference method) => method.Name.StartsWith("Map", StringComparison.Ordinal)
        && (method.DeclaringType.FullName.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal)
            || method.DeclaringType.FullName.StartsWith("Scalar.AspNetCore.", StringComparison.Ordinal));

    internal static bool IsCapability(MethodReference method) => IsMapping(method)
        || method.Name == "get_DataSources" && method.ReturnType.FullName.Contains("Microsoft.AspNetCore.Routing.EndpointDataSource", StringComparison.Ordinal)
        || method.Name is "Add" or "Insert" or "set_Item" && method.DeclaringType is GenericInstanceType collection
            && collection.GenericArguments.Any(type => type.FullName == "Microsoft.AspNetCore.Routing.EndpointDataSource")
        || method.Name is "AddApplicationPart" or "ConfigureApplicationPartManager"
            && method.DeclaringType.FullName.StartsWith("Microsoft.Extensions.DependencyInjection.", StringComparison.Ordinal)
        || method.Name == "get_ApplicationParts" && method.DeclaringType.FullName == "Microsoft.AspNetCore.Mvc.ApplicationParts.ApplicationPartManager"
        || method.Name is "RequireAuthorization" or "AllowAnonymous" or "WithMetadata"
            && method.DeclaringType.Namespace == "Microsoft.AspNetCore.Builder"
        || method.Name is "Add" or "Finally" && method.DeclaringType.Namespace == "Microsoft.AspNetCore.Builder"
            && method.Parameters.Any(parameter => parameter.ParameterType is GenericInstanceType callback
                && callback.ElementType.FullName == "System.Action`1"
                && callback.GenericArguments.Any(type => type.FullName == "Microsoft.AspNetCore.Builder.EndpointBuilder"))
        || method.Name == "get_Metadata" && method.DeclaringType.FullName == "Microsoft.AspNetCore.Builder.EndpointBuilder"
        || method.Name == "get_EndpointMetadata"
            && method.DeclaringType.FullName == "Microsoft.AspNetCore.Mvc.ApplicationModels.SelectorModel"
        || method.Name == "UseEndpoints" && method.DeclaringType.FullName.StartsWith("Microsoft.AspNetCore.Builder.", StringComparison.Ordinal)
        || method.Name == "Run" && method.DeclaringType.FullName == "Microsoft.AspNetCore.Builder.RunExtensions";

    internal static bool HasLiteralPath(MethodDefinition owner, Instruction call, string path) =>
        HasLiteralArgument(owner, call, 1, path);

    internal static bool HasLiteralArgument(MethodDefinition owner, Instruction call, int argument, string expected) =>
        ArgumentProducer(owner, call, argument) is { OpCode.Code: Code.Ldstr, Operand: string literal } && literal == expected;

    // Track stack producers over every reachable branch. Locals, fields and
    // computed expressions remain their own producers: even a local initialized
    // with the right string is not the direct literal this exception permits.
    // Delegate-cache branches may merge without losing the independent path slot.
    internal static Instruction? ArgumentProducer(MethodDefinition owner, Instruction call, int argument)
    {
        if (!owner.HasBody || call.Operand is not MethodReference target || argument >= target.Parameters.Count) return null;
        var states = new Dictionary<Instruction, Instruction?[]>();
        var pending = new Queue<Instruction>();
        states.Add(owner.Body.Instructions[0], []);
        pending.Enqueue(owner.Body.Instructions[0]);
        while (pending.TryDequeue(out var instruction))
        {
            if (instruction == call) continue;
            var stack = states[instruction].ToList();
            if (instruction.OpCode == OpCodes.Dup)
            {
                if (stack.Count == 0) return null;
                stack.Add(stack[^1]);
            }
            else
            {
                var pop = PopCount(instruction, owner);
                var push = PushCount(instruction);
                if (pop < 0 || push < 0 || stack.Count < pop) return null;
                stack.RemoveRange(stack.Count - pop, pop);
                for (var index = 0; index < push; index++) stack.Add(instruction);
            }
            if (instruction.OpCode.Code is Code.Leave or Code.Leave_S) stack.Clear();
            foreach (var next in Successors(instruction))
            {
                var incoming = stack.ToArray();
                if (!states.TryGetValue(next, out var previous))
                {
                    states.Add(next, incoming);
                    pending.Enqueue(next);
                    continue;
                }
                if (previous.Length != incoming.Length) return null;
                var changed = false;
                for (var index = 0; index < previous.Length; index++)
                    if (previous[index] is not null && previous[index] != incoming[index])
                    {
                        previous[index] = null;
                        changed = true;
                    }
                if (changed) pending.Enqueue(next);
            }
        }
        return states.TryGetValue(call, out var arguments) && arguments.Length >= target.Parameters.Count
            ? arguments[arguments.Length - target.Parameters.Count + argument] : null;
    }

    private static IEnumerable<Instruction> Successors(Instruction instruction)
    {
        if (instruction.OpCode.FlowControl is FlowControl.Return or FlowControl.Throw) yield break;
        if (instruction.Operand is Instruction branch) yield return branch;
        if (instruction.Operand is Instruction[] branches)
            foreach (var target in branches) yield return target;
        if (instruction.OpCode.FlowControl != FlowControl.Branch && instruction.Next is { } next) yield return next;
    }

    private static int PopCount(Instruction instruction, MethodDefinition owner) => instruction.OpCode.StackBehaviourPop switch
    {
        StackBehaviour.Pop0 => 0,
        StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
        StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8
            or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
        StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi
            or StackBehaviour.Popref_popi_popi8 or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8
            or StackBehaviour.Popref_popi_popref => 3,
        StackBehaviour.Varpop when instruction.OpCode == OpCodes.Ret => owner.ReturnType.FullName == "System.Void" ? 0 : 1,
        StackBehaviour.Varpop when instruction.Operand is MethodReference method =>
            method.Parameters.Count + (method.HasThis && instruction.OpCode != OpCodes.Newobj ? 1 : 0),
        _ => -1
    };

    private static int PushCount(Instruction instruction) => instruction.OpCode.StackBehaviourPush switch
    {
        StackBehaviour.Push0 => 0,
        StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4
            or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
        StackBehaviour.Varpush when instruction.Operand is MethodReference method =>
            instruction.OpCode == OpCodes.Newobj || method.ReturnType.FullName != "System.Void" ? 1 : 0,
        _ => -1
    };
}
