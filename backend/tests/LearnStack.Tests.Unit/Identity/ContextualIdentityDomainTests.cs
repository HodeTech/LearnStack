using FluentAssertions;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

public sealed class ContextualIdentityDomainTests
{
    private static readonly TenantId Tenant = TenantId.From(Guid.Parse("01916d82-4b10-7000-8000-000000000001"));
    private static readonly OrganizationId Organization = OrganizationId.From(Guid.Parse("01916d82-4b10-7000-8000-000000000002"));
    private static readonly UserId Actor = UserId.From(Guid.Parse("01916d82-4b10-7000-8000-00000000a001"));
    private static readonly DateTimeOffset At = new(2026, 10, 26, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Foundation_manifest_retains_complete_scoped_values_and_the_two_pair_minimum()
    {
        var manifest = IdentityFoundationManifest.Version1;
        manifest.Definitions.Select(definition => (definition.Scope, definition.Key)).Should().BeEquivalentTo(
            [(PermissionScope.Tenant, "identity.membership.admin"),
             (PermissionScope.Organization, "identity.membership.admin"),
             (PermissionScope.Tenant, "identity.role.admin"),
             (PermissionScope.Tenant, "identity.membership_profile.write"),
             (PermissionScope.Organization, "identity.membership_profile.write")]);
        manifest.Templates.Should().HaveCount(8);
        manifest.Templates.Select(template => template.BuiltInKey).Distinct().Should().HaveCount(7);
        manifest.Templates.Select(template => (template.BuiltInKey, template.Scope, template.Grants.Length)).Should().Equal(
            (BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant, 3),
            (BuiltInRoleKey.Instructor, PermissionScope.Tenant, 0),
            (BuiltInRoleKey.Learner, PermissionScope.Tenant, 1),
            (BuiltInRoleKey.Guardian, PermissionScope.Tenant, 0),
            (BuiltInRoleKey.Editor, PermissionScope.Tenant, 0),
            (BuiltInRoleKey.OrgAdmin, PermissionScope.Organization, 2),
            (BuiltInRoleKey.OrgInstructor, PermissionScope.Organization, 0),
            (BuiltInRoleKey.Learner, PermissionScope.Organization, 1));
        manifest.TenantAdminKernel.Select(definition => (definition.Scope, definition.Key)).Should().Equal(
            (PermissionScope.Tenant, "identity.membership.admin"), (PermissionScope.Tenant, "identity.role.admin"));
        manifest.IsActive(PermissionScope.Organization, IdentityFoundationManifest.RoleAdmin).Should().BeFalse();
        manifest.IsActive(PermissionScope.Tenant, "identity.unknown.admin").Should().BeFalse();
        manifest.Fingerprint.Should().Be("ba16d2c21f2547f3dc7f9dce24e25a18f30103186739ea00a33e9edf43834794");
        manifest.KernelSqlValues.Should().Be("('Tenant', 'identity.membership.admin'), ('Tenant', 'identity.role.admin')");
        manifest.RegistrySqlValues.Count(character => character == '(').Should().Be(5);
        manifest.RolePairsSqlValues.Count(character => character == '(').Should().Be(8);
        manifest.GrantSqlValues.Count(character => character == '(').Should().Be(7);
    }

    [Fact]
    public void Scope_and_identity_are_bound_into_each_seed_fingerprint()
    {
        var manifest = IdentityFoundationManifest.Version1;
        var id = Guid.Parse("01916d82-4b10-7000-8000-00000000a201");
        var roleId = Guid.Parse("01916d82-4b10-7000-8000-00000000a103");
        manifest.MembershipFingerprint(Tenant, null, id, Actor, roleId).Should().NotBe(
            manifest.MembershipFingerprint(Tenant, Organization, id, Actor, roleId));
        manifest.ProfileFingerprint(Tenant, null, id, null, "en").Should().NotBe(
            manifest.ProfileFingerprint(Tenant, null, id, "null", "en"));
        manifest.ProfileFingerprint(Tenant, null, id, "a|b", "en").Should().NotBe(
            manifest.ProfileFingerprint(Tenant, null, id, "a", "b|en"));
    }

    [Fact]
    public void Contained_graph_derives_scope_and_natural_keys_from_its_root()
    {
        var role = RoleFor(BuiltInRoleKey.Learner, PermissionScope.Organization);
        var result = Membership.Create(MembershipId.From(Guid.Parse("01916d82-4b10-7000-8000-00000000a201")),
            Tenant, Organization, Actor, role, At, Actor);
        result.IsSuccess.Should().BeTrue();
        var member = result.Value!;
        member.Profile.Membership.Should().BeSameAs(member);
        member.Profile.MembershipId.Should().Be(member.Id);
        member.Profile.TenantId.Should().Be(Tenant);
        member.Profile.OrganizationId.Should().Be(Organization);
        member.RoleAssignments.Should().ContainSingle().Which.OrganizationId.Should().Be(Organization);
        member.RoleAssignments.Single().RoleId.Should().Be(role.Id);
        member.RoleAssignments.Single().Membership.Should().BeSameAs(member);
        role.Grants.Should().ContainSingle().Which.PermissionScope.Should().Be(PermissionScope.Organization);
        role.IsTenantAdministrator.Should().BeFalse();
        member.Version.Should().Be(0);
    }

    [Fact]
    public void Membership_rejects_incompatible_scope_and_cross_tenant_roles()
    {
        var role = RoleFor(BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant);
        var id = MembershipId.From(Guid.Parse("01916d82-4b10-7000-8000-00000000a201"));
        Membership.Create(id, Tenant, Organization, Actor, role, At, Actor).IsFailure.Should().BeTrue();
        Membership.Create(id, TenantId.From(Guid.Parse("01916d82-4b10-7000-8000-000000000003")),
            null, Actor, role, At, Actor).IsFailure.Should().BeTrue();
        Membership.Create(id, Tenant, null, UserId.SystemActor, role, At, Actor).IsFailure.Should().BeTrue();
        Membership.Create(id, Tenant, null, UserId.From(Guid.NewGuid()), role, At, Actor).IsFailure.Should().BeTrue();
        Role.Create(role.Id, Tenant, BuiltInRoleKey.TenantAdmin, PermissionScope.Organization, At, Actor)
            .IsFailure.Should().BeTrue();
        Role.Create(role.Id, Tenant, BuiltInRoleKey.Learner, PermissionScope.Platform, At, Actor)
            .IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Profile_change_versions_only_its_owning_root_and_invalid_values_leave_it_unchanged()
    {
        var member = Member();
        member.SetProfile("Contextual Name", "en-GB", At.AddMinutes(1), Actor).IsSuccess.Should().BeTrue();
        member.Version.Should().Be(1);
        member.Profile.DisplayName.Should().Be("Contextual Name");
        member.SetProfile("Contextual Name", "en-GB", At.AddMinutes(2), Actor).IsSuccess.Should().BeTrue();
        member.Version.Should().Be(1);
        member.SetProfile(new string('x', 129), "en", At.AddMinutes(3), Actor).IsFailure.Should().BeTrue();
        member.SetProfile("canary\nvalue", "en", At.AddMinutes(3), Actor).IsFailure.Should().BeTrue();
        member.SetProfile("Other", "en--GB", At.AddMinutes(3), Actor).IsFailure.Should().BeTrue();
        member.SetProfile("broken\ud800", "en", At.AddMinutes(3), Actor).IsFailure.Should().BeTrue();
        member.Version.Should().Be(1);
        member.Profile.DisplayName.Should().Be("Contextual Name");
    }

    [Fact]
    public void Revocation_is_terminal_and_carries_the_complete_deletion_stamp()
    {
        var member = Member();
        member.Suspend(At.AddMinutes(1), Actor).IsSuccess.Should().BeTrue();
        member.SetProfile("Hidden", "en", At.AddMinutes(2), Actor).IsFailure.Should().BeTrue();
        member.Reactivate(At.AddMinutes(3), Actor).IsSuccess.Should().BeTrue();
        member.Revoke(At.AddMinutes(4), Actor).IsSuccess.Should().BeTrue();
        member.Status.Should().Be(MembershipStatus.Revoked);
        member.DeletedAt.Should().Be(At.AddMinutes(4));
        member.DeletedBy.Should().Be(Actor);
        member.Version.Should().Be(3);
        member.Reactivate(At.AddMinutes(5), Actor).IsFailure.Should().BeTrue();
        member.Suspend(At.AddMinutes(5), Actor).IsFailure.Should().BeTrue();
        member.Revoke(At.AddMinutes(5), Actor).IsFailure.Should().BeTrue();
        member.Profile.Should().NotBeNull();
        member.RoleAssignments.Should().ContainSingle();
    }

    [Fact]
    public void Lock_plan_copies_deduplicates_and_orders_the_entire_user_family()
    {
        var other = UserId.From(Guid.Parse("01916d82-4b10-7000-8000-00000000a002"));
        var users = new List<UserId> { Actor, other, Actor };
        var plan = new IdentityMutationLockPlan(users, Tenant, IdentityTenantLockMode.Exclusive);
        users.Clear();
        plan.UserIds.Should().HaveCount(2);
        plan.UserIds.Select(IdentityAdvisoryLockKey.ForUser).Should().BeInAscendingOrder();
        var invalid = () => new IdentityMutationLockPlan([UserId.SystemActor], Tenant, IdentityTenantLockMode.Shared);
        invalid.Should().Throw<ArgumentException>().WithMessage("Identity mutation target is invalid.*");
    }

    private static Role RoleFor(BuiltInRoleKey key, PermissionScope scope) => Role.Create(
        RoleId.From(Guid.Parse("01916d82-4b10-7000-8000-00000000a101")), Tenant, key, scope, At, Actor).Value!;

    private static Membership Member() => Membership.Create(
        MembershipId.From(Guid.Parse("01916d82-4b10-7000-8000-00000000a201")), Tenant, null,
        Actor, RoleFor(BuiltInRoleKey.Learner, PermissionScope.Tenant), At, Actor).Value!;
}
