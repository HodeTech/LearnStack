using FluentValidation;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;

namespace LearnStack.Modules.Identity.Application.Memberships;

public sealed class ListMembershipsQueryValidator : AbstractValidator<ListMembershipsQuery>
{
    public ListMembershipsQueryValidator()
    {
        RuleFor(request => request.Limit).GreaterThan(0).OverridePropertyName("limit").WithErrorCode("lockey_invalid_value");
        RuleFor(request => request.Cursor).Must(cursor => cursor is null || MembershipDirectoryCursorCodec.TryDecode(cursor, null, out _))
            .OverridePropertyName("cursor").WithErrorCode("lockey_invalid_value");
    }
}
