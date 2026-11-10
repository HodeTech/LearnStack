using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

public sealed partial class AuthenticatedHttpSurfaceTests
{
    private static bool ConsentSubjectHandoff(TypeDefinition controller, MethodDefinition action, Endpoint endpoint)
    {
        if (controller.FullName != "LearnStack.Api.Tenancy.ConsentSubjectController"
            || action.DeclaringType != controller || endpoint.Controller != controller.FullName
            || endpoint.Request != (action.Name switch
            {
                "Scopes" => typeof(ListOwnFieldConsentScopesQuery),
                "History" => typeof(ListOwnMembershipFieldConsentsQuery),
                "Withdraw" => typeof(WithdrawOwnMembershipFieldConsentCommand),
                _ => null,
            })) return false;
        var instructions = ActionBodies(controller, action.Name).SelectMany(method => method.Body.Instructions).ToArray();
        var calls = instructions.Select(item => item.Operand).OfType<MethodReference>().ToArray();
        if (instructions.Count(item => item.OpCode == OpCodes.Isinst && item.Operand is TypeReference type
                && type.FullName == endpoint.Request.FullName) != 1
            || calls.Count(target => target.DeclaringType.FullName == "LearnStack.Api.Authentication.ConsentSubjectRequest" && target.Name == "get_Request") != 1
            || calls.Count(target => target is GenericInstanceMethod generic && generic.Name == "Get" && generic.GenericArguments.Count == 1
                && generic.GenericArguments[0].FullName == "LearnStack.Api.Authentication.ConsentSubjectRequest") != 1
            || instructions.Any(item => item.OpCode == OpCodes.Newobj && item.Operand is MethodReference target
                && target.DeclaringType.FullName == endpoint.Request.FullName)) return false;
        return action.Name == "Withdraw"
            ? calls.Count(HandoffSignature) == 1 && calls.All(target => target.DeclaringType.FullName != Sender)
            : calls.Count(target => target.DeclaringType.FullName == Sender && target.Name == "Send") == 1
                && calls.All(target => target.DeclaringType.FullName != ProtectedResult);
    }

    private static bool CustomFieldTransitionHandoff(TypeDefinition controller, MethodDefinition action, Endpoint endpoint)
    {
        if (controller.FullName != "LearnStack.Api.Customization.CustomFieldDefinitionsController"
            || endpoint.Controller != controller.FullName || action.Name is not ("Retire" or "Discard")) return false;
        var actionInstructions = ActionBodies(controller, action.Name).SelectMany(method => method.Body.Instructions).ToArray();
        var helperCalls = actionInstructions.Where(instruction => instruction.Operand is MethodReference target
            && target.DeclaringType.FullName == controller.FullName && target.Name == "Transition").ToArray();
        if (helperCalls.Length != 1 || !actionInstructions.Any(instruction => instruction.OpCode == (action.Name == "Discard" ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0))) return false;
        var helper = ActionBodies(controller, "Transition").SelectMany(method => method.Body.Instructions).ToArray();
        var constructions = helper.Where(instruction => instruction.OpCode == OpCodes.Newobj && instruction.Operand is MethodReference target
            && target.DeclaringType.Namespace == "LearnStack.Modules.Customization.Application.Contracts.CustomFields")
            .Select(instruction => ((MethodReference)instruction.Operand).DeclaringType.FullName).Order(StringComparer.Ordinal).ToArray();
        string[] expected = [typeof(LearnStack.Modules.Customization.Application.Contracts.CustomFields.DiscardCustomFieldDefinitionDraftCommand).FullName!,
            typeof(LearnStack.Modules.Customization.Application.Contracts.CustomFields.RetireCustomFieldDefinitionCommand).FullName!];
        return constructions.SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal)
            && helper.Count(instruction => instruction.Operand is MethodReference target && HandoffSignature(target)) == 1
            && (action.Name == "Retire" ? endpoint.Request == typeof(LearnStack.Modules.Customization.Application.Contracts.CustomFields.RetireCustomFieldDefinitionCommand)
                : endpoint.Request == typeof(LearnStack.Modules.Customization.Application.Contracts.CustomFields.DiscardCustomFieldDefinitionDraftCommand));
    }
}
