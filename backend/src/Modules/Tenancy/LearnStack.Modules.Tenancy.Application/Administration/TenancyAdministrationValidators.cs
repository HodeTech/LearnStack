using FluentValidation;
using LearnStack.Modules.Tenancy.Application.Contracts.Branding;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Application.Locales;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Tenancy.Application.Administration;

internal sealed class ListOrganizationsQueryValidator : AbstractValidator<ListOrganizationsQuery>
{
    public ListOrganizationsQueryValidator()
    {
        RuleFor(request => request.Limit).GreaterThan(0).WithErrorCode("lockey_invalid_value");
        RuleFor(request => request.Cursor).Must(TenancyAdministrationInput.ValidCursor).WithErrorCode("lockey_invalid_value");
    }
}

internal sealed class GetOrganizationDetailQueryValidator : AbstractValidator<GetOrganizationDetailQuery>
{
    public GetOrganizationDetailQueryValidator() =>
        RuleFor(request => request.OrganizationId).Must(TenancyAdministrationInput.ValidId).WithErrorCode("lockey_identifier_required");
}

internal sealed class CreateManagedOrganizationCommandValidator : AbstractValidator<CreateManagedOrganizationCommand>
{
    public CreateManagedOrganizationCommandValidator()
    {
        RuleFor(request => request.Slug).Must(TenancyAdministrationInput.ValidSlug).WithErrorCode("lockey_slug_not_url_safe");
        RuleFor(request => request.DisplayName).Must(TenancyAdministrationInput.ValidName).WithErrorCode("lockey_display_name_required");
    }
}

internal sealed class RenameOrganizationCommandValidator : AbstractValidator<RenameOrganizationCommand>
{
    public RenameOrganizationCommandValidator()
    {
        RuleFor(request => request.OrganizationId).Must(TenancyAdministrationInput.ValidId).WithErrorCode("lockey_identifier_required");
        RuleFor(request => request.ExpectedVersion).GreaterThanOrEqualTo(0).WithErrorCode("lockey_concurrency_conflict");
        RuleFor(request => request.DisplayName).Must(TenancyAdministrationInput.ValidName).WithErrorCode("lockey_display_name_required");
    }
}

internal sealed class ArchiveOrganizationCommandValidator : AbstractValidator<ArchiveOrganizationCommand>
{
    public ArchiveOrganizationCommandValidator()
    {
        RuleFor(request => request.OrganizationId).Must(TenancyAdministrationInput.ValidId).WithErrorCode("lockey_identifier_required");
        RuleFor(request => request.ExpectedVersion).GreaterThanOrEqualTo(0).WithErrorCode("lockey_concurrency_conflict");
    }
}

internal sealed class SelectDefaultOrganizationCommandValidator : AbstractValidator<SelectDefaultOrganizationCommand>
{
    public SelectDefaultOrganizationCommandValidator()
    {
        RuleFor(request => request.OrganizationId).Must(TenancyAdministrationInput.ValidId).WithErrorCode("lockey_identifier_required");
        RuleFor(request => request.ExpectedVersion).GreaterThanOrEqualTo(0).WithErrorCode("lockey_concurrency_conflict");
    }
}

internal sealed class AddManagedTenantLocaleCommandValidator : AbstractValidator<AddManagedTenantLocaleCommand>
{
    public AddManagedTenantLocaleCommandValidator()
    {
        RuleFor(request => request.ExpectedVersion).GreaterThanOrEqualTo(0).WithErrorCode("lockey_concurrency_conflict");
        RuleFor(request => request.Locale).Must(LocaleInput.IsValid).WithErrorCode("lockey_locale_invalid");
        RuleFor(request => request.Sort).GreaterThanOrEqualTo((short)0).WithErrorCode("lockey_locale_invalid");
        RuleFor(request => request).Must(request => !request.IsDefault || request.IsEnabled)
            .OverridePropertyName(nameof(AddManagedTenantLocaleCommand.IsDefault)).WithErrorCode("lockey_locale_disabled");
    }
}

