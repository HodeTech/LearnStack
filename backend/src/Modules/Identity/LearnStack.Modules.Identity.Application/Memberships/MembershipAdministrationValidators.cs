using System.Collections.Immutable;
using FluentValidation;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Domain;

namespace LearnStack.Modules.Identity.Application.Memberships;

public sealed class GetMembershipDetailQueryValidator : AbstractValidator<GetMembershipDetailQuery>
{
    public GetMembershipDetailQueryValidator() =>
        RuleFor(request => request.MembershipId).Must(MembershipAdministrationInput.ValidId).WithErrorCode("lockey_invalid_value");
}

public sealed class SetMembershipProfileCommandValidator : AbstractValidator<SetMembershipProfileCommand>
{
    public SetMembershipProfileCommandValidator()
    {
        RuleFor(request => request.MembershipId).Must(MembershipAdministrationInput.ValidId).WithErrorCode("lockey_invalid_value");
        RuleFor(request => request.ExpectedVersion).GreaterThanOrEqualTo(0).WithErrorCode("lockey_concurrency_conflict");
        RuleFor(request => request.DisplayName).Must(value => MembershipProfile.IsValid(value, null)).WithErrorCode("lockey_invalid_value");
        RuleFor(request => request.PreferredLocale).Must(value => MembershipProfile.IsValid(null, value)).WithErrorCode("lockey_invalid_value");
    }
}

public sealed class SetOwnMembershipProfileCommandValidator : AbstractValidator<SetOwnMembershipProfileCommand>
{
    public SetOwnMembershipProfileCommandValidator()
    {
        RuleFor(request => request.ExpectedVersion).GreaterThanOrEqualTo(0).WithErrorCode("lockey_concurrency_conflict");
        RuleFor(request => request.DisplayName).Must(value => MembershipProfile.IsValid(value, null)).WithErrorCode("lockey_invalid_value");
        RuleFor(request => request.PreferredLocale).Must(value => MembershipProfile.IsValid(null, value)).WithErrorCode("lockey_invalid_value");
    }
}

public sealed class ChangeMembershipStatusCommandValidator : AbstractValidator<ChangeMembershipStatusCommand>
{
    public ChangeMembershipStatusCommandValidator()
    {
        RuleFor(request => request.MembershipId).Must(MembershipAdministrationInput.ValidId).WithErrorCode("lockey_invalid_value");
        RuleFor(request => request.ExpectedVersion).GreaterThanOrEqualTo(0).WithErrorCode("lockey_concurrency_conflict");
        RuleFor(request => request.Status).IsInEnum().WithErrorCode("lockey_invalid_value");
    }
}

public sealed class ReplaceMembershipRoleAssignmentsCommandValidator : AbstractValidator<ReplaceMembershipRoleAssignmentsCommand>
{
    public ReplaceMembershipRoleAssignmentsCommandValidator()
    {
        RuleFor(request => request.MembershipId).Must(MembershipAdministrationInput.ValidId).WithErrorCode("lockey_invalid_value");
        RuleFor(request => request.ExpectedVersion).GreaterThanOrEqualTo(0).WithErrorCode("lockey_concurrency_conflict");
        RuleFor(request => request.RoleIds).Must(MembershipAdministrationInput.ValidRoles).WithErrorCode("lockey_invalid_value");
    }
}

internal static class MembershipAdministrationInput
{
    internal static bool ValidId(Guid value) => value != Guid.Empty && value.Version == 7;
    internal static bool ValidRoles(ImmutableArray<Guid> values) => !values.IsDefault
        && values.Length <= Membership.MaximumRoleAssignments && values.All(ValidId)
        && values.Distinct().Count() == values.Length;

    internal static bool Valid(object request) => request switch
    {
        SetMembershipProfileCommand profile => ValidId(profile.MembershipId) && profile.ExpectedVersion >= 0
            && MembershipProfile.IsValid(profile.DisplayName, profile.PreferredLocale),
        SetOwnMembershipProfileCommand profile => profile.ExpectedVersion >= 0
            && MembershipProfile.IsValid(profile.DisplayName, profile.PreferredLocale),
        ChangeMembershipStatusCommand status => ValidId(status.MembershipId) && status.ExpectedVersion >= 0 && Enum.IsDefined(status.Status),
        ReplaceMembershipRoleAssignmentsCommand assignments => ValidId(assignments.MembershipId)
            && assignments.ExpectedVersion >= 0 && ValidRoles(assignments.RoleIds),
        _ => false,
    };
}
