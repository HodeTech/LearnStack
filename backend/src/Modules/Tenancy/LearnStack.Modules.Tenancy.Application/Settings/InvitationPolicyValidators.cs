using FluentValidation;
using LearnStack.Modules.Tenancy.Application.Contracts.Settings;

namespace LearnStack.Modules.Tenancy.Application.Settings;

internal sealed class SetTenantInvitationLifetimeValidator : AbstractValidator<SetTenantInvitationLifetimeCommand>
{
    public SetTenantInvitationLifetimeValidator()
    {
        RuleFor(request => request.Days).InclusiveBetween(1, 30).WithErrorCode("lockey_invalid_value");
        RuleFor(request => request).Must(request => request.ExpectedPresent == request.ExpectedVersion.HasValue
            && request.ExpectedVersion is null or >= 0).WithErrorCode("lockey_invalid_value");
    }
}
internal sealed class ReadInvitationPolicyReceiptValidator : AbstractValidator<ReadInvitationPolicyReceiptQuery>
{
    public ReadInvitationPolicyReceiptValidator() => RuleFor(request => request.SettingId).Must(id => id != Guid.Empty && id.Version == 7).WithErrorCode("lockey_identifier_required");
}
