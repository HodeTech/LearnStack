using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

public sealed partial class AuthenticatedHttpSurfaceTests
{
    private sealed record ProtectedShape(int Operation, string Name, int Kind, string Target, string? Version);
    private static readonly ProtectedShape[] ProtectedShapes =
    [
        new(1, "SetMembershipProfile", 0, "Membership", "ExpectedVersion"),
        new(2, "SetOwnMembershipProfile", 0, "Membership", "ExpectedVersion"),
        new(3, "ChangeMembershipStatus", 0, "Membership", "ExpectedVersion"),
        new(4, "ReplaceMembershipRoleAssignments", 0, "Membership", "ExpectedVersion"),
        new(5, "ChangeRoleGrants", 1, "Role", "ExpectedRoleVersion"),
        new(6, "AdoptRoleGrantManifest", 1, "Role", "ExpectedRoleVersion"),
        new(7, "CreateOrganization", 2, "Organization", null),
        new(8, "RenameOrganization", 2, "Organization", "ExpectedVersion"),
        new(9, "ArchiveOrganization", 2, "Organization", "ExpectedVersion"),
        new(10, "SelectDefaultOrganization", 3, "Tenant", "ExpectedVersion"),
    ];

    private static readonly Lazy<string[]> ProtectedSourceBaseline = new(() => ProtectedSourceOffenders(
        File.ReadAllText(Path.Combine(RepositoryPaths.BackendSrc(), "LearnStack.Api/Idempotency/ProtectedAdministrationOperation.cs")),
        File.ReadAllText(Path.Combine(RepositoryPaths.BackendSrc(), "LearnStack.Api/Idempotency/ProtectedAdministrationResult.cs"))).ToArray());

