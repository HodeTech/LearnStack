using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

public sealed partial class AuthenticatedHttpSurfaceTests
{
    private const string ProtectedResult = "LearnStack.Api.Idempotency.ProtectedAdministrationResult";
    private const string ProtectedDescriptor = "LearnStack.Api.Idempotency.ProtectedAdministrationDescriptor";
    private const string IdempotentMetadata = "LearnStack.Api.Idempotency.IdempotentAttribute";
    private const string TenantContext = "LearnStack.SharedKernel.Tenancy.ITenantContext";
    private const string Sender = "MediatR.ISender";
    private static readonly string[] HandoffParameters =
    ["Microsoft.AspNetCore.Http.HttpContext", Sender, TenantContext, "System.Object", "System.Threading.CancellationToken"];

    private static bool HandoffSignature(MethodReference target) => Key(target.DeclaringType) == "LearnStack.Api:" + ProtectedResult
        && target.Name == "ExecuteAsync" && !target.HasThis && !target.HasGenericParameters
        && target.ReturnType.FullName == "System.Threading.Tasks.Task`1<Microsoft.AspNetCore.Mvc.IActionResult>"
        && target.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(HandoffParameters, StringComparer.Ordinal);

    private static bool ProtectedHandoff(TypeDefinition controller, MethodDefinition action, Endpoint endpoint)
    {
        var bodies = ActionBodies(controller, action.Name).ToArray();
        var calls = bodies.SelectMany(method => method.Body.Instructions.Where(instruction => instruction.Operand is MethodReference)
            .Select(instruction => (Method: method, Instruction: instruction, Target: (MethodReference)instruction.Operand))).ToArray();
        if (calls.Any(site => site.Target.DeclaringType.FullName is Sender or "MediatR.IMediator" or "MediatR.IPublisher")) return false;
        var handoffs = calls.Where(site => site.Target.DeclaringType.FullName == ProtectedResult).ToArray();
        if (handoffs.Length != 1 || handoffs[0].Instruction.OpCode != OpCodes.Call || !HandoffSignature(handoffs[0].Target)) return false;
        var site = handoffs[0];
        var request = Argument(site.Method, site.Instruction, 3);
        return request is { OpCode.Code: Code.Newobj, Operand: MethodReference constructor }
            && Key(constructor.DeclaringType) == endpoint.Request.Assembly.GetName().Name + ":" + endpoint.Request.FullName
            && InjectedArgument(site.Method, Argument(site.Method, site.Instruction, 1), Sender, controller)
            && InjectedArgument(site.Method, Argument(site.Method, site.Instruction, 2), TenantContext, controller);
    }

    // Same fail-closed backwards value walk as the historical direct Send rule.
    // This works inside a generated async body without treating a local as a newobj.
    private static Instruction? Argument(MethodDefinition method, Instruction call, int argument)
    {
        if (call.Operand is not MethodReference target || argument < 0 || argument >= target.Parameters.Count) return null;
        var position = method.Body.Instructions.IndexOf(call) - 1;
        for (var index = target.Parameters.Count - 1; index > argument; index--)
            if (!PublicSurfaceTests.SkipValue(method.Body.Instructions, ref position)) return null;
        while (position >= 0 && method.Body.Instructions[position].OpCode == OpCodes.Nop) position--;
        return position < 0 ? null : method.Body.Instructions[position];
    }

    private static bool InjectedArgument(MethodDefinition method, Instruction? producer, string contract, TypeDefinition owner) =>
        producer is { OpCode.Code: Code.Ldfld, Operand: FieldReference field } && field.FieldType.FullName == contract
        && (Key(field.DeclaringType) == Key(owner) || IsGeneratedUnder(field.DeclaringType, owner));

    private static bool IsGeneratedUnder(TypeReference reference, TypeDefinition owner)
    {
        for (var current = reference; current.DeclaringType is { } parent; current = parent)
            if (Key(parent) == Key(owner) && current.Name.Contains('<', StringComparison.Ordinal)) return true;
        return false;
    }

