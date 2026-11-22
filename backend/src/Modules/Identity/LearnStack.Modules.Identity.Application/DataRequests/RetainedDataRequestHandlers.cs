using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;

namespace LearnStack.Modules.Identity.Application.DataRequests;

// The retained own-scope request types of ADR-0106 § 9 are as thin as the live ones: each consumes the exact live
// witness its own invocation was admitted with, builds the subject's caller and hands over to the same core. No
// Membership read admits them, so a Suspended person, a person whose Membership was revoked and a group where only a
// request survives are served alike; the cores still confine every row to the announced group and to the subject.

/// <summary>
/// Consumes only the exact live retained own data-request scope witness on its physical owner
/// (<see href="../../../../../docs/decisions/0106-subject-data-requests-authority-and-status.md">ADR-0106 § 9</see>),
/// as <c>OwnConsentEntryAuthorization</c> does for the consent subject.
/// </summary>
/// <remarks>
/// The witness was minted by the one HTTP source for this invocation and purpose, after the confined definer proved
/// the group for the verified subject on the real host. Nothing here re-reads the scope: the recheck the cores run
/// after their fences confirms the same witness, decision, owner and context are still live, so a closed request,
/// a replaced feature or a retargeted owner refuses the act.
/// </remarks>
public sealed class RetainedDataRequestAdmission(IOperationInvocationContext invocations, IOperationPolicyCatalog catalog,
    IUnitOfWork unit, ITenantContext context)
{
    internal OwnDataRequestScopeWitness? For(object request, params OwnDataRequestPurpose[] purposes)
    {
        if (request is not (ListRetainedDataRequestScopesQuery or ListRetainedDataRequestsQuery or CreateRetainedDataExportCommand
                or CancelRetainedDataRequestCommand or MintRetainedDataExportTicketCommand or ReadRetainedDataExportChunkCommand
                or ReadRetainedDataRequestReceiptQuery)
            || invocations.CurrentInvocation is not { IsActive: true, EntryKind: OperationEntryKind.Application, TrustedSource: null } invocation
            || !ReferenceEquals(invocation.Operation, request) || !ReferenceEquals(invocation.Context, context)
            || !catalog.TryGet(request.GetType(), out var policy) || !ReferenceEquals(policy, invocation.Policy)
            || policy.Kind != OperationPolicyKind.OwnDataRequestEntry
            || invocation.Candidate is not { IsActive: true, SourceKind: AuthorizationSourceKind.Http } candidate
            || invocations.CurrentDecision is not { IsActive: true, Outcome: ProtectedOperationOutcome.Allowed, DataRequestWitness: { } witness } decision
            || !ReferenceEquals(decision.Invocation, invocation) || !ReferenceEquals(witness.Invocation, invocation) || !witness.IsActive
            || !purposes.Contains(witness.Purpose)
            || unit is not IOwnDataRequestUnitOfWork owner || !owner.IsOwnDataRequestOwner(witness)
            || !context.IsResolved || context.Origin != TenantContextOrigin.HostAndOwnDataRequestScope
            || context.UserId != witness.Actor || context.TenantId != witness.TenantId || context.OrganizationId != witness.OrganizationId
            || candidate.Actor != witness.Actor || candidate.TenantId != witness.TenantId || candidate.OrganizationId != witness.OrganizationId)
            return null;
        return witness;
    }

    /// <summary>
    /// The subject's caller for one exact retained group: <paramref name="organization"/> is the group the request type
    /// selected, which must be the very group the witness proved.
    /// </summary>
    internal Result<DataRequestCaller> Admit(object request, OrganizationId? organization, params OwnDataRequestPurpose[] purposes)
    {
        var witness = For(request, purposes);
        if (witness?.Group is not { } group || group.TenantId != context.TenantId || group.OrganizationId != organization)
            return DataRequestService.Fail<DataRequestCaller>("lockey_forbidden");
        return Result.Ok(new DataRequestCaller(witness.Actor, DataRequestRole.Subject, token =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(ReferenceEquals(For(request, purposes), witness)
                ? Result.Ok(None.Value) : DataRequestService.Fail<None>("lockey_forbidden"));
        }));
    }

    /// <summary>The group a receipt read runs in is the witness's own, whichever keyed purpose it came from.</summary>
    internal Result<DataRequestCaller> AdmitReceipt(ReadRetainedDataRequestReceiptQuery request) =>
        For(request, OwnDataRequestPurpose.Create, OwnDataRequestPurpose.Cancel) is { Group: { } group }
            ? Admit(request, group.OrganizationId, OwnDataRequestPurpose.Create, OwnDataRequestPurpose.Cancel)
            : DataRequestService.Fail<DataRequestCaller>("lockey_forbidden");
}

