using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

public sealed partial class AuthenticatedHttpSurfaceTests
{
    private sealed record RequestValue(string Type);
    private sealed record VersionValue(string Request, string Property);
    private sealed record DescriptorValue(int Operation, string Request, int Kind, bool Creates);
    private sealed record LocalAddress(int Index);
    private sealed record ExceptionValue(string Type);
    private sealed record Evaluation(bool Valid, object? Value);

    private static IEnumerable<string> DescriptorRelationOffenders(TypeDefinition descriptor, Dictionary<string, TypeDefinition> map)
    {
        foreach (var shape in ProtectedShapes)
        {
            var endpoint = Endpoints.Single(entry => entry.ProtectedOperation == shape.Operation);
            var request = endpoint.Request.Assembly.GetName().Name + ":" + endpoint.Request.FullName;
            var value = new RequestValue(request);
            if (EvaluateDescriptor(descriptor, "For", shape.Operation, map) != new Evaluation(true,
                    new DescriptorValue(shape.Operation, request, shape.Kind, shape.Operation == 7)))
                yield return "Unexpected protected descriptor: exact enum-to-request relation " + shape.Name;
            if (EvaluateDescriptor(descriptor, "OperationFor", value, map) != new Evaluation(true, shape.Operation))
                yield return "Unexpected protected descriptor: exact request-to-enum relation " + shape.Name;
            if (EvaluateDescriptor(descriptor, "ExpectedVersion", value, map) != new Evaluation(true,
                    shape.Version is null ? null : new VersionValue(request, "get_" + shape.Version)))
                yield return "Unexpected protected descriptor: exact request-to-precondition relation " + shape.Name;
        }
        foreach (var unknown in new[] { 0, -1, int.MaxValue })
            if (EvaluateDescriptor(descriptor, "For", unknown, map) != new Evaluation(true, new ExceptionValue("System.ArgumentOutOfRangeException")))
                yield return "Unexpected protected descriptor: undefined operations refuse";
        if (EvaluateDescriptor(descriptor, "OperationFor", new RequestValue("unknown"), map) != new Evaluation(true, null)
            || EvaluateDescriptor(descriptor, "ExpectedVersion", new RequestValue("unknown"), map)
                != new Evaluation(true, new ExceptionValue("System.ArgumentException")))
            yield return "Unexpected protected descriptor: unregistered request refuses";
    }

