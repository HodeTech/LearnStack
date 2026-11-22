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

    private const string DataRequestEntryController = "LearnStack.Api.Identity.DataRequestEntryController";

    /// <summary>
    /// ADR-0106 § 9: like the consent subject, each retained data-request entry action sends exactly the request its
    /// source captured before authorization, read once from its request feature, and never constructs one. The keyed
    /// purposes hand off to the protected carrier; the rest send once.
    /// </summary>
    private static bool DataRequestEntryHandoff(TypeDefinition controller, MethodDefinition action, Endpoint endpoint)
    {
        const string Feature = "LearnStack.Api.Authentication.OwnDataRequestEntryRequest";
        if (controller.FullName != DataRequestEntryController
            || action.DeclaringType != controller || endpoint.Controller != controller.FullName
            || endpoint.Request != (action.Name switch
            {
                "Scopes" => typeof(LearnStack.Modules.Identity.Application.Contracts.DataRequests.ListRetainedDataRequestScopesQuery),
                "Requests" => typeof(LearnStack.Modules.Identity.Application.Contracts.DataRequests.ListRetainedDataRequestsQuery),
                "CreateExport" => typeof(LearnStack.Modules.Identity.Application.Contracts.DataRequests.CreateRetainedDataExportCommand),
                "CreateErasure" => typeof(LearnStack.Modules.Identity.Application.Contracts.DataRequests.CreateRetainedDataErasureCommand),
                "Cancel" => typeof(LearnStack.Modules.Identity.Application.Contracts.DataRequests.CancelRetainedDataRequestCommand),
                "RevokeExport" => typeof(LearnStack.Modules.Identity.Application.Contracts.DataRequests.RevokeRetainedDataExportCommand),
                "MintTicket" => typeof(LearnStack.Modules.Identity.Application.Contracts.DataRequests.MintRetainedDataExportTicketCommand),
                "ReadChunk" => typeof(LearnStack.Modules.Identity.Application.Contracts.DataRequests.ReadRetainedDataExportChunkCommand),
                _ => null,
            })) return false;
        var instructions = ActionBodies(controller, action.Name).SelectMany(method => method.Body.Instructions).ToArray();
        var calls = instructions.Select(item => item.Operand).OfType<MethodReference>().ToArray();
        if (instructions.Count(item => item.OpCode == OpCodes.Isinst && item.Operand is TypeReference type
                && type.FullName == endpoint.Request.FullName) != 1
            || calls.Count(target => target.DeclaringType.FullName == Feature && target.Name == "get_Request") != 1
            || calls.Count(target => target is GenericInstanceMethod generic && generic.Name == "Get" && generic.GenericArguments.Count == 1
                && generic.GenericArguments[0].FullName == Feature) != 1
            || instructions.Any(item => item.OpCode == OpCodes.Newobj && item.Operand is MethodReference target
                && target.DeclaringType.Namespace == endpoint.Request.Namespace)) return false;
        return endpoint.ProtectedOperation is not null
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