/// <summary>Returns the groups the definer proved for the subject on the real host; it reads nothing more.</summary>
public sealed class ListRetainedDataRequestScopesQueryHandler(RetainedDataRequestAdmission admission)
    : IRequestHandler<ListRetainedDataRequestScopesQuery, Result<RetainedDataRequestScopePage>>
{
    public Task<Result<RetainedDataRequestScopePage>> Handle(ListRetainedDataRequestScopesQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Limit is < 1 or > OwnDataRequestScopeWitness.MaximumPage || request.Cursor is { Length: > 64 })
            return Task.FromResult(DataRequestService.Fail<RetainedDataRequestScopePage>("lockey_validation_failed"));
        var witness = admission.For(request, OwnDataRequestPurpose.List);
        if (witness?.ScopePage is not { } page || page.Items.Length > request.Limit)
            return Task.FromResult(DataRequestService.Fail<RetainedDataRequestScopePage>("lockey_forbidden"));
        var items = page.Items.ToArray();
        if (!ReferenceEquals(admission.For(request, OwnDataRequestPurpose.List), witness))
            return Task.FromResult(DataRequestService.Fail<RetainedDataRequestScopePage>("lockey_forbidden"));
        return Task.FromResult(Result.Ok(new RetainedDataRequestScopePage(items, page.NextCursor)));
    }
}

public sealed class ListRetainedDataRequestsQueryHandler(RetainedDataRequestAdmission admission, DataRequestService service)
    : IRequestHandler<ListRetainedDataRequestsQuery, Result<Page<DataRequestView>>>
{
    public async Task<Result<Page<DataRequestView>>> Handle(ListRetainedDataRequestsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Limit is < 1 or > OwnDataRequestScopeWitness.MaximumPage)
            return DataRequestService.Fail<Page<DataRequestView>>("lockey_validation_failed");
        var caller = admission.Admit(request, request.OrganizationId, OwnDataRequestPurpose.Read);
        return caller.IsFailure ? Result<Page<DataRequestView>>.Fail(caller.Error)
            : await service.ListAsync(caller.Value, IdentityOperationPolicySource.RetainedDataRequestListOperation, request.Cursor,
                request.Limit, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class CreateRetainedDataExportCommandHandler(RetainedDataRequestAdmission admission, DataRequestService service)
    : IRequestHandler<CreateRetainedDataExportCommand, Result<DataRequestMutationResult>>
{
    public async Task<Result<DataRequestMutationResult>> Handle(CreateRetainedDataExportCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var caller = admission.Admit(request, request.OrganizationId, OwnDataRequestPurpose.Create);
        return caller.IsFailure ? Result<DataRequestMutationResult>.Fail(caller.Error)
            : await service.CreateExportAsync(caller.Value, caller.Value.Actor, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class CancelRetainedDataRequestCommandHandler(RetainedDataRequestAdmission admission, DataRequestService service)
    : IRequestHandler<CancelRetainedDataRequestCommand, Result<DataRequestMutationResult>>
{
    public async Task<Result<DataRequestMutationResult>> Handle(CancelRetainedDataRequestCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var caller = admission.Admit(request, request.OrganizationId, OwnDataRequestPurpose.Cancel);
        return caller.IsFailure ? Result<DataRequestMutationResult>.Fail(caller.Error)
            : await service.CancelAsync(caller.Value, request.RequestId, request.ExpectedVersion, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class MintRetainedDataExportTicketCommandHandler(RetainedDataRequestAdmission admission, DataExportDownloadService service)
    : IRequestHandler<MintRetainedDataExportTicketCommand, Result<DataExportTicketGrant>>
{
    public async Task<Result<DataExportTicketGrant>> Handle(MintRetainedDataExportTicketCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var caller = admission.Admit(request, request.OrganizationId, OwnDataRequestPurpose.Ticket);
        return caller.IsFailure ? Result<DataExportTicketGrant>.Fail(caller.Error)
            : await service.MintTicketAsync(caller.Value, request.RequestId, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class ReadRetainedDataExportChunkCommandHandler(RetainedDataRequestAdmission admission, DataExportDownloadService service)
    : IRequestHandler<ReadRetainedDataExportChunkCommand, Result<DataExportChunkView>>
{
    public async Task<Result<DataExportChunkView>> Handle(ReadRetainedDataExportChunkCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var caller = admission.Admit(request, request.OrganizationId, OwnDataRequestPurpose.Chunk);
        return caller.IsFailure ? Result<DataExportChunkView>.Fail(caller.Error)
            : await service.ReadChunkAsync(caller.Value, request.RequestId, request.Ticket, request.Index, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class ReadRetainedDataRequestReceiptQueryHandler(RetainedDataRequestAdmission admission, DataRequestService service)
    : IRequestHandler<ReadRetainedDataRequestReceiptQuery, Result<None>>
{
    public async Task<Result<None>> Handle(ReadRetainedDataRequestReceiptQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var caller = admission.AdmitReceipt(request);
        return caller.IsFailure ? Result<None>.Fail(caller.Error)
            : await service.ReadReceiptAsync(caller.Value, request.RequestId, cancellationToken).ConfigureAwait(false);
    }
}
