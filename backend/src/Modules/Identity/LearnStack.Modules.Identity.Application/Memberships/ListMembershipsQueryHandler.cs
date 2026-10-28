using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;

namespace LearnStack.Modules.Identity.Application.Memberships;

public sealed class ListMembershipsQueryHandler(IMembershipDirectoryReader reader, ITenantContext context)
    : IRequestHandler<ListMembershipsQuery, Result<Page<MembershipDirectoryItem>>>
{
    public async Task<Result<Page<MembershipDirectoryItem>>> Handle(ListMembershipsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!context.IsResolved || context.TenantId == TenantId.PlatformSentinel
            || context.UserId is not { } actor || actor == UserId.SystemActor)
            return Result<Page<MembershipDirectoryItem>>.Fail(new Error(LocalizedMessage.Of("lockey_not_found")));
        if (request.Limit <= 0) return Invalid("limit");
        var pagination = new CursorPagination(request.Cursor, request.Limit);
        var scope = MembershipDirectoryCursorCodec.Scope(actor, context.TenantId, context.OrganizationId);
        MembershipId? after = null;
        if (pagination.Cursor is { } cursor && !MembershipDirectoryCursorCodec.TryDecode(cursor, scope, out after))
            return Invalid("cursor");
        var fetched = await reader.ReadAsync(after, pagination.Limit + 1, cancellationToken);
        var rows = fetched.Take(pagination.Limit).ToArray();
        var next = fetched.Count > pagination.Limit && rows.Length != 0
            ? MembershipDirectoryCursorCodec.Encode(scope, MembershipId.From(rows[^1].MembershipId)) : null;
        return Result<Page<MembershipDirectoryItem>>.Ok(new(rows, new PageInfo(next, null, next is not null, false)));
    }

    private static Result<Page<MembershipDirectoryItem>> Invalid(string field) => Result<Page<MembershipDirectoryItem>>.Fail(
        new Error(LocalizedMessage.Of("lockey_validation_failed"), new Dictionary<string, IReadOnlyList<LocalizedMessage>>
        {
            [field] = [LocalizedMessage.Of("lockey_invalid_value")],
        }));
}