    private static TypeReference? ValueType(MethodDefinition method, Instruction? instruction) => instruction?.OpCode.Code switch
    {
        Code.Ldfld or Code.Ldsfld when instruction.Operand is FieldReference field => field.FieldType,
        Code.Ldloc_0 when method.Body.Variables.Count > 0 => method.Body.Variables[0].VariableType,
        Code.Ldloc_1 when method.Body.Variables.Count > 1 => method.Body.Variables[1].VariableType,
        Code.Ldloc_2 when method.Body.Variables.Count > 2 => method.Body.Variables[2].VariableType,
        Code.Ldloc_3 when method.Body.Variables.Count > 3 => method.Body.Variables[3].VariableType,
        Code.Ldloc or Code.Ldloc_S when instruction.Operand is VariableDefinition local => local.VariableType,
        _ => null
    };

    private static bool ProtectedDispatcher(TypeDefinition type, Dictionary<string, TypeDefinition> map)
    {
        if (Key(type) != "LearnStack.Api:" + ProtectedResult || !type.IsSealed || type.IsPublic) return false;
        var execute = type.Methods.SingleOrDefault(method => method.Name == "ExecuteAsync");
        if (execute is null || !HandoffSignature(execute)) return false;
        if (Flatten(type).SelectMany(owner => owner.Fields).Any(field => field.FieldType.FullName == Sender
                && !(field.Name == "sender" && IsGeneratedUnder(field.DeclaringType, type)))
            || Il.Methods(type).Any(pair => pair.Definition.Parameters.Any(parameter => parameter.ParameterType.FullName == Sender
                && !(pair.Definition == execute && parameter.Index == 1)))) return false;
        var entries = Endpoints.Where(endpoint => endpoint.ProtectedOperation is not null).ToArray();
        var expected = entries.Select(endpoint => endpoint.Request.Assembly.GetName().Name + ":" + endpoint.Request.FullName).Order(StringComparer.Ordinal).ToArray();
        var methods = Il.Methods(type).Where(pair => pair.Definition.HasBody).ToArray();
        var sends = methods.SelectMany(pair => pair.Definition.Body.Instructions
            .Where(instruction => instruction.Operand is MethodReference target && target.DeclaringType.FullName == Sender)
            .Select(instruction => (pair.Definition, pair.DeclaredAs, Instruction: instruction, Target: (MethodReference)instruction.Operand))).ToArray();
        if (sends.Length != 10 || sends.Any(site => site.DeclaredAs != "ExecuteAsync" || site.Instruction.OpCode != OpCodes.Callvirt
                || site.Target is not GenericInstanceMethod { Name: "Send", GenericArguments.Count: 1, Parameters.Count: 2 }
                || site.Target.Parameters[1].ParameterType.FullName != "System.Threading.CancellationToken"
                || !InjectedArgument(site.Definition, Receiver(site.Definition, site.Instruction), Sender, type))) return false;
        var requestTypes = sends.Select(site => ValueType(site.Definition, Argument(site.Definition, site.Instruction, 0))).ToArray();
        if (requestTypes.Any(reference => reference is null)
            || !requestTypes.Select(reference => Key(reference!)).Order(StringComparer.Ordinal).SequenceEqual(expected, StringComparer.Ordinal)) return false;
        for (var index = 0; index < sends.Length; index++)
        {
            if (!map.TryGetValue(Key(requestTypes[index]!), out var request)) return false;
            var response = request.Interfaces.Select(item => item.InterfaceType).OfType<GenericInstanceType>()
                .SingleOrDefault(contract => contract.ElementType.FullName == "MediatR.IRequest`1")?.GenericArguments.Single();
            if (response is null || ((GenericInstanceMethod)sends[index].Target).GenericArguments[0].FullName != response.FullName) return false;
        }
        var branches = ActionBodies(type, "ExecuteAsync").SelectMany(method => method.Body.Instructions)
            .Where(instruction => instruction.OpCode == OpCodes.Isinst).Select(instruction => instruction.Operand).OfType<TypeReference>().ToArray();
        return branches.Select(Key).Order(StringComparer.Ordinal).SequenceEqual(expected, StringComparer.Ordinal)
            && requestTypes.Select(reference => Key(reference!)).SequenceEqual(branches.Select(Key), StringComparer.Ordinal)
            && sends.Select((site, index) => TypedRequestOrigin(site.Definition, Argument(site.Definition, site.Instruction, 0), requestTypes[index]!, type, [])).All(valid => valid)
            && !Il.ReferencedTypeNames(type).Any(name => name is "MediatR.IMediator" or "MediatR.Mediator" or "MediatR.IPublisher");
    }

