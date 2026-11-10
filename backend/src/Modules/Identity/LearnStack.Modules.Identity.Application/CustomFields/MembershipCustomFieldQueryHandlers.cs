using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.CustomFields;
using LearnStack.Modules.Identity.Application.Memberships;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Identity.Application.CustomFields;

public sealed class GetMembershipCustomFieldsQueryHandler(MembershipCustomFieldQueryExecutor executor)
    : IRequestHandler<GetMembershipCustomFieldsQuery, Result<MembershipCustomFieldsDto>>
{
    public Task<Result<MembershipCustomFieldsDto>> Handle(GetMembershipCustomFieldsQuery request, CancellationToken cancellationToken) =>
        executor.ExecuteAsync(request, cancellationToken);
}
public sealed class GetOwnMembershipCustomFieldsQueryHandler(MembershipCustomFieldQueryExecutor executor)
    : IRequestHandler<GetOwnMembershipCustomFieldsQuery, Result<MembershipCustomFieldsDto>>
{
    public Task<Result<MembershipCustomFieldsDto>> Handle(GetOwnMembershipCustomFieldsQuery request, CancellationToken cancellationToken) =>
        executor.ExecuteAsync(request, cancellationToken);
}

public sealed class MembershipCustomFieldQueryExecutor(IMembershipAdministrationReader reader,
    IOperationInvocationContext invocations, Func<MembershipAdministrationAuthorization> authorization,
    MembershipCustomFieldService fields, IAuditSubject subject)
{
    public async Task<Result<MembershipCustomFieldsDto>> ExecuteAsync(object request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (invocations.CurrentInvocation is not { IsActive: true, EntryKind: OperationEntryKind.Application } invocation
            || !ReferenceEquals(invocation.Operation, request)
            || invocations.CurrentDecision is not { Outcome: ProtectedOperationOutcome.Allowed, IsActive: true } admitted
            || !ReferenceEquals(admitted.Invocation, invocation)) return MembershipAdministrationResult.Fail<MembershipCustomFieldsDto>("lockey_forbidden");
        var gate = authorization();
        if (!gate.Matches(request, invocation)) return MembershipAdministrationResult.Fail<MembershipCustomFieldsDto>("lockey_forbidden");
        if (request is GetMembershipCustomFieldsQuery query && !MembershipCustomFieldInput.ValidId(query.MembershipId))
            return MembershipAdministrationResult.Fail<MembershipCustomFieldsDto>("lockey_validation_failed");
        var member = request is GetMembershipCustomFieldsQuery target
            ? await reader.ReadDetailAsync(MembershipId.From(target.MembershipId), cancellationToken)
            : await reader.ReadOwnAsync(cancellationToken);
        var check = await gate.RecheckAsync(request, invocation, member, [], cancellationToken);
        if (check.Decision.Outcome != ProtectedOperationOutcome.Allowed)
            return MembershipAdministrationResult.Refused<MembershipCustomFieldsDto>(gate, check);
        var response = await fields.DiscloseAsync(member!, cancellationToken);
        check = await gate.RecheckAsync(request, invocation, member, [], cancellationToken);
        if (check.Decision.Outcome != ProtectedOperationOutcome.Allowed)
            return MembershipAdministrationResult.Refused<MembershipCustomFieldsDto>(gate, check);
        gate.ReportRecheck(check);
        if (request is GetMembershipCustomFieldsQuery) subject.Designate(member!);
        return response;
    }
}
