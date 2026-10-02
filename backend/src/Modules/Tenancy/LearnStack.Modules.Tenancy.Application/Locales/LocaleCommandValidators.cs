using FluentValidation;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.SharedKernel.Localization;

namespace LearnStack.Modules.Tenancy.Application.Locales;

internal sealed class AddTenantLocaleCommandValidator : AbstractValidator<AddTenantLocaleCommand>
{
    public AddTenantLocaleCommandValidator()
    {
        RuleFor(command => command.ExpectedVersion).GreaterThanOrEqualTo(0).WithErrorCode("lockey_concurrency_conflict");
        RuleFor(command => command.Locale).Must(LocaleInput.IsValid).WithErrorCode("lockey_locale_invalid");
        RuleFor(command => command.Sort).GreaterThanOrEqualTo((short)0).WithErrorCode("lockey_locale_invalid");
        RuleFor(command => command).Must(command => !command.IsDefault || command.IsEnabled)
            .OverridePropertyName(nameof(AddTenantLocaleCommand.IsDefault)).WithErrorCode("lockey_locale_disabled");
    }
}

internal sealed class SetDefaultTenantLocaleCommandValidator : AbstractValidator<SetDefaultTenantLocaleCommand>
{
    public SetDefaultTenantLocaleCommandValidator()
    {
        RuleFor(command => command.ExpectedVersion).GreaterThanOrEqualTo(0).WithErrorCode("lockey_concurrency_conflict");
        RuleFor(command => command.Locale).Must(LocaleInput.IsValid).WithErrorCode("lockey_locale_invalid");
    }
}

internal static class LocaleInput
{
    internal static bool IsValid(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > LocaleTag.MaxLength)
        {
            return false;
        }

        try
        {
            LocaleTag.EnsureWellFormed(value, nameof(value));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