    private static bool TypedRequestOrigin(MethodDefinition method, Instruction? producer, TypeReference expected,
        TypeDefinition owner, HashSet<Instruction> visited)
    {
        if (producer is null || !visited.Add(producer)) return false;
        if (producer.OpCode == OpCodes.Isinst && producer.Operand is TypeReference tested && Key(tested) == Key(expected))
            return OriginalRequest(method, Previous(producer), owner, []);
        var assignments = Assignments(method, producer).ToArray();
        return assignments.Length > 0 && assignments.All(value => TypedRequestOrigin(method, value, expected, owner, new(visited)));
    }

    private static bool OriginalRequest(MethodDefinition method, Instruction? producer, TypeDefinition owner, HashSet<Instruction> visited)
    {
        if (producer is null || !visited.Add(producer)) return false;
        if (producer is { OpCode.Code: Code.Ldfld, Operand: FieldReference { Name: "request", FieldType.FullName: "System.Object" } field }
            && IsGeneratedUnder(field.DeclaringType, owner)) return true;
        var assignments = Assignments(method, producer).ToArray();
        return assignments.Length > 0 && assignments.All(value => OriginalRequest(method, value, owner, new(visited)));
    }

    private static Instruction? Previous(Instruction instruction)
    {
        var previous = instruction.Previous;
        while (previous?.OpCode == OpCodes.Nop) previous = previous.Previous;
        return previous;
    }

    private static IEnumerable<Instruction?> Assignments(MethodDefinition method, Instruction producer)
    {
        var local = LocalIndex(producer);
        foreach (var instruction in method.Body.Instructions.TakeWhile(instruction => instruction != producer))
            if (local is not null && instruction.OpCode.Code is Code.Stloc or Code.Stloc_S or Code.Stloc_0 or Code.Stloc_1 or Code.Stloc_2 or Code.Stloc_3
                && LocalIndex(instruction) == local
                || producer is { OpCode.Code: Code.Ldfld, Operand: FieldReference loaded }
                    && instruction is { OpCode.Code: Code.Stfld, Operand: FieldReference stored } && stored.FullName == loaded.FullName)
                yield return Previous(instruction);
    }

    private static int? LocalIndex(Instruction instruction) => instruction.OpCode.Code switch
    {
        Code.Ldloc_0 or Code.Stloc_0 => 0,
        Code.Ldloc_1 or Code.Stloc_1 => 1,
        Code.Ldloc_2 or Code.Stloc_2 => 2,
        Code.Ldloc_3 or Code.Stloc_3 => 3,
        Code.Ldloc or Code.Ldloc_S or Code.Stloc or Code.Stloc_S when instruction.Operand is VariableDefinition local => local.Index,
        _ => null
    };

    private static Instruction? Receiver(MethodDefinition method, Instruction call)
    {
        if (call.Operand is not MethodReference target || !target.HasThis) return null;
        var position = method.Body.Instructions.IndexOf(call) - 1;
        for (var index = 0; index < target.Parameters.Count; index++)
            if (!PublicSurfaceTests.SkipValue(method.Body.Instructions, ref position)) return null;
        while (position >= 0 && method.Body.Instructions[position].OpCode == OpCodes.Nop) position--;
        return position < 0 ? null : method.Body.Instructions[position];
    }

