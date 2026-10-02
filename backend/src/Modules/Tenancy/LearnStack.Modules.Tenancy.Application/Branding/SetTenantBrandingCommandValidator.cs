using FluentValidation;
using LearnStack.Modules.Tenancy.Application.Contracts.Branding;

namespace LearnStack.Modules.Tenancy.Application.Branding;

internal sealed class SetTenantBrandingCommandValidator : AbstractValidator<SetTenantBrandingCommand>
{
    public SetTenantBrandingCommandValidator()
    {
        RuleFor(command => command.SettingId).NotEmpty().WithErrorCode("lockey_identifier_required");
        RuleFor(command => command.ExpectedVersion).Must(version => version is null or >= 0)
            .WithErrorCode("lockey_concurrency_conflict");
        // Complete theme resolution is performed once by the handler before any mutation.
        RuleFor(command => command.Theme).NotEmpty().WithErrorCode("lockey_branding_invalid");
    }
}
