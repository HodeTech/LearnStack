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
        new(11, "CreateInvitation", 4, "Invitation", null),
        new(12, "RevokeInvitation", 4, "Invitation", "ExpectedVersion"),
        new(13, "ReissueInvitation", 4, "Invitation", "ExpectedVersion"),
        new(14, "SetTenantInvitationLifetime", 5, "TenantSetting", "ExpectedVersion"),
        new(15, "RegisterCustomFieldDefinition", 6, "CustomFieldDefinition", null),
        new(16, "ReviseCustomFieldDraft", 6, "CustomFieldDefinition", "ExpectedVersion"),
        new(17, "PublishCustomFieldRevision", 6, "CustomFieldDefinition", "ExpectedVersion"),
        new(18, "RetireCustomFieldRevision", 6, "CustomFieldDefinition", "ExpectedVersion"),
        new(19, "DiscardCustomFieldDraft", 6, "CustomFieldDefinition", "ExpectedVersion"),
        new(20, "SetMemberCustomFields", 0, "Membership", "ExpectedVersion"),
        new(21, "SetOwnCustomFields", 0, "Membership", "ExpectedVersion"),
        new(22, "GrantOwnFieldConsent", 7, "MembershipFieldConsent", null),
        new(23, "WithdrawOwnFieldConsent", 7, "MembershipFieldConsent", null),
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
        if (creations.Length != ProtectedShapes.Length) yield return "Unexpected protected descriptor: require fourteen exact factories";
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
                || Integer(Argument(factory, creation, 3)) != (shape.Operation is 7 or 11 or 15 ? 1 : 0))
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
                    "For" => "ProtectedAdministrationOperation." + shape.Name + "=>new(operation,typeof(" + request + "),IdempotencyReplayTargetKind." + shape.Target + (shape.Operation is 7 or 11 or 15 ? ",true" : "") + ")",
                    "OperationFor" => request + "=>ProtectedAdministrationOperation." + shape.Name,
                    _ => request + (shape.Version is null ? "=>null" : "command=>command." + shape.Version)
                };
            }).ToArray();
            if (name == "ExpectedVersion") expected = expected.Where(arm => arm is not ("CreateManagedOrganizationCommand=>null" or "CreateInvitationCommand=>null"))
                .Where(arm => arm is not ("RegisterCustomFieldDefinitionCommand=>null" or "GrantOwnMembershipFieldConsentCommand=>null" or "WithdrawOwnMembershipFieldConsentCommand=>null"))
                .Append("CreateManagedOrganizationCommandorCreateInvitationCommand=>null")
                .Append("RegisterCustomFieldDefinitionCommandorGrantOwnMembershipFieldConsentCommandorWithdrawOwnMembershipFieldConsentCommand=>null").ToArray();
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
                10 => "Versioned(awaitsender.Send(command,cancellationToken),context.TenantId.Value)",
                11 => "Invitation(awaitsender.Send(command,cancellationToken),null)",
                12 or 13 => "Invitation(awaitsender.Send(command,cancellationToken),command.InvitationId)",
                14 => "Policy(awaitsender.Send(command,cancellationToken))",
                15 or 16 or 17 or 18 or 19 => "Definition(awaitsender.Send(command,cancellationToken),command.DefinitionId)",
                20 => "Membership(awaitsender.Send(command,cancellationToken),command.MembershipId)",
                21 => "Membership(awaitsender.Send(command,cancellationToken),null)",
                22 => "Consent(awaitsender.Send(command,cancellationToken),true)",
                23 => "Consent(awaitsender.Send(command,cancellationToken),false)",
                _ => throw new InvalidOperationException("Unknown expected protected operation")
            };
            return request + "command=>" + result;
        }).Append("_=>thrownewInvalidOperationException(\"The protected operation is not registered.\")");
        if (dispatch is not { Length: 1 } || Tokens(dispatch[0].GoverningExpression) != "request"
            || !dispatch[0].Arms.Select(Tokens).Order(StringComparer.Ordinal).SequenceEqual(branches.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            yield return "Unexpected protected typed dispatcher source: bind each exact typed request to its immutable target";

        static string Tokens(SyntaxNode node) => string.Concat(node.DescendantTokens().Select(token => token.Text));
    }
}
