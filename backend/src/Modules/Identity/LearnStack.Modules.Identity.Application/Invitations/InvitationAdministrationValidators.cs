using FluentValidation;
using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.Modules.Identity.Domain;

namespace LearnStack.Modules.Identity.Application.Invitations;

internal sealed class CreateInvitationValidator : AbstractValidator<CreateInvitationCommand>
{
    public CreateInvitationValidator()
    {
        RuleFor(request => request.Email).Must(value => InvitationMailbox.TryNormalize(value, out _)).WithErrorCode("lockey_invalid_value");
        RuleFor(request => request.RoleId).Must(InvitationAdministrationInput.Id).WithErrorCode("lockey_identifier_required");
        RuleFor(request => request.ExpectedRoleVersion).GreaterThanOrEqualTo(0).WithErrorCode("lockey_invalid_value");
    }
}
internal sealed class RevokeInvitationValidator : AbstractValidator<RevokeInvitationCommand>
{
    public RevokeInvitationValidator()
    {
        RuleFor(request => request.InvitationId).Must(InvitationAdministrationInput.Id).WithErrorCode("lockey_identifier_required");
        RuleFor(request => request.ExpectedVersion).GreaterThanOrEqualTo(0).WithErrorCode("lockey_invalid_value");
    }
}
internal sealed class ReissueInvitationValidator : AbstractValidator<ReissueInvitationCommand>
{
    public ReissueInvitationValidator()
    {
        RuleFor(request => request.InvitationId).Must(InvitationAdministrationInput.Id).WithErrorCode("lockey_identifier_required");
        RuleFor(request => request.ExpectedVersion).GreaterThanOrEqualTo(0).WithErrorCode("lockey_invalid_value");
    }
}
internal sealed class GetInvitationValidator : AbstractValidator<GetInvitationQuery>
{
    public GetInvitationValidator() => RuleFor(request => request.InvitationId).Must(InvitationAdministrationInput.Id).WithErrorCode("lockey_identifier_required");
}
internal sealed class ReadInvitationReceiptValidator : AbstractValidator<ReadInvitationReceiptQuery>
{
    public ReadInvitationReceiptValidator() => RuleFor(request => request.InvitationId).Must(InvitationAdministrationInput.Id).WithErrorCode("lockey_identifier_required");
}
internal sealed class ListInvitationsValidator : AbstractValidator<ListInvitationsQuery>
{
    public ListInvitationsValidator()
    {
        RuleFor(request => request.Limit).InclusiveBetween(1, 100).WithErrorCode("lockey_invalid_value");
        RuleFor(request => request.Cursor).Must(value => value is null || value.Length is > 0 and <= 1024).WithErrorCode("lockey_invalid_value");
    }
}
internal static class InvitationAdministrationInput
{
    internal static bool Id(Guid id) => id != Guid.Empty && id.Version == 7;
}
