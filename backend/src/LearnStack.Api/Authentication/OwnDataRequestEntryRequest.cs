using LearnStack.Modules.Identity.Application.Contracts.DataRequests;

namespace LearnStack.Api.Authentication;

/// <summary>One selected immutable retained-entry request; this feature grants no scope or persistence authority.</summary>
internal sealed class OwnDataRequestEntryRequest
{
    internal OwnDataRequestEntryRequest(object request)
    {
        if (request is not (ListRetainedDataRequestScopesQuery or ListRetainedDataRequestsQuery or CreateRetainedDataExportCommand
            or CreateRetainedDataErasureCommand or CancelRetainedDataRequestCommand or RevokeRetainedDataExportCommand
            or MintRetainedDataExportTicketCommand or ReadRetainedDataExportChunkCommand or ReadRetainedDataRequestReceiptQuery))
            throw new ArgumentException("Data request entry request refused.");
        Request = request;
    }
    internal object Request { get; }
    public override string ToString() => nameof(OwnDataRequestEntryRequest);
}