    private static IEnumerable<string> ProtectedAdministrationOffenders(ModuleDefinition[] modules, Dictionary<string, TypeDefinition> map)
    {
        if (!map.TryGetValue("LearnStack.Api:" + ProtectedResult, out var carrier) || !ProtectedDispatcher(carrier, map))
            yield return "Unexpected protected typed dispatcher: require exactly the ten bound typed sends";
        var expected = Endpoints.Where(entry => entry.ProtectedOperation is not null)
            .Select(entry => entry.Controller + "." + entry.Action).Order(StringComparer.Ordinal).ToArray();
        var callers = new List<string>();
        var constructors = new List<string>();
        foreach (var type in Types(modules).Where(type => !type.Name.Contains('<', StringComparison.Ordinal)))
            foreach (var pair in Il.Methods(type).Where(pair => pair.Definition.HasBody))
                foreach (var instruction in pair.Definition.Body.Instructions)
                {
                    if (instruction.Operand is not MethodReference target || Key(target.DeclaringType) != "LearnStack.Api:" + ProtectedResult) continue;
                    if (target.Name == "ExecuteAsync")
                        callers.Add(instruction.OpCode == OpCodes.Call && HandoffSignature(target)
                            ? type.FullName + "." + pair.DeclaredAs : "invalid " + type.FullName + "." + pair.DeclaredAs);
                    if (target.Name == ".ctor")
                        constructors.Add(instruction.OpCode == OpCodes.Newobj && type.FullName == ProtectedResult
                            && pair.Definition.Name.Contains("<ExecuteAsync>g__Create", StringComparison.Ordinal) ? "Create" : type.FullName + "." + pair.DeclaredAs);
                }
        if (!callers.Order(StringComparer.Ordinal).SequenceEqual(expected, StringComparer.Ordinal))
            yield return "Unexpected protected handoff callers: only the ten enumerated actions may invoke the carrier";
        if (carrier is null || carrier.Methods.Count(method => method.IsConstructor && !method.IsStatic) != 1
            || !carrier.Methods.Single(method => method.IsConstructor && !method.IsStatic).IsPrivate
            || !constructors.SequenceEqual(["Create"], StringComparer.Ordinal))
            yield return "Unexpected protected receipt construction: retain the private result and sole typed-dispatch creation";
        foreach (var error in DescriptorOffenders(map)) yield return error;
    }

    // Il's general type census is deliberately unchanged. Only these exact validated
    // attachment/readonly-member occurrences are projected for the authenticated walk.
    private static TypeReference[] HttpReferences(TypeDefinition type, Dictionary<string, TypeDefinition> map)
    {
        var detached = new List<(MethodDefinition Method, CustomAttribute Attribute, int Index)>();
        try
        {
            foreach (var endpoint in Endpoints.Where(entry => entry.Controller == type.FullName && entry.ProtectedOperation is not null))
            {
                var action = type.Methods.SingleOrDefault(method => method.Name == endpoint.Action);
                if (action is null || AttachedFilterOffenders(type, action, map).Any()) continue;
                foreach (var attribute in action.CustomAttributes.Where(attribute => IsKeyMetadata(attribute, endpoint)).ToArray())
                {
                    detached.Add((action, attribute, action.CustomAttributes.IndexOf(attribute)));
                    action.CustomAttributes.Remove(attribute);
                }
            }
            var context = ReadOnlyContextBoundary(type, map);
            var metadata = type.FullName == ProtectedResult && CarrierMetadataReadOnly(type)
                || type.FullName == HttpClassifier && ClassifierMetadataReadOnly(type);
            return Il.ReferencedTypeReferences(type).Where(reference => !(context && Key(reference) == "LearnStack.SharedKernel:" + TenantContext)
                && !(metadata && Key(reference) == "LearnStack.Api:" + IdempotentMetadata)).ToArray();
        }
        finally
        {
            foreach (var item in detached.AsEnumerable().Reverse()) item.Method.CustomAttributes.Insert(item.Index, item.Attribute);
        }
    }

