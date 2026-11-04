using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Identity.Application.Memberships;

public sealed class GetMembershipDetailQueryHandler(IMembershipAdministrationReader reader, IOperationInvocationContext invocations,
    Func<MembershipAdministrationAuthorization> authorization, IAuditSubject subject)
    : IRequestHandler<GetMembershipDetailQuery, Result<MembershipDetailDto>>
{
    public async Task<Result<MembershipDetailDto>> Handle(GetMembershipDetailQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (invocations.CurrentInvocation is not { IsActive: true, EntryKind: OperationEntryKind.Application } invocation
            || !ReferenceEquals(invocation.Operation, request)
            || invocations.CurrentDecision is not { Outcome: ProtectedOperationOutcome.Allowed, IsActive: true } admitted
            || !ReferenceEquals(admitted.Invocation, invocation)) return MembershipAdministrationResult.Fail<MembershipDetailDto>("lockey_forbidden");
        var gate = authorization();
        if (!gate.Matches(request, invocation)) return MembershipAdministrationResult.Fail<MembershipDetailDto>("lockey_forbidden");
        if (!MembershipAdministrationInput.ValidId(request.MembershipId))
            return MembershipAdministrationResult.Fail<MembershipDetailDto>("lockey_validation_failed");
        var member = await reader.ReadDetailAsync(MembershipId.From(request.MembershipId), cancellationToken);
        var check = await gate.RecheckAsync(request, invocation, member, [], cancellationToken);
        if (check.Decision.Outcome != ProtectedOperationOutcome.Allowed)
            return MembershipAdministrationResult.Refused<MembershipDetailDto>(gate, check);
        gate.ReportRecheck(check);
        subject.Designate(member!);
        return Result<MembershipDetailDto>.Ok(new(member!.Id.Value, member.UserId, member.OrganizationId,
            (ManagedMembershipStatus)member.Status, member.Profile.DisplayName, member.Profile.PreferredLocale,
            [.. member.RoleAssignments.Select(assignment => assignment.RoleId.Value).Order()], member.Version));
    }
}

public sealed class GetOwnMembershipProfileQueryHandler(IMembershipAdministrationReader reader, IOperationInvocationContext invocations,
    Func<MembershipAdministrationAuthorization> authorization)
    : IRequestHandler<GetOwnMembershipProfileQuery, Result<OwnMembershipProfileDto>>
{
    public async Task<Result<OwnMembershipProfileDto>> Handle(GetOwnMembershipProfileQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (invocations.CurrentInvocation is not { IsActive: true, EntryKind: OperationEntryKind.Application } invocation
            || !ReferenceEquals(invocation.Operation, request)
            || invocations.CurrentDecision is not { Outcome: ProtectedOperationOutcome.Allowed, IsActive: true } admitted
            || !ReferenceEquals(admitted.Invocation, invocation)) return MembershipAdministrationResult.Fail<OwnMembershipProfileDto>("lockey_forbidden");
        var gate = authorization();
        if (!gate.Matches(request, invocation)) return MembershipAdministrationResult.Fail<OwnMembershipProfileDto>("lockey_forbidden");
        var member = await reader.ReadOwnAsync(cancellationToken);
        var check = await gate.RecheckAsync(request, invocation, member, [], cancellationToken);
        if (check.Decision.Outcome != ProtectedOperationOutcome.Allowed)
            return MembershipAdministrationResult.Refused<OwnMembershipProfileDto>(gate, check);
        gate.ReportRecheck(check);
        return Result<OwnMembershipProfileDto>.Ok(new(member!.Profile.DisplayName, member.Profile.PreferredLocale, member.Version));
    }
}

internal static class MembershipAdministrationResult
{
    internal static Result<T> Refused<T>(MembershipAdministrationAuthorization gate, MembershipAdministrationAssessment check)
    {
        if (check.Decision.Outcome is ProtectedOperationOutcome.NotCovered or ProtectedOperationOutcome.Forbidden) gate.ReportRefusal(check);
        return Fail<T>(check.Decision.Outcome switch
        {
            ProtectedOperationOutcome.NotCovered => "lockey_not_found",
            ProtectedOperationOutcome.Forbidden => "lockey_forbidden",
            _ => "lockey_dependency_unavailable",
        });
    }

    internal static Result<T> Fail<T>(string key) => Result<T>.Fail(new Error(LocalizedMessage.Of(key)));
}