    // A bounded evaluator for three pure switches, not execution of production code.
    // It recognizes only argument/local/branch/type-test/descriptor construction and
    // typed version reads. Unknown instructions or calls invalidate the boundary.
    private static Evaluation EvaluateDescriptor(TypeDefinition owner, string name, object argument, Dictionary<string, TypeDefinition> map)
    {
        var method = owner.Methods.SingleOrDefault(candidate => candidate.Name == name);
        if (Key(owner) != "LearnStack.Api:" + ProtectedDescriptor
            || method is not { HasBody: true, IsStatic: true, Parameters.Count: 1 }
            || method.HasGenericParameters || method.HasThis || method.ExplicitThis
            || (name == "For"
                ? Key(method.Parameters[0].ParameterType) != OperationEnum || Key(method.ReturnType) != Key(owner)
                : !BclType(method.Parameters[0].ParameterType, "System.Object") || !DescriptorNullable(method.ReturnType, name))) return new(false, null);
        var stack = new List<object?>();
        var locals = new object?[method.Body.Variables.Count];
        var instruction = method.Body.Instructions.FirstOrDefault();
        object? Pop()
        {
            if (stack.Count == 0) throw new InvalidOperationException("Invalid symbolic stack.");
            var value = stack[^1]; stack.RemoveAt(stack.Count - 1); return value;
        }
        static bool Truth(object? value) => value is not null && (value is not int number || number != 0);
        try
        {
            for (var steps = 0; instruction is not null && steps < 4096; steps++)
            {
                var next = instruction.Next;
                var integer = Integer(instruction);
                if (integer is not null) stack.Add(integer.Value);
                else switch (instruction.OpCode.Code)
                    {
                        case Code.Nop: break;
                        case Code.Ldarg_0: stack.Add(argument); break;
                        case Code.Ldarg:
                        case Code.Ldarg_S:
                            if (instruction.Operand is not ParameterDefinition { Index: 0 }) return new(false, null);
                            stack.Add(argument); break;
                        case Code.Ldnull: stack.Add(null); break;
                        case Code.Ldstr: stack.Add((string)instruction.Operand); break;
                        case Code.Ldtoken:
                            if (instruction.Operand is not TypeReference token) return new(false, null);
                            stack.Add(Key(token)); break;
                        case Code.Ldloc:
                        case Code.Ldloc_S:
                        case Code.Ldloc_0:
                        case Code.Ldloc_1:
                        case Code.Ldloc_2:
                        case Code.Ldloc_3:
                            stack.Add(locals[LocalIndex(instruction)!.Value]); break;
                        case Code.Stloc:
                        case Code.Stloc_S:
                        case Code.Stloc_0:
                        case Code.Stloc_1:
                        case Code.Stloc_2:
                        case Code.Stloc_3:
                            locals[LocalIndex(instruction)!.Value] = Pop(); break;
                        case Code.Ldloca:
                        case Code.Ldloca_S:
                            stack.Add(new LocalAddress(((VariableDefinition)instruction.Operand).Index)); break;
                        case Code.Initobj:
                            if (instruction.Operand is not TypeReference initialized || !DescriptorNullable(initialized, name)
                                || Pop() is not LocalAddress address || address.Index < 0 || address.Index >= locals.Length
                                || !SameType(initialized, method.Body.Variables[address.Index].VariableType)) return new(false, null);
                            locals[address.Index] = null; break;
                        case Code.Dup:
                            if (stack.Count == 0) return new(false, null);
                            stack.Add(stack[^1]); break;
                        case Code.Pop: Pop(); break;
                        case Code.Sub:
                            if (Pop() is not int right || Pop() is not int left) return new(false, null);
                            stack.Add(unchecked(left - right)); break;
                        case Code.Ceq:
                            var second = Pop(); var first = Pop(); stack.Add(Equals(first, second) ? 1 : 0); break;
                        case Code.Cgt_Un:
                            var comparison = Pop(); var tested = Pop();
                            if (comparison is not null) return new(false, null);
                            stack.Add(tested is null ? 0 : 1); break;
                        case Code.Isinst:
                            if (instruction.Operand is not TypeReference type) return new(false, null);
                            var input = Pop(); stack.Add(input is RequestValue request && request.Type == Key(type) ? input : null); break;
                        case Code.Br: case Code.Br_S: next = (Instruction)instruction.Operand; break;
                        case Code.Brtrue:
                        case Code.Brtrue_S:
                            if (Truth(Pop())) next = (Instruction)instruction.Operand; break;
                        case Code.Brfalse:
                        case Code.Brfalse_S:
                            if (!Truth(Pop())) next = (Instruction)instruction.Operand; break;
                        case Code.Beq:
                        case Code.Beq_S:
                            var rhs = Pop(); if (Equals(Pop(), rhs)) next = (Instruction)instruction.Operand; break;
                        case Code.Bne_Un:
                        case Code.Bne_Un_S:
                            var unequal = Pop(); if (!Equals(Pop(), unequal)) next = (Instruction)instruction.Operand; break;
                        case Code.Switch:
                            if (Pop() is not int selected || instruction.Operand is not Instruction[] branches) return new(false, null);
                            if (selected >= 0 && selected < branches.Length) next = branches[selected]; break;
                        case Code.Call:
                        case Code.Callvirt:
                        case Code.Newobj:
                            if (instruction.Operand is not MethodReference call) return new(false, null);
                            var args = new object?[call.Parameters.Count];
                            for (var index = args.Length - 1; index >= 0; index--) args[index] = Pop();
                            var receiver = call.HasThis && instruction.OpCode != OpCodes.Newobj ? Pop() : null;
                            if (instruction.OpCode == OpCodes.Call && TypeFromHandleSignature(call)
                                && args is [string requestType]) stack.Add(requestType);
                            else if (instruction.OpCode == OpCodes.Newobj && DescriptorConstructor(call, owner)
                                && args is [int operation, string declared, int kind, int creates] && creates is 0 or 1)
                                stack.Add(new DescriptorValue(operation, declared, kind, creates == 1));
                            else if (instruction.OpCode == OpCodes.Newobj && NullableConstructor(call, name)
                                && args.Length == 1 && (name == "OperationFor" ? args[0] is int : args[0] is VersionValue)) stack.Add(args[0]);
                            else if (instruction.OpCode == OpCodes.Newobj && ExceptionConstructor(call, name))
                                stack.Add(new ExceptionValue(call.DeclaringType.FullName));
                            else if (instruction.OpCode.Code is Code.Call or Code.Callvirt && args.Length == 0
                                && receiver is RequestValue instance && VersionGetter(call, instance, map))
                                stack.Add(new VersionValue(instance.Type, call.Name));
                            else return new(false, null);
                            break;
                        case Code.Throw:
                            var exception = Pop(); return new(exception is ExceptionValue && stack.Count == 0, exception);
                        case Code.Ret:
                            var result = Pop(); return new(stack.Count == 0, result);
                        default: return new(false, null);
                    }
                instruction = next;
            }
        }
        catch (InvalidOperationException) { return new(false, null); }
        catch (IndexOutOfRangeException) { return new(false, null); }
        return new(false, null);
    }