internal sealed class SetManagedDefaultTenantLocaleCommandValidator : AbstractValidator<SetManagedDefaultTenantLocaleCommand>
{
    public SetManagedDefaultTenantLocaleCommandValidator()
    {
        RuleFor(request => request.ExpectedVersion).GreaterThanOrEqualTo(0).WithErrorCode("lockey_concurrency_conflict");
        RuleFor(request => request.Locale).Must(LocaleInput.IsValid).WithErrorCode("lockey_locale_invalid");
    }
}

internal sealed class SetTenantLocaleEnabledCommandValidator : AbstractValidator<SetTenantLocaleEnabledCommand>
{
    public SetTenantLocaleEnabledCommandValidator()
    {
        RuleFor(request => request.ExpectedVersion).GreaterThanOrEqualTo(0).WithErrorCode("lockey_concurrency_conflict");
        RuleFor(request => request.Locale).Must(LocaleInput.IsValid).WithErrorCode("lockey_locale_invalid");
    }
}

internal sealed class RemoveTenantLocaleCommandValidator : AbstractValidator<RemoveTenantLocaleCommand>
{
    public RemoveTenantLocaleCommandValidator()
    {
        RuleFor(request => request.ExpectedVersion).GreaterThanOrEqualTo(0).WithErrorCode("lockey_concurrency_conflict");
        RuleFor(request => request.Locale).Must(LocaleInput.IsValid).WithErrorCode("lockey_locale_invalid");
    }
}

internal sealed class SetManagedTenantBrandingCommandValidator : AbstractValidator<SetManagedTenantBrandingCommand>
{
    public SetManagedTenantBrandingCommandValidator()
    {
        RuleFor(request => request.ExpectedVersion).Must(version => version is null or >= 0).WithErrorCode("lockey_concurrency_conflict");
        RuleFor(request => request.Theme).NotEmpty().WithErrorCode("lockey_branding_invalid");
    }
}

internal static class TenancyAdministrationInput
{
    internal static bool ValidId(OrganizationId value) => StronglyTypedId.IsAssigned(value) && value.Value.Version == 7;
    internal static bool ValidSlug(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= UrlSlug.MaxLength && UrlSlug.IsUrlSafe(value);
    internal static bool ValidName(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= MappedLength.DisplayName;
    internal static bool ValidCursor(string? value) => value is null || value.Length is > 0 and <= 2048;

    internal static bool Valid(object request) => request switch
    {
        ListOrganizationsQuery query => query.Limit > 0 && ValidCursor(query.Cursor),
        GetOrganizationDetailQuery query => ValidId(query.OrganizationId),
        ReadOrganizationReceiptQuery query => ValidId(query.OrganizationId),
        ReadDefaultOrganizationReceiptQuery query => StronglyTypedId.IsAssigned(query.TenantId)
            && query.TenantId != TenantId.PlatformSentinel && query.TenantId.Value.Version == 7,
        CreateManagedOrganizationCommand command => ValidSlug(command.Slug) && ValidName(command.DisplayName),
        RenameOrganizationCommand command => ValidId(command.OrganizationId) && command.ExpectedVersion >= 0 && ValidName(command.DisplayName),
        ArchiveOrganizationCommand command => ValidId(command.OrganizationId) && command.ExpectedVersion >= 0,
        SelectDefaultOrganizationCommand command => ValidId(command.OrganizationId) && command.ExpectedVersion >= 0,
        AddManagedTenantLocaleCommand command => command.ExpectedVersion >= 0 && LocaleInput.IsValid(command.Locale)
            && command.Sort >= 0 && (!command.IsDefault || command.IsEnabled),
        SetManagedDefaultTenantLocaleCommand command => command.ExpectedVersion >= 0 && LocaleInput.IsValid(command.Locale),
        SetTenantLocaleEnabledCommand command => command.ExpectedVersion >= 0 && LocaleInput.IsValid(command.Locale),
        RemoveTenantLocaleCommand command => command.ExpectedVersion >= 0 && LocaleInput.IsValid(command.Locale),
        SetManagedTenantBrandingCommand command => command.ExpectedVersion is null or >= 0 && !string.IsNullOrWhiteSpace(command.Theme),
        _ => false,
    };
}