    private static IEnumerable<string> DescriptorOffenders(Dictionary<string, TypeDefinition> map)
    {
        foreach (var error in ProtectedSourceBaseline.Value) yield return error;
        if (!map.TryGetValue("LearnStack.Api:" + ProtectedDescriptor, out var descriptor))
        { yield return "Unexpected protected descriptor: missing owner"; yield break; }
        foreach (var error in DescriptorRelationOffenders(descriptor, map)) yield return error;
        var factory = descriptor.Methods.SingleOrDefault(method => method.Name == "For");
        if (factory is not { HasBody: true }) { yield return "Unexpected protected descriptor: missing factory"; yield break; }
        var creations = factory.Body.Instructions.Where(instruction => instruction.Operand is MethodReference target
            && target.DeclaringType.FullName == ProtectedDescriptor && target.Name == ".ctor").ToArray();
        if (creations.Length != 10) yield return "Unexpected protected descriptor: require ten exact factories";
        var requests = new List<string>();
        foreach (var creation in creations)
        {
            var requestProducer = Argument(factory, creation, 1);
            var token = requestProducer?.Previous;
            if (creation.OpCode != OpCodes.Newobj || requestProducer?.Operand is not MethodReference { Name: "GetTypeFromHandle", DeclaringType.FullName: "System.Type" }
                || token is not { OpCode.Code: Code.Ldtoken, Operand: TypeReference request })
            { yield return "Unexpected protected descriptor: require literal request type"; continue; }
            requests.Add(Key(request));
            var endpoint = Endpoints.SingleOrDefault(entry => entry.Request.FullName == request.FullName && entry.ProtectedOperation is not null);
            var shape = ProtectedShapes.SingleOrDefault(item => item.Operation == endpoint?.ProtectedOperation);
            if (shape is null || Integer(Argument(factory, creation, 2)) != shape.Kind
                || Integer(Argument(factory, creation, 3)) != (shape.Operation == 7 ? 1 : 0))
                yield return "Unexpected protected descriptor: target kind/creation mismatch for " + request.FullName;
        }
        var expected = Endpoints.Where(entry => entry.ProtectedOperation is not null)
            .Select(entry => entry.Request.Assembly.GetName().Name + ":" + entry.Request.FullName).Order(StringComparer.Ordinal);
        if (!requests.Order(StringComparer.Ordinal).SequenceEqual(expected, StringComparer.Ordinal))
            yield return "Unexpected protected descriptor: wrong, duplicated or missing request";
        var operationFor = descriptor.Methods.SingleOrDefault(method => method.Name == "OperationFor");
        IEnumerable<string> cases = operationFor is { HasBody: true } ? operationFor.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Isinst)
            .Select(instruction => instruction.Operand).OfType<TypeReference>().Select(Key).Order(StringComparer.Ordinal) : Array.Empty<string>();
        if (!cases.SequenceEqual(expected, StringComparer.Ordinal)) yield return "Unexpected protected descriptor: wrong typed operation cases";
    }

    private static int? Integer(Instruction? instruction) => instruction?.OpCode.Code switch
    {
        Code.Ldc_I4_M1 => -1,
        Code.Ldc_I4_0 => 0,
        Code.Ldc_I4_1 => 1,
        Code.Ldc_I4_2 => 2,
        Code.Ldc_I4_3 => 3,
        Code.Ldc_I4_4 => 4,
        Code.Ldc_I4_5 => 5,
        Code.Ldc_I4_6 => 6,
        Code.Ldc_I4_7 => 7,
        Code.Ldc_I4_8 => 8,
        Code.Ldc_I4 or Code.Ldc_I4_S => Convert.ToInt32(instruction.Operand, System.Globalization.CultureInfo.InvariantCulture),
        _ => null
    };

    // The source relation closes the typed branch-to-enum/result association; IL
    // independently counts actual calls, concrete arguments and descriptor operands.
    // Tokens exclude comments/trivia, never strings containing a claimed relation.
    private static IEnumerable<string> ProtectedSourceOffenders(string descriptorSource, string carrierSource)
    {
        var descriptor = CSharpSyntaxTree.ParseText(descriptorSource).GetRoot();
        var carrier = CSharpSyntaxTree.ParseText(carrierSource).GetRoot();
        foreach (var name in new[] { "For", "OperationFor", "ExpectedVersion" })
        {
            var method = descriptor.DescendantNodes().OfType<MethodDeclarationSyntax>().SingleOrDefault(item => item.Identifier.ValueText == name);
            var expression = method?.ExpressionBody?.Expression as SwitchExpressionSyntax;
            var expected = ProtectedShapes.Select(shape =>
            {
                var request = Endpoints.Single(entry => entry.ProtectedOperation == shape.Operation).Request.Name;
                return name switch
                {
                    "For" => "ProtectedAdministrationOperation." + shape.Name + "=>new(operation,typeof(" + request + "),IdempotencyReplayTargetKind." + shape.Target + (shape.Operation == 7 ? ",true" : "") + ")",
                    "OperationFor" => request + "=>ProtectedAdministrationOperation." + shape.Name,
                    _ => request + (shape.Version is null ? "=>null" : "command=>command." + shape.Version)
                };
            }).ToArray();
            var fallback = name switch
            {
                "For" => "_=>thrownewArgumentOutOfRangeException(nameof(operation))",
                "OperationFor" => "_=>null",
                _ => "_=>thrownewArgumentException(\"The protected operation is not registered.\",nameof(request))"
            };
            if (expression is null || !expression.Arms.Select(Tokens).Order(StringComparer.Ordinal)
                    .SequenceEqual(expected.Append(fallback).Order(StringComparer.Ordinal), StringComparer.Ordinal))
                yield return "Unexpected protected descriptor source: " + name;
        }
        var execute = carrier.DescendantNodes().OfType<MethodDeclarationSyntax>().SingleOrDefault(method => method.Identifier.ValueText == "ExecuteAsync");
        var dispatch = execute?.DescendantNodes().OfType<SwitchExpressionSyntax>().ToArray();
        var branches = ProtectedShapes.Select(shape =>
        {
            var request = Endpoints.Single(entry => entry.ProtectedOperation == shape.Operation).Request.Name;
            var result = shape.Operation switch
            {
                1 or 3 or 4 => "Membership(awaitsender.Send(command,cancellationToken),command.MembershipId)",
                2 => "Membership(awaitsender.Send(command,cancellationToken),null)",
                5 or 6 => "Versioned(awaitsender.Send(command,cancellationToken),command.RoleId)",
                7 => "Organization(awaitsender.Send(command,cancellationToken),null)",
                8 or 9 => "Organization(awaitsender.Send(command,cancellationToken),command.OrganizationId)",
                _ => "Versioned(awaitsender.Send(command,cancellationToken),context.TenantId.Value)"
            };
            return request + "command=>" + result;
        }).Append("_=>thrownewInvalidOperationException(\"The protected operation is not registered.\")");
        if (dispatch is not { Length: 1 } || Tokens(dispatch[0].GoverningExpression) != "request"
            || !dispatch[0].Arms.Select(Tokens).Order(StringComparer.Ordinal).SequenceEqual(branches.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            yield return "Unexpected protected typed dispatcher source: bind each exact typed request to its immutable target";

        static string Tokens(SyntaxNode node) => string.Concat(node.DescendantTokens().Select(token => token.Text));
    }
}
