using FluentValidation;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Permissions;

namespace LearnStack.Modules.Identity.Application.Seeding;

public sealed class InitializeSeedRoleCommandValidator : AbstractValidator<InitializeSeedRoleCommand>
{
    public InitializeSeedRoleCommandValidator()
    {
        RuleFor(request => request.RoleId).Must(SeedFoundationGate.ValidRoot);
        RuleFor(request => request).Must(request => IdentityFoundationManifest.Version1.FindTemplate(
            request.BuiltInKey, request.CapabilityScope) is not null);
        RuleFor(request => request.ManifestVersion).Equal(1);
        RuleFor(request => request.ExpectedFingerprint).Must(SeedFoundationValidation.IsFingerprint);
    }
}

public sealed class CreateFirstSeedAdminMembershipCommandValidator : AbstractValidator<CreateFirstSeedAdminMembershipCommand>
{
    public CreateFirstSeedAdminMembershipCommandValidator()
    {
        RuleFor(request => request.MembershipId).Must(SeedFoundationGate.ValidRoot);
        RuleFor(request => request.UserId).Must(SeedFoundationGate.ValidUser);
        RuleFor(request => request.RoleId).Must(SeedFoundationGate.ValidRoot);
        RuleFor(request => request.ManifestVersion).Equal(1);
        RuleFor(request => request.ExpectedFingerprint).Must(SeedFoundationValidation.IsFingerprint);
    }
}

public sealed class CreateSeedMembershipCommandValidator : AbstractValidator<CreateSeedMembershipCommand>
{
    public CreateSeedMembershipCommandValidator()
    {
        RuleFor(request => request.MembershipId).Must(SeedFoundationGate.ValidRoot);
        RuleFor(request => request.UserId).Must(SeedFoundationGate.ValidUser);
        RuleFor(request => request.RoleId).Must(SeedFoundationGate.ValidRoot);
        RuleFor(request => request.MembershipScope).Must(SeedFoundationValidation.IsContextualScope);
        RuleFor(request => request.ManifestVersion).Equal(1);
        RuleFor(request => request.ExpectedFingerprint).Must(SeedFoundationValidation.IsFingerprint);
    }
}

public sealed class SetSeedMembershipProfileCommandValidator : AbstractValidator<SetSeedMembershipProfileCommand>
{
    public SetSeedMembershipProfileCommandValidator()
    {
        RuleFor(request => request.MembershipId).Must(SeedFoundationGate.ValidRoot);
        RuleFor(request => request.MembershipScope).Must(SeedFoundationValidation.IsContextualScope);
        RuleFor(request => request).Must(request => MembershipProfile.IsValid(request.DisplayName, request.PreferredLocale));
        RuleFor(request => request.ManifestVersion).Equal(1);
        RuleFor(request => request.ExpectedFingerprint).Must(SeedFoundationValidation.IsFingerprint);
    }
}

public sealed class GetRoleSeedStateQueryValidator : AbstractValidator<GetRoleSeedStateQuery>
{
    public GetRoleSeedStateQueryValidator()
    {
        RuleFor(request => request.RoleId).Must(SeedFoundationGate.ValidRoot);
        RuleFor(request => request).Must(request => IdentityFoundationManifest.Version1.FindTemplate(
            request.BuiltInKey, request.CapabilityScope) is not null);
        RuleFor(request => request.ManifestVersion).Equal(1);
        RuleFor(request => request.ExpectedFingerprint).Must(SeedFoundationValidation.IsFingerprint);
    }
}

public sealed class GetMembershipSeedStateQueryValidator : AbstractValidator<GetMembershipSeedStateQuery>
{
    public GetMembershipSeedStateQueryValidator()
    {
        RuleFor(request => request.MembershipId).Must(SeedFoundationGate.ValidRoot);
        RuleFor(request => request.UserId).Must(SeedFoundationGate.ValidUser);
        RuleFor(request => request.RoleId).Must(SeedFoundationGate.ValidRoot);
        RuleFor(request => request.MembershipScope).Must(SeedFoundationValidation.IsContextualScope);
        RuleFor(request => request.ManifestVersion).Equal(1);
        RuleFor(request => request.ExpectedFingerprint).Must(SeedFoundationValidation.IsFingerprint);
    }
}

internal static class SeedFoundationValidation
{
    internal static bool IsContextualScope(PermissionScope scope) => scope is PermissionScope.Tenant or PermissionScope.Organization;
    internal static bool IsFingerprint(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
