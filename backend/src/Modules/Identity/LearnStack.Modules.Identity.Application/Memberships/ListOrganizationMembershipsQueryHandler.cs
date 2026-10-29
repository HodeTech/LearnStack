using FluentValidation;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;

namespace LearnStack.Modules.Identity.Application.Memberships;

public sealed class ListOrganizationMembershipsQueryHandler(IIdentityAdministrationReadStore reader,
    IdentityAdministrationReadAuthorization authorization, ITenantContext context)
    : IRequestHandler<ListOrganizationMembershipsQuery, Result<Page<MembershipDirectoryItem>>>
{
    public async Task<Result<Page<MembershipDirectoryItem>>> Handle(ListOrganizationMembershipsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var admission = await authorization.AdmitAsync(request, cancellationToken);
        if (admission.IsFailure) return Result<Page<MembershipDirectoryItem>>.Fail(admission.Error);
        if (context.OrganizationId is null) return IdentityAdministrationReadAuthorization.Fail<Page<MembershipDirectoryItem>>("lockey_forbidden");
        if (request.Limit <= 0) return IdentityAdministrationReadAuthorization.Fail<Page<MembershipDirectoryItem>>("lockey_validation_failed");
        var pagination = new CursorPagination(request.Cursor, request.Limit);
        var scope = MembershipDirectoryCursorCodec.Scope(context.UserId!.Value, context.TenantId, context.OrganizationId,
            IdentityOperationPolicySource.OrganizationMembershipDirectoryOperation);
        MembershipId? after = null;
        if (request.Cursor is { } cursor && !MembershipDirectoryCursorCodec.TryDecode(cursor, scope, out after))
            return IdentityAdministrationReadAuthorization.Fail<Page<MembershipDirectoryItem>>("lockey_validation_failed");
        var fetched = await reader.ReadOrganizationMembersAsync(after, pagination.Limit + 1, cancellationToken);
        var check = await authorization.RecheckAsync(request, admission.Value, cancellationToken);
        if (check.IsFailure) return Result<Page<MembershipDirectoryItem>>.Fail(check.Error);
        var rows = fetched.Take(pagination.Limit).ToArray();
        var next = fetched.Count > pagination.Limit && rows.Length != 0
            ? MembershipDirectoryCursorCodec.Encode(scope, MembershipId.From(rows[^1].MembershipId)) : null;
        return Result.Ok(new Page<MembershipDirectoryItem>(rows, new PageInfo(next, null, next is not null, false)));
    }
}

internal sealed class ListOrganizationMembershipsQueryValidator : AbstractValidator<ListOrganizationMembershipsQuery>
{
    public ListOrganizationMembershipsQueryValidator()
    {
        RuleFor(query => query.Limit).GreaterThan(0).WithErrorCode("lockey_invalid_value");
        RuleFor(query => query.Cursor).Must(cursor => cursor is null || MembershipDirectoryCursorCodec.TryDecode(cursor, null, out _))
            .WithErrorCode("lockey_invalid_value");
    }
}
