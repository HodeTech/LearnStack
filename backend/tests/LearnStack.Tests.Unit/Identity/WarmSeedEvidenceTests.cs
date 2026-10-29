using FluentAssertions;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Application.Seeding;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

public sealed class WarmSeedEvidenceTests
{
    private static readonly TenantId Tenant = TenantId.From(Guid.Parse("01916d82-4b10-7000-8000-000000000001"));
    private static readonly OrganizationId Organization = OrganizationId.From(Guid.Parse("01916d82-4b10-7000-8000-000000000011"));
    private static readonly UserId User = UserId.From(Guid.Parse("01916d82-4b10-7000-8000-00000000a001"));
    private static readonly Guid Member = Guid.Parse("01916d82-4b10-7000-8000-00000000a201");
    private static readonly Guid Role = Guid.Parse("01916d82-4b10-7000-8000-00000000a101");
    private static readonly DateTimeOffset At = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Active", false)]
    [InlineData("Suspended", false)]
    [InlineData("Revoked", true)]
    public void Valid_edited_empty_profile_and_removed_assignment_are_installed_history(string status, bool revoked)
    {
        var row = Membership() with
        {
            Status = status,
            Version = 3,
            RoleIds = [],
            DisplayName = null,
            PreferredLocale = null,
            Evidence = new(At, UserId.SystemActor, At.AddMinutes(1), User),
            DeletedAt = revoked ? At.AddMinutes(1) : null,
            DeletedBy = revoked ? User : null
        };
        SeedIdentityInstallationEvidence.MembershipIsInstalled(row, Member, Tenant, null, User).Should().BeTrue();
        SeedIdentityInstallationEvidence.MembershipIsPristine(row, Role, "Demo", "en").Should().BeFalse();
    }

    [Fact]
    public void Original_empty_profile_is_pristine_but_a_human_created_lookalike_is_not_installed()
    {
        var row = Membership();
        SeedIdentityInstallationEvidence.MembershipIsInstalled(row, Member, Tenant, null, User).Should().BeTrue();
        SeedIdentityInstallationEvidence.MembershipIsPristine(row, Role, "Demo", "en").Should().BeTrue();
        SeedIdentityInstallationEvidence.MembershipIsInstalled(row with { Evidence = new(At, User, null, null) },
            Member, Tenant, null, User).Should().BeFalse();
    }

    [Fact]
    public void Revocation_requires_retained_tombstone_and_foreign_scope_is_never_completion()
    {
        var row = Membership() with
        {
            Status = "Revoked",
            Version = 2,
            Evidence = new(At, UserId.SystemActor, At.AddMinutes(1), User)
        };
        SeedIdentityInstallationEvidence.MembershipIsInstalled(row, Member, Tenant, null, User).Should().BeFalse();
        SeedIdentityInstallationEvidence.MembershipIsInstalled(Membership() with { OrganizationId = Organization },
            Member, Tenant, null, User).Should().BeFalse();
    }

    [Fact]
    public void Removed_initial_grant_requires_an_actual_later_root_version_and_is_never_pristine()
    {
        var row = Administrator() with { Grants = IdentityFoundationManifest.Version1.TenantAdminKernel };
        SeedIdentityInstallationEvidence.RoleIsInstalled(row, Role, Tenant, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant).Should().BeFalse();
        row = row with { Version = 1, Evidence = new(At, UserId.SystemActor, At.AddMinutes(1), User) };
        SeedIdentityInstallationEvidence.RoleIsInstalled(row, Role, Tenant, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant).Should().BeTrue();
        SeedIdentityInstallationEvidence.RoleIsPristine(row).Should().BeFalse();
    }

    [Fact]
    public void Adopted_v2_role_remains_installed_after_ordinary_delta_removal_but_unknown_pin_refuses()
    {
        var release = IdentityRoleRelease.Version2;
        var row = Administrator() with
        {
            Version = 2,
            Evidence = new(At, UserId.SystemActor, At.AddMinutes(1), User),
            AdoptedReleaseVersion = release.Version,
            AdoptedReleaseFingerprint = release.Fingerprint,
            Grants = IdentityFoundationManifest.Version1.TenantAdminKernel
        };
        SeedIdentityInstallationEvidence.RoleIsInstalled(row, Role, Tenant, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant).Should().BeTrue();
        SeedIdentityInstallationEvidence.RoleIsInstalled(row with { AdoptedReleaseFingerprint = "unknown" },
            Role, Tenant, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant).Should().BeFalse();
    }

    [Fact]
    public void Locale_continuation_accepts_only_the_original_prefix_at_exact_version()
    {
        var declaration = Declaration();
        var first = declaration.OriginalLocales[0];
        var row = new TenantSeedDto(Tenant, "demo", "Demo", "Trial", Organization, 2, [first],
            At, UserId.SystemActor, At, UserId.SystemActor);
        SeedTenantContinuationEvidence.IsPristinePrefix(row, declaration).Should().BeTrue();
        SeedTenantContinuationEvidence.IsPristinePrefix(row with { Version = 4 }, declaration).Should().BeFalse("a removed locale is not a new prefix");
        SeedTenantContinuationEvidence.IsPristinePrefix(row with { Locales = [declaration.OriginalLocales[1]] }, declaration).Should().BeFalse();
        SeedTenantContinuationEvidence.IsPristinePrefix(row with { UpdatedBy = User }, declaration).Should().BeFalse();
        SeedTenantContinuationEvidence.IsPristinePrefix(row with { AdoptedSeedOriginVersion = 1 }, declaration).Should().BeFalse();
    }

    private static MembershipSeedDto Membership() => new(Member, Tenant, null, User, "Active", 0, null, null,
        null, null, [Role], new(At, UserId.SystemActor, null, null), GraphIsValid: true);

    private static RoleSeedDto Administrator() => new(Role, Tenant, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant,
        0, null, null, IdentityFoundationManifest.Version1.FindTemplate(BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant)!.Grants,
        new(At, UserId.SystemActor, null, null), 1, IdentityFoundationManifest.Version1.Fingerprint, GraphIsValid: true);

    private static SeedTenantDeclaration Declaration() => new(Tenant, "demo", "Demo", new(Organization, "first", "First"),
        new(OrganizationId.From(Guid.Parse("01916d82-4b10-7000-8000-000000000012")), "second", "Second"),
        [new("en", true, true, 0), new("tr", true, false, 1)], "demo.test", null, null, null);
}