    private static bool ReadOnlyContextBoundary(TypeDefinition type, Dictionary<string, TypeDefinition> map)
    {
        if (Key(type) != "LearnStack.Api:" + ProtectedResult
            && !Endpoints.Any(entry => entry.Controller == type.FullName && entry.ProtectedOperation is not null)) return false;
        if (!map.TryGetValue("LearnStack.SharedKernel:" + TenantContext, out var contract)) return false;
        var expected = new (string Name, string Type)[]
        {
            ("IsResolved", "System.Boolean"), ("TenantId", "LearnStack.SharedKernel.Identifiers.TenantId"),
            ("OrganizationId", "System.Nullable`1<LearnStack.SharedKernel.Identifiers.OrganizationId>"),
            ("UserId", "System.Nullable`1<LearnStack.SharedKernel.Identifiers.UserId>"),
            ("CausalActorUserId", "System.Nullable`1<LearnStack.SharedKernel.Identifiers.UserId>"),
            ("Origin", "System.Nullable`1<LearnStack.SharedKernel.Tenancy.TenantContextOrigin>"),
            ("HostScope", "LearnStack.SharedKernel.Tenancy.HostScope"),
            ("CorrelationId", "System.String"), ("ModuleName", "System.String")
        };
        if (!contract.IsInterface || contract.Methods.Count != expected.Length || contract.Properties.Count != expected.Length
            || expected.Any(item => contract.Properties.SingleOrDefault(property => property.Name == item.Name) is not { SetMethod: null, GetMethod: { } getter } property
                || property.PropertyType.FullName != item.Type || getter.ReturnType.FullName != item.Type
                || !getter.HasThis || !getter.IsPublic || !getter.IsSpecialName || getter.Name != "get_" + item.Name
                || getter.Parameters.Count != 0 || getter.HasGenericParameters)) return false;
        foreach (var pair in Il.Methods(type))
        {
            if (pair.Definition.ReturnType.FullName == TenantContext) return false;
            foreach (var parameter in pair.Definition.Parameters.Where(parameter => parameter.ParameterType.FullName == TenantContext))
                if (type.FullName == ProtectedResult
                    ? !(pair.Definition.DeclaringType == type && (pair.Definition.Name, parameter.Index) is (".ctor", 4) or ("ExecuteAsync", 2) or ("Matches", 1))
                    : !(pair.Definition.DeclaringType == type && pair.Definition.IsConstructor && !pair.Definition.IsStatic && parameter.Index == 1)) return false;
            if (!pair.Definition.HasBody) continue;
            var approvedLoads = new HashSet<Instruction>();
            foreach (var instruction in pair.Definition.Body.Instructions)
            {
                if (instruction.Operand is TypeReference reference && Il.ExpandedTypeReferences(reference).Any(item => item.FullName == TenantContext)) return false;
                if (instruction is { OpCode.Code: Code.Stfld, Operand: FieldReference stored } && stored.FieldType.FullName == TenantContext)
                {
                    if (!ContextField(stored, type)) return false;
                    if (Previous(instruction) is { } storedValue) approvedLoads.Add(storedValue);
                }
                if (instruction.Operand is not MethodReference call) continue;
                if (call.DeclaringType.FullName == TenantContext
                    && (instruction.OpCode != OpCodes.Callvirt || !contract.Methods.Any(getter => getter.FullName == call.FullName))) return false;
                if (call.ReturnType.FullName == TenantContext) return false;
                if (call.Parameters.Any(parameter => parameter.ParameterType.FullName == TenantContext)
                    && !(instruction.OpCode == OpCodes.Call && HandoffSignature(call)
                        || type.FullName == ProtectedResult && Key(call.DeclaringType) == Key(type) && call.Name == ".ctor")) return false;
                if (call is GenericInstanceMethod generic && generic.GenericArguments.Any(argument => argument.FullName == TenantContext)) return false;
                if (call.DeclaringType.FullName == TenantContext && Receiver(pair.Definition, instruction) is { } receiver) approvedLoads.Add(receiver);
                for (var index = 0; index < call.Parameters.Count; index++)
                    if (call.Parameters[index].ParameterType.FullName == TenantContext && Argument(pair.Definition, instruction, index) is { } passed) approvedLoads.Add(passed);
                if (type.FullName == ProtectedResult && pair.DeclaredAs == "ExecuteAsync" && call.DeclaringType.FullName == "System.ArgumentNullException"
                    && call.Name == "ThrowIfNull" && Argument(pair.Definition, instruction, 0) is { } nullChecked) approvedLoads.Add(nullChecked);
            }
            foreach (var instruction in pair.Definition.Body.Instructions)
                if ((instruction is { OpCode.Code: Code.Ldfld, Operand: FieldReference field } && field.FieldType.FullName == TenantContext
                        || ContextParameter(pair.Definition, instruction)) && !approvedLoads.Contains(instruction)) return false;
        }
        if (Flatten(type).SelectMany(owner => owner.Fields).Any(field => field.FieldType.FullName == TenantContext && !ContextField(field, type))) return false;
        return true;
    }

