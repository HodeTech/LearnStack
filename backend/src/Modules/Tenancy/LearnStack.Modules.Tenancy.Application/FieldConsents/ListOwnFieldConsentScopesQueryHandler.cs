using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.FieldConsents;

public sealed class ListOwnFieldConsentScopesQueryHandler(OwnConsentEntryAuthorization authorization)
    : IRequestHandler<ListOwnFieldConsentScopesQuery, Result<MembershipFieldConsentScopePage>>
{
    public Task<Result<MembershipFieldConsentScopePage>> Handle(ListOwnFieldConsentScopesQuery request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Limit is < 1 or > 20 || request.Cursor is { Length: > 256 })
            return Task.FromResult(Fail("lockey_validation_failed"));
        var witness = authorization.For(request, ConsentSubjectPurpose.Discover);
        if (witness?.ScopePage is not { } page || page.Items.Length > request.Limit)
            return Task.FromResult(Fail("lockey_forbidden"));
        var items = page.Items.Select(item => new MembershipFieldConsentScope(item.MembershipId, item.TenantId, item.OrganizationId)).ToArray();
        if (!ReferenceEquals(authorization.For(request, ConsentSubjectPurpose.Discover), witness))
            return Task.FromResult(Fail("lockey_forbidden"));
        return Task.FromResult(Result.Ok(new MembershipFieldConsentScopePage(items, page.NextCursor)));
    }
    private static Result<MembershipFieldConsentScopePage> Fail(string code) => OwnFieldConsentAuthorization.Failure<MembershipFieldConsentScopePage>(code);
}
