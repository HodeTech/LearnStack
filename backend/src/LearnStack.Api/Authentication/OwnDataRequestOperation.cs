using LearnStack.Api.Identity;
using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.SharedKernel.Identity;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace LearnStack.Api.Authentication;

/// <summary>
/// The closed HTTP purposes of the retained own data-request scope (ADR-0106 § 9): exactly nine selector-free native
/// entry operations, each one exact path, method, action and request type. Any other path is not this entry.
/// </summary>
internal sealed class OwnDataRequestOperation(string path, string method, string action, Type requestType, OwnDataRequestPurpose purpose)
{
    internal const string Root = "/api/v1/identity/data-request-entry";
    internal Type RequestType { get; } = requestType;
    internal OwnDataRequestPurpose Purpose { get; } = purpose;
    private static readonly OwnDataRequestOperation Scopes = new(Root + "/scopes", "GET", nameof(DataRequestEntryController.Scopes), typeof(ListRetainedDataRequestScopesQuery), OwnDataRequestPurpose.List);
    private static readonly OwnDataRequestOperation Requests = new(Root + "/requests", "GET", nameof(DataRequestEntryController.Requests), typeof(ListRetainedDataRequestsQuery), OwnDataRequestPurpose.Read);
    private static readonly OwnDataRequestOperation Exports = new(Root + "/exports", "POST", nameof(DataRequestEntryController.CreateExport), typeof(CreateRetainedDataExportCommand), OwnDataRequestPurpose.Create);
    private static readonly OwnDataRequestOperation Erasures = new(Root + "/erasures", "POST", nameof(DataRequestEntryController.CreateErasure), typeof(CreateRetainedDataErasureCommand), OwnDataRequestPurpose.Create);
    private static readonly OwnDataRequestOperation Closures = new(Root + "/closures", "POST", nameof(DataRequestEntryController.CreateClosure), typeof(CreateRetainedAccountClosureCommand), OwnDataRequestPurpose.Create);
    private static readonly OwnDataRequestOperation Revocations = new(Root + "/revocations", "POST", nameof(DataRequestEntryController.RevokeExport), typeof(RevokeRetainedDataExportCommand), OwnDataRequestPurpose.Revoke);
    private static readonly OwnDataRequestOperation Cancellations = new(Root + "/cancellations", "POST", nameof(DataRequestEntryController.Cancel), typeof(CancelRetainedDataRequestCommand), OwnDataRequestPurpose.Cancel);
    private static readonly OwnDataRequestOperation Tickets = new(Root + "/tickets", "POST", nameof(DataRequestEntryController.MintTicket), typeof(MintRetainedDataExportTicketCommand), OwnDataRequestPurpose.Ticket);
    private static readonly OwnDataRequestOperation Chunks = new(Root + "/chunks", "POST", nameof(DataRequestEntryController.ReadChunk), typeof(ReadRetainedDataExportChunkCommand), OwnDataRequestPurpose.Chunk);

    internal static OwnDataRequestOperation? ForPath(PathString path) => path.Value switch
    {
        Root + "/scopes" => Scopes,
        Root + "/requests" => Requests,
        Root + "/exports" => Exports,
        Root + "/erasures" => Erasures,
        Root + "/closures" => Closures,
        Root + "/cancellations" => Cancellations,
        Root + "/revocations" => Revocations,
        Root + "/tickets" => Tickets,
        Root + "/chunks" => Chunks,
        _ => null
    };

    internal bool Matches(HttpContext http) => http.Request.Path == path && http.Request.Method == method
        && ReferenceEquals(ForPath(http.Request.Path), this)
        && http.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>() is { } descriptor
        && descriptor.ControllerTypeInfo.Assembly == typeof(OwnDataRequestOperation).Assembly
        && descriptor.ControllerTypeInfo.FullName == "LearnStack.Api.Identity.DataRequestEntryController"
        && descriptor.MethodInfo.DeclaringType == descriptor.ControllerTypeInfo.AsType() && descriptor.MethodInfo.Name == action
        && http.GetEndpoint()?.Metadata.GetMetadata<OperationPolicyAttribute>()?.RequestType == RequestType;

    public override string ToString() => nameof(OwnDataRequestOperation);
}