    private static bool ContextField(FieldReference field, TypeDefinition owner) => field.FieldType.FullName == TenantContext
        && (owner.FullName == ProtectedResult
            ? field.Name == "context" && IsGeneratedUnder(field.DeclaringType, owner)
            : Key(field.DeclaringType) == Key(owner) && field.Name == "<tenant>P");

    private static bool ContextParameter(MethodDefinition method, Instruction instruction)
    {
        if (instruction.OpCode.Code is Code.Ldarg or Code.Ldarg_S && instruction.Operand is ParameterDefinition parameter)
            return parameter.ParameterType.FullName == TenantContext;
        var index = instruction.OpCode.Code switch { Code.Ldarg_0 => 0, Code.Ldarg_1 => 1, Code.Ldarg_2 => 2, Code.Ldarg_3 => 3, _ => -1 };
        if (method.HasThis) index--;
        return index >= 0 && index < method.Parameters.Count && method.Parameters[index].ParameterType.FullName == TenantContext;
    }

    private static bool ClassifierMetadataReadOnly(TypeDefinition type)
    {
        if (type.Fields.Any(field => Il.ExpandedTypeReferences(field.FieldType).Any(reference => reference.FullName == IdempotentMetadata))) return false;
        var reads = 0;
        var getters = 0;
        foreach (var pair in Il.Methods(type).Where(pair => pair.Definition.HasBody))
            foreach (var instruction in pair.Definition.Body.Instructions)
            {
                if (instruction.Operand is TypeReference reference && reference.FullName == IdempotentMetadata
                    && (instruction.OpCode != OpCodes.Isinst || !pair.Definition.Name.Contains("HasCanonicalKeyMetadata", StringComparison.Ordinal))) return false;
                if (instruction.Operand is not MethodReference target) continue;
                if (target.DeclaringType.FullName == IdempotentMetadata)
                {
                    if (pair.DeclaredAs != "HasCanonicalKeyMetadata" || instruction.OpCode != OpCodes.Callvirt
                        || !ExactKeyMetadataGetter(target, type.Module)) return false;
                    getters++;
                }
                if (target is GenericInstanceMethod generic && generic.GenericArguments.Any(argument => argument.FullName == IdempotentMetadata))
                {
                    if (pair.DeclaredAs != "HasCanonicalKeyMetadata") return false;
                    if (generic.Name == "GetOrderedMetadata" && instruction.OpCode == OpCodes.Callvirt
                        && generic.DeclaringType.FullName == "Microsoft.AspNetCore.Http.EndpointMetadataCollection") reads++;
                    else if (EmptyKeyMetadataArray(instruction)) continue;
                    else if (instruction.OpCode != OpCodes.Call || generic.DeclaringType.FullName != "System.Linq.Enumerable"
                        || generic.Name is not ("OfType" or "ToArray" or "All")) return false;
                }
            }
        return reads == 1 && getters == 1;
    }

    private static bool EmptyKeyMetadataArray(Instruction instruction) => instruction.OpCode == OpCodes.Call
        && instruction.Operand is GenericInstanceMethod { Name: "Empty", HasThis: false, ExplicitThis: false, GenericArguments.Count: 1, Parameters.Count: 0 } method
        && BclType(method.DeclaringType, "System.Array") && Key(method.GenericArguments[0]) == "LearnStack.Api:" + IdempotentMetadata
        && method.ElementMethod.GenericParameters.Count == 1
        && method.ReturnType is ArrayType { Rank: 1, IsVector: true, ElementType: GenericParameter { Type: GenericParameterType.Method, Position: 0 } parameter }
        && parameter.Owner == method.ElementMethod;

