using System.Security.Cryptography;
using FluentValidation;
using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.SharedKernel.Identity;

namespace LearnStack.Modules.Identity.Application.Invitations;

internal static class InvitationEntryInput
{
    internal static bool Valid(Guid id, string code)
    {
        if (!InvitationVerifier.TryDigest(id, code, out var digest)) return false;
        CryptographicOperations.ZeroMemory(digest); return true;
    }
}
public sealed class InspectInvitationQueryValidator : AbstractValidator<InspectInvitationQuery>
{
    public InspectInvitationQueryValidator() => RuleFor(request => request).Must(request => InvitationEntryInput.Valid(request.InvitationId, request.Code))
        .WithMessage("lockey_invalid_value").OverridePropertyName("invitation");
}
public sealed class GetOwnInvitationResultQueryValidator : AbstractValidator<GetOwnInvitationResultQuery>
{
    public GetOwnInvitationResultQueryValidator() => RuleFor(request => request).Must(request => InvitationEntryInput.Valid(request.InvitationId, request.Code))
        .WithMessage("lockey_invalid_value").OverridePropertyName("invitation");
}
public sealed class AcceptInvitationCommandValidator : AbstractValidator<AcceptInvitationCommand>
{
    public AcceptInvitationCommandValidator() => RuleFor(request => request).Must(request => request.ExpectedVersion >= 0
        && InvitationEntryInput.Valid(request.InvitationId, request.Code)).WithMessage("lockey_invalid_value").OverridePropertyName("invitation");
}
