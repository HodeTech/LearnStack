using FluentAssertions;
using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class AuthenticatedHttpSurfaceTests
{
    private static bool InvitationEntryHandoff(TypeDefinition controller, MethodDefinition action, Endpoint entry)
    {
        if (controller.FullName != "LearnStack.Api.Identity.InvitationEntryController"
            || action.DeclaringType != controller || entry.Controller != controller.FullName) return false;
        var allowed = entry.Request == typeof(AcceptInvitationCommand)
            ? new[] { typeof(AcceptInvitationCommand).FullName!, typeof(GetOwnInvitationResultQuery).FullName! }
            : new[] { entry.Request.FullName! };
        if (allowed.Any(name => name != typeof(InspectInvitationQuery).FullName && name != typeof(AcceptInvitationCommand).FullName
            && name != typeof(GetOwnInvitationResultQuery).FullName)) return false;
        var instructions = ActionBodies(controller, action.Name).SelectMany(method => method.Body.Instructions).ToArray();
        var calls = instructions.Select(item => item.Operand).OfType<MethodReference>().ToArray();
        var selected = instructions.Where(item => item.OpCode == OpCodes.Isinst && item.Operand is TypeReference type
            && type.FullName.StartsWith("LearnStack.Modules.Identity.Application.Contracts.Invitations.", StringComparison.Ordinal))
            .Select(item => ((TypeReference)item.Operand).FullName).ToArray();
        return selected.Order(StringComparer.Ordinal).SequenceEqual(allowed.Order(StringComparer.Ordinal))
            && calls.Count(target => target.DeclaringType.FullName == "MediatR.ISender" && target.Name == "Send") == allowed.Length
            && calls.Count(target => target.DeclaringType.FullName == "LearnStack.Api.Authentication.InvitationEntryRequest" && target.Name == "get_Request") == 1
            && calls.Count(target => target is GenericInstanceMethod generic && generic.Name == "Get"
                && generic.GenericArguments.Count == 1 && generic.GenericArguments[0].FullName == "LearnStack.Api.Authentication.InvitationEntryRequest") == 1
            && !instructions.Any(item => item.OpCode == OpCodes.Newobj && item.Operand is MethodReference target
                && allowed.Contains(target.DeclaringType.FullName, StringComparer.Ordinal));
    }
    [Fact]
    public void Invitation_entry_actual_controller_handoffs_require_exact_selected_types_and_pipeline_dispatch()
    {
        using var module = ModuleDefinition.ReadModule(typeof(LearnStack.Api.Identity.InvitationEntryController).Assembly.Location);
        var controller = module.GetType("LearnStack.Api.Identity.InvitationEntryController");
        foreach (var entry in Endpoints.Where(value => value.Controller == controller.FullName))
        {
            var action = controller.Methods.Single(method => method.Name == entry.Action);
            InvitationEntryHandoff(controller, action, entry).Should().BeTrue();
            var instructions = ActionBodies(controller, action.Name).SelectMany(method => method.Body.Instructions).ToArray();
            foreach (var call in instructions.Where(item => item.Operand is MethodReference target
                && target.DeclaringType.FullName == "MediatR.ISender" && target.Name == "Send"))
            {
                var original = call.OpCode; var operand = call.Operand;
                call.OpCode = OpCodes.Nop; call.Operand = null;
                try { InvitationEntryHandoff(controller, action, entry).Should().BeFalse(); }
                finally { call.OpCode = original; call.Operand = operand; }
            }
            foreach (var selected in instructions.Where(item => item.OpCode == OpCodes.Isinst && item.Operand is TypeReference type
                && type.FullName.StartsWith("LearnStack.Modules.Identity.Application.Contracts.Invitations.", StringComparison.Ordinal)))
            {
                var original = selected.Operand; selected.Operand = module.TypeSystem.Object;
                try { InvitationEntryHandoff(controller, action, entry).Should().BeFalse(); }
                finally { selected.Operand = original; }
            }
        }
    }
}