    private static bool CarrierMetadataReadOnly(TypeDefinition type)
    {
        if (Flatten(type).Any(owner => owner.Fields.Any(field => CarriesKeyMetadata(field.FieldType))
                || owner.Properties.Any(property => CarriesKeyMetadata(property.PropertyType))
                || owner.Events.Any(item => CarriesKeyMetadata(item.EventType)))
            || Il.Methods(type).Any(pair => CarriesKeyMetadata(pair.Definition.ReturnType)
                || pair.Definition.Parameters.Any(parameter => CarriesKeyMetadata(parameter.ParameterType))
                || pair.Definition.HasBody && (pair.Definition.Body.Variables.Any(variable => CarriesKeyMetadata(variable.VariableType)
                        && !ReadOnlyKeyMetadataLocal(pair.Definition, pair.DeclaredAs, variable))
                    || pair.Definition.Body.ExceptionHandlers.Any(handler => handler.CatchType is { } caught && CarriesKeyMetadata(caught))))) return false;
        var reads = 0;
        var getters = 0;
        foreach (var pair in Il.Methods(type).Where(pair => pair.Definition.HasBody))
            foreach (var instruction in pair.Definition.Body.Instructions)
            {
                if (instruction.Operand is TypeReference reference && CarriesKeyMetadata(reference)
                    || instruction.Operand is FieldReference field && (CarriesKeyMetadata(field.FieldType) || CarriesKeyMetadata(field.DeclaringType))) return false;
                if (instruction.Operand is not MethodReference target) continue;
                if (target.DeclaringType.FullName == IdempotentMetadata)
                {
                    if (pair.DeclaredAs != "ExecuteAsync" || instruction.OpCode.Code is not (Code.Call or Code.Callvirt)
                        || !ExactKeyMetadataGetter(target, type.Module)) return false;
                    getters++;
                }
                else if (target is GenericInstanceMethod generic && generic.GenericArguments.Any(CarriesKeyMetadata))
                {
                    if (pair.DeclaredAs != "ExecuteAsync" || !CarrierMetadataFetch(instruction)) return false;
                    reads++;
                }
                else if (CarriesKeyMetadata(target.DeclaringType) || CarriesKeyMetadata(target.ReturnType)
                    || target.Parameters.Any(parameter => CarriesKeyMetadata(parameter.ParameterType))) return false;
            }
        return reads == 1 && getters == 1;
    }
    private static bool ReadOnlyKeyMetadataLocal(MethodDefinition method, string declaredAs, VariableDefinition local)
    {
        if (declaredAs != "ExecuteAsync" || local.VariableType is TypeSpecification
            || Key(local.VariableType) != "LearnStack.Api:" + IdempotentMetadata) return false;
        var stores = method.Body.Instructions.Where(instruction => instruction.OpCode.Code is Code.Stloc or Code.Stloc_S
            or Code.Stloc_0 or Code.Stloc_1 or Code.Stloc_2 or Code.Stloc_3 && LocalIndex(instruction) == local.Index).ToArray();
        var loads = method.Body.Instructions.Where(instruction => instruction.OpCode.Code is Code.Ldloc or Code.Ldloc_S
            or Code.Ldloc_0 or Code.Ldloc_1 or Code.Ldloc_2 or Code.Ldloc_3 && LocalIndex(instruction) == local.Index).ToArray();
        if (stores.Length != 1 || loads.Length == 0 || Previous(stores[0]) is not { } fetch || !CarrierMetadataFetch(fetch)
            || method.Body.Instructions.Any(instruction => instruction.OpCode.Code is Code.Ldloca or Code.Ldloca_S
                && instruction.Operand is VariableDefinition addressed && addressed.Index == local.Index)) return false;
        var getterRead = false;
        foreach (var load in loads)
        {
            if (!ReadOnlyKeyMetadataUse(method, load, out var readsGetter)) return false;
            getterRead |= readsGetter;
        }
        return getterRead;
    }

