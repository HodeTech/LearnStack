using FluentValidation;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Identity.Domain;

namespace LearnStack.Modules.Identity.Application.Roles;

public sealed class ChangeRoleGrantsCommandValidator : AbstractValidator<ChangeRoleGrantsCommand>
{
    public ChangeRoleGrantsCommandValidator()
    {
        RuleFor(request => request.RoleId).Must(RoleGrantInput.ValidId).WithErrorCode("lockey_invalid_value");
        RuleFor(request => request.ExpectedRoleVersion).GreaterThanOrEqualTo(0).WithErrorCode("lockey_concurrency_conflict");
        RuleFor(request => request.Grants).Must(grants => !grants.IsDefault && grants.Length <= IdentityFoundationManifest.Version1.Definitions.Length
            && grants.All(definition => definition is not null) && grants.Distinct().Count() == grants.Length)
            .WithErrorCode("lockey_invalid_value");
    }
}

public sealed class AdoptRoleGrantManifestCommandValidator : AbstractValidator<AdoptRoleGrantManifestCommand>
{
    public AdoptRoleGrantManifestCommandValidator()
    {
        RuleFor(request => request.RoleId).Must(RoleGrantInput.ValidId).WithErrorCode("lockey_invalid_value");
        RuleFor(request => request.ExpectedRoleVersion).GreaterThanOrEqualTo(0).WithErrorCode("lockey_concurrency_conflict");
        RuleFor(request => request.ManifestVersion).GreaterThan(0).WithErrorCode("lockey_invalid_value");
        RuleFor(request => request.ManifestFingerprint).Must(value => value is { Length: 64 }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')).WithErrorCode("lockey_invalid_value");
    }
}

internal static class RoleGrantInput
{
    internal static bool ValidId(Guid value) => value != Guid.Empty && value.Version == 7;
}
