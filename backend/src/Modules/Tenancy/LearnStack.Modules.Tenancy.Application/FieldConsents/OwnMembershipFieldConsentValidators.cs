using FluentValidation;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.Modules.Tenancy.Domain;

namespace LearnStack.Modules.Tenancy.Application.FieldConsents;

public sealed class GrantOwnMembershipFieldConsentCommandValidator : AbstractValidator<GrantOwnMembershipFieldConsentCommand>
{
    public GrantOwnMembershipFieldConsentCommandValidator()
    {
        RuleFor(x => x.DefinitionId).Must(id => id != Guid.Empty && id.Version == 7);
        RuleFor(x => x.DefinitionVersion).GreaterThan(0);
        RuleFor(x => x.NoticeLocale).NotEmpty().MaximumLength(35);
        RuleFor(x => x.ExpectedNoticeHash).NotEmpty().Matches("^[0-9a-f]{64}$");
        RuleFor(x => x.ExpectedSequence).InclusiveBetween(0, MembershipFieldConsentTransition.MaximumSequence);
        RuleFor(x => x).Must(x => (x.ExpectedSequence == 0 && x.ExpectedEvidenceId is null)
            || (x.ExpectedSequence > 0 && x.ExpectedEvidenceId is { } id && id != Guid.Empty && id.Version == 7));
        RuleFor(x => x.Confirmed).Equal(true);
    }
}

public sealed class WithdrawOwnMembershipFieldConsentCommandValidator : AbstractValidator<WithdrawOwnMembershipFieldConsentCommand>
{
    public WithdrawOwnMembershipFieldConsentCommandValidator()
    {
        RuleFor(x => x.EvidenceId).Must(id => id != Guid.Empty && id.Version == 7);
        RuleFor(x => x.ExpectedEvidenceId).Must(id => id != Guid.Empty && id.Version == 7);
        RuleFor(x => x.ExpectedSequence).InclusiveBetween(1, MembershipFieldConsentTransition.MaximumSequence);
        RuleFor(x => x.Confirmed).Equal(true);
    }
}

public sealed class GetOwnMembershipFieldConsentQueryValidator : AbstractValidator<GetOwnMembershipFieldConsentQuery>
{
    public GetOwnMembershipFieldConsentQueryValidator()
    {
        RuleFor(x => x.MembershipId).Must(id => id != Guid.Empty && id.Version == 7);
        RuleFor(x => x.DefinitionId).Must(id => id != Guid.Empty && id.Version == 7);
        RuleFor(x => x.DefinitionVersion).GreaterThan(0);
    }
}

public sealed class ListOwnFieldConsentScopesQueryValidator : AbstractValidator<ListOwnFieldConsentScopesQuery>
{
    public ListOwnFieldConsentScopesQueryValidator()
    {
        RuleFor(x => x.Limit).InclusiveBetween(1, 20);
        RuleFor(x => x.Cursor).MaximumLength(256);
    }
}

public sealed class ListOwnMembershipFieldConsentsQueryValidator : AbstractValidator<ListOwnMembershipFieldConsentsQuery>
{
    public ListOwnMembershipFieldConsentsQueryValidator()
    {
        RuleFor(x => x.MembershipId).Must(id => id != Guid.Empty && id.Version == 7);
        RuleFor(x => x.Limit).InclusiveBetween(1, 20);
        RuleFor(x => x.Cursor).MaximumLength(256);
    }
}
