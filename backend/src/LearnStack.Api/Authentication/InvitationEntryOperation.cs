using LearnStack.Api.Identity;
using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.SharedKernel.Persistence;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace LearnStack.Api.Authentication;

/// <summary>The three exact recipient purposes; ordinary native proxy admission is unchanged.</summary>
internal sealed class InvitationEntryOperation
{
    private InvitationEntryOperation(string path, Type requestType, string action, TransactionMode mode)
    { Path = path; RequestType = requestType; Action = action; Mode = mode; }
    internal string Path { get; }
    internal Type RequestType { get; }
    internal string Action { get; }
    internal TransactionMode Mode { get; }
    private static readonly InvitationEntryOperation Inspect = new("/api/v1/identity/invitation-entry/inspections", typeof(InspectInvitationQuery), nameof(InvitationEntryController.Inspect), TransactionMode.ReadOnly);
    private static readonly InvitationEntryOperation Accept = new("/api/v1/identity/invitation-entry/acceptances", typeof(AcceptInvitationCommand), nameof(InvitationEntryController.Accept), TransactionMode.ReadWrite);
    private static readonly InvitationEntryOperation Result = new("/api/v1/identity/invitation-entry/results", typeof(GetOwnInvitationResultQuery), nameof(InvitationEntryController.Result), TransactionMode.ReadOnly);
    internal static InvitationEntryOperation? ForPath(PathString path) => path.Value switch
    {
        "/api/v1/identity/invitation-entry/inspections" => Inspect,
        "/api/v1/identity/invitation-entry/acceptances" => Accept,
        "/api/v1/identity/invitation-entry/results" => Result,
        _ => null
    };
    internal bool Matches(HttpContext http) => HttpMethods.IsPost(http.Request.Method) && ForPath(http.Request.Path) == this
        && http.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>() is { } action
        && action.ControllerTypeInfo.Assembly == typeof(InvitationEntryOperation).Assembly
        && action.ControllerTypeInfo.FullName == "LearnStack.Api.Identity.InvitationEntryController"
        && action.MethodInfo.Name == Action && action.MethodInfo.DeclaringType == action.ControllerTypeInfo.AsType()
        && http.GetEndpoint()?.Metadata.GetMetadata<OperationPolicyAttribute>()?.RequestType == RequestType;
}