    // Follow only the metadata value's stack uses. Compiler null-conditionals may
    // duplicate it across a null branch; those are still reads. No call, store,
    // box, address or delegate escape is accepted while the value is on stack.
    private static bool ReadOnlyKeyMetadataUse(MethodDefinition method, Instruction load, out bool readsGetter)
    {
        readsGetter = false;
        var pending = new Queue<(Instruction? Instruction, int Copies)>();
        pending.Enqueue((load.Next, 1));
        var visited = new HashSet<(Instruction, int)>();
        var steps = 0;
        while (pending.TryDequeue(out var state))
        {
            var (instruction, copies) = state;
            if (copies == 0) continue;
            if (instruction is null || ++steps > 128 || !visited.Add((instruction, copies))) return false;
            switch (instruction.OpCode.Code)
            {
                case Code.Nop: pending.Enqueue((instruction.Next, copies)); break;
                case Code.Dup: pending.Enqueue((instruction.Next, copies + 1)); break;
                case Code.Pop: pending.Enqueue((instruction.Next, copies - 1)); break;
                case Code.Br: case Code.Br_S: pending.Enqueue(((Instruction)instruction.Operand, copies)); break;
                case Code.Brtrue:
                case Code.Brtrue_S:
                case Code.Brfalse:
                case Code.Brfalse_S:
                    pending.Enqueue(((Instruction)instruction.Operand, copies - 1));
                    pending.Enqueue((instruction.Next, copies - 1));
                    break;
                case Code.Call:
                case Code.Callvirt:
                    if (copies != 1 || instruction.Operand is not MethodReference getter || !ExactKeyMetadataGetter(getter, method.Module)) return false;
                    readsGetter = true;
                    break;
                case Code.Ldnull:
                    if (copies != 1 || instruction.Next?.OpCode.Code != Code.Ceq) return false;
                    break;
                default: return false;
            }
        }
        return true;
    }

    private static bool CarrierMetadataFetch(Instruction instruction) => instruction.OpCode == OpCodes.Callvirt
        && instruction.Operand is GenericInstanceMethod
        { Name: "GetMetadata", HasThis: true, ExplicitThis: false, GenericArguments.Count: 1, Parameters.Count: 0 } method
        && method.DeclaringType is not TypeSpecification && !method.DeclaringType.HasGenericParameters
        && Key(method.DeclaringType) == "Microsoft.AspNetCore.Http.Abstractions:Microsoft.AspNetCore.Http.EndpointMetadataCollection"
        && method.ElementMethod.HasThis && !method.ElementMethod.ExplicitThis
        && method.ElementMethod.CallingConvention == MethodCallingConvention.Generic
        && method.ElementMethod.GenericParameters.Count == 1
        && method.GenericArguments[0].FullName == IdempotentMetadata && Key(method.GenericArguments[0]) == "LearnStack.Api:" + IdempotentMetadata
        && method.ReturnType is GenericParameter { Type: GenericParameterType.Method, Position: 0 } result
        && result.Owner == method.ElementMethod && method.ElementMethod.GenericParameters[0] == result;

    private static bool CarriesKeyMetadata(TypeReference reference) =>
        Il.ExpandedTypeReferences(reference).Any(item => Key(item) == "LearnStack.Api:" + IdempotentMetadata);

    private static bool ExactKeyMetadataGetter(MethodReference target, ModuleDefinition module)
    {
        if (Key(target.DeclaringType) != "LearnStack.Api:" + IdempotentMetadata || target.Name != "get_ProtectedOperation"
            || !target.HasThis || target.ExplicitThis || target.HasGenericParameters || target.Parameters.Count != 0
            || !DescriptorNullable(target.ReturnType, "OperationFor")) return false;
        var attribute = module.GetType(IdempotentMetadata);
        var getter = attribute?.Methods.SingleOrDefault(method => method.Name == "get_ProtectedOperation");
        if (getter is not { HasBody: true, IsPublic: true, IsSpecialName: true, HasThis: true, ExplicitThis: false, HasGenericParameters: false, Parameters.Count: 0 }
            || target.FullName != getter.FullName
            || !SameType(target.ReturnType, getter.ReturnType)) return false;
        var body = getter.Body.Instructions.Where(instruction => instruction.OpCode != OpCodes.Nop).ToArray();
        return body is [{ OpCode.Code: Code.Ldarg_0 }, { OpCode.Code: Code.Ldfld, Operand: FieldReference field }, { OpCode.Code: Code.Ret }]
            && Key(field.DeclaringType) == "LearnStack.Api:" + IdempotentMetadata && field.Name == "<ProtectedOperation>k__BackingField"
            && SameType(field.FieldType, getter.ReturnType);
    }

}