    private const string OperationEnum = "LearnStack.Api:LearnStack.Api.Idempotency.ProtectedAdministrationOperation";

    // Require the target's System.Runtime reference symbols as well as each
    // complete signature; a familiar namespace/member spelling is insufficient.
    private static bool BclType(TypeReference type, string name) => type.FullName == name
        && type.GetElementType().Scope.Name.Replace(".dll", "", StringComparison.Ordinal) == "System.Runtime";

    private static bool OrdinarySignature(MethodReference method, bool instance) => method.HasThis == instance
        && !method.ExplicitThis && !method.HasGenericParameters && method is not GenericInstanceMethod
        && method.CallingConvention == MethodCallingConvention.Default;

    private static bool TypeFromHandleSignature(MethodReference call) => OrdinarySignature(call, false)
        && call.Name == "GetTypeFromHandle" && BclType(call.DeclaringType, "System.Type")
        && BclType(call.ReturnType, "System.Type") && call.Parameters.Count == 1
        && BclType(call.Parameters[0].ParameterType, "System.RuntimeTypeHandle");

    private static bool DescriptorConstructor(MethodReference call, TypeDefinition owner)
    {
        if (!OrdinarySignature(call, true) || call.Name != ".ctor" || Key(call.DeclaringType) != Key(owner)
            || Key(owner) != "LearnStack.Api:" + ProtectedDescriptor || !BclType(call.ReturnType, "System.Void")
            || call.Parameters.Count != 4 || Key(call.Parameters[0].ParameterType) != OperationEnum
            || !BclType(call.Parameters[1].ParameterType, "System.Type")
            || Key(call.Parameters[2].ParameterType) != "LearnStack.SharedKernel:LearnStack.SharedKernel.Idempotency.IdempotencyReplayTargetKind"
            || !BclType(call.Parameters[3].ParameterType, "System.Boolean")) return false;
        var declared = owner.Methods.Where(method => method.IsConstructor && !method.IsStatic
            && method.FullName == call.FullName).ToArray();
        return declared.Length == 1 && OrdinarySignature(declared[0], true)
            && SameType(declared[0].ReturnType, call.ReturnType)
            && declared[0].Parameters.Select((parameter, index) => SameType(parameter.ParameterType, call.Parameters[index].ParameterType)).All(equal => equal);
    }

    private static bool DescriptorNullable(TypeReference type, string method) => type is GenericInstanceType nullable
        && BclType(nullable.ElementType, "System.Nullable`1") && nullable.GenericArguments.Count == 1
        && (method == "OperationFor" ? Key(nullable.GenericArguments[0]) == OperationEnum
            : method == "ExpectedVersion" && BclType(nullable.GenericArguments[0], "System.Int64"));

    private static bool NullableConstructor(MethodReference call, string method) => OrdinarySignature(call, true)
        && call.Name == ".ctor" && DescriptorNullable(call.DeclaringType, method) && BclType(call.ReturnType, "System.Void")
        && call.Parameters.Count == 1 && call.Parameters[0].ParameterType is GenericParameter
        { Type: GenericParameterType.Type, Position: 0, Owner: TypeReference parameterOwner }
        && SameType(parameterOwner, call.DeclaringType.GetElementType());

    private static bool ExceptionConstructor(MethodReference call, string method) => OrdinarySignature(call, true)
        && call.Name == ".ctor" && BclType(call.ReturnType, "System.Void")
        && (method == "For" && BclType(call.DeclaringType, "System.ArgumentOutOfRangeException") && call.Parameters.Count == 1
            || method == "ExpectedVersion" && BclType(call.DeclaringType, "System.ArgumentException") && call.Parameters.Count == 2)
        && call.Parameters.All(parameter => BclType(parameter.ParameterType, "System.String"));

    private static bool VersionGetter(MethodReference call, RequestValue instance, Dictionary<string, TypeDefinition> map)
    {
        if (!OrdinarySignature(call, true) || Key(call.DeclaringType) != instance.Type || call.Parameters.Count != 0
            || !BclType(call.ReturnType, "System.Int64") || !map.TryGetValue(instance.Type, out var request)) return false;
        var shape = ProtectedShapes.SingleOrDefault(item => Endpoints.Single(entry => entry.ProtectedOperation == item.Operation)
            .Request.FullName == request.FullName);
        if (shape?.Version is not { } propertyName || call.Name != "get_" + propertyName) return false;
        var property = request.Properties.SingleOrDefault(candidate => candidate.Name == propertyName);
        return property is { GetMethod: { } getter } && BclType(property.PropertyType, "System.Int64")
            && getter.IsPublic && getter.IsSpecialName && OrdinarySignature(getter, true) && getter.Parameters.Count == 0
            && BclType(getter.ReturnType, "System.Int64") && getter.FullName == call.FullName
            && Key(getter.DeclaringType) == instance.Type;
    }
}
