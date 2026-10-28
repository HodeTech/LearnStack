using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

internal static partial class EndpointComposition
{
    private const string FixedMetadata = "LearnStack.Api.Authentication.InfrastructureOperationMetadata";
    private const string VersioningOwner = "LearnStack.Api.Versioning.ApiVersioningExtensions";
    private const string ConventionExtensions = "Microsoft.AspNetCore.Builder.RoutingEndpointConventionBuilderExtensions";
    private const string RouteBuilder = "Microsoft.AspNetCore.Builder.RouteHandlerBuilder";
    private const string ConventionBuilder = "Microsoft.AspNetCore.Builder.IEndpointConventionBuilder";

    // This is a data-only attachment to an already enumerated mapping, not a
    // general WithMetadata exception. The receiver and sole array element must
    // come directly from the existing mapping and one exact fixed declaration.
    internal static bool IsFixedInfrastructureMetadata(MethodDefinition owner, Instruction call)
    {
        if (owner.Module.Assembly.Name.Name != "LearnStack.Api" || !owner.HasBody
            || call.OpCode != OpCodes.Call || call.Operand is not GenericInstanceMethod
            { Name: "WithMetadata", GenericArguments.Count: 1, Parameters.Count: 2 } target
            || target.DeclaringType.FullName != ConventionExtensions
            || target.Parameters[1].ParameterType.FullName != "System.Object[]") return false;

        var health = owner.DeclaringType.FullName == "Program" && owner.Name == "<Main>$";
        var versioning = owner.DeclaringType.FullName == VersioningOwner && owner.Name == "MapLearnStackOpenApi";
        if (!(health || versioning) || target.GenericArguments[0].FullName != (health ? RouteBuilder : ConventionBuilder)) return false;
        var attachments = owner.Body.Instructions.Where(instruction => instruction.Operand is MethodReference
        { Name: "WithMetadata", DeclaringType.FullName: ConventionExtensions }).ToArray();
        if (attachments.Length != (health ? 1 : 2)) return false;

        var store = call.Previous;
        var declaration = store?.Previous;
        var index = declaration?.Previous;
        var duplicate = index?.Previous;
        var array = duplicate?.Previous;
        var size = array?.Previous;
        if (store is null || declaration is null || index is null || duplicate is null || array is null || size is null) return false;
        if (store.OpCode != OpCodes.Stelem_Ref || declaration.OpCode != OpCodes.Call
            || declaration.Operand is not MethodReference { DeclaringType.FullName: FixedMetadata, Parameters.Count: 0 } getter
            || getter.ReturnType.FullName != FixedMetadata
            || index.OpCode != OpCodes.Ldc_I4_0 || duplicate.OpCode != OpCodes.Dup
            || array.OpCode != OpCodes.Newarr || array.Operand is not TypeReference { FullName: "System.Object" }
            || size.OpCode != OpCodes.Ldc_I4_1
            || ArgumentProducer(owner, call, 1) != array) return false;
        // No branch may skip element initialization while retaining the array.
        var interior = new[] { array, duplicate, index, declaration, store, call };
        if (owner.Body.Instructions.Any(instruction => instruction.Operand is Instruction branch && interior.Contains(branch)
            || instruction.Operand is Instruction[] branches && branches.Any(interior.Contains))) return false;
        if (owner.Body.Instructions.Count(instruction => instruction.Operand is MethodReference method
                && method.DeclaringType.FullName == FixedMetadata && method.Name == getter.Name) != 1) return false;

        var receiver = ArgumentProducer(owner, call, 0);
        if (health)
            return getter.Name == "get_Health" && IsCall(receiver, ConventionExtensions, "WithName", 2)
                && receiver!.Operand is GenericInstanceMethod { GenericArguments.Count: 1 } name
                && name.GenericArguments[0].FullName == RouteBuilder
                && HasLiteralArgument(owner, receiver, 1, "HealthCheck")
                && ArgumentProducer(owner, receiver, 0) is { } mapping
                && IsCall(mapping, "Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions", "MapGet", 3)
                && HasLiteralPath(owner, mapping, "/healthz");
        return getter.Name switch
        {
            "get_OpenApi" => IsCall(receiver, "Microsoft.AspNetCore.Builder.OpenApiEndpointRouteBuilderExtensions", "MapOpenApi", 2)
                && ArgumentProducer(owner, receiver!, 1) is { } path
                && IsCall(path, "System.String", "Concat", 3)
                && HasLiteralArgument(owner, path, 0, "/openapi/{documentName:regex(^(")
                && HasLiteralArgument(owner, path, 2, ")$)}.json"),
            "get_Documentation" => IsCall(receiver, "Scalar.AspNetCore.ScalarEndpointRouteBuilderExtensions", "MapScalarApiReference", 3)
                && HasLiteralPath(owner, receiver!, "/docs"),
            _ => false,
        };
    }

    private static bool IsCall(Instruction? instruction, string declaringType, string name, int parameters) =>
        instruction is { OpCode.Code: Code.Call, Operand: MethodReference target }
        && target.DeclaringType.FullName == declaringType && target.Name == name && target.Parameters.Count == parameters;
}
