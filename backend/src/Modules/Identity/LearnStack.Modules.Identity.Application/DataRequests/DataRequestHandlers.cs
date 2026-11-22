using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;

namespace LearnStack.Modules.Identity.Application.DataRequests;

// The live-session request types are thin: each admits its own exact invocation, builds the caller the core serves
// and hands over. The retained-entry types of ADR-0106 § 9 add their own admission over the same cores.

/// <summary>Admits a live-session data-request operation and builds the caller its core serves.</summary>
public sealed class LiveDataRequestAdmission(DataRequestAuthorization authorization, ITenantContext context)
{
    internal async Task<Result<DataRequestCaller>> AdmitAsync(object request, CancellationToken cancellationToken)
    {
        var admitted = await authorization.AdmitAsync(request, cancellationToken).ConfigureAwait(false);
        if (admitted.IsFailure)
        {
            return Result<DataRequestCaller>.Fail(admitted.Error);
        }

        var invocation = admitted.Value;
        var role = DataRequestAuthorization.IsAdministration(request) ? DataRequestRole.Administrator : DataRequestRole.Subject;
        return Result.Ok(new DataRequestCaller(context.UserId!.Value, role,
            token => authorization.RecheckAsync(request, invocation, token)));
    }
}

public sealed class CreateOwnDataExportCommandHandler(LiveDataRequestAdmission admission, DataRequestService service)
    : IRequestHandler<CreateOwnDataExportCommand, Result<DataRequestMutationResult>>
{
    public async Task<Result<DataRequestMutationResult>> Handle(CreateOwnDataExportCommand request, CancellationToken cancellationToken)
    {
        var caller = await admission.AdmitAsync(request, cancellationToken).ConfigureAwait(false);
        return caller.IsFailure ? Result<DataRequestMutationResult>.Fail(caller.Error)
            : await service.CreateExportAsync(caller.Value, caller.Value.Actor, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class ListOwnDataRequestsQueryHandler(LiveDataRequestAdmission admission, DataRequestService service)
    : IRequestHandler<ListOwnDataRequestsQuery, Result<Page<DataRequestView>>>
{
    public async Task<Result<Page<DataRequestView>>> Handle(ListOwnDataRequestsQuery request, CancellationToken cancellationToken)
    {
        var caller = await admission.AdmitAsync(request, cancellationToken).ConfigureAwait(false);
        return caller.IsFailure ? Result<Page<DataRequestView>>.Fail(caller.Error)
            : await service.ListAsync(caller.Value, IdentityOperationPolicySource.OwnDataRequestListOperation, request.Cursor, request.Limit,
                cancellationToken).ConfigureAwait(false);
    }
}

public sealed class GetOwnDataRequestQueryHandler(LiveDataRequestAdmission admission, DataRequestService service)
    : IRequestHandler<GetOwnDataRequestQuery, Result<DataRequestView>>
{
    public async Task<Result<DataRequestView>> Handle(GetOwnDataRequestQuery request, CancellationToken cancellationToken)
    {
        var caller = await admission.AdmitAsync(request, cancellationToken).ConfigureAwait(false);
        return caller.IsFailure ? Result<DataRequestView>.Fail(caller.Error)
            : await service.GetAsync(caller.Value, request.RequestId, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class CancelOwnDataRequestCommandHandler(LiveDataRequestAdmission admission, DataRequestService service)
    : IRequestHandler<CancelOwnDataRequestCommand, Result<DataRequestMutationResult>>
{
    public async Task<Result<DataRequestMutationResult>> Handle(CancelOwnDataRequestCommand request, CancellationToken cancellationToken)
    {
        var caller = await admission.AdmitAsync(request, cancellationToken).ConfigureAwait(false);
        return caller.IsFailure ? Result<DataRequestMutationResult>.Fail(caller.Error)
            : await service.CancelAsync(caller.Value, request.RequestId, request.ExpectedVersion, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class RevokeOwnDataExportCommandHandler(LiveDataRequestAdmission admission, DataRequestService service)
    : IRequestHandler<RevokeOwnDataExportCommand, Result<DataExportArtifactMutationResult>>
{
    public async Task<Result<DataExportArtifactMutationResult>> Handle(RevokeOwnDataExportCommand request, CancellationToken cancellationToken)
    {
        var caller = await admission.AdmitAsync(request, cancellationToken).ConfigureAwait(false);
        return caller.IsFailure ? Result<DataExportArtifactMutationResult>.Fail(caller.Error)
            : await service.RevokeAsync(caller.Value, request.RequestId, request.ExpectedArtifactVersion, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class MintOwnDataExportTicketCommandHandler(LiveDataRequestAdmission admission, DataExportDownloadService service)
    : IRequestHandler<MintOwnDataExportTicketCommand, Result<DataExportTicketGrant>>
{
    public async Task<Result<DataExportTicketGrant>> Handle(MintOwnDataExportTicketCommand request, CancellationToken cancellationToken)
    {
        var caller = await admission.AdmitAsync(request, cancellationToken).ConfigureAwait(false);
        return caller.IsFailure ? Result<DataExportTicketGrant>.Fail(caller.Error)
            : await service.MintTicketAsync(caller.Value, request.RequestId, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class ReadOwnDataExportChunkCommandHandler(LiveDataRequestAdmission admission, DataExportDownloadService service)
    : IRequestHandler<ReadOwnDataExportChunkCommand, Result<DataExportChunkView>>
{
    public async Task<Result<DataExportChunkView>> Handle(ReadOwnDataExportChunkCommand request, CancellationToken cancellationToken)
    {
        var caller = await admission.AdmitAsync(request, cancellationToken).ConfigureAwait(false);
        return caller.IsFailure ? Result<DataExportChunkView>.Fail(caller.Error)
            : await service.ReadChunkAsync(caller.Value, request.RequestId, request.Ticket, request.Index, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class ReadOwnDataRequestReceiptQueryHandler(LiveDataRequestAdmission admission, DataRequestService service)
    : IRequestHandler<ReadOwnDataRequestReceiptQuery, Result<None>>
{
    public async Task<Result<None>> Handle(ReadOwnDataRequestReceiptQuery request, CancellationToken cancellationToken)
    {
        var caller = await admission.AdmitAsync(request, cancellationToken).ConfigureAwait(false);
        return caller.IsFailure ? Result<None>.Fail(caller.Error)
            : await service.ReadReceiptAsync(caller.Value, request.RequestId, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class CreateDataExportForSubjectCommandHandler(LiveDataRequestAdmission admission, DataRequestService service)
    : IRequestHandler<CreateDataExportForSubjectCommand, Result<DataRequestMutationResult>>
{
    public async Task<Result<DataRequestMutationResult>> Handle(CreateDataExportForSubjectCommand request, CancellationToken cancellationToken)
    {
        var caller = await admission.AdmitAsync(request, cancellationToken).ConfigureAwait(false);
        return caller.IsFailure ? Result<DataRequestMutationResult>.Fail(caller.Error)
            : await service.CreateExportForMembershipAsync(caller.Value, request.MembershipId, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class ListDataRequestsQueryHandler(LiveDataRequestAdmission admission, DataRequestService service)
    : IRequestHandler<ListDataRequestsQuery, Result<Page<DataRequestView>>>
{
    public async Task<Result<Page<DataRequestView>>> Handle(ListDataRequestsQuery request, CancellationToken cancellationToken)
    {
        var caller = await admission.AdmitAsync(request, cancellationToken).ConfigureAwait(false);
        return caller.IsFailure ? Result<Page<DataRequestView>>.Fail(caller.Error)
            : await service.ListAsync(caller.Value, IdentityOperationPolicySource.DataRequestListOperation, request.Cursor, request.Limit,
                cancellationToken).ConfigureAwait(false);
    }
}

public sealed class GetDataRequestQueryHandler(LiveDataRequestAdmission admission, DataRequestService service)
    : IRequestHandler<GetDataRequestQuery, Result<DataRequestView>>
{
    public async Task<Result<DataRequestView>> Handle(GetDataRequestQuery request, CancellationToken cancellationToken)
    {
        var caller = await admission.AdmitAsync(request, cancellationToken).ConfigureAwait(false);
        return caller.IsFailure ? Result<DataRequestView>.Fail(caller.Error)
            : await service.GetAsync(caller.Value, request.RequestId, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class CancelDataRequestCommandHandler(LiveDataRequestAdmission admission, DataRequestService service)
    : IRequestHandler<CancelDataRequestCommand, Result<DataRequestMutationResult>>
{
    public async Task<Result<DataRequestMutationResult>> Handle(CancelDataRequestCommand request, CancellationToken cancellationToken)
    {
        var caller = await admission.AdmitAsync(request, cancellationToken).ConfigureAwait(false);
        return caller.IsFailure ? Result<DataRequestMutationResult>.Fail(caller.Error)
            : await service.CancelAsync(caller.Value, request.RequestId, request.ExpectedVersion, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class RevokeDataExportCommandHandler(LiveDataRequestAdmission admission, DataRequestService service)
    : IRequestHandler<RevokeDataExportCommand, Result<DataExportArtifactMutationResult>>
{
    public async Task<Result<DataExportArtifactMutationResult>> Handle(RevokeDataExportCommand request, CancellationToken cancellationToken)
    {
        var caller = await admission.AdmitAsync(request, cancellationToken).ConfigureAwait(false);
        return caller.IsFailure ? Result<DataExportArtifactMutationResult>.Fail(caller.Error)
            : await service.RevokeAsync(caller.Value, request.RequestId, request.ExpectedArtifactVersion, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class ReadDataRequestReceiptQueryHandler(LiveDataRequestAdmission admission, DataRequestService service)
    : IRequestHandler<ReadDataRequestReceiptQuery, Result<None>>
{
    public async Task<Result<None>> Handle(ReadDataRequestReceiptQuery request, CancellationToken cancellationToken)
    {
        var caller = await admission.AdmitAsync(request, cancellationToken).ConfigureAwait(false);
        return caller.IsFailure ? Result<None>.Fail(caller.Error)
            : await service.ReadReceiptAsync(caller.Value, request.RequestId, cancellationToken).ConfigureAwait(false);
    }
}
